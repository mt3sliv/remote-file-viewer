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

Rebuild the Windows worker runtime along with the managed library: token-launched
workers use the new `--pipe` protocol option. Existing service launches retain
redirected stdin/stdout, and custom host launchers remain supported.

The managed regression suite includes a token-launch child-process probe. It
skips native launch only for error 1314 (missing privilege) on ordinary developer
accounts. Set `REMOTEFRAME_REQUIRE_TOKEN_LAUNCH=1` on a provisioned Windows test
host to make that condition fail the check. The probe verifies user SID, host
session, argument quoting, private temporary storage, pipe communication and exit.

Before deploying, test real authenticated IIS requests from two Windows accounts,
the intended profiles and Office installations, share delegation, saved-file
permissions, denied users, and app-pool recycle cleanup. A same-account process
probe does not establish IIS, alternate-account Office or remote-share acceptance.
