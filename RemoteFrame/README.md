# RemoteFrame

GitHub build checks and tag-triggered NuGet publishing are described in
[Publishing.md](docs/Publishing.md). Package versions now default from
`Directory.Build.props`. The Windows runtime is distributed through size-bounded
dependency packages. Starting with preview.11, `RemoteFrame.Office.Windows`
includes the worker for installed applications; `RemoteFrame.Office.Windows.WithLibreOffice`
also includes the optional bundled LibreOffice engine.

RemoteFrame is an ASP.NET Core middleware/endpoint library for server-side text rendering and Windows x64 Office desktop sessions. The browser receives PNG frames and sends input over WebSockets; documents remain on the server.

## Run the local demo

The demo consumes `RemoteFrame.Office.Windows.WithLibreOffice`, which includes the middleware, worker and bundled engine. The configured development version is `0.3.0-preview.11`; first build it into `artifacts/packages` with `pwsh -File packaging/Build-Distribution.ps1`. This defaults to verified upstream engine binaries and rebuilds our worker without compiling LibreOffice. Run:

```powershell
dotnet run --project RemoteFrame/src/RemoteFrame.Server
```

From inside the RemoteFrame directory, use `dotnet run --project src/RemoteFrame.Server` instead. Open **http://127.0.0.1:5088**. There is no API key, login route, authentication cookie, or sign-in screen. The demo binds to loopback and rejects non-loopback clients.

All files in `samples` are copied into the demo's build/publish output and registered by filename, including `readme.txt`, `TestLibreOfficeWriterDocument.odt`, and `TestLibreOfficeCalcDocument.ods`. Choose a file link to open its viewer in a new tab. Click inside Office to edit; **Save document** invokes the selected application's Save command. Edits persist to the output sample copy, leaving the repository's original samples unchanged. Text rendering is read-only. Closing a session discards unsaved edits.

## Use in your web app

Install `RemoteFrame.Office.Windows` for supported installed Excel, Word or LibreOffice applications. Install `RemoteFrame.Office.Windows.WithLibreOffice` to include the optional LibreOffice fallback. Preview.10 and earlier included LibreOffice with `RemoteFrame.Office.Windows`; switch to the convenience package to preserve that behavior. Published versions are listed on [NuGet.org](https://www.nuget.org/packages/RemoteFrame.Office.Windows). The development version in `Directory.Build.props` may be newer than the latest published release.

```csharp
using RemoteFrame.AspNetCore;

builder.Services.AddRemoteFrame(options =>
{
    options.EnableOffice = true;
    options.Files.Add("document", @"C:\Documents\example.docx");
});

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.UseWebSockets();
app.MapRemoteFrame("/remote-frame");
app.Run();
```

Production applications should keep the library's default authorization requirement and configure their own authentication. See [the package guide](src/RemoteFrame.AspNetCore/PACKAGE.md). Use `-p:UseRemoteFramePackages=false` only for managed source development and tests. Office sessions always require the worker package. Consumers using the optional bundled engine do not need a system LibreOffice installation.

Starting with preview.10, `MapRemoteFrame("/remote-frame")` also serves the embedded viewer. Link to `/remote-frame/viewer?fileId=document` with `target="_blank" rel="noopener"`; the page loads FPS settings, POSTs a session and opens the WebSocket. The Server demo lists file links that open this same packaged page. Settings are collapsed by default; the viewer has no file-selection dropdown. No `wwwroot` assets need copying. The package binds defaults from the host configuration's `RemoteFrame` section, falling back to built-in values for missing settings. Use `AuthorizeFileAsync` and/or `ResolveFileAsync` for host-managed private catalogs, as explained in the package guide.

The demo loads its worker from `bin/<configuration>/net10.0/remote-frame/win-x64/components/worker/worker`, copied from the runtime NuGet; it does not use a separately published worker under `artifacts`. Release `0.3.0-preview.3` includes the modal-form pointer routing and title-bar close fixes. After rebuilding the distribution and building the demo with default package references, run `pwsh -File tests/TestDemoRuntime.ps1` to verify the selected package version and compare the deployed worker/helper hashes with the fresh build. Restart the server and reopen sessions after updating packages; existing sessions keep their original worker process. `pwsh -File tests/TestPointerInput.ps1` rebuilds the native helper and checks real Windows modal buttons and X on a disposable private desktop without executing Excel macros.

Release `0.3.0-preview.4` also pairs mouse releases with the original pressed control and follows the application's redirected mouse capture. Cancelling a gesture cancels tracking on those controls rather than clicking at the top-left corner of whichever window is now visible. The browser handles pointer cancellation, lost capture, window blur, hidden pages and mouse-button chords, and reconciles a missed release from subsequent pointer state. Reload the viewer after upgrading to load these browser changes. The Windows pointer test includes capture transferred from a child control to its parent and release over another button; it is not a validation of every MSForms/VBA control.

Windows package-based demo builds now run `tests/TestDemoRuntime.ps1` automatically using PowerShell 7. The check compares the deployed worker/helper and their manifest hashes with the selected restored runtime package, so a restored new version with stale output fails the build. The check also supports `-ServerOutputDirectory` for staged builds and honors `RemoteFramePackageVersion` overrides. Source-only builds skip this package check. If an old process remains running, stop it, rebuild the actual configuration you launch (for example Debug), restart, and reload the viewer; changing Release output does not update a running Debug server.

Release `0.3.0-preview.5` fixes off-screen Office dialogs and capture/input placement. A live Excel UserForm was at native coordinates (1920,280), outside a 1280×800 remote viewport, while `PrintWindow` drew it at the capture origin. Owned titled dialogs are now brought into the viewport without resizing them, and each window is rendered to a local surface and composited at its actual position. The Windows regression recreates the off-screen placement and a reset print origin, checks pixel positions, and clicks the dialog button and X. Oversized dialogs can still be clipped; increase capture dimensions if needed. The capture fix does not change macro trust settings; data-workflow verification remains separate.

Live browser checks with the supplied XLSM and installed Excel confirmed that X closes the actual UserForm, the worksheet button reopens it, and the form's command button hides it. See the [workbook compatibility notes](docs/compatibility/ExampleExcelWithUserForms.md) for the exact validation scope; DAO results and save/reopen preservation were not independently verified.

Release `0.3.0-preview.7` also fixes Excel extending an ordinary cell click toward the top of the sheet. On the inactive private desktop, Windows GetCursorPos fails even when the posted mouse message has the correct coordinates. The worker input helper adapts only the worker-owned Excel executable's GetCursorPos import (including delayed imports) to the latest remote screen position; it never moves the host cursor or switches desktops. The adapter restores the import during orderly disposal, and its callback stays mapped until Excel exits. Tests cover regular/delayed imports, negative coordinates, position updates and restoration.

Release `0.3.0-preview.6` resolves a reproduced mouse-release race on busy application threads. Tagged remote mouse events are resolved against native capture when the application removes them from its queue, with coordinates converted at that time. The helper tracks button transitions so an older queued move cannot reassert a button after release. Mouse-key state is updated on the target GUI thread; unrelated application messages and keyboard modifier handling are left alone. The native regression delays capture by 200 ms, queues a rapid release, checks stale moves after release, and verifies cancellation and intentional dragging. Both the worker and input helper must be upgraded together. Run `pwsh -File tests/TestPointerInput.ps1` with the Windows C++ build tools installed to rebuild the helper and run these tests.

## Native runtime delivery

The Windows preview includes a full LibreOffice distribution, app-local Visual C++ runtime DLLs, a self-contained C# worker, native input helper, and notices. By default, Office sessions try the host account's file association when supported, then compatible installed applications discovered through Windows App Paths, then bundled LibreOffice. Supported installed adapters are Excel (`.xls`, `.xlsx`, `.xlsm`, `.ods`), Word (`.doc`, `.docx`, `.odt`), and LibreOffice. Presentations currently use a supported LibreOffice adapter. Unsupported associations and applications with a different architecture from the worker are skipped; startup failures advance to the next candidate. No association command templates, shell execution, DDE or attachment to an existing Office process are used. Set `PreferInstalledOfficeApplications = false` to require the bundled engine.

Each Office session has a private Windows desktop and document working copy; a Windows job object terminates its Office processes when the worker exits. LibreOffice also gets a private user profile. Excel and Word use the host account's installed application settings and licensing, with new-instance launch switches. Applications still need the packaged worker/input helper even when an installed editor is selected. Text remains handled by the portable read-only renderer; arbitrary default applications and Linux desktop adapters are not implemented.

Selection uses Windows' executable association lookup and documented [Office new-instance launch switches](https://support.microsoft.com/en-us/office/lifecycle/command-line-switches-for-microsoft-office-products). Windows App Paths discovery checks both per-user and machine registrations in both registry views. An installed 32-bit application is skipped by the x64 worker because the keyboard helper must match the application's architecture.

Run `dotnet run --project tests/RemoteFrame.ApplicationTests` for selection/fallback checks. After publishing the worker with its native input helper beside it, `pwsh -File tests/TestInstalledExcel.ps1` checks installed Excel capture, resizing, input and save using a disposable generated workbook and a nonexistent bundled-engine path. Installed-app integration probes require an interactive Windows account with the relevant application installed.

On this Windows host, installed Excel passed the generated XLSX probe and installed Word passed the copied ODT edit/save probe, including the format confirmation. The Word probe uses `tests/TestWindowsWorker.ps1` with `-PreferInstalledApplications -ExpectedApplication Word -EditAndSave -ConfirmFormatDialog`; provide the published `-Worker` and an unused `-Engine` path to prove installed-only operation. Format confirmations remain visible to users; the worker does not automatically accept application dialogs. These checks do not validate every Office version, file format, macro, add-in or dialog.

[Maintainer build instructions](packaging/README.md) describe how to rebuild the local packages. Maintainer tooling and engine inputs are build-time requirements, not consumer-host requirements. Native payloads are large and remain under ignored `artifacts/` and NuGet/build output folders.

## Validation and limitations

Private-desktop capture, text input and saved-file persistence have been checked with the supplied Writer and Calc documents on this Windows machine. NuGet layout tests cover build/publish output, hidden files and extensionless resources. The demo package integration is tested separately.

This remains a Windows x64 preview. Linux/ARM64 workers and payloads are not implemented. A clean Windows machine, IIS/service hosting, all DOCX/XLSX/PPTX features, IME, clipboard and all menus/shortcuts still need acceptance testing. File dialogs run with the server account's filesystem access; private desktops isolate UI, not OS permissions. Simultaneous edits use optimistic content-hash conflict detection. Screenshots/OCR can copy visible content.



The [source-build pipeline](packaging/libreoffice/README.md) now provides the engine build and packaging handoff. Its baseline has no VBA patches. Real source compilation is pending the Cygwin build environment; the demo continues to use the tested prebuilt preview.

## Frame rate and resolution

Configure server defaults and ceilings through `RemoteFrameOptions` (the demo binds the `RemoteFrame` section in `appsettings.json`). Default capture and stream sizes are 1280 × 800 at a target ceiling of 5 FPS. The default server FPS limit is 30; absolute limits are 60 FPS, 3840 × 2160, and 8,294,400 pixels per frame. Capture dimensions must be at least 320 × 200; stream dimensions at least 160 × 100 and no larger than capture. Invalid requests receive HTTP 400 before opening a provider. Invalid server defaults fail startup.

Capture size changes the application viewport. Stream size downsamples that capture using nearest-neighbor sampling before PNG encoding. This allows lower bandwidth without changing application layout; matching aspect ratios avoids stretching. The browser offers FPS and capture dimensions plus automatic stream sizing from available viewport space and device pixel ratio (capped at 2), with debounced updates on resize. Pointer input includes the displayed PNG dimensions and is mapped back into the current capture viewport.

- `GET /api/frame-settings` returns defaults and server ceilings.
- `POST /api/sessions` accepts `{ "fileId": "readme.txt", "configuration": { "framesPerSecond": 10, "captureWidth": 1280, "captureHeight": 800, "streamWidth": 960, "streamHeight": 600 } }`. Omit `configuration` to use server defaults. The response includes `id` and accepted `configuration`.
- `PUT /api/sessions/{id}/configuration` accepts a complete configuration object and applies it without reopening the document. Updates share the capture/input lock and enforce the same owner, origin checks and limits as creation.

There is one capture/send operation in flight. Pacing subtracts capture and transmission time from the frame interval and does not queue catch-up captures. Actual FPS depends on rendering, PNG compression, network and browser speed; the target is a ceiling, not a performance guarantee. Browser decoding keeps one decode and the newest pending frame, replacing intermediate pending frames to bound memory. Canvas dimensions change only when the frame size changes. This bounds the browser's application decode queue; it does not provide end-to-end network backpressure.

Repeated identical configuration updates are ignored; FPS-only updates change stream pacing without reconfiguring the renderer. Stream-size-only updates avoid moving the Office window. Text sessions cache their PNG until scrolling or dimensions change, and PNG chunk checksums use a lookup table. These changes reduce avoidable rendering work without changing the PNG/input APIs or exposing document bytes. The Office worker payload must be rebuilt to pick up its resize optimization. The lag review's user request was recoverable, but its assistant findings were stored as unresolved content references; these fixes follow direct source inspection. Live Office latency and Linux acceptance still require measurements in those environments.

Text rendering and managed resizing remain portable to Windows and Linux. The existing Office worker remains Windows-only; no Linux Office payload exists in this repository. Office file working copies, private profiles, save conflict detection, file allowlists and owner checks are preserved. Custom `IRenderSession` providers must implement `ConfigureAsync`; unsupported providers are rejected explicitly. Existing native payloads must be rebuilt with the updated worker; the server checks its configuration acknowledgement.

Run source checks with `dotnet build RemoteFrame.sln -p:UseRemoteFramePackages=false`, `dotnet run --project tests/RemoteFrame.StreamingTests`, `dotnet run --project tests/RemoteFrame.RuntimeTests`, and `node tests/RemoteFrame.StreamingTests/viewer.test.cjs`. The streaming checks exercise real HTTP/WebSocket endpoints plus managed text resizing; live Office and Linux acceptance checks require their respective environments.

The prior frame-rate design conversation was accessible, but its assistant answer contained only an unresolved content-reference placeholder. These defaults and API choices therefore follow the requirements in this task rather than a recovered detailed proposal.

### Rebuilding the bundled distribution

Choose the engine input independently of whether the demo uses NuGet packages or project references:

```powershell
# Normal packaging: official binaries plus a rebuilt RemoteFrame worker.
pwsh -File packaging/Build-Distribution.ps1 -OfficeEngine Binary

# Engine/Basic/VBA changes: compile the engine and require upstream checks to pass.
pwsh -File packaging/Build-Distribution.ps1 -OfficeEngine Source

# Repackage an already successful source build.
pwsh -File packaging/Build-Distribution.ps1 -OfficeEngine Source -SourceBuildReceipt C:/build/source-build.json
```

Binary mode is the default and needs no Cygwin/Clang/LibreOffice build. `packaging/libreoffice/binary.lock.json` pins the official MSI URL/checksum. It downloads once, extracts without installing Office, and verifies the archive and full extracted inventory before reuse. Both modes compile our worker/input helper, include licenses/CRT, run bundled-only Writer/Calc edit-save checks and create hash manifests before packaging. Binary releases default from `Directory.Build.props`; source releases default to `0.3.0-source.2`. Managed packages follow the selected distribution version. Source mode retains strict recipe/receipt validation and patch provenance; binary provenance never claims a successful local upstream source build.

Then restore/build the demo normally; the native dependency copies its complete payload into build and publish output. To consume a source release, pass `-p:RemoteFramePackageVersion=0.3.0-source.2`. `UseRemoteFramePackages=false` remains a separate managed development switch and does not select or compile an engine. Consumers install the aggregate package only. Existing native/aggregate package versions are never overwritten; choose new `-PackageVersion` and `-ManagedPackageVersion` values for later releases.

The current source attempt compiled and linked the engine but failed the upstream `VclFiltersTest::testCVEs` JPEG-load assertion. Its checkout/output are preserved; it has no successful receipt and cannot be released as a tested source build. The temporary Defender exception was removed and verified. No VBA extension patches have been implemented. Binary packaging is independent of the source-build and VBA feasibility work.

The Windows x64 binary distribution `0.3.0-preview.2` has been built locally. Bundled-only Writer/Calc capture, resizing, editing and saving passed, followed by an unchanged-engine inventory check. A separate NuGet consumer passed authentication/isolation/streaming/live-configuration checks. The demo built with default package references and streamed all sample files with installed-app preference disabled. XLSM preview was checked; VBA execution/extensions were not. Clean-host/service and Linux/ARM64 acceptance remain outstanding.
