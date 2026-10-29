# LibreOffice source-build pipeline

This is the opt-in engine-development path. For normal distributions use `pwsh -File packaging/Build-Distribution.ps1 -OfficeEngine Binary`; it packages verified official binaries with a freshly built RemoteFrame worker. `-OfficeEngine Source` selects this source pipeline, or accepts an existing successful `-SourceBuildReceipt`. See [distribution packaging](../README.md).

This pipeline builds the Office engine from a pinned upstream Git revision, then passes the tested output into the native NuGet packaging process. It does not alter the middleware API or add VBA implementation changes. The initial patch series is empty.

## Baseline and scope

- Upstream repository: https://github.com/LibreOffice/core
- Release: `libreoffice-26.2.5.2`
- Immutable commit: `cd7284b4cbbfeb507e630c1aac019f4157393acb`
- Initial build target: Windows x64, Cygwin plus MSVC 2026.
- Runtime packages: `RemoteFrame.Office.Runtime.win-x64` and aggregate `RemoteFrame.Office.Windows`.

The release tag was checked against upstream's peeled Git reference. The profile uses the Cygwin/MSVC path supported by the pinned [configure.ac](https://github.com/LibreOffice/core/blob/libreoffice-26.2.5.2/configure.ac). Upstream's [README](https://github.com/LibreOffice/core/blob/libreoffice-26.2.5.2/README.md) also documents a newer WSL-helper build route; that route and Linux source/package builds are not implemented by this first recipe.

The desktop modules and existing Basic/VBA implementation remain enabled. The profile disables Java, the SDK, bundled help and update services for this application-owned baseline. This is not a promise of byte-identical builds across different compiler/SDK installations; keep build images/toolchains controlled and retain the build log.

## Build machine prerequisites

Use a Windows x64 build machine with Visual Studio 2026 C++ tools and Windows SDK, Git, PowerShell 7, the .NET 10 SDK, and a configured Cygwin build environment. The shell checks for Git, GNU make, Perl, autoconf/automake, pkg-config, Python 3, zip/unzip, bison, flex, gperf, tar and curl. Upstream configuration also requires the Cygwin `patch` and `nasm` packages, native Windows pkgconf, and Visual Studio's optional C++ Clang compiler/toolset for Skia/PDFium. Upstream configure performs the remaining dependency/compiler checks. This recipe does not install tools or change OS features automatically.

These are **maintainer build dependencies**. Applications consuming the resulting native package still do not need Python, Cygwin, Visual Studio, or a separate LibreOffice installation.

The pinned source's `.config/admin_java_and_deps.winget` provides the native Windows pkgconf and make tools and their hashes. Stage them once under the workspace, then initialize the environment in the same PowerShell session used for Build:

```powershell
New-Item -ItemType Directory -Force artifacts/build-tools/opt/bin | Out-Null
Invoke-WebRequest https://dev-www.libreoffice.org/extern/pkgconf-2.4.3.exe -OutFile artifacts/build-tools/opt/bin/pkgconf-2.4.3.exe
Invoke-WebRequest https://dev-www.libreoffice.org/bin/cygwin/make-4.2.1-msvc.exe -OutFile artifacts/build-tools/opt/bin/make.exe
Invoke-WebRequest https://download.qt.io/official_releases/jom/jom_1_1_4.zip -OutFile artifacts/build-tools/jom_1_1_4.zip
# Verify SHA-256 d533c1ef49214229681e90196ed2094691e8c4a0a0bef0b2c901debcb562682b before extracting.
Expand-Archive artifacts/build-tools/jom_1_1_4.zip artifacts/build-tools/jom
dotnet publish tools/RemoteFrame.LinkWrapper -c Release --self-contained false -o artifacts/build-tools/link-wrapper
pwsh -NoProfile -File tests/TestLinkWrapper.ps1
. ./packaging/libreoffice/Set-WindowsBuildEnvironment.ps1
```

The environment helper verifies both SHA-256 hashes before use, discovers the configured Visual Studio installation and Clang compiler, and generates a shell environment with native make and Cygwin helpers on PATH. It sets upstream Clang overrides for the Visual Studio 2026 `Llvm/x64/bin` layout; the pinned configure script probes the older layout. Its include prefix was verified with an actual local-header compiler probe. These are maintainer build tools, not consumer runtime dependencies. No Defender settings are changed by the environment helper.

The environment helper also selects the maintainer-only `RemoteFrame.LinkWrapper` through the top-level `GMAKE_OPTIONS` and GNU make's `gb_LINK` override. It keeps GNU-only options out of the native JOM invocation; upstream clears MAKEFLAGS for its nmake/JOM external projects. JOM is selected to parallelize OpenSSL rather than serial nmake. For oversized response-file lines it puts whitespace-separated arguments on separate lines, preserving quoted paths, before invoking the actual MSVC linker. This extends the formatting workaround that upstream already applies to its merged library to cases such as Skia under long workspace paths (MSVC LNK1170). It changes no engine sources, object files, linker flags, or runtime behavior, and is not shipped to consumers. Its regression probe reproduces LNK1170 with the real MSVC linker, verifies the fix and quoted paths, checks unchanged short files, and checks error propagation.

For the bundled Python UWP launcher projects only, the helper sets MSBuild's supported `ForceImportBeforeCppTargets` hook to `packaging/native/PythonUwpCompat.props`. It opts those two projects into the legacy experimental coroutine APIs still implemented by the current MSVC STL, resolving STL1011 without changing their code or suppressing diagnostics in other projects. `pwsh -File tests/TestPythonUwpCompat.ps1` reproduces the compiler error, verifies both launchers, and verifies that an unrelated project still fails the same deprecation check.

The Cygwin invocation establishes a login environment, then replaces it with a non-login build shell so terminal-clearing logout hooks cannot overwrite the build exit code.

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

The build profile explicitly selects Visual Studio 2026, supported by the pinned upstream configure script. Preflight discovers the matching C++ installation using `vswhere`; the native input helper also uses discovery rather than a hard-coded edition/path. Use `-BashPath` when Cygwin is installed elsewhere. Packaging additionally needs the input helper's C++ toolchain, app-local CRT distribution files and applicable notices, as before.

Each recipe has its own directory under the build root, identified by a hash of the source lock, profile, ordered patches and pipeline scripts. It contains `core/`, `configure.args`, `build.log`, `prepared.json`, and—only after build/check success—`source-build.json`. No successful receipt is retained after a failed rebuild. Upstream dependency URLs/checksums are supplied by the pinned source tree.

## Packaging handoff

`BuildWindowsRuntime.ps1 -EngineMode Source` requires `-SourceBuildReceipt`. It verifies the current recipe, upstream revision, staged Git tree, absence of unrecorded source edits and the complete output-file inventory. It derives the engine path from the verified checkout's `instdir`. Binary mode instead requires a verified archive/extraction receipt; it cannot use source receipts or arbitrary installed directories.

Before creating packages it runs the existing Writer and Calc edit/save probes against the freshly staged engine and worker. It embeds source provenance, the source lock, configure profile and applied patches with the runtime notices. Native payload staging uses recipe-specific directories to avoid mixing engine versions.

Source-built packages use a separate `*-source.N` version supplied at packaging time. Existing versions cannot be overwritten. The aggregate depends on that source-built native version and updated managed `0.3.0-preview.2` packages. The demo defaults to binary aggregate `0.3.0-preview.2`; pass `-p:RemoteFramePackageVersion=0.3.0-source.2` to consume a source release. Older aggregates do not contain the updated worker.

## Future engine patches

Add Git patches under `patches/` and list them in application order in `patches/series`. Unlisted patches, duplicates, escaping paths and patches that fail to apply are rejected. Recipe changes get a fresh checkout/build directory. Add upstream engine regression tests with future VBA patches, rebuild, and publish a new source package version. Do not claim full VBA compatibility based only on a successful baseline build.

## Validation performed here

`pwsh -File RemoteFrame/tests/TestSourceBuild.ps1` exercises local Git preparation/reuse, patch application, recipe identity, and receipt rejection for changed outputs, source edits, untracked inputs, failed tests and invalid patch entries. These are text fixtures, not compiled LibreOffice binaries.

**Current source status:** prerequisites and configuration pass. The linker response-file adapter, parallel OpenSSL/JOM setup and Python UWP compatibility hook addressed toolchain failures, and the main engine compiled/linked. The attempt then failed `VclFiltersTest::testCVEs` while loading `CVE-2004-0200-1.jpg`. No successful source receipt exists. The checkout/output remain available for diagnosis; the temporary Defender exception was removed and cleanup verified. Normal binary distribution packaging is independent of this failure. Consumers do not need the maintainer toolchain.
