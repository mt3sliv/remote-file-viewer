# RemoteFrame.Office.Windows

Install this aggregate package for .NET 10 ASP.NET Core integration with Windows
x64 Office sessions:

```shell
dotnet add package RemoteFrame.Office.Windows --prerelease
```

It brings in `RemoteFrame.AspNetCore`, `RemoteFrame.Core`, `RemoteFrame.Rendering`
and the self-contained RemoteFrame worker and native input helper for Windows x64.
Starting with preview.11, this package does not include LibreOffice. It uses a
supported installed Excel, Word or LibreOffice application with matching x64
architecture. Keep `remote-frame/win-x64/components/worker` in deployments.

For bundled LibreOffice fallback, install
[RemoteFrame.Office.Windows.WithLibreOffice](https://www.nuget.org/packages/RemoteFrame.Office.Windows.WithLibreOffice)
instead. Preview.10 and earlier included LibreOffice in this package; upgrading
those applications to the convenience package preserves the previous behavior.

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
to force bundled LibreOffice when its optional package is deployed. Without that
bundle, disabling installed applications leaves no application candidates.
Installed Excel/Word require their own installation
and licensing and are not supplied by this package. The packaged worker/helper is
required in either mode.

Office runs in a session-private Windows desktop with a working document copy.
Save propagates changes to the configured server file, unless that file changed
externally. Closing discards unsaved edits. Application save/format dialogs remain
inside the viewer. Macro trust and Office security settings are not changed.

The worker includes its .NET runtime and component license notices. The separate
LibreOffice package supplies its engine, Visual C++ dependencies and notices.
This is a Windows x64 preview;
Linux/ARM64 native runtimes are not published. Managed text rendering is portable,
but installing this aggregate does not provide a Linux Office worker.

[Source](https://github.com/mt3sliv/remote-file-viewer) ·
[Release workflow](https://github.com/mt3sliv/remote-file-viewer/blob/dev/RemoteFrame/docs/Publishing.md)
