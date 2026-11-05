using System.ComponentModel;

namespace RemoteFrame.AspNetCore;

internal sealed class CallerWorkerLaunchException : Win32Exception
{
    internal string PublicMessage => NativeErrorCode switch
    {
        5 => "Windows denied starting the document worker as your Windows identity (error 5). The worker and Office application have not started. Ask the server administrator to check the launch diagnostics.",
        1314 => "Windows could not start the document worker because a required host privilege is unavailable (error 1314). The worker and Office application have not started.",
        _ => $"Windows could not start the document worker as your Windows identity (error {NativeErrorCode}). The worker and Office application have not started. Check the server launch diagnostics."
    };

    internal CallerWorkerLaunchException(int error, string diagnostics)
        : base(error, $"CreateProcessWithTokenW failed: Windows error {error} ({new Win32Exception(error).Message}). " +
            "Stage=worker-process-create; worker not started; Excel/Word/LibreOffice launch not attempted. " +
            Guidance(error) + Environment.NewLine + diagnostics) { }

    private static string Guidance(int error) => error switch
    {
        5 => "Access denied does not identify one cause and is not proof of a missing SeImpersonatePrivilege. " +
            "Check caller access to the worker executable, runtime DLLs and parent directories; caller token restrictions and logon context; " +
            "profile-loading access when enabled; and applicable application-control/security policy. " +
            "A successful 4624 logon is authentication evidence, not proof of process-launch permission. Correlate the caller LogonId below with the event's TargetLogonId and LogonType.",
        1314 => "Inspect SeImpersonatePrivilege on the actual hosting process token below. Account policy assignments may require an app-pool recycle or service restart before the running process receives them.",
        2 or 3 => "Verify the deployed worker executable and its parent directory exist at WorkerPath below.",
        193 => "Check worker executable format and host architecture; deploy the supported Windows x64 worker runtime.",
        1326 or 1385 => "Check the caller's Windows logon token and relevant logon-policy restrictions; do not infer local process-launch permission from successful HTTP authentication.",
        _ => "Use the native Windows error and token/path diagnostics below to investigate this process-creation failure."
    };
}
