using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteFrame.Core;
using RemoteFrame.Rendering;

namespace RemoteFrame.AspNetCore;

public sealed class RemoteFrameOptions
{
    public bool RequireAuthorization { get; set; } = true;
    public bool EnableOffice { get; set; }
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
}

public static class RemoteFrameExtensions
{
    public static IServiceCollection AddRemoteFrame(this IServiceCollection services, Action<RemoteFrameOptions> configure)
    {
        services.AddOptions<RemoteFrameOptions>().Configure(configure);
        services.TryAddSingleton<SessionRegistry>();
        services.AddHostedService(sp => sp.GetRequiredService<SessionRegistry>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRenderProvider, TextRenderProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRenderProvider, OfficeRenderProvider>());
        return services;
    }

    public static RouteGroupBuilder MapRemoteFrame(this IEndpointRouteBuilder endpoints, string prefix = "")
    {
        var routes = endpoints.MapGroup(prefix);
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<RemoteFrameOptions>>().Value;
        if (options.RequireAuthorization)
            routes.RequireAuthorization(policy => policy.RequireAuthenticatedUser()
                .RequireAssertion(context => !string.IsNullOrWhiteSpace(context.User.FindFirstValue(ClaimTypes.NameIdentifier))));
        else routes.AllowAnonymous();
        var fileMap = options.Files;
        var logger = endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("RemoteFrame");
        routes.MapGet("/api/files", () => Results.Ok(fileMap.Keys.OrderBy(x => x)));
        routes.MapPost("/api/sessions", async (HttpContext ctx, OpenRequest request,
            SessionRegistry sessions, IEnumerable<IRenderProvider> providers) =>
        {
            if (!SameOrigin(ctx)) return Results.StatusCode(403);
            if (request.FileId is null || !fileMap.TryGetValue(request.FileId, out var filePath)) return Results.NotFound();
            var provider = providers.FirstOrDefault(x => x.CanOpen(filePath));
            if (provider is null) return Results.BadRequest(new { error = "No provider supports this extension." });
            if (!File.Exists(filePath)) return Results.NotFound();
            IRenderSession renderer;
            try { renderer = await provider.OpenAsync(filePath, ctx.RequestAborted); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or DecoderFallbackException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
            { logger.LogWarning(ex, "Cannot open render session"); return Results.BadRequest(new { error = "File cannot be opened. Check the bundled Office runtime and server logs." }); }
            try
            {
                var id = sessions.Create(Owner(ctx), renderer);
                return Results.Ok(new { id });
            }
            catch (InvalidOperationException)
            {
                await renderer.DisposeAsync();
                return Results.StatusCode(429);
            }
        });

        routes.MapDelete("/api/sessions/{id}", async (HttpContext ctx, string id, SessionRegistry sessions) =>
        {
            if (!SameOrigin(ctx)) return Results.StatusCode(403);
            await sessions.RemoveAsync(id, Owner(ctx));
            return Results.NoContent();
        });

        routes.Map("/ws/{id}", async (HttpContext ctx, string id, SessionRegistry sessions) =>
        {
            if (!SameOrigin(ctx)) { ctx.Response.StatusCode = 403; return; }
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            var render = sessions.Attach(id, Owner(ctx));
            if (render is null) { ctx.Response.StatusCode = 404; return; }
            WebSocket accepted;
            try { accepted = await ctx.WebSockets.AcceptWebSocketAsync(); }
            catch { await sessions.RemoveAsync(id, Owner(ctx)); throw; }
            using var socket = accepted;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            cts.CancelAfter(TimeSpan.FromMinutes(10)); // bounded connection lifetime
            var ct = cts.Token;
            try
            {
                var streaming = StreamFrames();
                try
                {
                    var buffer = new byte[2048];
                    while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        var msg = await socket.ReceiveAsync(buffer.AsMemory(), ct);
                        if (msg.MessageType == WebSocketMessageType.Close) break;
                        if (msg.MessageType != WebSocketMessageType.Text || !msg.EndOfMessage)
                        { await socket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "Invalid input", CancellationToken.None); break; }
                        InputEvent? input;
                        try
                        {
                            input = JsonSerializer.Deserialize<InputEvent>(buffer.AsSpan(0, msg.Count),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                        }
                        catch (JsonException) { continue; }
                        if (input is null) continue;
                        await render.ApplyAsync(input, ct);
                    }
                }
                finally { cts.Cancel(); try { await streaming; } catch (OperationCanceledException) { } }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch (IOException ex) { logger.LogWarning(ex, "Render worker disconnected"); }
            finally { await sessions.RemoveAsync(id, Owner(ctx)); }
            async Task StreamFrames()
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var frame = await render.CaptureAsync(ct);
                        await socket.SendAsync(frame.Png.AsMemory(), WebSocketMessageType.Binary, true, ct);
                        await Task.Delay(200, ct);
                    }
                }
                finally { cts.Cancel(); }
            }
        });

        return routes;
    }
    private static string Owner(HttpContext ctx) => ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "local-demo";
    private static bool SameOrigin(HttpContext ctx)
    {
        if (!ctx.Request.Headers.TryGetValue("Origin", out var header)) return !ctx.WebSockets.IsWebSocketRequest;
        if (!Uri.TryCreate(header.ToString(), UriKind.Absolute, out var origin)) return false;
        return string.Equals(origin.Host, ctx.Request.Host.Host, StringComparison.OrdinalIgnoreCase)
            && origin.Port == (ctx.Request.Host.Port ?? (ctx.Request.IsHttps ? 443 : 80))
            && string.Equals(origin.Scheme, ctx.Request.Scheme, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record OpenRequest(string? FileId);
}
