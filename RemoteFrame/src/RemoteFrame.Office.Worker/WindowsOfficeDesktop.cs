using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;
using RemoteFrame.Core;

namespace RemoteFrame.Office.Worker;

// All desktop APIs stay on this dedicated worker's synchronous main thread.
internal sealed class WindowsOfficeDesktop : IDisposable
{
    public string Application { get; private set; } = "";
    public int Width { get; private set; } = 1280;
    public int Height { get; private set; } = 800;
    public void Resize(int width, int height)
    {
        new FrameConfiguration(CaptureWidth: width, CaptureHeight: height, StreamWidth: width, StreamHeight: height).Validate();
        if (width == Width && height == Height) return;
        Check(Native.MoveWindow(mainWindow, 0, 0, width, height, true), "Resize Office window");
        Width = width; Height = height;
    }
    private readonly nint originalDesktop;
    private nint desktop, job, mainWindow;
    private readonly string profile = Path.Combine(Path.GetTempPath(), "RemoteFrame-" + Guid.NewGuid().ToString("N"));
    private readonly string desktopName = "RemoteFrame-" + Guid.NewGuid().ToString("N");
    private uint buttons;
    private readonly Dictionary<int, nint> pressedTargets = new();
    private readonly CloseButtonGesture closeButton = new();
    private nint inputModule;
    private readonly Dictionary<uint, (nint Call, nint Queue)> inputHooks = new();
    private const ulong PointerTag = 0x52464d5000000000UL;
    private string source = "", working = "", sourceHash = "", savedHash = "";
    private bool opened;
    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public WindowsOfficeDesktop(string engine, string document)
        : this(new OfficeApplication(engine, OfficeApplicationKind.LibreOffice), document) { }

    internal WindowsOfficeDesktop(OfficeApplication application, string document)
    {
        Native.SetThreadDpiAwarenessContext(-4); // Use physical pixels for window sizing and capture.
        originalDesktop = Native.GetThreadDesktop(Native.GetCurrentThreadId());
        try
        {
            Directory.CreateDirectory(profile);
            source = Path.GetFullPath(document);
            Directory.CreateDirectory(Path.Combine(profile, "document"));
            working = Path.Combine(profile, "document", Path.GetFileName(document));
            sourceHash = Hash(source);
            File.Copy(source, working);
            savedHash = Hash(working);
            if (sourceHash != savedHash || Hash(source) != sourceHash)
                throw new IOException("The server file changed while opening the Office session.");
            Directory.CreateDirectory(Path.Combine(profile, "user"));
            File.WriteAllText(Path.Combine(profile, "user", "registrymodifications.xcu"), """
                <?xml version="1.0" encoding="UTF-8"?>
                <oor:items xmlns:oor="http://openoffice.org/2001/registry" xmlns:xs="http://www.w3.org/2001/XMLSchema">
                  <item oor:path="/org.openoffice.Office.Update/Update">
                    <prop oor:name="Enabled" oor:op="fuse"><value>false</value></prop>
                  </item>
                  <item oor:path="/org.openoffice.Office.Jobs/Jobs/org.openoffice.Office.Jobs:Job['UpdateCheck']/Arguments">
                    <prop oor:name="AutoCheckEnabled" oor:type="xs:boolean" oor:op="fuse"><value>false</value></prop>
                    <prop oor:name="AutoDownloadEnabled" oor:type="xs:boolean" oor:op="fuse"><value>false</value></prop>
                  </item>
                  <item oor:path="/org.openoffice.Office.Common/Misc">
                    <prop oor:name="FirstRun" oor:op="fuse"><value>false</value></prop>
                    <prop oor:name="ShowTipOfTheDay" oor:op="fuse"><value>false</value></prop>
                  </item>
                  <item oor:path="/org.openoffice.Office.Common/Security/Scripting">
                    <prop oor:name="MacroSecurityLevel" oor:op="fuse"><value>3</value></prop>
                  </item>
                  <item oor:path="/org.openoffice.Setup/Product">
                    <prop oor:name="WhatsNew" oor:op="fuse"><value>false</value></prop>
                    <prop oor:name="WhatsNewDialog" oor:op="fuse"><value>false</value></prop>
                  </item>
                </oor:items>
                """);
            if (application.Kind == OfficeApplicationKind.LibreOffice)
            {
                Environment.SetEnvironmentVariable("SAL_DISABLESKIA", "1");
                Environment.SetEnvironmentVariable("PYTHONDONTWRITEBYTECODE", "1");
            }
            desktop = Native.CreateDesktop(desktopName, null, 0, 0, 0x01ff, 0);
            Check(desktop != 0, "Create private desktop");
            Check(Native.SetThreadDesktop(desktop), "Attach private desktop");
            Native.PeekMessage(out _, 0, 0, 0, 0); // create the worker thread's input queue
            job = Native.CreateJobObject(0, null);
            Check(job != 0, "Create Office process job");
            var limits = new Native.JobLimits();
            limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            Check(Native.SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<Native.JobLimits>()), "Configure process cleanup");
            var startup = new Native.StartupInfo { Size = Marshal.SizeOf<Native.StartupInfo>(), Desktop = desktopName };
            var command = new StringBuilder(application.CommandLine(working, profile));
            Check(Native.CreateProcess(application.Executable, command, 0, 0, false, 0x08000004, 0, Path.GetDirectoryName(application.Executable), ref startup, out var child), "Launch isolated Office application");
            try
            {
                Check(Native.AssignProcessToJobObject(job, child.Process), "Assign Office process cleanup");
                Check(Native.ResumeThread(child.Thread) != uint.MaxValue, "Resume Office process");
            }
            catch { Native.TerminateProcess(child.Process, 1); throw; }
            finally { Native.CloseHandle(child.Thread); Native.CloseHandle(child.Process); }
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(90))
            {
                mainWindow = Windows().FirstOrDefault(window =>
                {
                    var name = new StringBuilder(256);
                    Native.GetClassName(window, name, name.Capacity);
                    Native.GetWindowThreadProcessId(window, out var pid);
                    using var owner = Process.GetProcessById((int)pid);
                    return Native.IsProcessInJob(owner.Handle, job, out var belongs) && belongs &&
                        application.MatchesWindow(name.ToString()) && Native.GetWindowTextLength(window) > 0;
                });
                if (mainWindow != 0) break;
                Thread.Sleep(100);
            }
            if (mainWindow == 0)
            {
                var diagnostic = Windows().Select(window =>
                {
                    var name = new StringBuilder(256); var title = new StringBuilder(256);
                    Native.GetClassName(window, name, name.Capacity); Native.GetWindowText(window, title, title.Capacity);
                    return name + ": " + title;
                });
                throw new IOException($"{application.Kind} did not open an isolated document window within 90 seconds. " + string.Join("; ", diagnostic));
            }
            Native.ShowWindow(mainWindow, 9); // Restore before sizing, including maximized default windows.
            Native.MoveWindow(mainWindow, 0, 0, Width, Height, true);
            Thread.Sleep(400);
            Application = application.Kind.ToString();
            opened = true;
        }
        catch { Dispose(); throw; }
    }

    private List<nint> Windows()
    {
        var windows = new List<nint>();
        Native.EnumWindowsCallback callback = (window, _) =>
        {
            var name = new StringBuilder(256);
            Native.GetClassName(window, name, name.Capacity);
            if (Native.IsWindowVisible(window) && !name.ToString().StartsWith("UAC", StringComparison.Ordinal)) windows.Add(window);
            return true;
        };
        Native.EnumDesktopWindows(desktop, callback, 0);
        return windows;
    }

    public byte[] Capture()
    {
        PersistSavedCopy();
        if (!Native.IsWindow(mainWindow)) throw new IOException("The Office document window has closed.");
        var windows = Windows();
        foreach (var window in windows)
        {
            if (window == mainWindow || !IsOwnedBy(window, mainWindow) || Native.GetWindowTextLength(window) == 0 ||
                !Native.GetWindowRect(window, out var r)) continue;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0) continue;
            int x = Math.Clamp(r.Left, 0, Math.Max(0, Width - w));
            int y = Math.Clamp(r.Top, 0, Math.Max(0, Height - h));
            // Office may restore a UserForm on a host monitor outside our private viewport.
            // Center wholly off-screen dialogs; clamp partially visible ones without resizing them.
            if (r.Left >= Width || r.Top >= Height || r.Right <= 0 || r.Bottom <= 0)
            { x = Math.Max(0, (Width - w) / 2); y = Math.Max(0, (Height - h) / 2); }
            if (x != r.Left || y != r.Top) Native.MoveWindow(window, x, y, w, h, true);
        }
        var dc = Native.CreateCompatibleDC(0);
        Check(dc != 0, "Create capture context");
        nint bitmap = 0, previous = 0;
        try
        {
            var header = new Native.BitmapInfo { Size = 40, Width = Width, Height = -Height, Planes = 1, BitCount = 32 };
            bitmap = Native.CreateDIBSection(dc, ref header, 0, out var pixels, 0, 0);
            Check(bitmap != 0, "Create capture bitmap");
            previous = Native.SelectObject(dc, bitmap);
            Native.PatBlt(dc, 0, 0, Width, Height, 0x00000042);
            foreach (var window in windows.AsEnumerable().Reverse())
            {
                if (!Native.GetWindowRect(window, out var rect) || rect.Right <= 0 || rect.Bottom <= 0 ||
                    rect.Left >= Width || rect.Top >= Height) continue;
                PaintWindow(dc, window, rect);
            }
            Native.GdiFlush();
            var result = new byte[Width * Height * 4];
            Marshal.Copy(pixels, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (previous != 0) Native.SelectObject(dc, previous);
            if (bitmap != 0) Native.DeleteObject(bitmap);
            Native.DeleteDC(dc);
        }
    }

    private static void PaintWindow(nint destination, nint window, Native.Rect rect)
    {
        // PrintWindow can reset the supplied DC's origin (Excel/MSForms does this).
        // Render locally, then position pixels ourselves so input and capture share coordinates.
        int width = Math.Min(3840, rect.Right - rect.Left), height = Math.Min(2160, rect.Bottom - rect.Top);
        if (width <= 0 || height <= 0) return;
        var local = Native.CreateCompatibleDC(destination);
        Check(local != 0, "Create window capture context");
        nint bitmap = 0, previous = 0;
        try
        {
            var header = new Native.BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            bitmap = Native.CreateDIBSection(local, ref header, 0, out _, 0, 0);
            Check(bitmap != 0, "Create window capture bitmap");
            previous = Native.SelectObject(local, bitmap);
            Native.PatBlt(local, 0, 0, width, height, 0x00000042);
            Native.PrintWindow(window, local, 2);
            // Applications may also leave a changed origin after painting.
            Native.SetViewportOrgEx(local, 0, 0, out _);
            Native.BitBlt(destination, rect.Left, rect.Top, width, height, local, 0, 0, 0x00cc0020);
        }
        finally
        {
            if (previous != 0) Native.SelectObject(local, previous);
            if (bitmap != 0) Native.DeleteObject(bitmap);
            Native.DeleteDC(local);
        }
    }

    public void Apply(InputEvent input)
    {
        var point = new Native.Point { X = Math.Clamp(input.X, 0, Width - 1), Y = Math.Clamp(input.Y, 0, Height - 1) };
        if (input.Kind.StartsWith("pointer", StringComparison.Ordinal) || input.Kind == "wheel")
        {
            // Match releases to their press even when a dialog, another control or the
            // title bar is now under the pointer. Native controls can redirect capture.
            if (input.Kind == "pointerup" && pressedTargets.Remove(input.Button, out var pressed))
            {
                buttons &= ~ButtonFlag(input.Button);
                PostPointer(CaptureTarget(pressed), input, point);
                return;
            }
            if (input.Kind == "pointermove" && pressedTargets.Count != 0)
            {
                PostPointer(CaptureTarget(pressedTargets.Values.First()), input, point);
                return;
            }
            var target = Windows().FirstOrDefault(window => Native.IsWindowEnabled(window) && Native.GetWindowRect(window, out var r) &&
                point.X >= r.Left && point.Y >= r.Top && point.X < r.Right && point.Y < r.Bottom);
            if (target == 0) return; // Do not send input to the disabled document behind a modal form.
            // Title-bar controls are non-client areas: client mouse messages cannot activate X.
            // Query with screen coordinates before translating or descending into child controls.
            var hit = Native.SendMessageTimeout(target, 0x84, 0, Pack(point), 2, 250, out var hitTest);
            bool overClose = hit != 0 && hitTest == 20 && Native.IsWindowEnabled(target); // HTCLOSE
            if (closeButton.Update(input.Kind, input.Button, target, overClose))
            {
                buttons &= ~1u;
                // Queue rather than synchronously entering Excel's modal close/VBA handler.
                // SC_CLOSE follows the normal QueryClose path; never destroy the form directly.
                Native.PostMessage(target, 0x112, 0xf060, Pack(point)); // WM_SYSCOMMAND / SC_CLOSE
                return;
            }
            if (overClose && input.Kind != "wheel")
            {
                if (input.Kind == "pointerdown" && input.Button == 0) buttons &= ~1u;
                return;
            }
            if (input.Kind == "pointerup") return; // No matching client-area press.
            var screenPoint = point;
            Native.ScreenToClient(target, ref point);
            while (true)
            {
                var child = Native.ChildWindowFromPointEx(target, point, 7);
                if (child == 0 || child == target) break;
                Native.MapWindowPoints(target, child, ref point, 1);
                target = child;
            }
            if (input.Kind == "pointerdown")
            {
                if (input.Button is < 0 or > 2) return;
                if (pressedTargets.ContainsKey(input.Button)) CancelPointer();
                buttons |= ButtonFlag(input.Button);
                pressedTargets[input.Button] = target;
                Focus(target);
            }
            PostPointer(target, input, screenPoint);
        }
        else if (input.Kind == "release")
        {
            CancelPointer();
        }
        else if (input.Kind is "key" or "save") Key(input);
    }

    private static uint ButtonFlag(int button) => button switch { 0 => 1u, 1 => 0x10u, 2 => 2u, _ => 0u };
    private static nint CaptureTarget(nint pressed)
    {
        if (!Native.IsWindow(pressed)) return 0;
        var info = new Native.GuiThreadInfo { Size = Marshal.SizeOf<Native.GuiThreadInfo>() };
        var thread = Native.GetWindowThreadProcessId(pressed, out _);
        return Native.GetGUIThreadInfo(thread, ref info) && info.Capture != 0 ? info.Capture : pressed;
    }
    private void PostPointer(nint target, InputEvent input, Native.Point screenPoint)
    {
        if (target == 0 || !Native.IsWindow(target)) return;
        var down = input.Button switch { 1 => 0x207u, 2 => 0x204u, _ => 0x201u };
        var message = input.Kind switch { "pointerdown" => down, "pointerup" => down + 1, "wheel" => 0x20au, _ => 0x200u };
        var data = (nuint)(buttons | (input.Ctrl ? 8u : 0u) | (input.Shift ? 4u : 0u));
        if (input.Kind == "wheel") data |= (nuint)(unchecked((ushort)(-Math.Clamp(input.Delta, -10, 10) * 120)) << 16);
        EnsureInputHooks(target);
        if (!Native.PostMessage(target, message, unchecked((nuint)(PointerTag | (uint)data)), Pack(screenPoint)))
            throw new IOException("Office could not queue pointer input.");
    }
    private void CancelPointer()
    {
        closeButton.Update("release", 0, 0, false);
        var targets = pressedTargets.Values.Select(CaptureTarget).Where(x => x != 0).Distinct().ToArray();
        pressedTargets.Clear();
        buttons = 0;
        // Cancel tracking without synthesizing a click at (0,0) or on another form.
        foreach (var target in targets)
        {
            EnsureInputHooks(target);
            Native.PostMessage(target, 0x1f, unchecked((nuint)PointerTag), 0); // WM_CANCELMODE
        }
    }

    private void Focus(nint target)
    {
        var thread = Native.GetWindowThreadProcessId(target, out _);
        if (Native.AttachThreadInput(Native.GetCurrentThreadId(), thread, true))
        {
            try
            {
                // A UserForm is a separate top-level window; activating its disabled owner
                // steals focus from the form controls.
                var root = Native.GetAncestor(target, 2); // GA_ROOT, not the owner chain
                Native.SetActiveWindow(root != 0 ? root : target);
                Native.SetFocus(target);
            }
            finally { Native.AttachThreadInput(Native.GetCurrentThreadId(), thread, false); }
        }
    }

    private void Key(InputEvent input)
    {
        var windows = Windows();
        // Office creates enabled helper/shadow windows too; they are not keyboard targets.
        var active = mainWindow;
        if (!Native.IsWindowEnabled(active))
            active = windows.FirstOrDefault(window => Native.IsWindowEnabled(window) &&
                Native.GetWindowTextLength(window) > 0 && IsOwnedBy(window, mainWindow));
        if (active == 0) throw new IOException("Office has no enabled document or dialog window.");
        var thread = Native.GetWindowThreadProcessId(active, out _);
        var info = new Native.GuiThreadInfo { Size = Marshal.SizeOf<Native.GuiThreadInfo>() };
        Native.GetGUIThreadInfo(thread, ref info);
        var target = info.Focus != 0 && (info.Focus == active || Native.IsChild(active, info.Focus)) ? info.Focus : active;
        var key = input.Kind == "save" ? "s" : input.Key ?? "";
        var ctrl = input.Ctrl || input.Kind == "save";
        SetModifiers(target, ctrl, input.Shift, input.Alt);
        if (key.Length == 1 && !ctrl && !input.Alt)
        {
            Native.PostMessage(target, 0x102, key[0], 1); // WM_CHAR includes Unicode UTF-16
            Thread.Sleep(20); // Allow VCL's deferred input queue to drain before changing modifier state.
            SetModifiers(target, false, false, false);
            return;
        }
        var code = key switch
        {
            "Enter" => 13,
            "Tab" => 9,
            "Escape" => 27,
            "Backspace" => 8,
            "Delete" => 46,
            "ArrowLeft" => 37,
            "ArrowUp" => 38,
            "ArrowRight" => 39,
            "ArrowDown" => 40,
            "PageUp" => 33,
            "PageDown" => 34,
            "Home" => 36,
            "End" => 35,
            _ when key.Length == 1 => char.ToUpperInvariant(key[0]),
            _ => 0
        };
        if (code == 0) return;
        try
        {
            var scan = Native.MapVirtualKey((uint)code, 0);
            var extended = code is >= 33 and <= 46 ? 0x01000000 : 0;
            var flags = (nint)(1 | (scan << 16) | (uint)extended);
            Native.PostMessage(target, input.Alt ? 0x104u : 0x100u, (nuint)code, flags);
            Thread.Sleep(80);
            Native.PostMessage(target, input.Alt ? 0x105u : 0x101u, (nuint)code, flags | unchecked((nint)0xc0000000));
            Thread.Sleep(20);
        }
        finally
        {
            // Enter/Escape can destroy a modal dialog before the key-up/reset runs.
            var resetTarget = Native.IsWindow(target) ? target : mainWindow;
            if (Native.IsWindow(resetTarget)) SetModifiers(resetTarget, false, false, false);
        }
    }

    private void SetModifiers(nint window, bool ctrl, bool shift, bool alt)
    {
        EnsureInputHooks(window);
        var mask = (ctrl ? 1 : 0) | (shift ? 2 : 0) | (alt ? 4 : 0);
        var result = Native.SendMessageTimeout(window, Native.RegisterWindowMessage("RemoteFrame.Office.SetModifiers.v1"),
            (nuint)mask, 0, 2, 2000, out _);
        if (result == 0)
        {
            var name = new StringBuilder(256);
            Native.GetClassName(window, name, name.Capacity);
            throw new IOException($"Update Office modifier state timed out for {name}.");
        }
        if (Native.GetProp(window, "RemoteFrame.ModifierMask") != mask + 1)
            throw new IOException("Office input helper did not receive the modifier command.");
    }

    private void EnsureInputHooks(nint window)
    {
        var thread = Native.GetWindowThreadProcessId(window, out _);
        Check(thread != 0, "Find Office input thread");
        if (!inputHooks.ContainsKey(thread))
        {
            if (inputModule == 0)
                inputModule = Native.LoadLibrary(Path.Combine(AppContext.BaseDirectory, "RemoteFrame.InputHook.dll"));
            Check(inputModule != 0, "Load bundled Office input helper");
            var callback = Native.GetProcAddress(inputModule, "RemoteFrameInputProc");
            Check(callback != 0, "Find bundled Office input callback");
            var hook = Native.SetWindowsHookEx(4, callback, inputModule, thread);
            Check(hook != 0, "Attach Office input helper");
            try
            {
                var queuedCallback = Native.GetProcAddress(inputModule, "RemoteFrameQueuedInputProc");
                Check(queuedCallback != 0, "Find queued Office input callback; rebuild the input helper");
                var queuedHook = Native.SetWindowsHookEx(3, queuedCallback, inputModule, thread); // WH_GETMESSAGE
                Check(queuedHook != 0, "Attach queued Office input helper");
                inputHooks.Add(thread, (hook, queuedHook));
            }
            catch { Native.UnhookWindowsHookEx(hook); throw; }
        }
    }

    private static nint Pack(Native.Point p) => (nint)((p.X & 0xffff) | ((p.Y & 0xffff) << 16));
    private static bool IsOwnedBy(nint window, nint owner)
    {
        for (var current = Native.GetWindow(window, 4); current != 0; current = Native.GetWindow(current, 4))
            if (current == owner) return true;
        return false;
    }
    private void PersistSavedCopy()
    {
        if (!opened || string.IsNullOrEmpty(working) || !File.Exists(working)) return;
        string current;
        try { current = Hash(working); }
        catch (IOException) { return; } // LibreOffice may be replacing its saved file.
        if (current == savedHash) return;
        if (Hash(source) != sourceHash) throw new IOException("The server file changed in another editor. The Office save was not copied back.");
        var staging = source + ".remoteframe-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var input = new FileStream(working, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                input.CopyTo(output);
            if (Hash(staging) != current) return;
            File.Move(staging, source, overwrite: true);
            savedHash = current; sourceHash = current;
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }
    private static void Check(bool value, string operation)
    { if (!value) throw new Win32Exception(Marshal.GetLastWin32Error(), operation); }
    public void Dispose()
    {
        try { PersistSavedCopy(); } catch (IOException ex) { Console.Error.WriteLine(ex.Message); }
        if (inputHooks.Count != 0 && Native.IsWindow(mainWindow))
            Native.SendMessageTimeout(mainWindow, Native.RegisterWindowMessage("RemoteFrame.Office.DisableCursor.v1"), 0, 0, 2, 1000, out _);
        foreach (var hook in inputHooks.Values)
        { Native.UnhookWindowsHookEx(hook.Call); Native.UnhookWindowsHookEx(hook.Queue); }
        inputHooks.Clear();
        if (inputModule != 0) { Native.FreeLibrary(inputModule); inputModule = 0; }
        if (job != 0) { Native.CloseHandle(job); job = 0; }
        if (desktop != 0)
        {
            Native.SetThreadDesktop(originalDesktop);
            Native.CloseDesktop(desktop); desktop = 0;
        }
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try { if (Directory.Exists(profile)) Directory.Delete(profile, true); break; }
            catch (IOException) { Thread.Sleep(100); }
            catch (UnauthorizedAccessException) { Thread.Sleep(100); }
        }
    }
}
