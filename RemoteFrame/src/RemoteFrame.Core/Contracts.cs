namespace RemoteFrame.Core;

public sealed record InputEvent(string Kind, int Delta = 0, string? Key = null,
    int X = 0, int Y = 0, int Button = 0, bool Ctrl = false, bool Alt = false, bool Shift = false);
public sealed record Frame(byte[] Png, int Width, int Height);
public interface IRenderSession : IAsyncDisposable
{
    ValueTask<Frame> CaptureAsync(CancellationToken cancellationToken);
    ValueTask ApplyAsync(InputEvent input, CancellationToken cancellationToken);
}
public interface IRenderProvider
{
    bool CanOpen(string path);
    ValueTask<IRenderSession> OpenAsync(string path, CancellationToken cancellationToken);
}
