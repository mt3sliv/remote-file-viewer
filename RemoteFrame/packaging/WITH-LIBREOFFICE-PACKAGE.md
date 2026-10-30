# RemoteFrame.Office.Windows.WithLibreOffice

Install this convenience package for Windows x64 Office viewing with a bundled
LibreOffice fallback:

```shell
dotnet add package RemoteFrame.Office.Windows.WithLibreOffice --prerelease
```

It includes `RemoteFrame.Office.Windows` (ASP.NET Core integration and the worker)
and `RemoteFrame.LibreOffice.Runtime.win-x64` (the complete LibreOffice engine).
Supported installed Excel/Word/LibreOffice applications are preferred by default;
the bundled engine is the fallback. Microsoft Office is not included or licensed.
Set `PreferInstalledOfficeApplications = false` to use only bundled LibreOffice.

Both component trees under `remote-frame/win-x64/components` must be deployed.
LibreOffice is split into dependency packages because of its size; restore, build
and publish reassemble it automatically without startup downloads.

See the [integration and configuration guide](https://github.com/mt3sliv/remote-file-viewer/blob/dev/RemoteFrame/src/RemoteFrame.AspNetCore/PACKAGE.md).

## Migrating from preview.10 or earlier

Those releases included LibreOffice with `RemoteFrame.Office.Windows`. Starting
with preview.11, use this package to retain that behavior. If the server already
has a supported application, `RemoteFrame.Office.Windows` alone avoids downloading
and deploying LibreOffice. Native runtime packages currently support Windows x64
only; managed text rendering remains portable.
