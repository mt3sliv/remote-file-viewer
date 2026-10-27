# LibreOffice source-build pipeline

This pipeline builds the Office engine from a pinned upstream Git revision, then passes the tested output into the native NuGet packaging process. It does not alter the middleware API or add VBA implementation changes. The initial patch series is empty.

## Baseline and scope

- Upstream repository: https://github.com/LibreOffice/core
- Release: `libreoffice-26.2.5.2`
- Immutable commit: `cd7284b4cbbfeb507e630c1aac019f4157393acb`
- Initial build target: Windows x64, Cygwin plus MSVC 2022.
- Runtime packages: `RemoteFrame.Office.Runtime.win-x64` and aggregate `RemoteFrame.Office.Windows`.

The release tag was checked against upstream's peeled Git reference. The profile uses the Cygwin/MSVC path supported by the pinned [configure.ac](https://github.com/LibreOffice/core/blob/libreoffice-26.2.5.2/configure.ac). Upstream's [README](https://github.com/LibreOffice/core/blob/libreoffice-26.2.5.2/README.md) also documents a newer WSL-helper build route; that route and Linux source/package builds are not implemented by this first recipe.

The desktop modules and existing Basic/VBA implementation remain enabled. The profile disables Java, the SDK, bundled help and update services for this application-owned baseline. This is not a promise of byte-identical builds across different compiler/SDK installations; keep build images/toolchains controlled and retain the build log.

## Build machine prerequisites

Use a Windows x64 build machine with Visual Studio 2022 Community C++ tools and Windows SDK, Git, PowerShell 7, the .NET 10 SDK, and a configured Cygwin build environment. The shell checks for Git, GNU make, Perl, autoconf/automake, pkg-config, Python 3, zip/unzip, bison, flex, gperf, tar and curl. Upstream configure performs the remaining dependency/compiler checks. This recipe does not install tools or change OS features automatically.

These are **maintainer build dependencies**. Applications consuming the resulting native package still do not need Python, Cygwin, Visual Studio, or a separate LibreOffice installation.

Use a short, space-free build path with enough storage for the checkout, external dependency archives, build products and package staging. Upstream's full build/check can take substantial time and disk space. The scripts refuse to reset or replace an unfamiliar/modified checkout. Interrupted preparation requires a fresh build root rather than automatic recursive deletion.

## Commands from the repository root

Check tools first; this does not download the engine:

```powershell
pwsh -File RemoteFrame/packaging/libreoffice/Build-LibreOffice.ps1 -Phase Preflight -BuildRoot C:/rf-build
```

Optionally fetch the locked source and apply the ordered patches without compiling:

```powershell
pwsh -File RemoteFrame/packaging/libreoffice/Build-LibreOffice.ps1 -Phase Fetch -BuildRoot C:/rf-build
```

Build performs preflight, prepares source, runs upstream `autogen.sh`, `make fetch`, `make build`, and `make check`:

```powershell
pwsh -File RemoteFrame/packaging/libreoffice/Build-LibreOffice.ps1 -Phase Build -BuildRoot C:/rf-build -Jobs 8
```

After success, package the resulting engine with our worker and native dependencies:

```powershell
pwsh -File RemoteFrame/packaging/libreoffice/Build-LibreOffice.ps1 -Phase Package -BuildRoot C:/rf-build -PackageVersion 0.3.0-source.1 -VcRedistDirectory "C:/path/to/Microsoft.VC143.CRT" -MicrosoftNoticesPath "C:/path/to/ThirdPartyNotices.txt"
```

Use `-BashPath` when Cygwin is installed elsewhere. Packaging additionally needs the input helper's C++ toolchain, app-local CRT distribution files and applicable notices, as before.

Each recipe has its own directory under the build root, identified by a hash of the source lock, profile, ordered patches and pipeline scripts. It contains `core/`, `configure.args`, `build.log`, `prepared.json`, and—only after build/check success—`source-build.json`. No successful receipt is retained after a failed rebuild. Upstream dependency URLs/checksums are supplied by the pinned source tree.

## Packaging handoff

`BuildWindowsRuntime.ps1` now requires `-SourceBuildReceipt`; arbitrary installed binary directories are no longer accepted. It verifies the current recipe, upstream revision, staged Git tree, absence of unrecorded source edits and the complete output-file inventory. It derives the engine path from the verified checkout's `instdir`.

Before creating packages it runs the existing Writer and Calc edit/save probes against the freshly staged engine and worker. It embeds source provenance, the source lock, configure profile and applied patches with the runtime notices. Native payload staging uses recipe-specific directories to avoid mixing engine versions.

Source-built packages use a separate `*-source.N` version supplied at packaging time. Existing versions cannot be overwritten. The aggregate package depends on that source-built native version while retaining the current middleware package. After acceptance, explicitly update the demo's package reference to the new source-built aggregate version; the current working prebuilt preview stays selected until then.

## Future engine patches

Add Git patches under `patches/` and list them in application order in `patches/series`. Unlisted patches, duplicates, escaping paths and patches that fail to apply are rejected. Recipe changes get a fresh checkout/build directory. Add upstream engine regression tests with future VBA patches, rebuild, and publish a new source package version. Do not claim full VBA compatibility based only on a successful baseline build.

## Validation performed here

`pwsh -File RemoteFrame/tests/TestSourceBuild.ps1` exercises local Git preparation/reuse, patch application, recipe identity, and receipt rejection for changed outputs, source edits, untracked inputs, failed tests and invalid patch entries. These are text fixtures, not compiled LibreOffice binaries.

PowerShell/Bash syntax and the actual prerequisite failure are checked separately. **A real LibreOffice compilation has not run on this machine:** Cygwin and its supporting build tools are absent. No source-built engine/package has been produced or substituted into the demo yet. The next machine step is provisioning the documented build environment and running the Build phase, then Package and the existing demo smoke checks.
