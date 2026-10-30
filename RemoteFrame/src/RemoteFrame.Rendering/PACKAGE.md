# RemoteFrame.Rendering

Portable PNG rendering and a read-only text renderer for .NET 10. Depends on
[RemoteFrame.Core](https://www.nuget.org/packages/RemoteFrame.Core).

`TextRenderProvider` supports UTF text files with these extensions: `.txt`, `.log`,
`.json`, `.csv`, `.md`, `.cs`, `.xml`, `.yaml` and `.yml`. Files larger than 512 KiB,
invalid text and binary content are rejected. Scrolling is supported; editing and
saving are not. The renderer caches unchanged frames and supports capture/stream
size configuration.

`PngFrameEncoder` provides PNG encoding and BGRA downsampling helpers. Text uses
a built-in bitmap font with a limited glyph set; this is not a full document-layout
or Unicode typesetting engine.
This package does not add HTTP routes, a browser page or Office support. Install
[RemoteFrame.AspNetCore](https://www.nuget.org/packages/RemoteFrame.AspNetCore)
for web integration, or
[RemoteFrame.Office.Windows](https://www.nuget.org/packages/RemoteFrame.Office.Windows)
for the managed integration plus the Windows x64 Office runtime.

Managed rendering is tested on Windows and Linux. Native Office desktop sessions
are currently provided only for Windows x64.

[Source and documentation](https://github.com/mt3sliv/remote-file-viewer/tree/dev/RemoteFrame)
