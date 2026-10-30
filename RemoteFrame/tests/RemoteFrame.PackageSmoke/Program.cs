using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using RemoteFrame.AspNetCore;

// Exercises the packed NuGet artifacts, without referencing library source projects.
var file = Path.Combine(Path.GetTempPath(), "remoteframe-test-" + Guid.NewGuid().ToString("N") + ".txt");
await File.WriteAllTextAsync(file, "Package integration smoke test\nSecond line");
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery();
var resolverCalls = 0;
builder.Services.AddRemoteFrame(options =>
{
    foreach (var id in new[] { "test", "private", "denied" }) options.Files.Add(id, file);
    options.AuthorizeFileAsync = (context, id, ct) => ValueTask.FromResult(id != "denied" &&
        (id != "private" || context.User.FindFirstValue(ClaimTypes.NameIdentifier) == "alice"));
    options.ResolveFileAsync = (context, id, ct) =>
    {
        Interlocked.Increment(ref resolverCalls);
        return ValueTask.FromResult<string?>(id is "test" or "private" ||
            (id == "dynamic" && context.User.FindFirstValue(ClaimTypes.NameIdentifier) == "alice") ? file : null);
    };
});
await using var app = builder.Build();
app.UsePathBase("/tenant");
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
    Check((await client.GetAsync("/tenant/renderer/viewer")).StatusCode == HttpStatusCode.Unauthorized, "Embedded page requires authentication");
    Check((await client.GetAsync("/tenant/renderer/viewer.js")).StatusCode == HttpStatusCode.Unauthorized, "Embedded assets require authentication");
    Check((await client.GetAsync("/tenant/renderer/api/files")).StatusCode == HttpStatusCode.Unauthorized, "Anonymous access denied");
    client.DefaultRequestHeaders.Add("X-Test-User", "missing-id");
    Check((await client.GetAsync("/tenant/renderer/api/files")).StatusCode == HttpStatusCode.Forbidden, "Missing owner claim denied");
    SetUser("alice");
    var page = await client.GetAsync("/tenant/renderer/viewer?fileId=%3Cscript%3E");
    page.EnsureSuccessStatusCode();
    var html = await page.Content.ReadAsStringAsync();
    Check(html.Contains("/tenant/renderer/viewer.js") && html.Contains("/tenant/renderer/viewer.css") &&
        html.Contains("data-remote-frame-base=\"/tenant/renderer\"") && !html.Contains("{{") && !html.Contains(file), "Embedded page handles prefix and PathBase without exposing paths");
    Check(!html.Contains("<script>") && !html.Contains("data-csrf-token=\"\"") && resolverCalls == 0, "Page GET does not open or resolve files; antiforgery token is supplied");
    Check(!html.Contains("<select") && html.Contains("<details id=\"frameSettings\">") && !html.Contains("<details id=\"frameSettings\" open"), "Viewer has no file dropdown and settings are collapsed by default");
    Check(page.Headers.CacheControl?.NoStore == true && page.Headers.Contains("Content-Security-Policy"), "Viewer shell is not cached and has a content security policy");
    Check((await client.GetStringAsync("/tenant/renderer/viewer.js")).Contains("latestFrameReceiver"), "Viewer JavaScript embedded in the packed assembly");
    Check((await client.GetAsync("/tenant/renderer/viewer.css")).Content.Headers.ContentType?.MediaType == "text/css", "Viewer stylesheet served without static-file middleware");
    Check((await client.GetFromJsonAsync<string[]>("/tenant/renderer/api/files"))!.SequenceEqual(["private", "test"]), "Authorized configured listing");
    var calls = resolverCalls;
    Check((await client.PostAsJsonAsync("/tenant/renderer/api/sessions", new { fileId = "denied" })).StatusCode == HttpStatusCode.NotFound && resolverCalls == calls, "File authorization is enforced before resolution");
    SetUser("bob");
    Check((await client.GetFromJsonAsync<string[]>("/tenant/renderer/api/files"))!.SequenceEqual(["test"]), "Other users cannot enumerate private file IDs");
    Check((await client.PostAsJsonAsync("/tenant/renderer/api/sessions", new { fileId = "private" })).StatusCode == HttpStatusCode.NotFound, "Tampered private file ID cannot be opened");
    Check((await client.PostAsJsonAsync("/tenant/renderer/api/sessions", new { fileId = "dynamic" })).StatusCode == HttpStatusCode.NotFound, "Dynamic resolver enforces per-user access");
    SetUser("alice");
    var dynamicResponse = await client.PostAsJsonAsync("/tenant/renderer/api/sessions", new { fileId = "dynamic" });
    dynamicResponse.EnsureSuccessStatusCode();
    var dynamicData = await dynamicResponse.Content.ReadFromJsonAsync<JsonElement>();
    Check(!dynamicData.GetProperty("canSave").GetBoolean(), "Opaque text ID reports server-side save capability");
    (await client.DeleteAsync("/tenant/renderer/api/sessions/" + dynamicData.GetProperty("id").GetString())).EnsureSuccessStatusCode();
    var response = await client.PostAsJsonAsync("/tenant/renderer/api/sessions", new { fileId = "test", configuration = new { framesPerSecond = 10, captureWidth = 640, captureHeight = 400, streamWidth = 320, streamHeight = 200 } });
    response.EnsureSuccessStatusCode();
    var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
    SetUser("bob");
    (await client.DeleteAsync($"/tenant/renderer/api/sessions/{id}")).EnsureSuccessStatusCode();
    using var socket = new ClientWebSocket();
    socket.Options.SetRequestHeader("Origin", url);
    socket.Options.SetRequestHeader("X-Test-User", "alice");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    await socket.ConnectAsync(new Uri(url.Replace("http:", "ws:") + $"/tenant/renderer/ws/{id}"), timeout.Token);
    var bytes = new byte[1024 * 1024];
    var frame = await socket.ReceiveAsync(bytes.AsMemory(), timeout.Token);
    Check(frame.MessageType == WebSocketMessageType.Binary && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        "PNG stream and cross-user delete isolation");
    Check(System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)) == 320,
        "Packed middleware applies stream configuration");
    SetUser("alice");
    (await client.PutAsJsonAsync($"/tenant/renderer/api/sessions/{id}/configuration", new {
        framesPerSecond = 5, captureWidth = 640, captureHeight = 400, streamWidth = 640, streamHeight = 400
    })).EnsureSuccessStatusCode();
    Check(true, "Packed middleware accepts dynamic configuration");
    (await client.DeleteAsync($"/tenant/renderer/api/sessions/{id}")).EnsureSuccessStatusCode();
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
