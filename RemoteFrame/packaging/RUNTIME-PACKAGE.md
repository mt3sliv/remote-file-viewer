# RemoteFrame Windows x64 Office runtime

This package retains the original combined Office runtime dependency contract.
Starting with preview.11 this is a compatibility coordinator for the independent
`RemoteFrame.Office.Worker.Runtime.win-x64` and
`RemoteFrame.LibreOffice.Runtime.win-x64` packages. New applications should install
`RemoteFrame.Office.Windows` for installed applications only, or
[RemoteFrame.Office.Windows.WithLibreOffice](https://www.nuget.org/packages/RemoteFrame.Office.Windows.WithLibreOffice)
to include the bundled LibreOffice fallback.

When upgrading this coordinator to preview.11 or later, also upgrade
`RemoteFrame.AspNetCore` to preview.11 or later so it can discover the new component
layout. Existing schema-1 combined payloads remain supported by the newer library.

Preview.10 and earlier used `RemoteFrame.Office.Runtime.win-x64.PartNNN` packages.
The separate component coordinators now pin their own parts to exact versions.
Each part contains a subset of the original files. Transitive MSBuild targets copy
the complete runtime trees to `remote-frame/win-x64/components/worker` and
`remote-frame/win-x64/components/libreoffice` during build and publish,
without an install script or application-startup download. Individual parts are
not standalone engines.

The assembled bundle contains:

- The pinned official LibreOffice Windows x64 distribution and resources.
- A self-contained .NET 10 RemoteFrame desktop worker and native input helper.
- App-local Visual C++ runtime dependencies.
- A runtime manifest with file hashes and original component licenses/notices.

The worker can use supported installed Excel/Word/LibreOffice applications before
falling back to the bundled engine. Installed Microsoft Office is not included or
licensed by this package. Keep the worker/helper and entire assembled runtime in
the application's deployment even when installed Office is preferred.

Native Office sessions currently target Windows x64 only. Windows ARM64 and Linux
native payloads are not published. The platform names accepted by the manifest
validator do not indicate available runtime packages.

The current Windows engine comes from verified upstream binaries; it is not a
claim that RemoteFrame's optional LibreOffice source-build pipeline passed all
upstream tests. Runtime packaging checks bundled Writer/Calc capture, resizing,
editing and saving, then validates the complete inventory. Unattended service
operation, every Office/VBA workflow and additional native platforms are not
implied by these tests.

NuGet's license metadata is a short summary. Full component license texts are
included separately under `licenses/` in every runtime package and remain at their
original paths in the assembled runtime. No engine files are removed to meet
package size limits.

[Application integration guide](https://github.com/mt3sliv/remote-file-viewer/blob/dev/RemoteFrame/src/RemoteFrame.AspNetCore/PACKAGE.md) ·
[Maintainer build documentation](https://github.com/mt3sliv/remote-file-viewer/blob/dev/RemoteFrame/packaging/README.md)
