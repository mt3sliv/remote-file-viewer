# RemoteFrame LibreOffice Windows x64 runtime

This optional package supplies the complete pinned upstream LibreOffice binary
distribution, app-local Visual C++ dependencies and component licenses/notices.
It contains no Microsoft Office installation and no RemoteFrame worker.

Application developers should install
[RemoteFrame.Office.Windows.WithLibreOffice](https://www.nuget.org/packages/RemoteFrame.Office.Windows.WithLibreOffice)
to obtain both the integration/worker and this fallback engine.

The coordinator pins all `RemoteFrame.LibreOffice.Runtime.win-x64.PartNNN` packages
to exact versions. Each part contains original engine files; individual parts are
not standalone engines. Build and publish assemble the full tree at
`remote-frame/win-x64/components/libreoffice`. Keep the complete tree in deployments.
No engine files are removed to satisfy NuGet's package size limit, and no startup
download or installation script is used.

Only Windows x64 native packages are currently published. The engine uses verified
official binaries; this does not claim the optional source-build pipeline passed
all upstream tests. Packaging tests Writer/Calc capture, resizing, editing and saving.
Full notices remain in their original deployed paths and under `licenses/` in each
package; NuGet license metadata contains a short summary.
