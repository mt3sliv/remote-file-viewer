using System.Diagnostics;
using System.Runtime.InteropServices;
using RemoteFrame.Core;
using RemoteFrame.Office.Worker;

if (!OperatingSystem.IsWindows()) { Console.WriteLine("SKIP: private-desktop pointer checks require Windows."); return; }
if (args is ["--inspect-worker", var pid]) { DesktopInspection.Run(int.Parse(pid)); return; }
if (args is ["--trace-worker", var tracePid]) { DesktopInspection.Run(int.Parse(tracePid),true); return; }
if (args is ["--cursor-probe-worker", var cursorPid]) { DesktopInspection.Run(int.Parse(cursorPid),true,true); return; }
if (args is ["/x", var document]) { ModalFixture.Run(document); return; }

var file = Path.Combine(Path.GetTempPath(), "RemoteFrame-pointer-" + Guid.NewGuid().ToString("N") + ".txt");
File.WriteAllText(file, "initial");
try
{
    // Launch a disposable native window on the production worker's private desktop.
    // It uses real BUTTON controls and a nested modal message loop, without Office/macros.
    using var desktop = new WindowsOfficeDesktop(new OfficeApplication(Environment.ProcessPath!, OfficeApplicationKind.Excel), file);
    void Click(int x, int y)
    {
        desktop.Apply(new InputEvent("pointerdown", X: x, Y: y));
        desktop.Apply(new InputEvent("pointerup", X: x, Y: y));
    }
    void WaitFor(string expected)
    {
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < TimeSpan.FromSeconds(5))
        {
            desktop.Capture();
            if (File.ReadAllText(file) == expected) return;
            Thread.Sleep(50);
        }
        throw new Exception("Missing pointer result: " + expected + "; received: " + File.ReadAllText(file));
    }
    Native.Rect VisibleModal()
    {
        var pixels = desktop.Capture();
        Native.Rect rect = default;
        Native.EnumWindowsCallback find = (window, _) => {
            var cls = new System.Text.StringBuilder(256);
            Native.GetClassName(window, cls, cls.Capacity);
            if (cls.ToString() == "RemoteFrame.ModalFixture") Native.GetWindowRect(window, out rect);
            return true;
        };
        Native.EnumDesktopWindows(Native.GetThreadDesktop(Native.GetCurrentThreadId()), find, 0);
        if (rect.Left < 0 || rect.Top < 0 || rect.Right > desktop.Width || rect.Bottom > desktop.Height || rect.Right <= rect.Left)
            throw new Exception("An off-screen owned dialog was not brought into the remote viewport.");
        int marker = ((rect.Top + 40) * desktop.Width + rect.Left + 20) * 4;
        if (pixels[marker] != 0 || pixels[marker+1] != 0 || pixels[marker+2] != 0)
            throw new Exception($"A window resetting the print origin was not composited at its actual coordinates: rect={rect.Left},{rect.Top},{rect.Right},{rect.Bottom}; pixel={pixels[marker]},{pixels[marker+1]},{pixels[marker+2]}.");
        int outside = (10 * desktop.Width + 10) * 4;
        if (pixels[outside] == 0 && pixels[outside+1] == 0 && pixels[outside+2] == 0)
            throw new Exception("The dialog overwrote the main window's top-left corner.");
        return rect;
    }
    nint sinkWindow = 0;
    Native.EnumWindowsCallback locateSink = (window, parameter) => {
        for (var child = Native.GetWindow(window,5); child != 0; child = Native.GetWindow(child,2))
        {
            var cls = new System.Text.StringBuilder(256);
            Native.GetClassName(child,cls,cls.Capacity);
            if (cls.ToString() == "RemoteFrame.PointerSink") sinkWindow = child;
        }
        return true;
    };
    Native.EnumDesktopWindows(Native.GetThreadDesktop(Native.GetCurrentThreadId()),locateSink,0);
    if (sinkWindow == 0) throw new Exception("Missing fixture input sink.");
    // This control transfers capture to its parent, as composite/windowless controls can.
    desktop.Apply(new InputEvent("pointerdown", X:100, Y:200));
    desktop.Apply(new InputEvent("pointerup", X:100, Y:120)); // Over a DIFFERENT button.
    WaitFor("captured release");
    desktop.Apply(new InputEvent("key", Key:"~"));
    WaitFor("stale hover safely released");
    desktop.Apply(new InputEvent("pointerdown", X:100, Y:200));
    Thread.Sleep(100);
    desktop.Apply(new InputEvent("release"));
    WaitFor("capture cancelled");
    desktop.Apply(new InputEvent("pointermove", X:100, Y:200));
    WaitFor("hover without held buttons");
    desktop.Apply(new InputEvent("pointerdown", X:100, Y:200));
    desktop.Apply(new InputEvent("pointermove", X:300, Y:300));
    WaitFor("intentional drag");
    desktop.Apply(new InputEvent("pointerup", X:300, Y:300));
    WaitFor("captured release");
    Console.WriteLine("PASS: Redirected capture receives release over another control; cancellation clears capture and button flags.");
    Console.WriteLine("PASS: Delayed capture and native mouse-key state stay balanced; intentional dragging still works.");
    var opening = Stopwatch.StartNew();
    Click(100, 120);
    if (opening.Elapsed >= TimeSpan.FromSeconds(1))
        throw new Exception("Opening a modal form blocked pointer input waiting for its message loop.");
    Console.WriteLine("PASS: Opening a modal form does not block input until the synchronous-send timeout.");
    // Let the app consume the queued click and enter its modal loop.
    Thread.Sleep(250);
    var form = VisibleModal();
    Console.WriteLine("PASS: Off-screen owned form is visible and origin-resetting PrintWindow pixels match input coordinates.");
    Click(form.Left + 100, form.Top + 120);
    var timer = Stopwatch.StartNew();
    while (timer.Elapsed < TimeSpan.FromSeconds(5))
    {
        desktop.Capture();
        if (File.ReadAllText(file) == "modal button clicked while active") break;
        Thread.Sleep(50);
    }
    if (File.ReadAllText(file) != "modal button clicked while active")
        throw new Exception("Modal button did not receive queued input with its own top-level window active.");
    Console.WriteLine("PASS: Real private-desktop modal button receives click, has active focus, closes, and persists its result.");
    Thread.Sleep(100);
    Click(100, 120);
    Thread.Sleep(250);
    form = VisibleModal();
    Click(form.Right - 25, form.Top + 16);
    timer.Restart();
    while (timer.Elapsed < TimeSpan.FromSeconds(5))
    {
        desktop.Capture();
        if (File.ReadAllText(file) == "title-bar close requested") break;
        Thread.Sleep(50);
    }
    if (File.ReadAllText(file) != "title-bar close requested")
        throw new Exception("The modal form's title-bar close did not follow the normal WM_CLOSE path.");
    Console.WriteLine("PASS: Modal title-bar X follows the normal close path.");
}
finally { File.Delete(file); }

internal static class ModalFixture
{
    private delegate nint WindowProc(nint window, uint message, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style; public WindowProc Proc; public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? Menu; public string Name;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClass(ref WindowClass value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out Native.Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Native.Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Native.Message message);
    [DllImport("user32.dll")] private static extern bool EnableWindow(nint window, bool enabled);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetActiveWindow();
    [DllImport("user32.dll")] private static extern nint SetCapture(nint window);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll")] private static extern uint GetMessagePos();
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int index);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int result);
    private static readonly WindowProc Proc = Handle;
    private static string path = "";
    private static nint main, modal, sink;
    private static bool redirected;
    private static bool staleHover;

    public static void Run(string document)
    {
        path = document;
        Native.SetThreadDpiAwarenessContext(-4);
        foreach (var name in new[] { "XLMAIN", "RemoteFrame.ModalFixture", "RemoteFrame.PointerSink" })
        {
            var cls = new WindowClass { Proc = Proc, Name = name, Background = name == "RemoteFrame.ModalFixture" ? GetStockObject(4) : 6 };
            if (RegisterClass(ref cls) == 0) throw new Exception("Cannot register fixture window.");
        }
        main = CreateWindowEx(0, "XLMAIN", "Pointer fixture", 0x10cf0000, 0, 0, 1280, 800, 0, 0, 0, 0);
        CreateWindowEx(0, "BUTTON", "Open form", 0x50000000, 40, 60, 180, 50, main, 1, 0, 0);
        sink = CreateWindowEx(0, "RemoteFrame.PointerSink", "Capture probe", 0x50000000, 40, 140, 180, 50, main, 3, 0, 0);
        while (true)
        {
            Native.PeekMessage(out _, 0, 0, 0, 0); // Exercise inspection without removal as Excel's loops do.
            if (GetMessage(out var message, 0, 0, 0) <= 0) break;
            TranslateMessage(ref message); DispatchMessage(ref message);
        }
    }

    private static nint Handle(nint window, uint message, nuint wParam, nint lParam)
    {
        if (window == sink && message == 0x102 && wParam == '~')
        {
            staleHover = true;
            // A filtered application loop can consume a button-up before an older move.
            Native.PostMessage(sink, 0x200, unchecked((nuint)0x52464d5000000001UL), 100 | (200 << 16));
            return 0;
        }
        if (window == modal && message is 0x317 or 0x318 or 0x14) // Print/erase: emulate a reset target DC origin.
        {
            Native.SetViewportOrgEx((nint)wParam, 0, 0, out _);
            Native.PatBlt((nint)wParam, 0, 0, 400, 300, 0x00000042);
            return message == 0x14 ? 1 : 0;
        }
        if (window == sink && message == 0x201)
        {
            var messagePosition = GetMessagePos();
            if ((messagePosition & 65535) != 100 || (messagePosition >> 16) != 200)
            { File.WriteAllText(path,"incorrect native message position"); return 0; }
            if (((GetKeyState(16) & 0x8000) != 0) != ((wParam & 4) != 0))
            { File.WriteAllText(path,"mouse inherited stale Shift"); return 0; }
            if ((GetKeyState(1) & 0x8000) == 0) { File.WriteAllText(path,"press missing native key state"); return 0; }
            Thread.Sleep(200); // Excel can enter capture only after a busy GUI thread processes the press.
            redirected = true; SetCapture(main); return 0;
        }
        if (window == main && redirected && message == 0x202)
        {
            redirected = false; ReleaseCapture();
            File.WriteAllText(path,(GetKeyState(1) & 0x8000) == 0 ? "captured release" : "stuck native key state"); return 0;
        }
        if (window == main && redirected && message == 0x1f)
        {
            redirected = false; ReleaseCapture();
            File.WriteAllText(path,(GetKeyState(1) & 0x8000) == 0 ? "capture cancelled" : "cancel left native key down"); return 0;
        }
        if (window == main && redirected && message == 0x200)
        {
            File.WriteAllText(path, (wParam & 1) != 0 && (GetKeyState(1) & 0x8000) != 0 ? "intentional drag" : "incorrect drag state");
            return 0;
        }
        if (window == sink && message == 0x200)
        {
            bool up = (wParam & 0x13) == 0 && (GetKeyState(1) & 0x8000) == 0;
            File.WriteAllText(path, up ? (staleHover ? "stale hover safely released" : "hover without held buttons") : "stuck button flags");
            staleHover = false;
            return 0;
        }
        if (message == 0x111 && (wParam & 0xffff) == 1 && window == main)
        {
            EnableWindow(main, false);
            modal = CreateWindowEx(1, "RemoteFrame.ModalFixture", "Modal form", 0x10c80000, 1920, 280, 400, 300, main, 0, 0, 0);
            CreateWindowEx(0, "BUTTON", "Close form", 0x50000000, 40, 60, 180, 50, modal, 2, 0, 0);
            Native.SetActiveWindow(modal);
            while (Native.IsWindow(modal) && GetMessage(out var next, 0, 0, 0) > 0)
            { TranslateMessage(ref next); DispatchMessage(ref next); }
            EnableWindow(main, true);
            return 0;
        }
        if (message == 0x111 && (wParam & 0xffff) == 2 && window == modal)
        {
            File.WriteAllText(path, GetActiveWindow() == modal ? "modal button clicked while active" : "wrong active window");
            DestroyWindow(modal);
            return 0;
        }
        if (message == 0x10 && window == modal) File.WriteAllText(path, "title-bar close requested");
        if (message == 2 && window == main) PostQuitMessage(0);
        return DefWindowProc(window, message, wParam, lParam);
    }
}
