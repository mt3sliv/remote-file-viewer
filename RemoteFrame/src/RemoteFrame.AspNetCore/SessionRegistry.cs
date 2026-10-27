using Microsoft.Extensions.Hosting;
using RemoteFrame.Core;

namespace RemoteFrame.AspNetCore;

internal sealed class SessionRegistry : BackgroundService
{
    private sealed record Item(string Owner, SerializedSession Renderer, DateTime Created);
    private readonly Dictionary<string, Item> sessions = new();
    private readonly HashSet<string> attached = new();
    private readonly object gate = new();
    public int MaxPerUser { get; init; } = 3;
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(30);

    public string Create(string owner, IRenderSession renderer)
    {
        lock (gate)
        {
            if (sessions.Values.Count(x => x.Owner == owner) >= MaxPerUser)
                throw new InvalidOperationException("Session quota exceeded.");
            var id = Guid.NewGuid().ToString("N");
            sessions.Add(id, new Item(owner, new SerializedSession(renderer), DateTime.UtcNow));
            return id;
        }
    }

    public IRenderSession? Attach(string id, string owner)
    {
        lock (gate)
        {
            if (!sessions.TryGetValue(id, out var item) || item.Owner != owner ||
                DateTime.UtcNow - item.Created > Lifetime || !attached.Add(id)) return null;
            return item.Renderer;
        }
    }

    public async Task RemoveAsync(string id, string owner)
    {
        Item? removed;
        lock (gate)
        {
            if (!sessions.TryGetValue(id, out removed) || removed.Owner != owner) return;
            sessions.Remove(id);
            attached.Remove(id);
        }
        await removed.Renderer.DisposeAsync();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                KeyValuePair<string, Item>[] expired;
                lock (gate) expired = sessions.Where(x => DateTime.UtcNow - x.Value.Created > Lifetime).ToArray();
                foreach (var item in expired) await RemoveAsync(item.Key, item.Value.Owner);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            KeyValuePair<string, Item>[] remaining;
            lock (gate) remaining = sessions.ToArray();
            foreach (var item in remaining) await RemoveAsync(item.Key, item.Value.Owner);
        }
    }

    private sealed class SerializedSession(IRenderSession renderer) : IRenderSession
    {
        private readonly SemaphoreSlim operation = new(1);
        private bool disposed;
        public async ValueTask<Frame> CaptureAsync(CancellationToken ct)
        {
            await operation.WaitAsync(ct);
            try
            {
                if (disposed) throw new IOException("Render session is closed.");
                return await renderer.CaptureAsync(ct);
            }
            finally { operation.Release(); }
        }
        public async ValueTask ApplyAsync(InputEvent input, CancellationToken ct)
        {
            await operation.WaitAsync(ct);
            try
            {
                if (disposed) throw new IOException("Render session is closed.");
                await renderer.ApplyAsync(input, ct);
            }
            finally { operation.Release(); }
        }
        public async ValueTask DisposeAsync()
        {
            await operation.WaitAsync();
            try
            {
                if (disposed) return;
                disposed = true;
                await renderer.DisposeAsync();
            }
            finally { operation.Release(); }
        }
    }
}
