using System.Diagnostics;
using System.Text;

namespace RemoteFrame.AspNetCore;

internal sealed class WorkerConnection : IDisposable
{
    internal Process Process { get; }
    internal StreamWriter Input { get; }
    internal StreamReader Output { get; }
    private readonly IDisposable? lifetime;
    private bool disposed;

    internal WorkerConnection(Process process)
    { Process = process; Input = process.StandardInput; Output = process.StandardOutput; }

    internal WorkerConnection(Process process, Stream pipe, IDisposable lifetime)
    {
        Process = process;
        Input = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true);
        Output = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        this.lifetime = new Resources(pipe, lifetime);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { lifetime?.Dispose(); }
        finally
        {
            try { Input.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            try { Output.Dispose(); } finally { Process.Dispose(); }
        }
    }

    private sealed class Resources(Stream pipe, IDisposable job) : IDisposable
    { public void Dispose() { try { pipe.Dispose(); } finally { job.Dispose(); } } }
}
