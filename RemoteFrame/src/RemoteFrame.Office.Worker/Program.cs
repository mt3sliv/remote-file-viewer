using System.Text.Json;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using RemoteFrame.Core;
using RemoteFrame.Rendering;

namespace RemoteFrame.Office.Worker;

internal static class Program
{
    private static int Main(string[] args)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        NamedPipeClientStream? pipe = null;
        try
        {
            if (args.Length < 2) throw new ArgumentException("Expected engine path, document path and optional --bundled-only / --pipe <name>.");
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This worker build implements Windows private desktop sessions only.");
            var preferInstalled = true;
            string? pipeName = null;
            for (var index = 2; index < args.Length; index++)
            {
                if (args[index] == "--bundled-only" && preferInstalled) preferInstalled = false;
                else if (args[index] == "--pipe" && pipeName is null && index + 1 < args.Length) pipeName = args[++index];
                else throw new ArgumentException("Invalid worker option.");
            }
            if (pipeName is not null)
            {
                if (!pipeName.StartsWith("RemoteFrame-", StringComparison.Ordinal) || !Guid.TryParseExact(pipeName[12..], "N", out _))
                    throw new ArgumentException("Invalid worker pipe name.");
                pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification);
                pipe.Connect(30000);
                Console.SetIn(new StreamReader(pipe, Encoding.UTF8, leaveOpen: true));
                Console.SetOut(new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true });
            }
            using var desktop = OpenDesktop(args[0], args[1], preferInstalled);
            var configuration = new FrameConfiguration();
            string? line;
            while ((line = Console.ReadLine()) is not null)
            {
                try
                {
                    var input = JsonSerializer.Deserialize<InputEvent>(line, json) ?? throw new InvalidDataException("Missing input.");
                    if (input.Kind == "close") break;
                    if (input.Kind == "configure")
                    {
                        var value = JsonSerializer.Deserialize<JsonElement>(line).GetProperty("configuration").Deserialize<FrameConfiguration>(json)!;
                        value.Validate();
                        desktop.Resize(value.CaptureWidth, value.CaptureHeight);
                        configuration = value;
                        Reply(new { ok = true, configuration });
                    }
                    else if (input.Kind == "capture")
                    {
                        var pixels = desktop.Capture();
                        Reply(new
                        {
                            png = Convert.ToBase64String(PngFrameEncoder.EncodeBgra(pixels, desktop.Width, desktop.Height, configuration.StreamWidth, configuration.StreamHeight)),
                            width = configuration.StreamWidth,
                            height = configuration.StreamHeight,
                            application = desktop.Application
                        });
                    }
                    else { desktop.Apply(input); Reply(new { ok = true }); }
                }
                catch (Exception ex) { Reply(new { error = ex.Message }); }
            }
            return 0;
        }
        catch (Exception ex) { Reply(new { error = ex.Message }); Console.Error.WriteLine(ex); return 1; }
        finally { pipe?.Dispose(); }
    }
    private static WindowsOfficeDesktop OpenDesktop(string bundled, string document, bool preferInstalled)
    {
        return OfficeApplication.OpenFirst(OfficeApplication.Discover(document, bundled, preferInstalled), application =>
        {
            var desktop = new WindowsOfficeDesktop(application, document);
            Console.Error.WriteLine($"Office application selected: {application.Kind} ({application.Executable})");
            return desktop;
        });
    }
    private static void Reply(object value) { Console.WriteLine(JsonSerializer.Serialize(value)); Console.Out.Flush(); }
}
