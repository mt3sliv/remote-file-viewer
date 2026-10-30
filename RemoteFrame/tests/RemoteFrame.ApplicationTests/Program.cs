using RemoteFrame.Office.Worker;

static void Assert(bool condition, string message)
{ if (!condition) throw new Exception(message); }

var close = new CloseButtonGesture();
Assert(!close.Update("pointerup", 0, 1, true), "Unmatched release must not close a form.");
close.Update("pointerdown", 0, 1, true);
Assert(close.Update("pointerup", 0, 1, true), "Press/release on the same X must request close.");
Assert(!close.Update("pointerup", 0, 1, true), "Close gesture must run only once.");
close.Update("pointerdown", 0, 1, true);
Assert(!close.Update("pointerup", 0, 1, false), "Dragging off X must cancel close.");
close.Update("pointerdown", 0, 1, true);
Assert(!close.Update("pointerup", 0, 2, true), "Release over a different window must not close it.");
close.Update("pointerdown", 0, 1, true);
close.Update("release", 0, 0, false);
Assert(!close.Update("pointerup", 0, 1, true), "Lost pointer capture must cancel close.");
close.Update("pointerdown", 1, 1, true);
Assert(!close.Update("pointerup", 1, 1, true), "Non-left click must not close a form.");
close.Update("pointerdown", 0, 1, false);
Assert(!close.Update("pointerup", 0, 1, true), "Press in the client area must not arm X.");
Console.WriteLine("PASS: Form close gesture, drag-off, different-window, cancellation and mouse-button checks.");

var root = Path.GetFullPath("test apps");
string App(string folder, string name) => Path.Combine(root, folder, name);
var excel = App("Microsoft Office", "EXCEL.EXE");
var word = App("Microsoft Office", "WINWORD.EXE");
var installed = App("LibreOffice", "soffice.exe");
var bundled = App("bundled", "soffice.exe");
var unknown = App("unknown", "viewer.exe");
IReadOnlyList<OfficeApplication> Select(string ext, string? association, string[] apps, bool prefer = true) =>
    OfficeApplication.Select(ext, bundled, association, apps, p => p != unknown, prefer);

Assert(Select(".xlsx", installed, [excel, installed]).Select(x => x.Executable).SequenceEqual([installed, excel, bundled]),
    "Supported default must precede installed alternatives and bundled engine; candidates must be deduplicated.");
Assert(Select(".XLSX", unknown, [word, excel]).Select(x => x.Executable).SequenceEqual([excel, bundled]),
    "Unsupported association and incompatible Office applications must be ignored.");
Assert(Select(".xlsx", excel, [excel], false).Single().Executable == bundled, "Bundled-only override failed.");
Assert(Select(".pptx", word, [excel, word]).Single().Executable == bundled, "Unsupported native presentation adapters must fall back.");
Assert(Select(".xlsm", excel, []).First().Kind == OfficeApplicationKind.Excel, "Macro-enabled workbook extension not recognized.");
Assert(OfficeApplication.Select(".xlsx", "", excel, [], _ => true, true).Single().Kind == OfficeApplicationKind.Excel,
    "Installed Excel must work without bundled LibreOffice.");
Assert(OfficeApplication.Select(".xlsx", "", null, [], _ => true, true).Count == 0,
    "Absent optional engine must not produce an invalid fallback candidate.");
Assert(OfficeApplication.Select(".xlsx", "", excel, [], _ => true, false).Count == 0,
    "Bundled-only mode must not silently use installed Office when the engine is absent.");
try
{
    OfficeApplication.OpenFirst<string>([], _ => throw new Exception("No candidate should be launched."));
    throw new Exception("Missing applications must fail.");
}
catch (IOException ex) { Assert(ex.Message.Contains("WithLibreOffice"), "Missing-application error must explain the optional engine package."); }
Assert(OfficeApplication.Select(".xlsx", bundled, excel, [], _ => false, true).Single().Executable == bundled,
    "Uninstalled associations must fall back.");
Assert(OfficeApplication.Recognize("excel.exe", ".xlsx") is null, "Relative executable paths must be rejected.");
Assert(OfficeApplication.Recognize(word, ".docx")!.MatchesWindow("OpusApp"), "Word window adapter failed.");
Assert(!OfficeApplication.Recognize(excel, ".xlsx")!.MatchesWindow("OpusApp"), "Excel must not select a Word window.");
var working = Path.Combine(root, "document folder", "test.xlsx");
var profile = Path.Combine(root, "session");
Assert(new OfficeApplication(excel, OfficeApplicationKind.Excel).CommandLine(working, profile) == $"\"{excel}\" /x \"{working}\"",
    "Excel must use a separate instance and quote document paths.");
Assert(new OfficeApplication(word, OfficeApplicationKind.Word).CommandLine(working, profile).Contains(" /n "),
    "Word must use a separate instance.");
Assert(new OfficeApplication(bundled, OfficeApplicationKind.LibreOffice).CommandLine(working, profile).Contains("-env:UserInstallation="),
    "LibreOffice must use a session-specific profile.");
var attempted = new List<string>();
var candidates = Select(".xlsx", installed, [excel]);
var selected = OfficeApplication.OpenFirst(candidates, app =>
{
    attempted.Add(app.Executable);
    if (app.Executable != bundled) throw new IOException("Simulated application startup failure");
    return app.Executable;
});
Assert(selected == bundled && attempted.SequenceEqual([installed, excel, bundled]), "Startup failures must advance through all candidates.");
try
{
    OfficeApplication.OpenFirst<string>(candidates, _ => throw new IOException("Simulated failure"));
    throw new Exception("All-app failure was hidden.");
}
catch (IOException ex) { Assert(ex.InnerException is AggregateException errors && errors.InnerExceptions.Count == 3, "All startup failures must be retained."); }
attempted.Clear();
OfficeApplication.OpenFirst(candidates, app => { attempted.Add(app.Executable); return app; });
Assert(attempted.Count == 1, "Successful open must stop fallback attempts.");
if (OperatingSystem.IsWindows())
    Console.WriteLine("Host discovery: " + string.Join(", ", OfficeApplication.Discover(working, bundled, true).Select(x => x.Kind + ": " + x.Executable)));
Console.WriteLine("PASS: application priority, compatibility, missing associations, deduplication, bundled-only, startup fallback/errors and isolated launch commands.");
