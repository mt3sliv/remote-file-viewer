using System.Diagnostics;
using System.Text;

// Maintainer-only adapter. Keep long response-file lines below MSVC LNK1170's limit.
// No source, object files, linker arguments, or library contents are changed.
try
{
    var linker = Environment.GetEnvironmentVariable("REMOTEFRAME_REAL_LINK");
    if (string.IsNullOrWhiteSpace(linker) || !Path.IsPathFullyQualified(linker) ||
        !File.Exists(linker) || !string.Equals(Path.GetFileName(linker), "link.exe", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Set REMOTEFRAME_REAL_LINK to the actual MSVC link.exe.");
    if (string.Equals(Path.GetFullPath(linker), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("The linker adapter cannot invoke itself.");
    foreach (var argument in args)
    {
        if (!argument.StartsWith('@') || argument.Length == 1) continue;
        var path = argument[1..];
        using var reader = new StreamReader(path, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        var encoding = reader.CurrentEncoding;
        if (!text.Split('\n').Any(line => line.Length > 60000)) continue;
        var result = new StringBuilder(text.Length);
        bool quoted = false;
        int backslashes = 0;
        foreach (var character in text)
        {
            if (character == '"' && backslashes % 2 == 0) quoted = !quoted;
            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (result.Length > 0 && result[^1] != '\n') result.Append('\n');
            }
            else result.Append(character);
            backslashes = character == '\\' ? backslashes + 1 : 0;
        }
        if (quoted) throw new InvalidDataException("Unterminated quoted linker response-file argument.");
        reader.Dispose();
        File.WriteAllText(path, result.ToString(), encoding);
    }
    var start = new ProcessStartInfo(linker) { UseShellExecute = false };
    foreach (var argument in args) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new IOException("Could not start MSVC linker.");
    process.WaitForExit();
    return process.ExitCode;
}
catch (Exception exception)
{
    Console.Error.WriteLine("RemoteFrame linker adapter: " + exception.Message);
    return 1;
}
