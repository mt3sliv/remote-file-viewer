# Remote File Viewer

Remote File Viewer is a .NET repository for viewing and interacting with server-hosted documents in a browser. Its main project, **RemoteFrame**, renders documents on the server, streams PNG frames over WebSockets, and forwards browser input to the rendering session. The viewer receives images rather than the original document file.

The repository also includes an independent **Excel DAO COM bridge** sample for connecting Excel VBA to a local Microsoft Access database.

## What RemoteFrame provides

- An ASP.NET Core integration library with an embedded browser viewer and HTTP/WebSocket endpoints.
- Portable, read-only text rendering, plus Windows x64 desktop sessions for supported Office documents.
- Support for installed Excel, Word, and LibreOffice, with an optional bundled LibreOffice runtime for Writer, Calc, and Impress.
- Per-session Office working copies and private Windows desktops, browser keyboard/pointer input, document saving, and optimistic conflict detection.
- Configurable frame rate, capture resolution, stream resolution, and live session resizing.
- Host-managed authentication, file authorization and resolution, and session ownership checks.
- Separate options for source-file download, save-back, and Office process identities, including Windows caller impersonation for file access.

Typical Office formats include `.doc`, `.docx`, `.odt`, `.xls`, `.xlsx`, `.xlsm`, `.ods`, `.ppt`, `.pptx`, and `.odp`. Actual compatibility depends on the selected application and document features. Macro-enabled files are not a guarantee of VBA compatibility.

## Repository projects

| Project or directory | Purpose |
| --- | --- |
| [RemoteFrame.Core](RemoteFrame/src/RemoteFrame.Core/) | Shared rendering contracts, input/frame models, frame configuration, and pacing. |
| [RemoteFrame.Rendering](RemoteFrame/src/RemoteFrame.Rendering/) | Read-only text renderer and managed PNG encoding utilities. |
| [RemoteFrame.AspNetCore](RemoteFrame/src/RemoteFrame.AspNetCore/) | Application integration, embedded viewer, session lifecycle, authorization hooks, runtime validation, and worker communication. |
| [RemoteFrame.Office.Worker](RemoteFrame/src/RemoteFrame.Office.Worker/) | Windows Office application discovery, private-desktop capture, native input integration, and working-copy persistence. |
| [RemoteFrame.Server](RemoteFrame/src/RemoteFrame.Server/) | Local demonstration web app that lists sample files and opens the packaged viewer. |
| [Runtime packaging](RemoteFrame/packaging/) | Worker and LibreOffice runtime packages, Windows aggregate packages, build scripts, integrity manifests, and release tooling. |
| [RemoteFrame.RuntimeCheck](RemoteFrame/tools/RemoteFrame.RuntimeCheck/) | Maintainer tool for creating and validating runtime inventory manifests. |
| [RemoteFrame.LinkWrapper](RemoteFrame/tools/RemoteFrame.LinkWrapper/) | Maintainer adapter for long MSVC linker response-file lines during engine builds. |
| [RemoteFrame tests](RemoteFrame/tests/) | Managed streaming, identity, runtime and application-selection checks; package consumer smoke tests; Windows pointer and Office integration probes. |
| [ExcelDaoBridge](ExcelDaoComSample/ExcelDaoComSample/) | Separate .NET Framework 4.8 COM DLL and VBA example returning real `DAO.Database` objects backed by Access files. |
| [Document samples](RemoteFrame/samples/) | Text, Writer, Calc, and Excel samples used by the demo and compatibility checks. |

The [RemoteFrame solution](RemoteFrame/RemoteFrame.sln) contains the main library, server, worker, tools, and several test projects. The Excel DAO sample is built separately.

## Get started

### Run the local Office demo

Use Windows x64 with the .NET SDK selected by [global.json](RemoteFrame/global.json), PowerShell 7, and Visual Studio C++ build tools for the native input helper. The demo consumes locally built runtime packages by default. From the repository root:

```powershell
cd RemoteFrame
pwsh -File packaging/Build-Distribution.ps1
dotnet run --project src/RemoteFrame.Server
```

Open **http://127.0.0.1:5088** and choose a sample file. Each link opens a viewer in a new tab. The demo is anonymous, binds to loopback, and rejects non-loopback clients. Sample edits are saved to copies in the demo output directory, leaving repository samples unchanged.

The distribution build uses verified upstream LibreOffice binaries by default and rebuilds the RemoteFrame worker and input helper. It downloads and packages a substantial native runtime on the first build. See the [RemoteFrame guide](RemoteFrame/README.md) and [packaging instructions](RemoteFrame/packaging/README.md) for prerequisites, installed-Office-only builds, and engine source builds.

### Integrate into an ASP.NET Core app

Choose the package for the functionality you need:

| Package | Includes |
| --- | --- |
| `RemoteFrame.AspNetCore` | Managed integration and embedded viewer; suitable for portable text rendering. |
| `RemoteFrame.Office.Windows` | Managed integration and Windows x64 worker for supported installed Office applications. |
| `RemoteFrame.Office.Windows.WithLibreOffice` | Windows integration and worker plus the optional bundled LibreOffice engine. |

Register files using opaque IDs, call `AddRemoteFrame`, enable WebSockets, and map the endpoints with `MapRemoteFrame`. The viewer is embedded in the library, so no frontend asset copying is required. Configure the host application's authentication and keep the default authorization requirement for production use.

See the [ASP.NET Core package guide](RemoteFrame/src/RemoteFrame.AspNetCore/PACKAGE.md) for registration examples, viewer links, authorization callbacks, private file catalogs, and shared-file identity options.

### Use the Excel DAO sample

The DAO sample runs inside Windows desktop Excel and is independent of the browser viewer. It requires .NET Framework 4.8 build tools and a compatible Microsoft Access Database Engine installation. Build and register the COM DLL for your Office bitness, then use `CreateObject("ExcelDaoBridge.Factory")` from VBA.

Follow the [Excel DAO sample README](ExcelDaoComSample/ExcelDaoComSample/README.md) for build, registration, VBA usage, and cleanup instructions. Registration requires administrator permissions.

## Development and validation

For managed source development, bypass the demo's runtime package references:

```powershell
cd RemoteFrame
dotnet build RemoteFrame.sln -p:UseRemoteFramePackages=false
dotnet run --project tests/RemoteFrame.StreamingTests
dotnet run --project tests/RemoteFrame.RuntimeTests
dotnet run --project tests/RemoteFrame.ApplicationTests
node tests/RemoteFrame.StreamingTests/viewer.test.cjs
node tests/RemoteFrame.StreamingTests/viewer-lifecycle.test.cjs
```

Use the SDK version pinned in `RemoteFrame/global.json`. Browser script tests require Node.js. Managed source builds do not create the native Office payload; Office sessions still need a packaged worker. Windows native input, installed Office, engine, and packaging checks have additional prerequisites described in the project guides and scripts under `RemoteFrame/tests`.

## Platform support and boundaries

RemoteFrame is a preview. Managed text rendering supports Windows and Linux; the Office worker and distributed native payloads currently target **Windows x64**. Linux Office workers and ARM64 payloads are not implemented. Clean-machine, IIS/service hosting, and broader Office compatibility acceptance remain ongoing work.

Private desktops isolate application UI, but they are not an operating-system security sandbox. Office applications and file dialogs retain the permissions of their process identity. Configure file authorization and filesystem permissions accordingly. Streaming images also cannot prevent screenshots or OCR of visible content.

Source download and save-back default to the service account. Caller file access requires Windows credentials and, for remote shares, suitable delegation. Running Office as the caller additionally requires a host-provided worker launcher and appropriate permissions; see [shared files and identities](RemoteFrame/src/RemoteFrame.AspNetCore/PACKAGE.md#shared-files-and-identities).

## Documentation

- [RemoteFrame overview and detailed setup](RemoteFrame/README.md)
- [ASP.NET Core integration and configuration](RemoteFrame/src/RemoteFrame.AspNetCore/PACKAGE.md)
- [Browser viewer behavior](RemoteFrame/docs/Viewer.md)
- [Runtime packaging and engine builds](RemoteFrame/packaging/README.md)
- [Release and NuGet publishing](RemoteFrame/docs/Publishing.md)
- [Excel workbook compatibility notes](RemoteFrame/docs/compatibility/ExampleExcelWithUserForms.md)
- [Excel DAO COM bridge](ExcelDaoComSample/ExcelDaoComSample/README.md)

## License

Repository code is licensed under the [MIT License](LICENSE). Bundled LibreOffice and other third-party runtime components retain their own licenses and notices, which are included by the runtime packaging process.
