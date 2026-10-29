using RemoteFrame.Core;
using System.Text;

namespace RemoteFrame.Rendering;

public sealed class TextRenderProvider : IRenderProvider
{
    private const int MaxBytes = 512 * 1024;
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".log", ".json", ".csv", ".md", ".cs", ".xml", ".yaml", ".yml" };

    public bool CanOpen(string path) => Extensions.Contains(Path.GetExtension(path));

    public async ValueTask<IRenderSession> OpenAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaxBytes) throw new InvalidOperationException("File exceeds 512 KiB viewer limit.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        var value = await reader.ReadToEndAsync(ct);
        if (value.IndexOf('\0') >= 0) throw new InvalidOperationException("Binary files are unsupported.");
        return new TextSession(value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'));
    }

    private sealed class TextSession(string[] lines) : IRenderSession
    {
        private int _firstLine;
        private Frame? cachedFrame;
        private FrameConfiguration configuration = new(CaptureWidth: 960, CaptureHeight: 600, StreamWidth: 960, StreamHeight: 600);
        public async ValueTask ConfigureAsync(FrameConfiguration value, CancellationToken ct)
        {
            value.Validate();
            await _gate.WaitAsync(ct);
            try {
                if (value with { FramesPerSecond = configuration.FramesPerSecond } != configuration) cachedFrame = null;
                configuration = value;
            } finally { _gate.Release(); }
        }
        private readonly SemaphoreSlim _gate = new(1, 1);
        public async ValueTask ApplyAsync(InputEvent input, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                int step = input.Kind switch
                {
                    "wheel" => Math.Clamp(input.Delta, -10, 10) * 3,
                    "key" when input.Key == "ArrowDown" => 1,
                    "key" when input.Key == "ArrowUp" => -1,
                    "key" when input.Key == "PageDown" => 20,
                    "key" when input.Key == "PageUp" => -20,
                    "key" when input.Key == "Home" => -lines.Length,
                    "key" when input.Key == "End" => lines.Length,
                    _ => 0
                };
                var next = Math.Clamp(_firstLine + step, 0, Math.Max(0, lines.Length - 1));
                if (next != _firstLine) cachedFrame = null;
                _firstLine = next;
            }
            finally { _gate.Release(); }
        }
        public async ValueTask<Frame> CaptureAsync(CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try { return cachedFrame ??= new Frame(PngTextRasterizer.Render(lines, _firstLine, configuration.CaptureWidth, configuration.CaptureHeight, configuration.StreamWidth, configuration.StreamHeight), configuration.StreamWidth, configuration.StreamHeight); }
            finally { _gate.Release(); }
        }
        public ValueTask DisposeAsync() { _gate.Dispose(); return ValueTask.CompletedTask; }
    }
}
