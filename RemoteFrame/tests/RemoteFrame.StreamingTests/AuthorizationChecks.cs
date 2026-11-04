using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Principal;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteFrame.AspNetCore;
using RemoteFrame.Core;

internal static class AuthorizationChecks
{
    public static async Task RunAsync()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        File.WriteAllText(file, "Authorization test");
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("test", _ => { });
            builder.Services.AddAuthorization();
            var batchCalls = 0;
            var individualCalls = 0;
            builder.Services.AddRemoteFrame(o =>
            {
                o.Files.Add("allowed", file);
                o.Files.Add("denied", file);
                o.Files.Add("batch-hidden", file);
                o.AuthorizeFileAsync = (_, id, _) =>
                {
                    Interlocked.Increment(ref individualCalls);
                    return ValueTask.FromResult(id != "denied");
                };
                o.AuthorizeFilesAsync = (context, candidates, ct) =>
                {
                    Interlocked.Increment(ref batchCalls);
                    Check(candidates.SequenceEqual(["allowed", "batch-hidden", "denied"]), "Batch authorization receives sorted catalog IDs");
                    Check(ct == context.RequestAborted, "Batch authorization receives request cancellation token");
                    IReadOnlyCollection<string> filtered = context.Request.Headers.ContainsKey("X-Test-Empty-Listing")
                        ? Array.Empty<string>() : ["allowed", "allowed", "denied", "unknown", "ALLOWED"];
                    return ValueTask.FromResult(filtered);
                };
            });
            await using var app = builder.Build();
            app.Use(async (context, next) =>
            {
                var mode = context.Request.Headers["X-Test-Identity"].ToString();
                if (mode == "windows" && OperatingSystem.IsWindows())
                {
                    using var identity = WindowsIdentity.GetCurrent();
                    context.User = new WindowsPrincipal(identity);
                    await next(context);
                    return;
                }
                context.User = mode switch
                {
                    "alice" or "bob" => new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, mode)], "test")),
                    "sid-only" => new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-123")], "test")),
                    _ => new ClaimsPrincipal(new ClaimsIdentity())
                };
                await next(context);
            });
            app.UseAuthorization();
            app.UseWebSockets();
            app.MapRemoteFrame();
            await app.StartAsync();
            using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
            Check((await http.GetAsync("/api/files")).StatusCode == HttpStatusCode.Unauthorized, "Anonymous users remain unauthorized");
            http.DefaultRequestHeaders.Add("X-Test-Identity", "sid-only");
            Check((await http.GetAsync("/api/files")).StatusCode == HttpStatusCode.Forbidden, "Generic SID claims cannot substitute for Windows identity");
            Check(batchCalls == 0, "Route authorization runs before batch file filtering");
            http.DefaultRequestHeaders.Remove("X-Test-Identity");
            http.DefaultRequestHeaders.Add("X-Test-Identity", "alice");
            Check((await http.GetFromJsonAsync<string[]>("/api/files"))!.SequenceEqual(["allowed"]), "NameIdentifier authentication and file filtering still work");
            Check(batchCalls == 1 && individualCalls == 2, "Batch filter runs once and excludes IDs before individual checks");
            http.DefaultRequestHeaders.Add("X-Test-Empty-Listing", "true");
            Check((await http.GetFromJsonAsync<string[]>("/api/files"))!.Length == 0 && individualCalls == 2, "Empty batch result hides all files without individual checks");
            http.DefaultRequestHeaders.Remove("X-Test-Empty-Listing");
            var listCallsBeforeOpen = batchCalls;
            await CheckSessionOwnership("bob");
            Check(batchCalls == listCallsBeforeOpen, "Session access uses per-file authorization independently of listing filter");
            if (OperatingSystem.IsWindows())
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                Check(SessionOwner.Identifier(principal) == "windows:" + identity.User!.Value, "Windows SID supplies session identifier without NameIdentifier");
                identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "explicit-owner"));
                Check(SessionOwner.Identifier(principal) == "explicit-owner", "Explicit NameIdentifier keeps precedence over Windows SID");
                http.DefaultRequestHeaders.Remove("X-Test-Identity");
                http.DefaultRequestHeaders.Add("X-Test-Identity", "windows");
                Check((await http.GetFromJsonAsync<string[]>("/api/files"))!.SequenceEqual(["allowed"]), "Authenticated Windows users pass default route authorization");
                Check((await http.PostAsJsonAsync("/api/sessions", new { fileId = "denied" })).StatusCode == HttpStatusCode.NotFound, "Windows authentication still respects per-file authorization");
                await CheckSessionOwnership("alice");
            }
            await app.StopAsync();

            async Task CheckSessionOwnership(string other)
            {
                var owner = http.DefaultRequestHeaders.GetValues("X-Test-Identity").Single();
                var response = await http.PostAsJsonAsync("/api/sessions", new { fileId = "allowed" });
                response.EnsureSuccessStatusCode();
                var opened = await response.Content.ReadFromJsonAsync<Opened>();
                var path = $"/api/sessions/{opened!.Id}/configuration";
                Check((await http.PutAsJsonAsync(path, new FrameConfiguration())).IsSuccessStatusCode, owner + " can configure its own session");
                http.DefaultRequestHeaders.Remove("X-Test-Identity");
                http.DefaultRequestHeaders.Add("X-Test-Identity", other);
                Check((await http.PutAsJsonAsync(path, new FrameConfiguration())).StatusCode == HttpStatusCode.NotFound, "Different owner cannot configure " + owner + " session");
                http.DefaultRequestHeaders.Remove("X-Test-Identity");
                http.DefaultRequestHeaders.Add("X-Test-Identity", owner);
                (await http.DeleteAsync($"/api/sessions/{opened.Id}")).EnsureSuccessStatusCode();
            }
        }
        finally { File.Delete(file); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }

    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }
}
