# Package-owned Office runtime

The native payload is built by RemoteFrame maintainers, not by consuming web applications. The Windows x64 artifacts are built locally under ignored artifacts/; the repository tracks their worker source and packaging recipes. Linux/ARM64 artifacts are not implemented. This project packages a completed runtime tree; it does not turn the LibreOffice source repository into a working runtime by itself.

Each supported target has a runtime package: `RemoteFrame.Office.Runtime.win-x64`, `win-arm64`, `linux-x64`, or `linux-arm64`. The Windows preview aggregate package RemoteFrame.Office.Windows depends on the ASP.NET Core library and win-x64 runtime. Consumers install that one package. No packages have been published externally.

## Staging contract

Build a pinned LibreOffice source revision and our native worker for the target platform. Stage all required binaries, resources, configuration, fonts selected for redistribution, and required notices into one directory. Preserve the engine's directory layout. Resolve symlinks into regular files when staging; the validator rejects linked files/directories. Maintain an inventory of redistribution licenses for native dependencies.

Add `runtime.json` at the root with the following fields (hash values below are descriptive placeholders, not a publishable runtime):

```json
{
  "schemaVersion": 1,
  "runtimeIdentifier": "win-x64",
  "version": "PINNED-LIBREOFFICE-VERSION",
  "sourceRevision": "FULL-UPSTREAM-COMMIT",
  "engine": "office/program/soffice.exe",
  "worker": "worker/RemoteFrame.Office.Worker.exe",
  "licenses": ["notices/THIRD-PARTY-NOTICES.txt"],
  "files": {
    "office/program/soffice.exe": "SHA256-OF-FILE",
    "worker/RemoteFrame.Office.Worker.exe": "SHA256-OF-FILE",
    "notices/THIRD-PARTY-NOTICES.txt": "SHA256-OF-FILE"
  }
}
```

List every runtime file except `runtime.json` in `files`, using relative paths and SHA-256 hashes. Include every license/notice path in `licenses`. Manifest hashes detect damaged or inconsistent artifacts; authenticity still relies on the trusted package distribution and signing process.

## Build and verify

```powershell
dotnet pack RemoteFrame/packaging/RemoteFrame.Office.Runtime.csproj -c Release -o RemoteFrame/artifacts/packages -p:OfficeRuntimeIdentifier=win-x64 -p:OfficePayloadRoot=C:/build/staged-runtime
```

Packing validates the platform, required files, path containment, license inventory and hashes. Empty/incomplete payloads fail packaging. Assets are copied transitively into `remote-frame/<rid>/` in both build and publish output, preserving subdirectories. `BundledOfficeRuntime.Load()` resolves only this package-owned location; it never searches PATH, uses a system LibreOffice installation, or downloads a runtime.

## Release gates still outstanding

- Build actual Windows/Linux payloads and implement private-session capture/input workers.
- Validate the complete transitive native dependency set on clean supported OS images. The baseline OS/architecture/libc must be documented; bundling cannot make binaries independent of the operating system.
- Verify Linux execution permissions survive NuGet/build/publish; restore required executable bits internally if necessary.
- Verify application startup, menus/dialogs, input, save persistence, crashes, session isolation and cleanup without interactive server login.
- Complete upstream and third-party redistribution review, notices and corresponding-source delivery where required.
- Verify final aggregate package installation and clean-host acceptance before claiming Office support.

## Packaging tests

Run `pwsh -File RemoteFrame/tests/TestRuntimePackaging.ps1`. It creates a clearly marked text-only fixture package under ignored `artifacts/`, verifies integrity rejection cases, and installs/publishes a standalone NuGet consumer. Fixture binaries are never executed. These checks have passed on Windows; they do not validate a real Office runtime or Linux execution.

## Build the real Windows preview

Run `BuildWindowsRuntime.ps1` with `-LibreOfficeDirectory` pointing to the complete upstream Windows distribution, `-VcRedistDirectory` pointing to Visual Studio's x64 `Microsoft.VC143.CRT` redistribution directory, and `-MicrosoftNoticesPath` pointing to the applicable notices. The input helper build currently uses the Visual Studio 2022 Community x64 C++ toolchain. These inputs are needed only on the maintainer's build machine. The script publishes a self-contained .NET worker, compiles the input helper, copies all Office resources and CRT DLLs, includes notices, generates the full SHA-256 inventory, and builds the aggregate package into `artifacts/packages`.

The Windows engine input on this workspace is LibreOffice 26.2.5.2; its upstream build revision is read from `program/version.ini`. This stages an existing upstream binary distribution rather than compiling LibreOffice itself. `-UseStagedOffice` reuses an already-copied engine after checking the launcher hash; use a fresh staging directory when changing engine versions. This is for local preview validation, with external release and clean-host acceptance still pending.

The demo uses the aggregate NuGet package by default. After changing source or worker code, rebuild the packages before running the demo. Use a new preview version or a fresh local NuGet cache when changing an existing local package version.
