# Package-owned Office runtime

The native payload is built by RemoteFrame maintainers, not by consuming web applications. Windows x64 artifacts are generated locally under ignored artifacts/; the repository tracks their worker source and packaging recipes. Linux/ARM64 artifacts are not implemented. This project packages a completed runtime tree; it does not turn the LibreOffice source repository into a working runtime by itself.

Published native packages currently target Windows x64 only. Starting with preview.11, `RemoteFrame.Office.Windows` supplies integration and the worker for installed applications; `RemoteFrame.Office.Windows.WithLibreOffice` also includes the optional engine. `Pack-OfficeComponents.ps1` creates independent worker/LibreOffice manifests and size-bounded dependency parts. Build/publish deploy both trees under `remote-frame/win-x64/components` when selected. The old runtime ID remains a compatibility coordinator for both. Platform names accepted by the validator do not claim Windows ARM64 or Linux native packages exist. Consumer-facing READMEs are maintained separately from this build guide.

## Staging contract

For a worker-only build with no LibreOffice download or staging, run
`pwsh -File packaging/Build-WindowsWorker.ps1 -Version <fresh-version>`.
It builds the self-contained worker/input helper and its independent manifest,
then packs `RemoteFrame.Office.Worker.Runtime.win-x64` and its dependency parts.
Pack the managed libraries and `RemoteFrame.Office.Windows` at matching versions
to create the smaller integration distribution, or use
`pwsh -File packaging/Build-WorkerDistribution.ps1 -Version <fresh-version>` to do
all of this and verify a clean worker-only consumer's build and publish outputs.
The complete distribution command
also packs both components and `RemoteFrame.Office.Windows.WithLibreOffice`.

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

Packing validates the platform, required files, path containment, license inventory and hashes. Empty/incomplete payloads fail packaging. Assets are copied transitively into `remote-frame/<rid>/` in both build and publish output, preserving subdirectories. `BundledOfficeRuntime.Load()` validates only this package-owned location. The worker independently prefers supported installed applications before falling back to the validated bundled engine; installed applications are not part of the package inventory. Nothing is downloaded at application startup. Engine acceptance probes pass `--bundled-only` so an installed application cannot mask a defective bundled payload.

## Release gates still outstanding

- Implement Linux/ARM64 payloads and private-session capture/input workers.
- Validate the complete transitive native dependency set on clean supported OS images. The baseline OS/architecture/libc must be documented; bundling cannot make binaries independent of the operating system.
- Verify Linux execution permissions survive NuGet/build/publish; restore required executable bits internally if necessary.
- Verify application startup, menus/dialogs, input, save persistence, crashes, session isolation and cleanup without interactive server login.
- Complete upstream and third-party redistribution review, notices and corresponding-source delivery where required.
- Verify final aggregate package installation and clean-host acceptance before claiming Office support.

## Packaging tests

Run `pwsh -File RemoteFrame/tests/TestRuntimePackaging.ps1`. It creates a clearly marked text-only fixture package under ignored `artifacts/`, verifies integrity rejection cases, and installs/publishes a standalone NuGet consumer. Fixture binaries are never executed. These checks have passed on Windows; they do not validate a real Office runtime or Linux execution.

## Build the Windows engine from source

Use the [source-build pipeline](libreoffice/README.md). It pins LibreOffice 26.2.5.2 to commit cd7284b4cbbfeb507e630c1aac019f4157393acb, prepares a dedicated checkout, applies an ordered patch series, runs configure/build/upstream checks, and writes a receipt binding the recipe and native output inventory.

Run `pwsh -File packaging/Build-Distribution.ps1 -OfficeEngine Source` to build and package the engine, or provide `-SourceBuildReceipt` to reuse a successful build. The Package phase passes the validated source engine into the worker/CRT/NuGet packaging path, runs Writer/Calc edit-save probes, and records provenance and patches. Source releases use separate `*-source.N` versions and never accept failed/missing test receipts.

The current source engine compiled and linked but failed `VclFiltersTest::testCVEs` on a JPEG fixture. It has no successful receipt. No VBA extension patches are included. Build dependencies belong on the maintainer machine, not consumer hosts.

## Package with upstream binaries (default)

Cold preparation reports download and MSI extraction separately. Downloads use up
to three bounded attempts, with a 30-second connection limit, a 10-minute transfer
limit and stalled-transfer detection. The pinned SHA-256 remains mandatory before
promoting an archive into the cache. Administrative MSI extraction has a 20-minute
limit with progress messages and a retained diagnostic log. An interrupted image
does not become a verified cache receipt.

Run `pwsh -File packaging/Build-Distribution.ps1 -OfficeEngine Binary` (the switch can be omitted). `libreoffice/binary.lock.json` pins the official Windows x64 MSI and SHA-256 verified against Microsoft's winget manifest. `Prepare-WindowsBinary.ps1` verifies the download, creates an administrative image inside `artifacts/libreoffice-binary` without installing/registering Office, and writes a receipt binding its extracted file inventory to that archive. Cached inputs are reverified on reuse.

`BuildWindowsRuntime.ps1 -EngineMode Binary -BinaryBuildReceipt <path>` validates the receipt, rebuilds the worker/input helper, stages a fresh payload, runs bundled-only Writer/Calc edit-save checks, includes provenance/notices and packs managed/runtime/aggregate NuGets. Source mode accepts only `-SourceBuildReceipt`. Receipt types cannot be substituted. Binary provenance records the official archive and our smoke checks, not a successful local upstream source build. Binary mode needs .NET 10, PowerShell 7 and Visual Studio C++ tools for our small input helper; no Cygwin or LibreOffice compiler is used. Runtime/notices paths are discovered or supplied explicitly.

Binary versions default from `Directory.Build.props` (`0.3.0-preview.9`); managed versions follow the selected distribution version. Source distributions default to `0.3.0-source.2`. The demo consumes the binary aggregate by default and accepts `-p:RemoteFramePackageVersion=...` for another release. Later builds need fresh package versions to avoid stale NuGet caches. `UseRemoteFramePackages` controls demo package/project references independently of engine selection.

Run `tests/TestBinaryBuild.ps1` and `tests/TestSourceBuild.ps1` for receipt/mode integrity checks. Actual Office smoke checks run during packaging. Linux/ARM64 binary preparation is not implemented.

LibreOffice's automatic updater and Python bytecode writes are disabled inside each private worker session. A pinned package must not download updates or rewrite its engine directory. Packaging rechecks the complete engine inventory after smoke tests, including the explicitly overlaid CRT files. To resume a failed worker/probe step without copying the whole engine again, provide `-StagedPayload <previous generated win-x64 payload>`; the engine identity, every file hash and absence of obsolete worker files are checked before reuse. Changed stages are rejected, not silently accepted.
