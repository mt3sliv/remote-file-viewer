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
    public const int Width = 1280, Height = 800;
    private readonly nint originalDesktop;
    private nint desktop, job, mainWindow;
    private readonly string profile = Path.Combine(Path.GetTempPath(), "RemoteFrame-" + Guid.NewGuid().ToString("N"));
    private readonly string desktopName = "RemoteFrame-" + Guid.NewGuid().ToString("N");
    private uint buttons;
    private nint inputModule;
    private readonly Dictionary<uint, nint> inputHooks = new();
    private string source = "", working = "", sourceHash = "", savedHash = "";
    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public WindowsOfficeDesktop(string engine, string document)
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
                <oor:items xmlns:oor="http://openoffice.org/2001/registry">
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
            Environment.SetEnvironmentVariable("SAL_DISABLESKIA", "1");
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
            var command = new StringBuilder($"\"{engine}\" -env:UserInstallation={new Uri(profile).AbsoluteUri} --norestore --nodefault --nofirststartwizard \"{working}\"");
            Check(Native.CreateProcess(engine, command, 0, 0, false, 0x08000004, 0, Path.GetDirectoryName(engine), ref startup, out var child), "Launch bundled LibreOffice");
            try
            {
                Check(Native.AssignProcessToJobObject(job, child.Process), "Assign Office process cleanup");
                Check(Native.ResumeThread(child.Thread) != uint.MaxValue, "Resume Office process");
            }
            catch { Native.TerminateProcess(child.Process, 1); throw; }
            finally { Native.CloseHandle(child.Thread); Native.CloseHandle(child.Process); }
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(35))
            {
                mainWindow = Windows().FirstOrDefault(window =>
                {
                    var name = new StringBuilder(256);
                    Native.GetClassName(window, name, name.Capacity);
                    return name.ToString().Contains("SALFRAME", StringComparison.OrdinalIgnoreCase) && Native.GetWindowTextLength(window) > 0;
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
                throw new IOException("LibreOffice did not open a document window within 35 seconds. " + string.Join("; ", diagnostic));
            }
            Native.ShowWindow(mainWindow, 9); // Restore before sizing, including maximized default windows.
            Native.MoveWindow(mainWindow, 0, 0, Width, Height, true);
            Thread.Sleep(400);
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
            foreach (var window in Windows().AsEnumerable().Reverse())
            {
                Native.GetWindowRect(window, out var rect);
                Native.SetViewportOrgEx(dc, rect.Left, rect.Top, out _);
                Native.PrintWindow(window, dc, 2);
            }
            Native.SetViewportOrgEx(dc, 0, 0, out _);
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

    public void Apply(InputEvent input)
    {
        var point = new Native.Point { X = Math.Clamp(input.X, 0, Width - 1), Y = Math.Clamp(input.Y, 0, Height - 1) };
        if (input.Kind.StartsWith("pointer", StringComparison.Ordinal) || input.Kind == "wheel")
        {
            var target = Windows().FirstOrDefault(window => Native.GetWindowRect(window, out var r) &&
                point.X >= r.Left && point.Y >= r.Top && point.X < r.Right && point.Y < r.Bottom);
            if (target == 0) target = mainWindow;
            Native.ScreenToClient(target, ref point);
            while (true)
            {
                var child = Native.ChildWindowFromPointEx(target, point, 7);
                if (child == 0 || child == target) break;
                Native.MapWindowPoints(target, child, ref point, 1);
                target = child;
            }
            var flag = input.Button switch { 1 => 0x10u, 2 => 2u, _ => 1u };
            var down = input.Button switch { 1 => 0x207u, 2 => 0x204u, _ => 0x201u };
            if (input.Kind == "pointerdown") { buttons |= flag; Focus(target); }
            if (input.Kind == "pointerup") buttons &= ~flag;
            var message = input.Kind switch { "pointerdown" => down, "pointerup" => down + 1, "wheel" => 0x20au, _ => 0x200u };
            var data = (nuint)buttons;
            if (input.Kind == "wheel")
            {
                Native.ClientToScreen(target, ref point);
                data |= (nuint)(unchecked((ushort)(-Math.Clamp(input.Delta, -10, 10) * 120)) << 16);
            }
            Native.SendMessageTimeout(target, message, data, Pack(point), 2, 2000, out _);
        }
        else if (input.Kind == "release")
        {
            if (buttons != 0)
            {
                var released = input with { Kind = "pointerup" };
                foreach (var button in new[] { 0, 1, 2 }) Apply(released with { Button = button });
            }
            buttons = 0;
        }
        else if (input.Kind is "key" or "save") Key(input);
    }

    private void Focus(nint target)
    {
        var thread = Native.GetWindowThreadProcessId(target, out _);
        if (Native.AttachThreadInput(Native.GetCurrentThreadId(), thread, true))
        {
            try { Native.SetActiveWindow(mainWindow); Native.SetFocus(target); }
            finally { Native.AttachThreadInput(Native.GetCurrentThreadId(), thread, false); }
        }
    }

    private void Key(InputEvent input)
    {
        var windows = Windows();
        var active = windows.FirstOrDefault(window => Native.IsWindowEnabled(window));
        if (active == 0) active = mainWindow;
        var thread = Native.GetWindowThreadProcessId(active, out _);
        var info = new Native.GuiThreadInfo { Size = Marshal.SizeOf<Native.GuiThreadInfo>() };
        Native.GetGUIThreadInfo(thread, ref info);
        var target = info.Focus != 0 ? info.Focus : active;
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
            SetModifiers(target, false, false, false);
        }
    }

    private void SetModifiers(nint window, bool ctrl, bool shift, bool alt)
    {
        var thread = Native.GetWindowThreadProcessId(window, out _);
        if (!inputHooks.ContainsKey(thread))
        {
            if (inputModule == 0)
                inputModule = Native.LoadLibrary(Path.Combine(AppContext.BaseDirectory, "RemoteFrame.InputHook.dll"));
            Check(inputModule != 0, "Load bundled Office input helper");
            var callback = Native.GetProcAddress(inputModule, "RemoteFrameInputProc");
            Check(callback != 0, "Find bundled Office input callback");
            var hook = Native.SetWindowsHookEx(4, callback, inputModule, thread);
            Check(hook != 0, "Attach Office input helper");
            inputHooks.Add(thread, hook);
        }
        var mask = (ctrl ? 1 : 0) | (shift ? 2 : 0) | (alt ? 4 : 0);
        var result = Native.SendMessageTimeout(window, Native.RegisterWindowMessage("RemoteFrame.Office.SetModifiers.v1"),
            (nuint)mask, 0, 2, 2000, out _);
        Check(result != 0, "Update Office modifier state");
        if (Native.GetProp(window, "RemoteFrame.ModifierMask") != mask + 1)
            throw new IOException("Office input helper did not receive the modifier command.");
    }

    private static nint Pack(Native.Point p) => (nint)((p.X & 0xffff) | ((p.Y & 0xffff) << 16));
    private void PersistSavedCopy()
    {
        if (string.IsNullOrEmpty(working) || !File.Exists(working)) return;
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
        foreach (var hook in inputHooks.Values) Native.UnhookWindowsHookEx(hook);
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
