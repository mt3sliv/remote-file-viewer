# Viewer controls

The Save document button is hidden by default. Enable it in the host configuration:

```json
{
  "RemoteFrame": {
    "ShowSaveButton": true
  }
}
```

Hosts can also set `options.ShowSaveButton = true` in `AddRemoteFrame`. The viewer reads `showSaveButton` from `/api/frame-settings` and enables Save only when the session renderer reports `canSave`. This setting controls the UI; it is not a save authorization policy.

Enter fullscreen expands the viewer with the browser Fullscreen API. Exit fullscreen or Escape restores the page. Browsers without the API leave the button disabled. The image keeps its aspect ratio, and mouse and touch coordinates map to the displayed frame independently of device pixel ratio. Presses in the letterbox area are ignored; captured drags are bounded to the frame.

Close session stops the local stream, clears the canvas and disables session actions immediately. After the server confirms deletion, the viewer shows “Session closed”. A failed deletion displays an error. Open file starts a new session.
