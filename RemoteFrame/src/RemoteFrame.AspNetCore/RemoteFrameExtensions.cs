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
    public bool EnableViewer { get; set; } = true;
    public bool FitStreamToViewport { get; set; }
    public bool ShowSaveButton { get; set; }
    public bool PreferInstalledOfficeApplications { get; set; } = true;
    /// <summary>Optional per-request authorization for opaque file IDs. Also filters the configured file list.</summary>
    public Func<HttpContext, string, CancellationToken, ValueTask<bool>>? AuthorizeFileAsync { get; set; }
    /// <summary>Optional server-side resolver for a host-managed catalog. Return null for unknown or inaccessible IDs.</summary>
    public Func<HttpContext, string, CancellationToken, ValueTask<string?>>? ResolveFileAsync { get; set; }
    public FrameConfiguration DefaultFrameConfiguration { get; set; } = new();
    public int MaxFramesPerSecond { get; set; } = 30;
    public int MaxCaptureWidth { get; set; } = 3840;
    public int MaxCaptureHeight { get; set; } = 2160;
    public long MaxPixels { get; set; } = 8294400;
    public void Validate(FrameConfiguration value) => value.Validate(MaxFramesPerSecond, MaxCaptureWidth, MaxCaptureHeight, MaxPixels);
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
}

public static class RemoteFrameExtensions
{
    public static IServiceCollection AddRemoteFrame(this IServiceCollection services, Action<RemoteFrameOptions>? configure = null)
    {
        services.AddOptions<RemoteFrameOptions>().BindConfiguration("RemoteFrame").Configure(configure ?? (_ => { })).Validate(o =>
        {
            try { o.Validate(o.DefaultFrameConfiguration); return true; }
            catch (ArgumentException) { return false; }
        }, "Invalid RemoteFrame defaults or limits.").ValidateOnStart();
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
        if (options.EnableViewer) RemoteFrameViewer.Map(routes);
        var fileMap = options.Files;
        var logger = endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("RemoteFrame");
        routes.MapGet("/api/frame-settings", () => Results.Ok(new { defaults = options.DefaultFrameConfiguration, fitStreamToViewport = options.FitStreamToViewport, showSaveButton = options.ShowSaveButton,
            maxFramesPerSecond = options.MaxFramesPerSecond, maxCaptureWidth = options.MaxCaptureWidth,
            maxCaptureHeight = options.MaxCaptureHeight, maxPixels = options.MaxPixels }));
        routes.MapGet("/api/files", async (HttpContext ctx) =>
        {
            var allowed = new List<string>();
            foreach (var id in fileMap.Keys.OrderBy(x => x))
                if ((options.AuthorizeFileAsync is null || await options.AuthorizeFileAsync(ctx, id, ctx.RequestAborted)) &&
                    (options.ResolveFileAsync is null || await options.ResolveFileAsync(ctx, id, ctx.RequestAborted) is not null)) allowed.Add(id);
            return Results.Ok(allowed);
        });
        routes.MapPost("/api/sessions", async (HttpContext ctx, OpenRequest request,
            SessionRegistry sessions, IEnumerable<IRenderProvider> providers) =>
        {
            if (!SameOrigin(ctx)) return Results.StatusCode(403);
            if (string.IsNullOrWhiteSpace(request.FileId)) return Results.NotFound();
            if (options.AuthorizeFileAsync is not null && !await options.AuthorizeFileAsync(ctx, request.FileId, ctx.RequestAborted)) return Results.NotFound();
            var filePath = options.ResolveFileAsync is not null
                ? await options.ResolveFileAsync(ctx, request.FileId, ctx.RequestAborted)
                : fileMap.GetValueOrDefault(request.FileId);
            if (string.IsNullOrWhiteSpace(filePath)) return Results.NotFound();
            var provider = providers.FirstOrDefault(x => x.CanOpen(filePath));
            if (provider is null) return Results.BadRequest(new { error = "No provider supports this extension." });
            if (!File.Exists(filePath)) return Results.NotFound();
            var configuration = request.Configuration ?? options.DefaultFrameConfiguration;
            try { options.Validate(configuration); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            IRenderSession renderer;
            try { renderer = await provider.OpenAsync(filePath, ctx.RequestAborted); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or DecoderFallbackException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
            { logger.LogWarning(ex, "Cannot open render session"); return Results.BadRequest(new { error = "File cannot be opened. Check the Office worker, installed applications or optional LibreOffice runtime, and server logs." }); }
            try
            {
                await renderer.ConfigureAsync(configuration, ctx.RequestAborted);
                var id = sessions.Create(Owner(ctx), renderer, configuration);
                return Results.Ok(new { id, configuration, canSave = renderer.CanSave });
            }
            catch (NotSupportedException)
            {
                await renderer.DisposeAsync();
                return Results.BadRequest(new { error = "Provider does not support frame configuration." });
            }
            catch (IOException ex)
            {
                await renderer.DisposeAsync();
                logger.LogWarning(ex, "Cannot configure render session");
                return Results.BadRequest(new { error = "Renderer could not apply frame settings. Check the worker version and server logs." });
            }
            catch (OperationCanceledException)
            {
                await renderer.DisposeAsync();
                throw;
            }
            catch (InvalidOperationException)
            {
                await renderer.DisposeAsync();
                return Results.StatusCode(429);
            }
        });

        routes.MapPut("/api/sessions/{id}/configuration", async (HttpContext ctx, string id,
            FrameConfiguration configuration, SessionRegistry sessions) =>
        {
            if (!SameOrigin(ctx)) return Results.StatusCode(403);
            var render = sessions.Find(id, Owner(ctx));
            if (render is null) return Results.NotFound();
            try
            {
                options.Validate(configuration);
                await render.ConfigureAsync(configuration, ctx.RequestAborted);
                return Results.Ok(new { configuration = render.Configuration });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (NotSupportedException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (IOException) { return Results.Conflict(new { error = "Session is closed or could not be resized." }); }
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
                        if (input.Kind is "wheel" or "key" or "pointerdown" or "pointerup" or "pointermove" or "release" or "save")
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
                        var started = System.Diagnostics.Stopwatch.GetTimestamp();
                        var frame = await render.CaptureAsync(ct);
                        await socket.SendAsync(frame.Png.AsMemory(), WebSocketMessageType.Binary, true, ct);
                        var remaining = FramePacing.RemainingDelay(render.Configuration.FramesPerSecond,
                            System.Diagnostics.Stopwatch.GetElapsedTime(started));
                        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct);
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

    private sealed record OpenRequest(string? FileId, FrameConfiguration? Configuration);
}
