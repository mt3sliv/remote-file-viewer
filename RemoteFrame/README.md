# RemoteFrame

RemoteFrame is an ASP.NET Core middleware/endpoint library for server-side text rendering and a Windows x64 LibreOffice desktop preview. The browser receives PNG frames and sends input over WebSockets; documents remain on the server.

## Run the local demo

The demo consumes the `RemoteFrame.Office.Windows` NuGet package from `artifacts/packages`. The local preview packages have been built on this workspace. Run:

```powershell
dotnet run --project RemoteFrame/src/RemoteFrame.Server
```

From inside the RemoteFrame directory, use `dotnet run --project src/RemoteFrame.Server` instead. Open **http://127.0.0.1:5088**. There is no API key, login route, authentication cookie, or sign-in screen. The demo binds to loopback and rejects non-loopback clients.

All files in `samples` are copied into the demo's build/publish output and registered by filename, including `readme.txt`, `TestLibreOfficeWriterDocument.odt`, and `TestLibreOfficeCalcDocument.ods`. Choose a file and click **Open file**. Click inside Office to edit; **Save document** invokes LibreOffice Save. Edits persist to the output sample copy, leaving the repository's original samples unchanged. Text rendering is read-only. Closing a session discards unsaved edits.

## Use in your web app

Install `RemoteFrame.Office.Windows` from the local feed (version `0.2.0-preview.5`). It brings in the ASP.NET Core library and native runtime transitively. These preview packages are local artifacts, not published NuGet.org releases.

```csharp
using RemoteFrame.AspNetCore;

builder.Services.AddRemoteFrame(options =>
{
    options.RequireAuthorization = false; // local demo only
    options.EnableOffice = true;
    options.Files.Add("document", @"C:\Documents\example.docx");
});

var app = builder.Build();
app.UseWebSockets();
app.MapRemoteFrame();
app.Run();
```

Production applications should keep the library's default authorization requirement and configure their own authentication. See [the package guide](src/RemoteFrame.AspNetCore/PACKAGE.md). The source-development alternative is `-p:UseRemoteFramePackages=false`; it uses a project reference and does not automatically supply a native payload.

## Native runtime delivery

The Windows preview includes a full LibreOffice distribution, app-local Visual C++ runtime DLLs, a self-contained C# worker, native input helper, and notices. It does not search for or invoke an installed LibreOffice, run Python scripts, or download dependencies at application startup. Each Office session has a private Windows desktop, user profile and document working copy; a Windows job object terminates its Office processes when the worker exits.

[Maintainer build instructions](packaging/README.md) describe how to rebuild the local packages. Maintainer tooling and engine inputs are build-time requirements, not consumer-host requirements. Native payloads are large and remain under ignored `artifacts/` and NuGet/build output folders.

## Validation and limitations

Private-desktop capture, text input and saved-file persistence have been checked with the supplied Writer and Calc documents on this Windows machine. NuGet layout tests cover build/publish output, hidden files and extensionless resources. The demo package integration is tested separately.

This remains a Windows x64 preview. Linux/ARM64 workers and payloads are not implemented. A clean Windows machine, IIS/service hosting, all DOCX/XLSX/PPTX features, IME, clipboard and all menus/shortcuts still need acceptance testing. File dialogs run with the server account's filesystem access; private desktops isolate UI, not OS permissions. Simultaneous edits use optimistic content-hash conflict detection. Screenshots/OCR can copy visible content.


