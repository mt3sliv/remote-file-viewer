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
            if (args.Length != 2) throw new ArgumentException("Expected bundled engine path and document path.");
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This worker build implements Windows private desktop sessions only.");
            using var desktop = new WindowsOfficeDesktop(args[0], args[1]);
            string? line;
            while ((line = Console.ReadLine()) is not null)
            {
                try
                {
                    var input = JsonSerializer.Deserialize<InputEvent>(line, json) ?? throw new InvalidDataException("Missing input.");
                    if (input.Kind == "capture")
                    {
                        var pixels = desktop.Capture();
                        Reply(new
                        {
                            png = Convert.ToBase64String(PngFrameEncoder.EncodeBgra(pixels, WindowsOfficeDesktop.Width, WindowsOfficeDesktop.Height)),
                            width = WindowsOfficeDesktop.Width,
                            height = WindowsOfficeDesktop.Height
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
    private static void Reply(object value) { Console.WriteLine(JsonSerializer.Serialize(value)); Console.Out.Flush(); }
}
