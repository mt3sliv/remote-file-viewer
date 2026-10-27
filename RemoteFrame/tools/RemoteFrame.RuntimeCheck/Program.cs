using RemoteFrame.AspNetCore;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length >= 9 && args[0] == "--create")
{
    var root = Path.GetFullPath(args[1]);
    var inventory = new SortedDictionary<string, string>(StringComparer.Ordinal);
    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        if (relative == "runtime.json") continue;
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.SequentialScan);
        inventory.Add(relative, Convert.ToHexString(SHA256.HashData(stream)));
    }
    File.WriteAllText(Path.Combine(root, "runtime.json"), JsonSerializer.Serialize(new
    {
        schemaVersion = 1, runtimeIdentifier = args[2], version = args[3], sourceRevision = args[4],
        engine = args[5], worker = args[6], licenses = args[7..], files = inventory
    }));
    Console.WriteLine($"Created runtime manifest for {inventory.Count} files.");
    return 0;
}

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: RemoteFrame.RuntimeCheck <payload-directory> <runtime-identifier>");
    return 2;
}
try
{
    var runtime = BundledOfficeRuntime.Validate(args[0], args[1]);
    Console.WriteLine($"Validated Office runtime {runtime.Version} ({runtime.RuntimeIdentifier}).");
    return 0;
}
catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
