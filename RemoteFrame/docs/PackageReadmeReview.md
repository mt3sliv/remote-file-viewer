# NuGet README review

Reviewed the actual README documents returned by NuGet.org's v3 readme endpoint
for preview.9, covering Core, Rendering, AspNetCore, the Windows aggregate, runtime
coordinator and a representative runtime part. All runtime parts share a template.

| Published package | Finding in preview.9 | Correction for preview.10 |
|---|---|---|
| Core | Uses the repository README, including local demo instructions and the false claim that packages are unpublished | Dedicated contracts/package guide; no claim that Core contains HTTP/UI/native assets |
| Rendering | Same repository README, rather than renderer scope | Dedicated text/PNG guide, size/glyph limits and Windows/Linux managed scope |
| AspNetCore | Missing frame-settings/configuration routes; root-only browser-client guidance; no reusable UI | Complete API table, embedded viewer, PathBase/prefix examples, file-access hooks and authentication/CSRF behavior |
| Office.Windows | Maintainer build README with obsolete source.1/preview.1 versions and an unpublished claim | Consumer install/integration guide; supported installed apps, save behavior and Windows-only native scope |
| Runtime coordinator/parts | Same maintainer README implying unavailable ARM64/Linux payloads exist | Runtime-specific guide explaining exact-version parts, complete deployment tree, licenses and actual platform availability |

Consumer package READMEs now use absolute documentation links that also work on
NuGet.org. Maintainer build instructions remain in `packaging/README.md` and are
not reused as the consumer-facing runtime guide. The repository README remains a
repository guide rather than the Core/Rendering package description.

The package tests inspect the actual `.nuspec` README declaration and ZIP entry,
reject obsolete publication statements and repository-relative links, and verify
Core/Rendering/aggregate package identity. Release manifest generation runs this
check before artifacts can be published. Packed middleware integration tests serve
the embedded page, script and stylesheet without static-file middleware.

Published preview.9 packages are immutable; editing the source README cannot
change their embedded documents. The corrected documents and viewer are intended
for preview.10 and later.
