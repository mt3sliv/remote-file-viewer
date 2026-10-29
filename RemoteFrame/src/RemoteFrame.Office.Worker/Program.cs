using System.Text.Json;
using RemoteFrame.Core;
using RemoteFrame.Rendering;

namespace RemoteFrame.Office.Worker;

internal static class Program
{
    private static int Main(string[] args)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        try
        {
            if (args.Length is < 2 or > 3 || (args.Length == 3 && args[2] != "--bundled-only"))
                throw new ArgumentException("Expected bundled engine path, document path, and optional --bundled-only.");
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This worker build implements Windows private desktop sessions only.");
            using var desktop = OpenDesktop(args[0], args[1], args.Length == 2);
            var configuration = new FrameConfiguration();
            string? line;
            while ((line = Console.ReadLine()) is not null)
            {
                try
                {
                    var input = JsonSerializer.Deserialize<InputEvent>(line, json) ?? throw new InvalidDataException("Missing input.");
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
