using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RemoteFrame.Core;

namespace RemoteFrame.AspNetCore;

internal sealed class OfficeRenderProvider(IOptions<RemoteFrameOptions> options) : IRenderProvider
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".doc", ".docx", ".odt", ".xls", ".xlsx", ".xlsm", ".ods", ".ppt", ".pptx", ".odp" };
    public bool CanOpen(string path) => options.Value.EnableOffice && Extensions.Contains(Path.GetExtension(path));
    public async ValueTask<IRenderSession> OpenAsync(string path, CancellationToken ct)
    {
        var runtime = BundledOfficeRuntime.Load();
        var start = new ProcessStartInfo(runtime.WorkerPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true
        };
        start.ArgumentList.Add(runtime.EnginePath);
        start.ArgumentList.Add(Path.GetFullPath(path));
        if (!options.Value.PreferInstalledOfficeApplications) start.ArgumentList.Add("--bundled-only");
        var process = Process.Start(start) ?? throw new IOException("Cannot start the bundled Office worker.");
        var session = new WorkerSession(process);
        try { await session.CaptureAsync(ct); return session; }
        catch { await session.DisposeAsync(); throw; }
    }

    private sealed class WorkerSession(Process process) : IRenderSession
    {
        private readonly SemaphoreSlim gate = new(1);
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private bool disposed;
        private bool initialized;
        private async Task<JsonElement> Command(object command, CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            try
            {
                if (disposed) throw new IOException("Office session is closed.");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(initialized ? TimeSpan.FromSeconds(45) : TimeSpan.FromMinutes(5));
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command, Json).AsMemory(), timeout.Token);
                await process.StandardInput.FlushAsync(timeout.Token);
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null) throw new IOException("Office worker exited before replying.");
                var response = JsonSerializer.Deserialize<JsonElement>(line);
                initialized = true;
                if (response.TryGetProperty("error", out var error)) throw new IOException(error.GetString());
                return response;
            }
            finally { gate.Release(); }
        }
        public async ValueTask ConfigureAsync(FrameConfiguration configuration, CancellationToken ct)
        {
            configuration.Validate();
            var response = await Command(new { kind = "configure", configuration }, ct);
            if (!response.TryGetProperty("configuration", out var applied) || applied.Deserialize<FrameConfiguration>(Json) != configuration)
                throw new IOException("The bundled Office worker does not support the requested configuration. Rebuild the worker payload.");
        }
        public async ValueTask<Frame> CaptureAsync(CancellationToken ct)
        {
            var response = await Command(new { kind = "capture" }, ct);
            return new Frame(Convert.FromBase64String(response.GetProperty("png").GetString()!),
                response.GetProperty("width").GetInt32(), response.GetProperty("height").GetInt32());
        }
        public async ValueTask ApplyAsync(InputEvent input, CancellationToken ct) => await Command(input, ct);
        public async ValueTask DisposeAsync()
        {
            await gate.WaitAsync();
            try
            {
                if (disposed) return;
                disposed = true;
                process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                process.Dispose();
            }
            finally { gate.Release(); }
        }
    }
}
