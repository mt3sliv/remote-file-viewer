namespace RemoteFrame.Office.Worker;

// A close requires a left-button press and release over the same window's X.
// Keep this independent of Win32 so cancellation and drag-off behavior can be checked portably.
internal sealed class CloseButtonGesture
{
    private nint pressed;
    public bool Update(string kind, int button, nint window, bool overClose)
    {
        if (kind == "release") { pressed = 0; return false; }
        if (button != 0) return false;
        if (kind == "pointerdown") { pressed = overClose ? window : 0; return false; }
        if (kind != "pointerup") return false;
        var close = pressed != 0 && pressed == window && overClose;
        pressed = 0;
        return close;
    }
}
