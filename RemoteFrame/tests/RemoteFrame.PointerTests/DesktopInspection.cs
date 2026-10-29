using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using RemoteFrame.Office.Worker;

// Read-only routing diagnostics for an existing worker. Never activate or click a window.
internal static class DesktopInspection
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder value, uint size, out uint needed);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint OpenDesktop(string name, uint flags, bool inherit, uint access);
    private delegate bool DesktopCallback([MarshalAs(UnmanagedType.LPWStr)] string name, nint parameter);
    [DllImport("user32.dll")] private static extern nint GetProcessWindowStation();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDesktops(nint station, DesktopCallback callback, nint parameter);
    public static void Run(int pid, bool trace = false, bool cursorProbe = false)
    {
        using var worker = Process.GetProcessById(pid);
        var names = new HashSet<string>();
        foreach (ProcessThread thread in worker.Threads)
        {
            var desktop = Native.GetThreadDesktop((uint)thread.Id);
            var name = new StringBuilder(512);
            if (GetUserObjectInformation(desktop, 2, name, 1024, out _) && name.ToString().StartsWith("RemoteFrame-"))
                names.Add(name.ToString());
        }
        if (names.Count == 0)
        {
            // Thread desktop handles from another process need not be usable here.
            Console.WriteLine("Thread desktop unavailable; listing RemoteFrame desktops in this window station.");
            DesktopCallback collect = (name, _) => { if (name.StartsWith("RemoteFrame-")) names.Add(name); return true; };
            EnumDesktops(GetProcessWindowStation(), collect, 0);
        }
        if (names.Count == 0) throw new Exception("No worker private desktop was found.");
        foreach (var name in names)
        {
            var desktop = OpenDesktop(name, 0, false, 0x1ff);
            if (desktop == 0) throw new Exception("Cannot inspect the worker desktop.");
            var original = Native.GetThreadDesktop(Native.GetCurrentThreadId());
            // Associate only this diagnostic thread; do not switch the visible input desktop.
            var associated = Native.SetThreadDesktop(desktop);
            try
            {
                Console.WriteLine("Desktop: " + name);
                Native.EnumWindowsCallback callback = (window, _) =>
                {
                    if (!Native.IsWindowVisible(window)) return true;
                    var cls = new StringBuilder(256);
                    Native.GetClassName(window, cls, cls.Capacity);
                    Native.GetWindowRect(window, out var rect);
                    Console.WriteLine($"0x{window:X} class={cls} enabled={Native.IsWindowEnabled(window)} owner=0x{Native.GetWindow(window,4):X} rect={rect.Left},{rect.Top},{rect.Right},{rect.Bottom}");
                    var thread = Native.GetWindowThreadProcessId(window, out var windowProcess);
                    var gui = new Native.GuiThreadInfo { Size = Marshal.SizeOf<Native.GuiThreadInfo>() };
                    if (Native.GetGUIThreadInfo(thread, ref gui))
                        Console.WriteLine($"  thread={thread} active=0x{gui.Active:X} focus=0x{gui.Focus:X} capture=0x{gui.Capture:X}");
                    if (trace && cls.ToString() == "XLMAIN")
                    {
                        var module = Native.LoadLibrary(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                            "../../../../../artifacts/input-trace/" + (cursorProbe ? "RemoteFrame.CursorProbe.dll" : "RemoteFrame.InputTrace.dll"))));
                        if (module == 0) throw new Exception("Build tests/BuildInputTrace.cmd into artifacts/input-trace first.");
                        nint hook = 0, control = 0;
                        try
                        {
                            var callbackAddress = Native.GetProcAddress(module,cursorProbe ? "RemoteFrameCursorProbeProc" : "RemoteFrameTraceInputProc");
                            if (cursorProbe) control = Native.SetWindowsHookEx(4,Native.GetProcAddress(module,"RemoteFrameCursorControlProc"),module,thread);
                            hook = Native.SetWindowsHookEx(3,callbackAddress,module,thread);
                            if (hook == 0) throw new Exception("Cannot attach read-only input trace.");
                            Console.WriteLine("TRACE: " + Path.Combine(Path.GetTempPath(),$"RemoteFrame-input-{windowProcess}.log"));
                            Thread.Sleep(50000);
                        }
                        finally
                        {
                            if (cursorProbe)
                            {
                                Native.SendMessageTimeout(window,Native.RegisterWindowMessage("RemoteFrame.CursorProbe.Stop.v1"),0,0,2,2000,out var stopped);
                                Console.WriteLine("Virtual cursor calls: " + Native.GetProp(window,"RemoteFrame.CursorProbe.Calls"));
                                Console.WriteLine("Virtual cursor import installed: " + Native.GetProp(window,"RemoteFrame.CursorProbe.Installed"));
                            }
                            if (hook != 0) Native.UnhookWindowsHookEx(hook);
                            if (control != 0) Native.UnhookWindowsHookEx(control);
                            Native.FreeLibrary(module);
                        }
                    }
                    foreach (var p in new[] {new Native.Point {X=298,Y=16},new Native.Point {X=180,Y=180}})
                    {
                        if (p.X < rect.Left || p.Y < rect.Top || p.X >= rect.Right || p.Y >= rect.Bottom) continue;
                        Native.SendMessageTimeout(window, 0x84, 0, (p.X & 65535) | ((p.Y & 65535) << 16), 2, 250, out var hit);
                        Console.WriteLine($"  point={p.X},{p.Y} hit={hit}");
                    }
                    return true;
                };
                Native.EnumDesktopWindows(desktop, callback, 0);
            }
            finally
            {
                if (associated) Native.SetThreadDesktop(original);
                Native.CloseDesktop(desktop);
            }
        }
    }
}
