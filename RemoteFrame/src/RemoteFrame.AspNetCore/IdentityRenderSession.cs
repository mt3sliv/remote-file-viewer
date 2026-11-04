using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Microsoft.AspNetCore.Http;
using RemoteFrame.Core;

namespace RemoteFrame.AspNetCore;

public enum FileAccessIdentity { Service, Caller }

public sealed class FileAccessOptions
{
    public bool StageLocally { get; set; } = true;
    public FileAccessIdentity DownloadIdentity { get; set; }
    public FileAccessIdentity SaveBackIdentity { get; set; }
}

public sealed class ApplicationSessionOptions
{
    public FileAccessIdentity ProcessIdentity { get; set; }
    /// <summary>Host implementation must launch the worker as the authenticated Windows caller,
    /// preserve redirected pipes, and grant that caller access to the staged directory.
    /// The library owns and disposes the returned process.</summary>
    public Func<HttpContext, ProcessStartInfo, CancellationToken, ValueTask<Process>>? LaunchCallerWorkerAsync { get; set; }
}

internal sealed class IdentityRenderSession : IRenderSession
{
    private readonly IRenderSession inner;
    private readonly string source, local, directory;
    private readonly WindowsIdentity? caller;
    private readonly FileAccessIdentity saveIdentity;
    private byte[] sourceHash, localHash;
    private bool disposed;

    private IdentityRenderSession(IRenderSession inner, string source, string local, string directory,
        WindowsIdentity? caller, FileAccessIdentity saveIdentity, byte[] hash)
    {
        this.inner = inner; this.source = source; this.local = local; this.directory = directory;
        this.caller = caller; this.saveIdentity = saveIdentity; sourceHash = hash; localHash = hash;
    }

    internal static async ValueTask<IRenderSession> OpenAsync(IRenderProvider provider, string path,
        HttpContext context, RemoteFrameOptions options, CancellationToken ct)
    {
        var access = options.FileAccess;
        var app = options.ApplicationSession;
        if (!Enum.IsDefined(access.DownloadIdentity) || !Enum.IsDefined(access.SaveBackIdentity) || !Enum.IsDefined(app.ProcessIdentity))
            throw new InvalidOperationException("Unknown file access identity.");
        var needsCaller = access.DownloadIdentity == FileAccessIdentity.Caller || access.SaveBackIdentity == FileAccessIdentity.Caller || app.ProcessIdentity == FileAccessIdentity.Caller;
        WindowsIdentity? caller = null;
        string? directory = null;
        try
        {
            if (needsCaller)
            {
                if (!OperatingSystem.IsWindows() || context.User.Identity is not WindowsIdentity identity || !identity.IsAuthenticated)
                    throw new InvalidOperationException("Caller identity requires authenticated Windows credentials.");
                caller = new WindowsIdentity(identity.AccessToken.DangerousGetHandle());
            }
            if (!access.StageLocally)
            {
                if (needsCaller) throw new InvalidOperationException("Caller identity requires local staging.");
                return await provider.OpenAsync(path, ct);
            }
            if (app.ProcessIdentity == FileAccessIdentity.Caller && (provider is not OfficeRenderProvider || app.LaunchCallerWorkerAsync is null))
                throw new InvalidOperationException("Caller process identity requires Office and a host caller-worker launcher.");
            path = Path.GetFullPath(path);
            directory = Path.Combine(Path.GetTempPath(), "RemoteFrame-stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var local = Path.Combine(directory, Path.GetFileName(path));
            // Open only the source under impersonation. Local storage belongs to the service.
            using (var input = Run(access.DownloadIdentity, caller, () => File.OpenRead(path)))
            using (var output = new FileStream(local, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await input.CopyToAsync(output, ct);
            var hash = Hash(local);
            if (!hash.AsSpan().SequenceEqual(Run(access.DownloadIdentity, caller, () => Hash(path))))
                throw new IOException("Source changed while staging the document.");
            var renderer = provider is OfficeRenderProvider office
                ? await office.OpenAsync(local, app.ProcessIdentity == FileAccessIdentity.Caller
                    ? async (start, token) =>
                    {
                        var process = await app.LaunchCallerWorkerAsync!(context, start, token);
                        try
                        {
                            if (!OperatingSystem.IsWindows() || !OpenProcessToken(process.Handle, 8, out var accessToken))
                                throw new InvalidOperationException("Cannot verify caller worker identity.");
                            using (accessToken)
                            using (var actual = new WindowsIdentity(accessToken.DangerousGetHandle()))
                                if (actual.User != caller!.User) throw new InvalidOperationException("Worker was not launched as the caller.");
                            return process;
                        }
                        catch
                        {
                            try { if (!process.HasExited) process.Kill(true); }
                            finally { process.Dispose(); }
                            throw;
                        }
                    } : null, ct)
                : await provider.OpenAsync(local, ct);
            return new IdentityRenderSession(renderer, path, local, directory, caller, access.SaveBackIdentity, hash);
        }
        catch
        {
            if (OperatingSystem.IsWindows()) caller?.Dispose();
            if (directory is not null) Directory.Delete(directory, true);
            throw;
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);

    private static T Run<T>(FileAccessIdentity identity, WindowsIdentity? caller, Func<T> action)
    {
        if (identity == FileAccessIdentity.Service) return action();
        if (!OperatingSystem.IsWindows() || caller is null) throw new InvalidOperationException("Windows caller token unavailable.");
        return WindowsIdentity.RunImpersonated(caller.AccessToken, action);
    }

    private static byte[] Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }

    private void SaveBack()
    {
        if (!CanSave) return;
        var current = Hash(local);
        if (current.AsSpan().SequenceEqual(localHash)) return;
        var temporary = source + ".remoteframe-" + Guid.NewGuid().ToString("N") + ".tmp";
        using var input = File.OpenRead(local);
        Run(saveIdentity, caller, () =>
        {
            if (!Hash(source).AsSpan().SequenceEqual(sourceHash)) throw new IOException("Source changed in another editor; save-back refused.");
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                if (!Hash(temporary).AsSpan().SequenceEqual(current)) throw new IOException("Local document changed during save-back.");
                if (!Hash(source).AsSpan().SequenceEqual(sourceHash)) throw new IOException("Source changed during save-back.");
                File.Move(temporary, source, true);
                return true;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        });
        sourceHash = current; localHash = current;
    }

    public bool CanSave => inner.CanSave;
    public ValueTask ConfigureAsync(FrameConfiguration configuration, CancellationToken ct) => inner.ConfigureAsync(configuration, ct);
    public async ValueTask<Frame> CaptureAsync(CancellationToken ct)
    {
        var frame = await inner.CaptureAsync(ct); SaveBack(); return frame;
    }
    public async ValueTask ApplyAsync(InputEvent input, CancellationToken ct)
    {
        await inner.ApplyAsync(input, ct); SaveBack();
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try { await inner.DisposeAsync(); SaveBack(); }
        finally
        {
            if (OperatingSystem.IsWindows()) caller?.Dispose();
            Directory.Delete(directory, true);
        }
    }
}
