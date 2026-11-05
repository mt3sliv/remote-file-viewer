using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RemoteFrame.AspNetCore;

[SupportedOSPlatform("windows")]
internal static class WindowsCallerWorkerLauncher
{
    internal static async ValueTask<WorkerConnection> LaunchAsync(ProcessStartInfo start, WindowsIdentity caller,
        string directory, bool loadProfile, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var callerSid = caller.User ?? throw new InvalidOperationException("Caller has no Windows SID.");
        using var service = WindowsIdentity.GetCurrent();
        var serviceSid = service.User ?? throw new InvalidOperationException("Service has no Windows SID.");
        GrantDirectory(directory, callerSid);
        var temporary = Path.Combine(directory, "temp");
        Directory.CreateDirectory(temporary);
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(serviceSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(callerSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        var name = "RemoteFrame-" + Guid.NewGuid().ToString("N");
        var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 65536, 65536, security);
        Process? process = null;
        SafeFileHandle? job = null;
        try
        {
            Check(DuplicateTokenEx(caller.AccessToken, 0x000b, 0, 2, 1, out var primary), "Duplicate caller primary token");
            using (primary)
            {
                var arguments = start.ArgumentList.Concat(["--pipe", name]);
                var command = new StringBuilder(string.Join(" ", new[] { start.FileName }.Concat(arguments).Select(Quote)));
                if (command.Length >= 1024) throw new InvalidOperationException("Caller worker command exceeds the Windows token-launch limit of 1024 characters.");
                using var environment = EnvironmentBlock(primary, temporary);
                var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = 1, ShowWindow = 0 };
                // A null desktop keeps the worker in the host's session. Windows grants the
                // caller access to the inherited window station; Office creates its private desktop.
                if (!CreateProcessWithTokenW(primary, loadProfile ? 1u : 0, start.FileName, command,
                    0x08000404, environment.DangerousGetHandle(), Path.GetDirectoryName(start.FileName)!, ref startup, out var created))
                {
                    var error = Marshal.GetLastWin32Error(); // Preserve before diagnostic native calls.
                    throw new CallerWorkerLaunchException(error, WindowsLaunchDiagnostics.Capture(service, caller, primary,
                        start.FileName, directory, loadProfile));
                }
                try
                {
                    VerifyIdentity(created.Process, callerSid);
                    // Retain host control of the worker even when the caller's default process
                    // DACL would prevent Process.GetProcessById from reopening its handle.
                    var descriptor = new RawSecurityDescriptor($"D:P(A;;GA;;;SY)(A;;GA;;;{serviceSid.Value})(A;;GA;;;{callerSid.Value})");
                    var bytes = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(bytes, 0);
                    Check(SetKernelObjectSecurity(created.Process, 4, bytes), "Grant host control of caller worker");
                    job = new SafeFileHandle(CreateJobObjectW(0, null), true);
                    Check(!job.IsInvalid, "Create caller worker cleanup job");
                    var limits = new JobLimits { Basic = new BasicJobLimits { LimitFlags = 0x2000 } };
                    Check(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>()), "Configure caller worker cleanup");
                    Check(AssignProcessToJobObject(job, created.Process), "Assign caller worker cleanup job");
                    process = Process.GetProcessById((int)created.ProcessId);
                    _ = process.Handle; // Open while the original suspended process is still held.
                    ct.ThrowIfCancellationRequested();
                    Check(ResumeThread(created.Thread) != uint.MaxValue, "Resume caller worker");
                }
                catch { TerminateProcess(created.Process, 1); throw; }
                finally { CloseHandle(created.Thread); CloseHandle(created.Process); }
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var connected = pipe.WaitForConnectionAsync(timeout.Token);
            var exited = process!.WaitForExitAsync(timeout.Token);
            if (await Task.WhenAny(connected, exited) == exited)
            {
                await exited;
                throw new IOException($"Caller worker exited before connecting (exit code {process.ExitCode}). Rebuild/deploy the worker with named-pipe support and check runtime permissions.");
            }
            await connected;
            Check(GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var client), "Verify caller worker pipe client");
            if (client != (uint)process.Id) throw new IOException("Caller worker pipe connected from an unexpected process.");
            timeout.Cancel();
            return new WorkerConnection(process, pipe, job!);
        }
        catch
        {
            job?.Dispose();
            try { if (process is not null && !process.HasExited) process.Kill(true); }
            finally { process?.Dispose(); pipe.Dispose(); }
            throw;
        }
    }

    private static void GrantDirectory(string path, SecurityIdentifier sid)
    {
        var info = new DirectoryInfo(path);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(acl);
    }

    private static SafeEnvironment EnvironmentBlock(SafeAccessTokenHandle token, string temporary)
    {
        Check(CreateEnvironmentBlock(out var original, token, false), "Create caller environment");
        try
        {
            var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var cursor = original; Marshal.ReadInt16(cursor) != 0;)
            {
                var entry = Marshal.PtrToStringUni(cursor)!;
                var separator = entry.IndexOf('=', 1);
                if (separator > 0) values[entry[..separator]] = entry[(separator + 1)..];
                cursor += (entry.Length + 1) * 2;
            }
            values["TEMP"] = temporary; values["TMP"] = temporary;
            var block = string.Join('\0', values.Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
            return new SafeEnvironment(Marshal.StringToHGlobalUni(block));
        }
        finally { DestroyEnvironmentBlock(original); }
    }

    internal static string Quote(string value)
    {
        if (value.Contains('\0')) throw new ArgumentException("Arguments cannot contain NUL.");
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static void VerifyIdentity(nint process, SecurityIdentifier expected)
    {
        Check(OpenProcessToken(process, 8, out var token), "Read caller worker identity");
        using (token)
        using (var actual = new WindowsIdentity(token.DangerousGetHandle()))
            if (actual.User != expected) throw new InvalidOperationException("Worker was not launched as the caller.");
    }

    private static void Check(bool success, string operation)
    {
        if (!success)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"{operation}: Windows error {error} ({new Win32Exception(error).Message}).");
        }
    }

    private sealed class SafeEnvironment(nint pointer) : SafeHandle(pointer, true)
    {
        public override bool IsInvalid => handle == 0;
        protected override bool ReleaseHandle() { Marshal.FreeHGlobal(handle); return true; }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize; public nint ReservedPointer, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicJobLimits
    { public long ProcessTime, JobTime; public uint LimitFlags; public nuint MinWorkingSet, MaxWorkingSet; public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct JobLimits
    { public BasicJobLimits Basic; public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }

    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, nint attributes, int level, int type, out SafeAccessTokenHandle primary);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessWithTokenW(SafeAccessTokenHandle token, uint logonFlags, string application, StringBuilder command, uint flags, nint environment, string directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetKernelObjectSecurity(nint handle, uint information, byte[] descriptor);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out nint environment, SafeAccessTokenHandle token, bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(nint environment);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref JobLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(nint process, uint exitCode);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
