using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace RemoteFrame.AspNetCore;

[SupportedOSPlatform("windows")]
internal static class WindowsLaunchDiagnostics
{
    internal static string Capture(WindowsIdentity service, WindowsIdentity caller, SafeAccessTokenHandle primary,
        string worker, string staging, bool loadProfile)
    {
        // Diagnostics must never replace the original launch error. No token handles,
        // credentials, document contents or environment values are emitted.
        return string.Join(Environment.NewLine,
            "Completed before failure: staged-directory ACL, primary-token duplication, caller environment creation.",
            "Host: " + Identity(service),
            "Host SeImpersonatePrivilege: " + Inspect(() => ImpersonatePrivilege(service.AccessToken)),
            "Caller: " + Identity(caller),
            "Duplicated primary token: " + Inspect(() => DescribeToken(primary)),
            $"LoadCallerProfile={loadProfile}; HostProcessId={Environment.ProcessId}; HostSessionId={Inspect(() => Process.GetCurrentProcess().SessionId.ToString())}; Is64BitProcess={Environment.Is64BitProcess}",
            $"WorkerPath={worker}; StagingDirectory={staging}",
            "WorkerReadableAsCaller=" + Inspect(() => WindowsIdentity.RunImpersonated(caller.AccessToken, () =>
            {
                using var stream = File.Open(worker, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return "yes (read only; execute permission and dependent DLL access are not tested)";
            })));
    }

    private static string Identity(WindowsIdentity identity) => Inspect(() =>
        $"Sid={identity.User?.Value}; Authenticated={identity.IsAuthenticated}; AuthenticationType={identity.AuthenticationType}; " +
        $"ImpersonationLevel={identity.ImpersonationLevel}; {DescribeToken(identity.AccessToken)}");

    private static string DescribeToken(SafeAccessTokenHandle token)
    {
        var type = Read(token, 8);
        var session = Read(token, 12);
        var statistics = Read(token, 10);
        var elevation = Read(token, 18);
        var tokenType = BitConverter.ToInt32(type) switch { 1 => "Primary", 2 => "Impersonation", var other => other.ToString() };
        var logonId = ((ulong)BitConverter.ToUInt32(statistics, 12) << 32) | BitConverter.ToUInt32(statistics, 8);
        var elevationType = BitConverter.ToInt32(elevation) switch { 1 => "Default", 2 => "Full", 3 => "Limited", var other => other.ToString() };
        return $"TokenType={tokenType}; TokenSessionId={BitConverter.ToUInt32(session)}; LogonId=0x{logonId:x}; ElevationType={elevationType}";
    }

    private static string ImpersonatePrivilege(SafeAccessTokenHandle token)
    {
        if (!LookupPrivilegeValueW(null, "SeImpersonatePrivilege", out var expected))
            return $"unavailable (Windows error {Marshal.GetLastWin32Error()})";
        var privileges = Read(token, 3);
        var count = BitConverter.ToUInt32(privileges);
        for (var index = 0; index < count && 4L + (index + 1L) * 12 <= privileges.Length; index++)
        {
            var offset = 4 + index * 12;
            if (BitConverter.ToUInt32(privileges, offset) == expected.Low && BitConverter.ToInt32(privileges, offset + 4) == expected.High)
                return (BitConverter.ToUInt32(privileges, offset + 8) & 2) != 0 ? "present, enabled" : "present, disabled";
        }
        return "absent from host token";
    }

    private static byte[] Read(SafeAccessTokenHandle token, int kind)
    {
        GetTokenInformation(token, kind, null, 0, out var length);
        if (length <= 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var data = new byte[length];
        if (!GetTokenInformation(token, kind, data, length, out _)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return data;
    }

    private static string Inspect(Func<string> read)
    {
        try { return read(); }
        catch (Exception ex) { return $"unavailable ({ex.GetType().Name}: {ex.Message})"; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, byte[]? data, int length, out int required);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool LookupPrivilegeValueW(string? system, string name, out Luid value);
}
