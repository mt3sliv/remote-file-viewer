using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using RemoteFrame.AspNetCore;

// Exercises the packed NuGet artifacts, without referencing library source projects.
var file = Path.Combine(Path.GetTempPath(), "remoteframe-test-" + Guid.NewGuid().ToString("N") + ".txt");
await File.WriteAllTextAsync(file, "Package integration smoke test\nSecond line");
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
builder.Services.AddRemoteFrame(options => options.Files.Add("test", file));
await using var app = builder.Build();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (context.Request.Headers.TryGetValue("X-Test-User", out var user))
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            user == "missing-id" ? [] : new[] { new Claim(ClaimTypes.NameIdentifier, user.ToString()) }, "Test"));
    await next(context);
});
app.UseAuthorization();
app.UseWebSockets();
app.MapRemoteFrame("/renderer");
try
{
    await app.StartAsync();
    var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var client = new HttpClient { BaseAddress = new Uri(url) };
    Check((await client.GetAsync("/renderer/api/files")).StatusCode == HttpStatusCode.Unauthorized, "Anonymous access denied");
    client.DefaultRequestHeaders.Add("X-Test-User", "missing-id");
    Check((await client.GetAsync("/renderer/api/files")).StatusCode == HttpStatusCode.Forbidden, "Missing owner claim denied");
    SetUser("alice");
    Check((await client.GetFromJsonAsync<string[]>("/renderer/api/files"))!.SequenceEqual(["test"]), "Prefixed file listing");
    var response = await client.PostAsJsonAsync("/renderer/api/sessions", new { fileId = "test" });
    response.EnsureSuccessStatusCode();
    var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
    SetUser("bob");
    (await client.DeleteAsync($"/renderer/api/sessions/{id}")).EnsureSuccessStatusCode();
    using var socket = new ClientWebSocket();
    socket.Options.SetRequestHeader("Origin", url);
    socket.Options.SetRequestHeader("X-Test-User", "alice");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    await socket.ConnectAsync(new Uri(url.Replace("http:", "ws:") + $"/renderer/ws/{id}"), timeout.Token);
    var bytes = new byte[1024 * 1024];
    var frame = await socket.ReceiveAsync(bytes.AsMemory(), timeout.Token);
    Check(frame.MessageType == WebSocketMessageType.Binary && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        "PNG stream and cross-user delete isolation");
    SetUser("alice");
    (await client.DeleteAsync($"/renderer/api/sessions/{id}")).EnsureSuccessStatusCode();
    socket.Abort();
    Console.WriteLine("Package integration checks passed.");
    void SetUser(string user)
    {
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Add("X-Test-User", user);
    }
}
finally
{
    await app.StopAsync();
    File.Delete(file);
}

static void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine("PASS: " + name);
}
