using System.Runtime.InteropServices;
using System.Text;
using System.Reflection.PortableExecutable;
using Microsoft.Win32;

namespace RemoteFrame.Office.Worker;

internal enum OfficeApplicationKind { LibreOffice, Excel, Word }

internal sealed record OfficeApplication(string Executable, OfficeApplicationKind Kind)
{
    internal static T OpenFirst<T>(IEnumerable<OfficeApplication> applications, Func<OfficeApplication, T> open)
    {
        var failures = new List<Exception>();
        foreach (var application in applications)
        {
            try { return open(application); }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Office application failed: {application.Executable}: {ex.Message}");
                failures.Add(ex);
            }
        }
        throw new IOException("No Office application could open an isolated document session.", new AggregateException(failures));
    }

    public bool MatchesWindow(string name) => Kind switch
    {
        OfficeApplicationKind.Excel => name == "XLMAIN",
        OfficeApplicationKind.Word => name == "OpusApp",
        _ => name.Contains("SALFRAME", StringComparison.OrdinalIgnoreCase)
    };

    // Only supported adapters can launch applications; never execute association command templates/DDE.
    internal static OfficeApplication? Recognize(string? executable, string extension)
    {
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable)) return null;
        var name = Path.GetFileName(executable).ToLowerInvariant();
        var kind = name switch
        {
            "excel.exe" when extension is ".xls" or ".xlsx" or ".xlsm" or ".ods" => OfficeApplicationKind.Excel,
            "winword.exe" when extension is ".doc" or ".docx" or ".odt" => OfficeApplicationKind.Word,
            "soffice.exe" => OfficeApplicationKind.LibreOffice,
            _ => (OfficeApplicationKind?)null
        };
        return kind is null ? null : new(executable, kind.Value);
    }

    internal static IReadOnlyList<OfficeApplication> Select(string extension, string bundled,
        string? associated, IEnumerable<string> installed, Func<string, bool> exists, bool preferInstalled)
    {
        var result = new List<OfficeApplication>();
        if (preferInstalled)
            foreach (var path in new[] { associated }.Concat(installed))
            {
                var app = Recognize(path, extension.ToLowerInvariant());
                if (app is not null && exists(app.Executable) && !result.Any(x =>
                    string.Equals(x.Executable, app.Executable, StringComparison.OrdinalIgnoreCase))) result.Add(app);
            }
        result.RemoveAll(x => string.Equals(x.Executable, bundled, StringComparison.OrdinalIgnoreCase));
        result.Add(new(bundled, OfficeApplicationKind.LibreOffice));
        return result;
    }

    public string CommandLine(string working, string profile)
    {
        // File-system paths cannot contain quotes; explicit application + quoted paths avoid shell parsing.
        if (new[] { Executable, working, profile }.Any(x => x.Contains('"'))) throw new ArgumentException("Invalid application path.");
        var switches = Kind switch
        {
            OfficeApplicationKind.Excel => "/x", // Separate process; never attach to an existing Excel instance.
            OfficeApplicationKind.Word => "/n", // Separate instance with the specified document.
            _ => $"-env:UserInstallation={new Uri(profile).AbsoluteUri} --norestore --nodefault --nofirststartwizard"
        };
        return $"\"{Executable}\" {switches} \"{working}\"";
    }

    public static IReadOnlyList<OfficeApplication> Discover(string document, string bundled, bool preferInstalled)
    {
        if (!OperatingSystem.IsWindows()) return [new(bundled, OfficeApplicationKind.LibreOffice)];
        var extension = Path.GetExtension(document).ToLowerInvariant();
        string? associated = null;
        if (preferInstalled)
        {
            var length = 32768u;
            var buffer = new StringBuilder((int)length);
            if (AssocQueryString(0, 2, extension, "open", buffer, ref length) == 0) associated = buffer.ToString();
        }
        var installed = new List<string>();
        if (preferInstalled)
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                    foreach (var name in new[] { "excel.exe", "winword.exe", "soffice.exe" })
                    {
                        try
                        {
                            using var root = RegistryKey.OpenBaseKey(hive, view);
                            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + name);
                            if (key?.GetValue(null) is string path) installed.Add(path.Trim('"'));
                        }
                        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                        { Console.Error.WriteLine($"Cannot inspect installed {name}: {ex.Message}"); }
                    }
        return Select(extension, bundled, associated, installed, CompatibleExecutable, preferInstalled);
    }

    private static bool CompatibleExecutable(string path)
    {
        // The in-process keyboard helper must have the same architecture as its target application.
        try
        {
            using var input = File.OpenRead(path);
            using var pe = new PEReader(input);
            var expected = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => Machine.Amd64,
                Architecture.Arm64 => Machine.Arm64,
                _ => Machine.I386
            };
            return pe.PEHeaders.CoffHeader.Machine == expected;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException) { return false; }
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(uint flags, uint kind, string association, string extra,
        StringBuilder output, ref uint length);
}
