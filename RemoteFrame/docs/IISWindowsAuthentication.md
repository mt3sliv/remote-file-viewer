# IIS Windows authentication and caller-owned Office sessions

RemoteFrame can start its Office worker using the authenticated Windows caller's
token. Set `ApplicationSession.ProcessIdentity` to `Caller` and leave
`LaunchCallerWorkerAsync` unset to use the built-in Windows launcher.
This is a native process launch under that identity; thread impersonation alone
does not change the identity of an Office child process.

## Host configuration

Install the IIS Windows Authentication feature and the ASP.NET Core Hosting
Bundle. Enable Windows Authentication for the application and disable Anonymous
Authentication when every request must use Windows credentials. Ensure IIS passes
the authenticated Windows identity to ASP.NET Core for the selected hosting model.
See [ASP.NET Core Windows authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/windowsauth).

For an application hosted behind IIS:

```csharp
using Microsoft.AspNetCore.Server.IISIntegration;
using RemoteFrame.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthentication(IISDefaults.AuthenticationScheme);
builder.Services.AddAuthorization();
builder.Services.AddRemoteFrame(options =>
{
    options.EnableOffice = true;
    options.RequireAuthorization = true;
    options.ApplicationSession.ProcessIdentity = FileAccessIdentity.Caller;
    options.ApplicationSession.LoadCallerProfile = true;
    options.FileAccess.StageLocally = true;
    options.FileAccess.StagingDirectory = @"C:\ProgramData\RemoteFrame\Sessions";
    options.FileAccess.DownloadIdentity = FileAccessIdentity.Caller;
    options.FileAccess.SaveBackIdentity = FileAccessIdentity.Caller;
    options.Files.Add("report", @"\\fileserver\documents\report.xlsx");
    // Configure AuthorizeFileAsync for your application's document permissions.
});

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.UseWebSockets();
app.MapRemoteFrame("/remote-frame");
app.Run();
```

The application must receive an authenticated `WindowsIdentity`, not just a
username or a cookie containing Windows-looking claims. RemoteFrame accepts its
SID for route authorization and session ownership when `NameIdentifier` is absent.
Per-file authorization callbacks still apply.

## Account and filesystem requirements

- The app-pool/process account needs `SeImpersonatePrivilege` to call
  `CreateProcessWithTokenW`. Provision it through the deployment's Windows security
  policy if required; RemoteFrame does not grant account privileges or request UAC.
- Provision the staging root with full control for the hosting account. Each new
  session directory grants its caller Modify access, inherited by its document
  and temporary files. Parent directories must permit traversal.
- Give callers read/execute access to the deployed worker, its .NET/runtime files,
  the optional bundled engine and any selected installed application. Keep those
  binaries writable only by trusted deployment administrators.
- The caller's token/profile must support local process creation and the selected
  Office application. Windows validates those permissions; failure does not retry
  under the app-pool account.
- Remote share access under `Caller` additionally needs appropriate share/NTFS
  permissions and delegation. Local worker launch does not solve the network
  double-hop problem. Download, process and save-back identities remain separate.

The launcher keeps the process in the hosting session and uses a hidden window;
Office creates its existing private desktop there. It does not switch the physical
desktop or attach to a user's interactive Office instance. The Windows token API
grants the user access to the inherited window station/desktop during launch.
The worker and its descendants are assigned to a kill-on-close job so host/session
cleanup terminates them. Profile loading defaults on; session-local `TEMP` and
`TMP` prevent the worker from depending on the app-pool account's temp directory.

## Deployment and acceptance checks

### Diagnose a failed launch

Session-open failures include a `diagnosticId`; the viewer displays it as a
reference. Match it to the server warning for the same request. Windows error 5
means access denied and does not by itself establish which permission failed.

For a failure at `worker-process-create`, `CreateProcessWithTokenW` did not create
the worker. Excel/Word/LibreOffice has not been launched. The server exception
records the native system error plus the host and caller SIDs, authentication
type, token type, impersonation level, session ID, elevation type and Windows
logon ID. It reports whether `SeImpersonatePrivilege` is present and enabled on
the actual host token, and whether the caller can read the worker executable.
That read check does not establish execute access or dependent-DLL access.
Token duplication and environment creation have already succeeded at this stage.

Use the caller's `LogonId` to find the matching event 4624 `TargetLogonId` and
inspect its LogonType. A successful logon proves authentication occurred; it does
not prove that Windows permits creating a local process under that token. Check:

- Caller read/execute/traversal permissions on the worker, runtime dependencies
  and deployment parents, not just the app-pool account's permissions.
- The logged host privilege state, token restrictions, and applicable Windows
  application-control/security policy. Error 1314 specifically reports a required
  privilege is unavailable; error 5 should not be treated as that diagnosis.
- Profile access when `LoadCallerProfile=true`. As a controlled comparison,
  test `false` only if the selected workload can run without the caller's profile;
  do not automatically retry with a different identity or profile policy.

For `application-open`, the worker connected successfully but Office discovery,
startup or document initialization failed. The server log retains individual
application names, executable paths and exceptions, including file-access
failures, rather than suggesting an Office installation is always missing.
Failures before token process creation continue to log their specific operation
(for example, token duplication, staging permissions or environment creation).

Server paths, SID/token diagnostics and full worker exception details remain in
server logs. Browser responses contain the failure stage, a safe explanation,
the native error where applicable, and the diagnostic reference. Access to the
server logs should be restricted to administrators.

Rebuild the Windows worker runtime along with the managed library: token-launched
workers use the new `--pipe` protocol option. Existing service launches retain
redirected stdin/stdout, and custom host launchers remain supported.

The managed regression suite always checks cross-process pipe communication,
argument quoting, worker startup errors and cancellation. Native caller-token
process creation is a separate, opt-in integration probe: generic hosted runners
can deny it with access denied (5) or privilege not held (1314), even when ordinary
process creation works. A skipped native probe is not a successful token-launch
validation.

On a provisioned Windows test host, require the native probe with:

```powershell
$env:REMOTEFRAME_REQUIRE_TOKEN_LAUNCH = '1'
pwsh -File packaging/Test-Managed.ps1
```

In this mode every native launch error, including 5 and 1314, fails the suite.
The probe verifies user SID, host session, argument quoting, private temporary
storage, pipe communication and exit. Production launch failures also always fail
explicitly; this test selection does not change runtime identity behavior.

Before deploying, test real authenticated IIS requests from two Windows accounts,
the intended profiles and Office installations, share delegation, saved-file
permissions, denied users, and app-pool recycle cleanup. A same-account process
probe does not establish IIS, alternate-account Office or remote-share acceptance.
