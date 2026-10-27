# RemoteFrame.AspNetCore

Register RemoteFrame in an existing ASP.NET Core application:

```csharp
using RemoteFrame.AspNetCore;

builder.Services.AddRemoteFrame(options =>
{
    options.Files.Add("notes", "/srv/documents/notes.txt");
});

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.UseWebSockets();
app.MapRemoteFrame("/remote-frame");
app.Run();
```

The host configures its own authentication and authorization services. Every endpoint requires authorization and a nonempty ClaimTypes.NameIdentifier claim. Configured files are shared across authenticated users; apply application authorization policies for access restrictions. The optional prefix integrates with existing routes.

| Method | Route relative to prefix | Purpose |
|---|---|---|
| GET | `/api/files` | List configured file IDs |
| POST | `/api/sessions` | Open `{ "fileId": "notes" }` |
| DELETE | `/api/sessions/{id}` | Close the caller's session |
| WebSocket | `/ws/{id}` | Receive PNG frames and send JSON input events |

REST endpoints manage sessions; WebSockets transport live frames and input. Browser connections must be same-origin. Cookie-authenticated hosts should apply their CSRF protections. Only server configuration supplies paths. The example browser client uses root-relative paths; applications using a prefix must use that prefix in their client URLs.

NuGet resolves RemoteFrame.Core and RemoteFrame.Rendering transitively. No Python, pip, scripts, Xvfb, or Openbox installation is used. The Server example application is not part of the package.

## Office runtime

Install the local `RemoteFrame.Office.Windows` aggregate package (`0.2.0-preview.5`) to include this library and the Windows x64 native engine. Set `options.EnableOffice = true`. The engine and worker come from the package output; they never resolve a system LibreOffice installation. The package contains its own C# worker/runtime and a native Windows input helper, with no Python bridge.

The library still requires authorization by default. For a loopback-only demo, explicitly set `options.RequireAuthorization = false` and omit authentication/authorization services and middleware. The included Server project demonstrates this mode without API keys or login cookies.

Office sessions edit private working copies. Saved changes are propagated back to the configured file; unsaved changes are discarded on close. Concurrent external changes are detected using hashes before replacement. The browser Save button requests a shortcut; dialogs such as format confirmations remain inside the streamed UI. This is a Windows x64 preview; Linux/ARM64 and clean-machine/service acceptance remain outstanding.


