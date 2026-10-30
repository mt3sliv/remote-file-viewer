using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace RemoteFrame.AspNetCore;

/// <summary>Validates package-owned Office components without consulting PATH or installed applications.
/// EnginePath is empty when only the standalone worker is deployed.</summary>
public sealed record BundledOfficeRuntime(string RuntimeIdentifier, string Version, string EnginePath, string WorkerPath)
{
    private static readonly Lazy<BundledOfficeRuntime> Current = new(() => Validate(
        Path.Combine(AppContext.BaseDirectory, "remote-frame", CurrentRuntimeIdentifier()), CurrentRuntimeIdentifier(), verifyAllFiles: false));
    public static BundledOfficeRuntime Load() => Current.Value;

    public static string CurrentRuntimeIdentifier()
    {
        var platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" :
            throw new PlatformNotSupportedException("Office runtimes currently target Windows and Linux.");
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException("Office runtimes require x64 or arm64.")
        };
        return platform + "-" + architecture;
    }

    /// <summary>Checks a staged runtime before packaging, or a deployed package payload.</summary>
    public static BundledOfficeRuntime Validate(string directory, string expectedRuntimeIdentifier, bool verifyAllFiles = true)
    {
        var root = Path.GetFullPath(directory);
        if (Directory.Exists(Path.Combine(root, "components", "worker")))
        {
            foreach (var path in new[] { root, Path.Combine(root, "components"), Path.Combine(root, "components", "worker"), Path.Combine(root, "components", "libreoffice") })
                if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Runtime component directories must not be links.");
            var workerRuntime = ValidatePayload(Path.Combine(root, "components", "worker"), expectedRuntimeIdentifier, verifyAllFiles);
            if (workerRuntime.EnginePath.Length != 0 || workerRuntime.WorkerPath.Length == 0)
                throw new InvalidDataException("Expected a standalone worker runtime.");
            var libreRoot = Path.Combine(root, "components", "libreoffice");
            if (!Directory.Exists(libreRoot)) return workerRuntime;
            var libre = ValidatePayload(libreRoot, expectedRuntimeIdentifier, verifyAllFiles);
            if (libre.EnginePath.Length == 0 || libre.WorkerPath.Length != 0)
                throw new InvalidDataException("Expected a standalone LibreOffice runtime.");
            return workerRuntime with { EnginePath = libre.EnginePath };
        }
        return ValidatePayload(root, expectedRuntimeIdentifier, verifyAllFiles);
    }

    private static BundledOfficeRuntime ValidatePayload(string root, string expectedRuntimeIdentifier, bool verifyAllFiles)
    {
        var manifestPath = Path.Combine(root, "runtime.json");
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException($"Office worker runtime '{expectedRuntimeIdentifier}' is missing. Expected manifest: {manifestPath}. Restore and deploy RemoteFrame.Office.Windows. For bundled LibreOffice fallback, use RemoteFrame.Office.Windows.WithLibreOffice.");
        var manifest = JsonSerializer.Deserialize<RuntimeManifest>(File.ReadAllText(manifestPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Office runtime manifest is empty.");
        var component = manifest.SchemaVersion == 2 ? manifest.Component : null;
        if ((manifest.SchemaVersion != 1 && (manifest.SchemaVersion != 2 || component is not ("worker" or "libreoffice"))) || manifest.RuntimeIdentifier != expectedRuntimeIdentifier ||
            string.IsNullOrWhiteSpace(manifest.Version) || string.IsNullOrWhiteSpace(manifest.SourceRevision) ||
            manifest.Files is not { Count: > 0 } || manifest.Licenses is not { Length: > 0 })
            throw new InvalidDataException("Office runtime manifest is incomplete or targets a different platform.");
        if ((component != "worker" && string.IsNullOrWhiteSpace(manifest.Engine)) ||
            (component != "libreoffice" && string.IsNullOrWhiteSpace(manifest.Worker)) ||
            (component == "worker" && manifest.Engine is not null) || (component == "libreoffice" && manifest.Worker is not null))
            throw new InvalidDataException("Runtime entry points do not match its component.");
        var entryPoints = new[] { manifest.Engine, manifest.Worker }.OfType<string>().ToArray();

        string Resolve(string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\') ||
                relative.Contains(':') || relative.Split('/').Any(part => part is "" or "." or ".."))
                throw new InvalidDataException("Runtime paths must be relative paths using forward slashes.");
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("Runtime path escapes the package directory.");
            for (var current = new FileInfo(path) as FileSystemInfo; current is not null; current = Directory.GetParent(current.FullName))
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Runtime payloads must contain regular files and directories, not links.");
                if (current.FullName == root) break;
            }
            return path;
        }

        var requiredFiles = manifest.Licenses.Concat(entryPoints).ToHashSet();
        if (manifest.Files.ContainsKey("worker/RemoteFrame.InputHook.dll")) requiredFiles.Add("worker/RemoteFrame.InputHook.dll");
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var (relative, expectedHash) in manifest.Files)
        {
            if (!paths.Add(relative) || expectedHash is null || expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
                throw new InvalidDataException("Runtime manifest contains duplicate paths or invalid SHA-256 hashes.");
            if (!verifyAllFiles && !requiredFiles.Contains(relative)) continue;
            using var file = File.OpenRead(Resolve(relative));
            var actualHash = Convert.ToHexString(SHA256.HashData(file));
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Office runtime integrity check failed: {relative}");
        }
        foreach (var required in manifest.Licenses.Concat(entryPoints))
            if (required is null || !manifest.Files.ContainsKey(required))
                throw new InvalidDataException("Runtime engine, worker and license notices must be listed in the manifest.");
        void CheckInventory(string directoryPath)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directoryPath))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Runtime payloads must not contain links.");
                if ((attributes & FileAttributes.Directory) != 0) CheckInventory(entry);
                else if (Path.GetFullPath(entry) != manifestPath &&
                    !manifest.Files.ContainsKey(Path.GetRelativePath(root, entry).Replace('\\', '/')))
                    throw new InvalidDataException("Every runtime file must be included in the manifest.");
            }
        }
        if (verifyAllFiles) CheckInventory(root);
        var engine = manifest.Engine is null ? "" : Resolve(manifest.Engine);
        var worker = manifest.Worker is null ? "" : Resolve(manifest.Worker);
        if (expectedRuntimeIdentifier.StartsWith("linux-", StringComparison.Ordinal) && !OperatingSystem.IsWindows())
        {
            var executable = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            foreach (var path in new[] { engine, worker })
                if (path.Length != 0 && (File.GetUnixFileMode(path) & executable) == 0)
                    throw new InvalidDataException("Linux Office engine and worker must retain executable permissions.");
        }
        return new BundledOfficeRuntime(expectedRuntimeIdentifier, manifest.Version!, engine, worker);
    }

    private sealed class RuntimeManifest
    {
        public int SchemaVersion { get; set; }
        public string? Component { get; set; }
        public string? RuntimeIdentifier { get; set; }
        public string? Version { get; set; }
        public string? SourceRevision { get; set; }
        public string? Engine { get; set; }
        public string? Worker { get; set; }
        public string[]? Licenses { get; set; }
        public Dictionary<string, string>? Files { get; set; }
    }
}
