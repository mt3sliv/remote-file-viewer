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

    public string Create(string owner, IRenderSession renderer, FrameConfiguration configuration)
    {
        lock (gate)
        {
            if (sessions.Values.Count(x => x.Owner == owner) >= MaxPerUser)
                throw new InvalidOperationException("Session quota exceeded.");
            var id = Guid.NewGuid().ToString("N");
            sessions.Add(id, new Item(owner, new SerializedSession(renderer, configuration), DateTime.UtcNow));
            return id;
        }
    }

    public SerializedSession? Attach(string id, string owner)
    {
        lock (gate)
        {
            if (!sessions.TryGetValue(id, out var item) || item.Owner != owner ||
                DateTime.UtcNow - item.Created > Lifetime || !attached.Add(id)) return null;
            return item.Renderer;
        }
    }

    public SerializedSession? Find(string id, string owner)
    {
        lock (gate) return sessions.TryGetValue(id, out var item) && item.Owner == owner &&
            DateTime.UtcNow - item.Created <= Lifetime ? item.Renderer : null;
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

    internal sealed class SerializedSession(IRenderSession renderer, FrameConfiguration initialConfiguration) : IRenderSession
    {
        public bool CanSave => renderer.CanSave;
        private readonly SemaphoreSlim operation = new(1);
        private bool disposed;
        private FrameConfiguration configuration = initialConfiguration;
        public FrameConfiguration Configuration => Volatile.Read(ref configuration);
        private FrameConfiguration displayed = initialConfiguration;
        public async ValueTask ConfigureAsync(FrameConfiguration value, CancellationToken ct)
        {
            await operation.WaitAsync(ct);
            try
            {
                if (disposed) throw new IOException("Render session is closed.");
                if (value == configuration) return;
                // FPS is enforced by the stream loop and does not change the renderer viewport.
                if (value with { FramesPerSecond = configuration.FramesPerSecond } != configuration)
                    await renderer.ConfigureAsync(value, ct);
                Volatile.Write(ref configuration, value);
            }
            finally { operation.Release(); }
        }
        public async ValueTask<Frame> CaptureAsync(CancellationToken ct)
        {
            await operation.WaitAsync(ct);
            try
            {
                if (disposed) throw new IOException("Render session is closed.");
                var frame = await renderer.CaptureAsync(ct);
                displayed = configuration;
                return frame;
            }
            finally { operation.Release(); }
        }
        public async ValueTask ApplyAsync(InputEvent input, CancellationToken ct)
        {
            await operation.WaitAsync(ct);
            try
            {
                if (disposed) throw new IOException("Render session is closed.");
                if (input.Kind is "pointerdown" or "pointerup" or "pointermove" or "wheel")
                {
                    // Browser dimensions identify its displayed frame even while a resize is in flight.
                    int width = input.FrameWidth is >= 160 and <= 3840 ? input.FrameWidth : displayed.StreamWidth;
                    int height = input.FrameHeight is >= 100 and <= 2160 ? input.FrameHeight : displayed.StreamHeight;
                    input = input with {
                        X = (int)((long)Math.Clamp(input.X, 0, width - 1) * configuration.CaptureWidth / width),
                        Y = (int)((long)Math.Clamp(input.Y, 0, height - 1) * configuration.CaptureHeight / height)
                    };
                }
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
