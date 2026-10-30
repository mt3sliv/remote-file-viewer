# GitHub Actions and NuGet.org releases

The repository root contains `.github/workflows/ci.yml` and `publish.yml`.
All commands run inside the `RemoteFrame` solution directory.

## Build checks

Pushes and pull requests to `dev` and `main` run managed builds/tests on Ubuntu
and Windows, browser input tests, and clean split-package restore/build/publish
tests. An independent Windows worker job builds the native helper, tests pointer/modal
input and verifies the worker-only distribution. The full Windows runtime job
downloads the pinned official LibreOffice MSI, verifies its hashes, and builds
and tests the complete distribution. Installed Excel is not present on hosted
runners; its separate integration test still requires an Excel-equipped machine.

The Windows build restores only the verified upstream binary distribution; it
does not compile LibreOffice from source. Its archive/extraction cache is keyed
by `binary.lock.json` and revalidated on every use. Source builds remain available
through the existing source-receipt workflow, outside these release actions.

CI retains the complete `nuget-release` artifact for inspection. The release workflow
builds independent groups and uploads `nuget-managed`, `nuget-worker`,
`nuget-libreoffice` and `nuget-integration`. Each has a `release-<Group>.json`
manifest recording package versions, source commit, sizes, SHA-256 hashes and
dependency versions. Retention is 14 days. Publish jobs use these tested artifacts
rather than rebuilding them.

## One-time account setup

1. Create/use a NuGet.org account and check ownership/availability of the
   `RemoteFrame.*` package IDs. Select the intended account or organization as
   package owner, including new runtime part IDs.
2. In GitHub, create an environment named `nuget` and set its variable
   `NUGET_USER` to the NuGet.org profile name (not an email address). The workflow
   uses this environment for publishing. Configure its release controls to suit
   the repository's policy.
3. In NuGet.org Trusted Publishing, add a GitHub policy:
   - Repository owner: `mt3sliv`
   - Repository: `remote-file-viewer`
   - Workflow file: `publish.yml` (file name only)
   - Environment: `nuget`
   - Package scope: `RemoteFrame.*`, permitting new packages and new versions.

No long-lived API key is required. `NuGet/login@v1` exchanges the job's GitHub
OIDC identity for a temporary key immediately before publishing. Only that job
receives `id-token: write`; pull requests never publish. Each publish job obtains
its own temporary key through the same workflow/environment policy.

See [NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing).

## Releasing

The development default version lives in `Directory.Build.props`. Release tags
override it consistently for managed packages, the worker, runtime parts,
runtime coordinator and Windows aggregate. Use a fresh version; do not overwrite
an already published package version.

For example, after committing the intended release:

```powershell
git tag v0.3.0-preview.11
git push origin v0.3.0-preview.11
```

This starts managed, worker and LibreOffice builds in parallel. Publishing proceeds
as soon as each group's checks and dependencies finish:

| Publish job | Packages | Waits for |
| --- | --- | --- |
| Managed | Core, Rendering, ASP.NET Core | Managed tests on Windows and Linux |
| Worker | Worker runtime/parts, Office.Windows | Worker-only tests and managed publication |
| LibreOffice | LibreOffice runtime/parts | Engine build, native smoke checks and deployed inventory verification |
| Integration | Compatibility runtime and Office.Windows.WithLibreOffice | Worker and LibreOffice publication |

Managed and worker packages do not wait for LibreOffice. Each manifest permits only
its group's package IDs and explicitly listed external dependencies at the same
release version. Dependencies within a group are pushed before their dependents.
Stable tags such as `v0.3.0` are also supported. Manual dispatch is
allowed only when selecting a `v<version>` tag, so an ordinary branch dispatch
cannot publish. A later group failure leaves earlier groups published. Rerun the
failed publish job to reuse the same verified artifacts, with already uploaded
versions skipped. Dependent convenience packages stay blocked until their required
groups succeed.

For matching source/version commits, the release workflow first looks for a
successful CI run retaining `nuget-release`. It verifies the CI workflow identity,
repository, tag commit and artifact availability, then verifies the downloaded
manifest and every package hash. This reuses the complete engine verification
already performed by CI and avoids a cold archive download on the tag runner.

To recover an interrupted release, manually run `publish.yml` on `dev`, supplying
`recovery_release_tag` (for example `v0.3.0-preview.11`) and `recovery_ci_run_id`
(a successful CI run for that exact tag commit). Recovery verifies the tag against
the run, downloads its retained artifact and validates version, commit and hashes
before requesting a publishing key. It publishes the verified artifact with
existing package versions skipped. No tag is moved and no packages are rebuilt.

Dependencies are uploaded before their coordinators. NuGet.org may take time
to index new packages after upload; the action does not claim immediate search
visibility. Local validation uses `-ValidateOnly` and never obtains a publishing key.

## Runtime size and installation

NuGet.org accepts packages up to 250 MB. The runtime is now split into deterministic
file groups capped at 180,000,000 uncompressed payload bytes, with an additional
240,000,000-byte check on every finished release package. An individually oversized
file fails the build rather than silently removing runtime content.

Starting with preview.11, `Pack-OfficeComponents.ps1` separates the worker from
LibreOffice. `RemoteFrame.Office.Worker.Runtime.win-x64` and
`RemoteFrame.LibreOffice.Runtime.win-x64` pin their own size-bounded parts.
`RemoteFrame.Office.Windows` includes only the worker/integration;
`RemoteFrame.Office.Windows.WithLibreOffice` adds the engine. The old
`RemoteFrame.Office.Runtime.win-x64` ID remains a coordinator for both components.
Build and publish assemble independent trees under `remote-frame/win-x64/components`.
Schema-2 manifests validate each component independently; legacy schema-1 combined
payloads remain supported. Original component
licenses and notices remain present. Each part includes a short license summary
for NuGet's metadata plus all full license texts under `licenses/`. The metadata
file is checked against NuGet.org's separate 1 MB license-file limit; full notices
are preserved without truncation, even when individually larger than 1 MB.

No installation-time download code or NuGet install script is introduced.
The generic split-packager accepts Windows/Linux runtime identifiers, but this
repository currently builds only the Windows native distribution. Managed Linux
tests do not imply a published Linux native runtime.

## Local verification

```powershell
pwsh -File packaging/Test-Managed.ps1
pwsh -File tests/TestSplitRuntimePackaging.ps1
pwsh -File tests/TestPointerInput.ps1
pwsh -File packaging/Build-Distribution.ps1 -OfficeEngine Binary
dotnet build src/RemoteFrame.Server -c Release
pwsh -File tests/TestDemoRuntime.ps1 -Configuration Release
pwsh -File packaging/New-ReleaseManifest.ps1 -Version 0.3.0-preview.9
pwsh -File packaging/Publish-NuGet.ps1 -Version 0.3.0-preview.9 -ValidateOnly
```

The split fixture uses a separate feed and clean restore cache, then compares
every output file's hash after both build and publish, including extensionless
and hidden files. The production distribution build also checks the actual
runtime package sizes and runs bundled Writer/Calc edit/save tests.

Local validation of preview.8 produced 15 packages (10 runtime parts and five
library/coordinator packages); the largest part was about 74.7 MB. The packaged
demo built with zero warnings/errors and its complete 19,698-file runtime passed
hash validation. Managed, browser, native pointer/modal, binary-provenance, clean
split restore/build/publish, release-policy, and workflow syntax checks passed.
GitHub CI also passed on Ubuntu and hosted Windows. The first preview.8 upload was
rejected before any package was published because its combined license metadata
exceeded NuGet.org's 1 MB limit. Preview.9 separates the short metadata summary
from full license files and adds regression tests for oversized license metadata.
