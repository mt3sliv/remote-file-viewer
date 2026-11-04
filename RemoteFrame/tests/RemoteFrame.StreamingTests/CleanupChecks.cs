using Microsoft.Extensions.Logging;
using RemoteFrame.AspNetCore;
using RemoteFrame.Core;

internal static class CleanupChecks
{
    internal static async Task RunAsync()
    {
        var logger = new TestLogger();
        using var registry = new SessionRegistry(logger) { Lifetime = TimeSpan.FromSeconds(-1) };
        await registry.StartAsync(default);
        var failed = new Session(true);
        var healthy = new Session(false);
        registry.Create("user", failed, new());
        registry.Create("user", healthy, new());
        await registry.RemoveExpiredAsync();
        Check(failed.Disposals == 1 && healthy.Disposals == 1, "Expiry continues after failed session disposal");
        Check(logger.Errors == 1, "Failed session cleanup is logged");
        await registry.RemoveExpiredAsync();
        Check(failed.Disposals == 1, "Failed session is removed without repeated disposal");
        Check(registry.ExecuteTask is { IsCompleted: false }, "Cleanup failure does not stop background service");
        var shutdownFailure = new Session(true);
        var shutdownHealthy = new Session(false);
        registry.Create("user", shutdownFailure, new());
        registry.Create("user", shutdownHealthy, new());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await registry.StopAsync(deadline.Token);
        Check(shutdownFailure.Disposals == 1 && shutdownHealthy.Disposals == 1 && logger.Errors == 2,
            "Shutdown cleans every remaining session despite failures");
    }

    private static void Check(bool result, string message)
    {
        if (!result) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
    private sealed class TestLogger : ILogger<SessionRegistry>
    {
        internal int Errors;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (level == LogLevel.Error) Errors++; }
    }
    private sealed class Session(bool fail) : IRenderSession
    {
        internal int Disposals;
        public ValueTask<Frame> CaptureAsync(CancellationToken ct) => throw new NotSupportedException();
        public ValueTask ApplyAsync(InputEvent input, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync()
        {
            Disposals++;
            return fail ? ValueTask.FromException(new IOException("Simulated save-back failure")) : ValueTask.CompletedTask;
        }
    }
}
