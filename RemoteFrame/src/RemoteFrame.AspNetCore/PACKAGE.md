# RemoteFrame.AspNetCore

ASP.NET Core endpoints and an embedded browser viewer for .NET 10. Text rendering
works on Windows and Linux. Install `RemoteFrame.Office.Windows` instead when you
also need the complete Windows x64 Office runtime; it brings this package in
transitively. This package alone does not contain native Office binaries.

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
`ClaimTypes.NameIdentifier`. Session creation, configuration and WebSocket access
are bound to that identifier. Keep it stable and unique per user. Additional host
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
list entries are filtered through both callbacks when supplied. `/api/files` does
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
fallback. Set `PreferInstalledOfficeApplications = false` to force the bundle.
Installed Microsoft Office requires its own installation/licensing. The packaged
worker/helper is needed in either mode. Macro trust settings are not changed.

Sessions edit private working copies. Save propagates changes to the configured
server file; concurrent external modifications are detected before replacement.
Unsaved edits are discarded on close. Save/format prompts remain in the streamed
application. Native Office operation is currently Windows x64 only; Linux/ARM64
native payloads are not published. Managed text rendering is portable.

[Source](https://github.com/mt3sliv/remote-file-viewer) ·
[Windows runtime guide](https://github.com/mt3sliv/remote-file-viewer/blob/dev/RemoteFrame/packaging/OFFICE-WINDOWS-PACKAGE.md)
