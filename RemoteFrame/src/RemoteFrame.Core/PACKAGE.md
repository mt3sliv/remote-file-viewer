# RemoteFrame.Core

Shared contracts for RemoteFrame renderers and ASP.NET Core integration, targeting .NET 10.

- `IRenderProvider` selects and opens a supported server-side file.
- `IRenderSession` configures capture, produces PNG frames, accepts input and disposes the session.
- `FrameConfiguration` describes FPS, capture dimensions and stream dimensions, with validation.
- `Frame`, `InputEvent` and `FramePacing` provide frame/input data and pacing helpers.

This package contains no viewer UI, HTTP endpoints, Office binaries or desktop worker.
For a web application, install [RemoteFrame.AspNetCore](https://www.nuget.org/packages/RemoteFrame.AspNetCore).
For Windows x64 Office sessions, install [RemoteFrame.Office.Windows](https://www.nuget.org/packages/RemoteFrame.Office.Windows), which includes the managed packages and runtime transitively.

Custom sessions default to `CanSave = false`. Starting with preview.10, override
`IRenderSession.CanSave` to advertise save support to the packaged viewer. Existing
implementations remain compatible through the default interface implementation.

Documents stay on the host; frames contain their rendered visual content. These
contracts do not implement authentication, authorization or file storage.

[Source and documentation](https://github.com/mt3sliv/remote-file-viewer/tree/dev/RemoteFrame)
