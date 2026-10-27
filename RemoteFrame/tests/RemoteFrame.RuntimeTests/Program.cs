using System.Security.Cryptography;
using System.Text.Json;
using RemoteFrame.AspNetCore;

// Text fixtures validate package integrity only. They are never executed as Office binaries.
var root = Path.Combine(Path.GetTempPath(), "remoteframe-runtime-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    Directory.CreateDirectory(Path.Combine(root, "office", "program"));
    Directory.CreateDirectory(Path.Combine(root, "worker"));
    Directory.CreateDirectory(Path.Combine(root, "notices"));
    var files = new Dictionary<string, string>();
    foreach (var path in new[] { "office/program/soffice.exe", "worker/worker.exe", "notices/LICENSE.txt" })
    {
        File.WriteAllText(Path.Combine(root, path), "NOT AN ENGINE: packaging test fixture " + path);
        files[path] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, path))));
    }
    void WriteManifest(string rid = "win-x64", string engine = "office/program/soffice.exe", string[]? licenses = null)
    {
        File.WriteAllText(Path.Combine(root, "runtime.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, runtimeIdentifier = rid, version = "test-fixture", sourceRevision = "test-fixture-only",
            engine, worker = "worker/worker.exe", licenses = licenses ?? ["notices/LICENSE.txt"], files
        }));
    }
    WriteManifest();
    var valid = BundledOfficeRuntime.Validate(root, "win-x64");
    if (!File.Exists(valid.EnginePath)) throw new Exception("Valid runtime did not resolve");
    Console.WriteLine("PASS: Valid payload resolves without an installed application");
    _ = BundledOfficeRuntime.Validate(root, "win-x64", verifyAllFiles: false);
    Console.WriteLine("PASS: Startup validation resolves the package-owned entry points");
    Reject(() => BundledOfficeRuntime.Validate(root, "linux-x64"), "Wrong platform");
    File.AppendAllText(Path.Combine(root, "worker/worker.exe"), "corruption");
    Reject(() => BundledOfficeRuntime.Validate(root, "win-x64"), "Corrupted binary");
    Reject(() => BundledOfficeRuntime.Validate(root, "win-x64", verifyAllFiles: false), "Corrupted startup binary");
    File.WriteAllText(Path.Combine(root, "worker/worker.exe"), "NOT AN ENGINE: packaging test fixture worker/worker.exe");
    WriteManifest(licenses: ["notices/MISSING.txt"]);
    Reject(() => BundledOfficeRuntime.Validate(root, "win-x64"), "Missing license");
    files.Add("../outside.txt", new string('0', 64));
    WriteManifest();
    Reject(() => BundledOfficeRuntime.Validate(root, "win-x64"), "Escaping path");
    files.Remove("../outside.txt");
    WriteManifest();
    File.WriteAllText(Path.Combine(root, "unlisted.dll"), "not inventoried");
    Reject(() => BundledOfficeRuntime.Validate(root, "win-x64"), "Unlisted file");
    File.Delete(Path.Combine(root, "unlisted.dll"));
    File.Delete(Path.Combine(root, "runtime.json"));
    Reject(() => BundledOfficeRuntime.Validate(root, "win-x64"), "Missing runtime");
    Console.WriteLine("Runtime validation checks passed.");
}
finally { Directory.Delete(root, recursive: true); }

static void Reject(Action action, string name)
{
    try { action(); }
    catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
    {
        Console.WriteLine("PASS: " + name + " rejected");
        return;
    }
    throw new Exception(name + " was unexpectedly accepted");
}
