# RemoteFrame.AspNetCore

ASP.NET Core endpoints and an embedded browser viewer for .NET 10. Text rendering
works on Windows and Linux. Install `RemoteFrame.Office.Windows` instead when you
also need the Windows x64 worker for supported installed Office applications.
Use `RemoteFrame.Office.Windows.WithLibreOffice` to additionally bundle LibreOffice.
Both bring this package in transitively. This package alone contains no native binaries.

```shell
dotnet add package RemoteFrame.AspNetCore --prerelease
```

## Register and map

Configure your application's authentication and authorization services, then:

```csharp
using RemoteFrame.AspNetCore;

builder.Services.AddRemoteFrame(options =>
{
    options.Files.Add("notes", "/srv/documents/notes.txt");
    // options.EnableOffice = true; // requires the Windows runtime package
});

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.UseWebSockets();
app.MapRemoteFrame("/remote-frame");
app.Run();
```

By default, every RemoteFrame route requires an authenticated user with a nonempty
`ClaimTypes.NameIdentifier`, or an authenticated Windows identity with a user SID.
An explicit `NameIdentifier` takes precedence; otherwise Windows sessions use
`windows:<SID>` as their owner identifier. A generic SID claim alone does not
enable this fallback. The host must still configure Windows authentication;
RemoteFrame does not enable an authentication scheme or sign users in.
Session creation, configuration and WebSocket access are bound to the same
identifier. Keep it stable and unique per user. Additional host
endpoint policies can be applied to the returned route group. Only a local,
loopback-only demo should set `RequireAuthorization = false`.

## Open the packaged viewer (preview.10 and later)

The viewer is embedded in the ASP.NET Core assembly. It needs no copied `wwwroot`
files, static-file middleware, Razor setup or separate frontend package.
Set `options.EnableViewer = false` to keep only the API/WebSocket routes when
using a custom client or host-owned viewer routes.

A host application can list authorized files and provide an ordinary link:

```html
<a href="/remote-frame/viewer?fileId=notes" target="_blank" rel="noopener">Open viewer</a>
```

URL-encode the file ID when generating links. Include the application's PathBase
in the link when mounted under a subpath. For example, `/portal/remote-frame/viewer?fileId=notes`
works with `UsePathBase("/portal")` and `MapRemoteFrame("/remote-frame")`.

The page shows the selected opaque ID; viewer settings are collapsed by default.
Its script fetches
server defaults, POSTs to `/api/sessions`, then connects to `/ws/{id}`. GET of the
page itself never starts a renderer. Requests and sockets use the same route
prefix, PathBase, origin and browser authentication. The target FPS is a limit;
actual frame rate also depends on capture, encoding and network cost.

The built-in page uses same-origin browser authentication, typically cookies.
A Bearer-header-only application needs an authenticated browser navigation/session
flow; the viewer does not accept access tokens in URLs.

The viewer never contains a file selector or enumerates the file catalog. It opens
only the ID supplied by the host link. Opening `/remote-frame/viewer` without an ID
instructs the user to return to the application's file list. Close/tab unload releases the session; disconnect cleanup
also runs on the server. Save is enabled from the renderer's `CanSave` capability,
not from the ID or its extension. The existing REST/WebSocket APIs remain usable
by custom clients. Preview.9 and earlier do not include the viewer page.

## Authorize and resolve files on the server

Configured `options.Files` entries are shared across authenticated users unless
your host restricts them. Hiding a button in a file list is not authorization.
Preview.10 adds a callback that filters configured listings and checks every open:

```csharp
options.Files.Add("finance-report", @"C:\Documents\finance.xlsx");
options.AuthorizeFileAsync = (context, id, cancellationToken) =>
    ValueTask.FromResult(id != "finance-report" || context.User.IsInRole("Finance"));
```

For batch filtering of `GET /api/files`, set `AuthorizeFilesAsync`:

```csharp
options.AuthorizeFilesAsync = (context, candidateIds, cancellationToken) =>
    ValueTask.FromResult<IReadOnlyCollection<string>>(
        candidateIds.Where(id => id != "finance-report" ||
            context.User.IsInRole("Finance")).ToArray());
```

The callback runs once per authorized listing request and receives a read-only,
sorted snapshot of configured IDs and the request cancellation token. It can use
an asynchronous database permission lookup to return the allowed IDs in a batch.
Only returned IDs present in the candidate snapshot are considered, using ordinal,
case-sensitive matching. Unknown IDs and duplicates cannot add list entries;
the catalog's sort order is preserved. Return an empty collection to list nothing.
A null result or callback failure fails the request without returning a file list.

Batch filtering runs before the existing per-file authorization and resolution
checks. When both authorization callbacks are configured, an entry must pass both.
Leaving `AuthorizeFilesAsync` unset preserves the per-file listing behavior.
This callback controls listing only: it does not restrict direct session opens.
Use `AuthorizeFileAsync` and/or an access-checking `ResolveFileAsync` to enforce
permission when opening a file, including files omitted from the listing.

For a database/storage-backed catalog, set `ResolveFileAsync` instead of putting
all paths into configuration:

```csharp
options.ResolveFileAsync = async (context, fileId, cancellationToken) =>
{
    var catalog = context.RequestServices.GetRequiredService<IDocumentCatalog>();
    return await catalog.FindAuthorizedPathAsync(context.User, fileId, cancellationToken);
};
```

`IDocumentCatalog` is a host-defined service, not a RemoteFrame interface. Its method
must check the current user's access and return a trusted server path, or null
for inaccessible/unknown IDs. Do not treat the ID as a path or blindly append it
to a storage directory. The resolver supersedes the static path lookup; the optional
`AuthorizeFileAsync` check runs first. Denied/unknown opens return 404. Configured
list entries are filtered through authorization and resolution callbacks when supplied. `/api/files` does
not enumerate a dynamic catalog; your application owns that list and links directly
to the viewer. No disk paths are included in session responses or viewer markup.

Authorizing an Office session permits its editing/saving behavior. `CanSave` is a
viewer capability hint, not a read-only authorization boundary. Grant Office
access only where those operations are permitted by your application's policy.

## Routes relative to MapRemoteFrame's prefix

| Method | Route | Purpose |
|---|---|---|
| GET | `/viewer?fileId=notes` | Embedded viewer; selected file opens through client POST |
| GET | `/viewer.js`, `/viewer.css` | Embedded assets with the same authorization |
| GET | `/api/files` | Configured file IDs filtered for the caller |
| GET | `/api/frame-settings` | Default configuration and server limits |
| POST | `/api/sessions` | Open `{ "fileId": "notes", "configuration": { ... } }`; configuration is optional |
| PUT | `/api/sessions/{id}/configuration` | Change the caller's FPS/capture/stream settings |
| DELETE | `/api/sessions/{id}` | Close the caller's session |
| WebSocket | `/ws/{id}` | PNG frames and JSON input, including the Save shortcut |

An open response contains `id`, `configuration`, and (from preview.10) `canSave`.
Only server-side configuration/resolution supplies file paths. Text is read-only;
Office may be editable. REST mutations and WebSockets reject cross-origin browser
requests. Cookie-authenticated hosts should also apply their CSRF validation.
When the host registers `IAntiforgery`, the viewer obtains tokens and sends the
configured header on state-changing REST requests. The host still chooses and
enforces the validation policy; registering antiforgery alone does not enforce it.

`AddRemoteFrame()` automatically binds the `RemoteFrame` section of the host's
configuration (including appsettings.json, environment overrides and other host
configuration sources). Missing settings retain built-in defaults. An explicit
`AddRemoteFrame(options => ...)` callback runs after binding and can override them.
For example:

```json
{
  "RemoteFrame": {
    "DefaultFrameConfiguration": {
      "FramesPerSecond": 20,
      "CaptureWidth": 1280,
      "CaptureHeight": 800,
      "StreamWidth": 960,
      "StreamHeight": 600
    },
    "FitStreamToViewport": false,
    "MaxFramesPerSecond": 30
  }
}
```

Library fallbacks are 5 FPS, 1280x800 capture/stream, fitting disabled, a 30 FPS server limit and an
8,294,400-pixel ceiling. `DefaultFrameConfiguration`, `MaxFramesPerSecond`,
`MaxCaptureWidth`, `MaxCaptureHeight` and `MaxPixels` customize supported values.
The demo uses different defaults. Connections are bounded to ten minutes; reopen
when disconnected. File/session quotas and expiry are enforced server-side.

## Office behavior

With `EnableOffice = true` and the Windows x64 runtime, the worker prefers supported
installed Excel/Word/LibreOffice applications before the bundled LibreOffice
fallback when its optional package is deployed. Set
`PreferInstalledOfficeApplications = false` to force the bundle; without that
package, this setting leaves no application candidates.
Installed Microsoft Office requires its own installation/licensing. The packaged
worker/helper is needed in either mode. Macro trust settings are not changed.

Sessions edit private working copies. Save propagates changes to the configured
server file; concurrent external modifications are detected before replacement.
Unsaved edits are discarded on close. Save/format prompts remain in the streamed
application. Native Office operation is currently Windows x64 only; Linux/ARM64
native payloads are not published. Managed text rendering is portable.

[Source](https://github.com/mt3sliv/remote-file-viewer) ·
[Windows runtime guide](https://github.com/mt3sliv/remote-file-viewer/blob/dev/RemoteFrame/packaging/OFFICE-WINDOWS-PACKAGE.md)
# Shared files and identities

File transfer and Office process identity are configured separately:

```json
{
  "RemoteFrame": {
    "FileAccess": {
      "StageLocally": true,
      "DownloadIdentity": "Caller",
      "SaveBackIdentity": "Caller"
    },
    "ApplicationSession": { "ProcessIdentity": "Service" }
  }
}
```

All identities default to `Service`; `StageLocally` defaults to `true`. Register
share files using UNC paths in `Files` or `ResolveFileAsync`. Each session stages
the source to a unique local directory before opening its provider. Office keeps
its existing separate application working copy. Saved changes are copied back
under `SaveBackIdentity`, with content-hash conflict checks and a temporary file
beside the source followed by replacement. These checks are optimistic; they do
not lock out other editors between the final check and replacement. The selected
save identity needs read, create, replace and delete permissions in the source
directory. Closing a session cleans up staging and does not invoke Office Save.
Unsaved application edits remain unsaved.

If final save-back or renderer cleanup fails, the staging directory is retained
in place for recovery. Server error logs include its path and the source path;
the library does not expose these paths to the browser or overwrite a conflicting
source file. An administrator can retrieve the saved staged document and remove
the retained directory after recovery. Retained directories are not automatically
purged by RemoteFrame and remain subject to the host's temporary-storage policy.
Session cleanup failures are logged individually so expiration and shutdown can
continue cleaning other sessions.

`Caller` file access requires an authenticated `WindowsIdentity` on the opening
HTTP request. Its token is duplicated for the session and disposed on closure.
Cookie/JWT identity alone does not provide Windows credentials. Remote SMB access
also requires suitable Windows delegation. Missing credentials or denied access
fail without retrying as the service. File authorization callbacks still apply.
Service operations explicitly revert ambient Windows impersonation, including
when the host invokes RemoteFrame from inside a caller impersonation scope.

`ProcessIdentity = Caller` uses the built-in Windows token launcher unless
`options.ApplicationSession.LaunchCallerWorkerAsync` is supplied. The built-in
launcher duplicates the authenticated Windows token into a primary token and calls
`CreateProcessWithTokenW`. It requires `SeImpersonatePrivilege` on the host account;
missing privileges, unusable tokens, denied access or startup failure fail explicitly.
It verifies the worker's SID before resuming it and verifies the connecting process
ID on a unique named pipe restricted to the host and caller. No password is stored
and no service credentials or environment block are passed to the caller process.

The caller receives Modify access to its staging directory. `FileAccess.StagingDirectory`
can select a host-managed staging root; otherwise the host temporary directory is
used. The worker gets session-local `TEMP` and `TMP` directories, and its process
tree is assigned to a kill-on-close job. `ApplicationSession.LoadCallerProfile`
defaults to `true` to load the caller's registry profile for Office settings.
Set it to `false` only when the deployment does not require those settings.
Worker, engine and installed-application files must already be readable/executable
by the caller; the launcher does not modify deployment or installation ACLs.

The built-in launcher requires the updated worker with `--pipe` support. Rebuild
and deploy both managed packages and the worker runtime together. Old workers
fail startup rather than running as the service. The Windows token API has a
1024-character command-line limit; overly long deployment/document paths are
rejected. Caller processes stay in the hosting session, not the user's interactive
desktop. See [CreateProcessWithTokenW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createprocesswithtokenw).

The optional host override receives the opening `HttpContext`, configured
`ProcessStartInfo`, and cancellation token. It must return a caller-owned process
with redirected stdin/stdout and preserve the arguments. It remains responsible
for staging access, profile, environment and process-lifetime handling. The library
checks its returned worker SID as well. Caller process mode supports Office providers only.

With `StageLocally = false`, only service identities are accepted and the original
provider path is used. Office still makes its own working copy. Text rendering is
read-only in either mode. Live delegated-share and alternate-account Office
acceptance checks must be performed in the deployment environment.
