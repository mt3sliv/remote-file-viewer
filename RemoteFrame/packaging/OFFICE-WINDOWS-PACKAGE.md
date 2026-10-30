# RemoteFrame.Office.Windows

Install this aggregate package for .NET 10 ASP.NET Core integration with Windows
x64 Office sessions:

```shell
dotnet add package RemoteFrame.Office.Windows --prerelease
```

It brings in `RemoteFrame.AspNetCore`, `RemoteFrame.Core`, `RemoteFrame.Rendering`
and the complete Windows x64 runtime. Consumers do not need to build LibreOffice
or separately install it. The runtime is split into dependency packages to stay
within NuGet.org size limits; restore/build/publish assemble the original tree
under `remote-frame/win-x64` automatically. Keep that tree in deployments.

## Integrate with your application

Configure your application's authentication and authorization services first:

```csharp
using RemoteFrame.AspNetCore;

builder.Services.AddRemoteFrame(options =>
{
    options.EnableOffice = true;
    options.Files.Add("report", @"C:\Documents\report.xlsx");
});

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.UseWebSockets();
app.MapRemoteFrame("/remote-frame");
app.Run();
```

Starting with preview.10, open `/remote-frame/viewer?fileId=report` for the embedded
viewer, FPS/capture controls, session creation and WebSocket connection. A host
file-list button can link there with `target="_blank" rel="noopener"`. The page
uses the same authentication and authorization as the API. Earlier versions,
including preview.9, require a host-provided browser client.

All authenticated users can access configured files unless the host applies
additional file authorization. Preview.10 adds per-request `AuthorizeFileAsync`
and `ResolveFileAsync` hooks for private catalogs. Browser input contains opaque
file IDs, never server paths. See the
[ASP.NET Core package guide](https://github.com/mt3sliv/remote-file-viewer/blob/dev/RemoteFrame/src/RemoteFrame.AspNetCore/PACKAGE.md)
for full configuration and API details.

## Application selection and saving

The worker prefers supported installed applications: the host account's supported
file association, compatible Windows App Paths applications, then bundled
LibreOffice. Excel, Word and LibreOffice adapters are supported; arbitrary
associated executables are not. Set `PreferInstalledOfficeApplications = false`
to force bundled LibreOffice. Installed Excel/Word require their own installation
and licensing and are not supplied by this package. The packaged worker/helper is
required in either mode.

Office runs in a session-private Windows desktop with a working document copy.
Save propagates changes to the configured server file, unless that file changed
externally. Closing discards unsaved edits. Application save/format dialogs remain
inside the viewer. Macro trust and Office security settings are not changed.

The bundle uses a pinned official LibreOffice binary distribution, a self-contained
RemoteFrame worker and native input helper, Visual C++ runtime dependencies and
component notices. Full licenses are included. This is a Windows x64 preview;
Linux/ARM64 native runtimes are not published. Managed text rendering is portable,
but installing this aggregate does not provide a Linux Office worker.

[Source](https://github.com/mt3sliv/remote-file-viewer) ·
[Release workflow](https://github.com/mt3sliv/remote-file-viewer/blob/dev/RemoteFrame/docs/Publishing.md)
