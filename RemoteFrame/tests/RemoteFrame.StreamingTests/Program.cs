using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using RemoteFrame.AspNetCore;
using RemoteFrame.Core;
using RemoteFrame.Rendering;

static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS: " + message); }
static RemoteFrameOptions BoundSettings(string json, Action<RemoteFrameOptions>? configure = null)
{
    using var data = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
    var configuration = new ConfigurationBuilder().AddJsonStream(data).Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddRemoteFrame(configure);
    using var provider = services.BuildServiceProvider();
    return provider.GetRequiredService<IOptions<RemoteFrameOptions>>().Value;
}
Check(BoundSettings("{}").DefaultFrameConfiguration == new FrameConfiguration(), "Missing appsettings section uses built-in frame defaults");
Check(BoundSettings("{\"RemoteFrame\":{\"DefaultFrameConfiguration\":{\"FramesPerSecond\":14}}}").DefaultFrameConfiguration == new FrameConfiguration(FramesPerSecond:14), "Partial appsettings overrides only supplied defaults");
var fromConfig = BoundSettings("{\"RemoteFrame\":{\"FitStreamToViewport\":true,\"DefaultFrameConfiguration\":{\"FramesPerSecond\":18,\"CaptureWidth\":960,\"CaptureHeight\":600,\"StreamWidth\":480,\"StreamHeight\":300}}}");
Check(fromConfig.FitStreamToViewport && fromConfig.DefaultFrameConfiguration == new FrameConfiguration(18,960,600,480,300), "Appsettings controls FPS, capture, stream size and fitting");
Check(BoundSettings("{\"RemoteFrame\":{\"DefaultFrameConfiguration\":{\"FramesPerSecond\":14}}}", o => o.DefaultFrameConfiguration = new(FramesPerSecond:20)).DefaultFrameConfiguration.FramesPerSecond == 20, "Explicit host configuration overrides appsettings");
static void Reject(FrameConfiguration value) {
    try { value.Validate(); } catch (ArgumentException) { return; }
    throw new Exception("Invalid settings accepted");
}
foreach (var value in new[] {new FrameConfiguration(0), new FrameConfiguration(61), new FrameConfiguration(CaptureWidth: int.MaxValue),
    new FrameConfiguration(StreamWidth: 1281), new FrameConfiguration(StreamHeight: -1), new FrameConfiguration(CaptureHeight: 199)}) Reject(value);
Check(true, "Invalid FPS, dimensions, overflow and upscaling rejected");
Check(FramePacing.RemainingDelay(10, TimeSpan.FromMilliseconds(40)) == TimeSpan.FromMilliseconds(60), "Pacing subtracts capture/send time");
Check(FramePacing.RemainingDelay(10, TimeSpan.FromMilliseconds(150)) == TimeSpan.Zero, "Slow capture does not accumulate frame debt");
var config = new FrameConfiguration(10, 640, 400, 320, 200);
var file = Path.GetTempFileName(); var textFile = Path.ChangeExtension(file, ".txt");
File.WriteAllText(textFile, "RemoteFrame test\nSecond line\nThird line");
try {
    var provider = new TextRenderProvider();
    await using (var session = await provider.OpenAsync(textFile, default)) {
        await session.ConfigureAsync(config, default);
        var frame = await session.CaptureAsync(default);
        Check(ReferenceEquals(frame, await session.CaptureAsync(default)), "Unchanged text reuses encoded frame");
        await session.ConfigureAsync(config with { FramesPerSecond = 20 }, default);
        Check(ReferenceEquals(frame, await session.CaptureAsync(default)), "FPS-only text update preserves cached frame");
        await session.ApplyAsync(new InputEvent("key", Key: "ArrowDown"), default);
        var scrolled = await session.CaptureAsync(default);
        Check(!frame.Png.SequenceEqual(scrolled.Png), "Text scroll invalidates cached frame");
        Check(frame.Width == 320 && frame.Height == 200 && BinaryPrimitives.ReadInt32BigEndian(frame.Png.AsSpan(16,4)) == 320,
            "Text capture produces resized PNG");
        await session.ConfigureAsync(config with { CaptureWidth = 800, StreamWidth = 400 }, default);
        Check((await session.CaptureAsync(default)).Width == 400, "Text capture resizes dynamically");
    }
    byte[] pixels = [0,0,255,255, 0,255,0,255, 255,0,0,255, 255,255,255,255];
    var png = PngFrameEncoder.EncodeBgra(pixels, 2, 2, 1, 1);
    for (int offset = 8; offset < png.Length;) {
        int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
        uint crc = uint.MaxValue;
        foreach (byte b in png.AsSpan(offset + 4, length + 4)) {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u);
        }
        Check(~crc == BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length, 4)), "PNG chunk checksum is valid");
        offset += length + 12;
    }
    Check(BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16,4)) == 1, "BGRA downsampling produces requested PNG size");
    using (var compressed = new MemoryStream(png,41,BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(33,4))))
    using (var zlib = new ZLibStream(compressed,CompressionMode.Decompress)) {
        var decoded = new MemoryStream(); zlib.CopyTo(decoded);
        Check(decoded.ToArray().SequenceEqual(new byte[]{0,255,0,0}), "Downsampled BGRA pixels encode correct RGB data");
    }
    var fake = new TestProvider();
    var builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning);
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Services.AddSingleton<IRenderProvider>(fake);
    builder.Services.AddRemoteFrame(o => { o.RequireAuthorization = false; o.EnableViewer = false; o.DefaultFrameConfiguration = config; o.MaxFramesPerSecond = 20; o.MaxPixels = 1000000; o.Files.Add("text",textFile); o.Files.Add("fake", file); });
    await using var app = builder.Build();
    app.Use(async (ctx,next) => { ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, ctx.Request.Headers["X-Owner"].FirstOrDefault() ?? "alice")])); await next(ctx); });
    app.UseWebSockets(); app.MapRemoteFrame(); await app.StartAsync();
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var http = new HttpClient { BaseAddress = new Uri(address) };
    Check((await http.GetAsync("/viewer")).StatusCode == HttpStatusCode.NotFound, "Embedded viewer can be disabled without changing API routes");
    var unknown = await http.PostAsJsonAsync("/api/sessions", new {fileId=textFile});
    Check(unknown.StatusCode == HttpStatusCode.NotFound, "Client paths cannot bypass file allowlist");
    var invalid = await http.PostAsJsonAsync("/api/sessions", new {fileId="fake", configuration=config with { FramesPerSecond=21 }});
    Check(invalid.StatusCode == HttpStatusCode.BadRequest && fake.OpenCount == 0, "Limits enforced before opening renderer");
    http.DefaultRequestHeaders.Add("Origin", "https://other.example");
    Check((await http.PostAsJsonAsync("/api/sessions",new {fileId="fake"})).StatusCode == HttpStatusCode.Forbidden, "Cross-origin session creation rejected");
    http.DefaultRequestHeaders.Remove("Origin");
    var created = await http.PostAsJsonAsync("/api/sessions", new {fileId="fake"});
    created.EnsureSuccessStatusCode();
    var data = await created.Content.ReadFromJsonAsync<Opened>(); var id = data!.Id;
    Check(data.Configuration == config, "Server defaults negotiated in session response");
    Check(data.CanSave, "Save capability comes from the renderer, not an opaque file ID extension");
    (await http.PutAsJsonAsync($"/api/sessions/{id}/configuration", config)).EnsureSuccessStatusCode();
    (await http.PutAsJsonAsync($"/api/sessions/{id}/configuration", config with { FramesPerSecond = 20 })).EnsureSuccessStatusCode();
    Check(fake.ConfigureCount == 1, "Identical and FPS-only settings do not reconfigure renderer");
    (await http.PutAsJsonAsync($"/api/sessions/{id}/configuration", config)).EnsureSuccessStatusCode();
    http.DefaultRequestHeaders.Add("X-Owner", "bob");
    Check((await http.PutAsJsonAsync($"/api/sessions/{id}/configuration", config)).StatusCode == HttpStatusCode.NotFound, "Other owners cannot resize session");
    http.DefaultRequestHeaders.Remove("X-Owner");
    using var ws = new ClientWebSocket(); ws.Options.SetRequestHeader("Origin", address);
    await ws.ConnectAsync(new Uri(address.Replace("http:", "ws:") + "/ws/" + id), default);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    async Task<byte[]> Frame() {
        var result = new MemoryStream(); var buffer = new byte[65536]; WebSocketReceiveResult part;
        do { part = await ws.ReceiveAsync(buffer,deadline.Token); result.Write(buffer,0,part.Count); } while (!part.EndOfMessage);
        return result.ToArray();
    }
    await Frame(); var watch = Stopwatch.StartNew(); await Frame(); await Frame();
    Check(watch.ElapsedMilliseconds >= 150 && watch.ElapsedMilliseconds < 800, "Pacing includes capture work without catch-up bursts");
    var changed = config with { CaptureWidth=800, CaptureHeight=600, StreamWidth=400, StreamHeight=300, FramesPerSecond=20 };
    (await http.PutAsJsonAsync($"/api/sessions/{id}/configuration",changed)).EnsureSuccessStatusCode();
    byte[] next; do { next = await Frame(); } while (BinaryPrimitives.ReadInt32BigEndian(next.AsSpan(16,4)) != 400);
    Check(BinaryPrimitives.ReadInt32BigEndian(next.AsSpan(20,4)) == 300, "Live update changes streamed PNG dimensions");
    await ws.SendAsync(System.Text.Encoding.UTF8.GetBytes("{\"kind\":\"pointerdown\",\"x\":200,\"y\":150}"),WebSocketMessageType.Text,true,deadline.Token);
    var input = await fake.Input.Task.WaitAsync(deadline.Token);
    Check(input.X == 400 && input.Y == 300, "Stream coordinates map to capture coordinates");
    Check((await http.PutAsJsonAsync($"/api/sessions/{id}/configuration",changed with { CaptureWidth=3840, CaptureHeight=2160 })).StatusCode == HttpStatusCode.BadRequest, "Pixel budget enforced during live updates");
    await ws.SendAsync(System.Text.Encoding.UTF8.GetBytes("{\"kind\":\"configure\"}"),WebSocketMessageType.Text,true,deadline.Token);
    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Done", deadline.Token);
    await http.DeleteAsync($"/api/sessions/{id}");
    await app.StopAsync();
} finally { File.Delete(file); File.Delete(textFile); }
Console.WriteLine("All streaming checks passed.");
record Opened(string Id, FrameConfiguration Configuration, bool CanSave);
sealed class TestProvider : IRenderProvider {
    public int OpenCount;
    public int ConfigureCount;
    public TaskCompletionSource<InputEvent> Input = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool CanOpen(string path) => !path.EndsWith(".txt");
    public ValueTask<IRenderSession> OpenAsync(string path,CancellationToken ct) { OpenCount++; return ValueTask.FromResult<IRenderSession>(new Session(this)); }
    sealed class Session(TestProvider parent) : IRenderSession {
        public bool CanSave => true;
        FrameConfiguration config = new();
        public ValueTask ConfigureAsync(FrameConfiguration value,CancellationToken ct) { parent.ConfigureCount++; config=value; return ValueTask.CompletedTask; }
        public async ValueTask<Frame> CaptureAsync(CancellationToken ct) { await Task.Delay(40,ct); return new(PngFrameEncoder.EncodeBgra(new byte[config.CaptureWidth*config.CaptureHeight*4],config.CaptureWidth,config.CaptureHeight,config.StreamWidth,config.StreamHeight),config.StreamWidth,config.StreamHeight); }
        public ValueTask ApplyAsync(InputEvent input,CancellationToken ct) { parent.Input.TrySetResult(input); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
