# RemoteFrame Windows x64 worker runtime

Supplies the self-contained .NET 10 RemoteFrame worker, native input helper and
their license notices. It contains no LibreOffice or Microsoft Office.
Install [RemoteFrame.Office.Windows](https://www.nuget.org/packages/RemoteFrame.Office.Windows)
for integration with supported installed Excel, Word or LibreOffice applications.
Applications must match the worker's x64 architecture.

The coordinator pins its dependency parts to exact versions. Build and publish copy
the full worker tree to `remote-frame/win-x64/components/worker`. Keep that tree in
deployments. No startup download or installation script is used.

For bundled LibreOffice fallback, install
[RemoteFrame.Office.Windows.WithLibreOffice](https://www.nuget.org/packages/RemoteFrame.Office.Windows.WithLibreOffice).
Only Windows x64 native packages are currently published. Full third-party notices
are included under `licenses/` and in the deployed worker tree.
