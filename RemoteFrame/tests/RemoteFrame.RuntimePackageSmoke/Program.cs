// This fixture package contains text files, never executable Office binaries.
var root = Path.Combine(AppContext.BaseDirectory, "remote-frame", "win-x64");
foreach (var path in new[] { "runtime.json", "office/program/soffice.exe", "worker/worker.exe", "notices/LICENSE.txt", "office/program/extensionless", "office/program/.dir" })
{
    if (!File.Exists(Path.Combine(root, path))) throw new Exception("Missing packaged asset: " + path);
}
if (!File.ReadAllText(Path.Combine(root, "office/program/soffice.exe")).StartsWith("TEST FIXTURE"))
    throw new Exception("Unexpected runtime package used for fixture test");
Console.WriteLine("PASS: NuGet runtime assets preserve their complete tree in output.");

