# Representative workbook: ExampleExcelWithUserForms.xlsm

Static inspection performed 2026-10-29, without executing VBA, activating COM objects, changing macro/ActiveX settings, or editing the workbook.

SHA-256: `AB2752710713536B7082945AA048E395F24807217439F17BB929CDC2891D1261`.
The supplied workbook and `samples/ExampleExcelWithUserForms.xlsm` are identical.

## Actual workflow

The workbook has one worksheet (`Sheet1`), one embedded worksheet ActiveX command button, and one VBA UserForm (`UserForm1`) containing a label and command button. The serialized form contains the text Refresh. Extracted VBA contains three modules: empty `ThisWorkbook`, `Sheet1`, and `UserForm1`. No workbook-open handler appears in those sources.

1. `Sheet1.CommandButton1_Click` calls `UserForm1.Show`.
2. The form's command-button handler calls `UserForm1.Hide`.
3. It creates the late-bound COM object `ExcelDaoBridge.Factory`.
4. `OpenOrCreateSample()` returns a real open DAO Database.
5. `db.OpenRecordset("SELECT * FROM [People]")` returns a DAO recordset.
6. `Sheet1.Range("A2").CopyFromRecordset rs` transfers the data to cells.
7. The recordset and database are closed on the successful path.

The workbook already contains the sample names/cities. Seeing those cells or a PNG preview does not prove that the form handler, COM activation, or the query ran.

## Dependencies and evidence

The adjacent `ExcelDaoComSample/ExcelDaoComSample/src/ExcelDaoBridge/DaoFactory.cs` matches the workbook's COM ProgID. It targets .NET Framework 4.8, returns actual DAO COM objects, tries `DAO.DBEngine.160` then `.120`, and seeds People with Ada Lovelace/London, Grace Hopper/New York, and Alan Turing/Manchester when creating a database.

Read-only checks found registrations for `ExcelDaoBridge.Factory` and `DAO.DBEngine.120` in this host process's registry view. The registered bridge DLL and ACE DAO DLL exist. These checks do not prove successful activation, dependency loading, matching Excel bitness, or execution in a RemoteFrame session. The bridge registration points to a DLL under the user's Downloads directory, rather than the adjacent repository build.

The bridge's default database is `%LOCALAPPDATA%/ExcelDaoBridge/sample.accdb`. That path is shared by sessions running under the same account. RemoteFrame's isolated document working copy does not isolate this database. Before executing the workflow in a multi-session test, provide a session-specific database path through a scoped bridge configuration or equivalent test setup; do not redirect all host application data.

## Compatibility assessment

**Installed Excel on Windows:** first target for functional acceptance. The workbook uses native Excel VBA, MSForms/ActiveX, Windows COM, and ACE/DAO. Macro and ActiveX policies, registration and bitness must permit the workflow. Nothing was enabled or changed during this inspection.

**Bundled LibreOffice on Windows:** unverified. Separate checkpoints are control import/click events, UserForm import/show/hide, COM activation, DAO dispatch and Excel Range recordset transfer. A static search found no `CopyFromRecordset` declaration/implementation in the pinned 26.2.5.2 `sc` and `oovbaapi` sources; treat this as a likely compatibility gap, not a measured runtime failure. LibreOffice documents [limited VBA support](https://help.libreoffice.org/latest/en-US/text/sbasic/shared/vbasupport.html). Excel explicitly supports [ADO and DAO recordsets in CopyFromRecordset](https://learn.microsoft.com/en-us/office/vba/api/excel.range.copyfromrecordset).

**Linux:** this exact workflow depends on the Windows-only .NET Framework COM bridge and ACE/DAO. Supporting its VBA syntax or form alone would not supply those dependencies; a different data-access backend/interface would also be required. RemoteFrame has no Linux Office worker yet.

RemoteFrame currently sets bundled LibreOffice's macro security level to 3 in each private profile. Successful XLSM preview from the packaged demo was not a macro execution test.

## Functional acceptance test

Use a disposable workbook copy and isolated test database, retaining the original VBA/form contents. Do not change global trust settings or register/install components automatically.

1. Establish a native Excel baseline through RemoteFrame's own private desktop and input protocol.
2. Clear the existing output cells in the test copy so cached data cannot satisfy the check.
3. Click the worksheet button; verify the form appears in streamed frames.
4. Click the form command button; verify the form hides and the database is queried.
5. Verify the regenerated IDs/names/cities against the isolated database (the SQL has no ORDER BY).
6. Save/reopen; check values and preservation of VBA modules, form and ActiveX control.
7. Run two sessions with separate test databases; verify workbook and database isolation.
8. Test bundled LibreOffice under an explicitly scoped macro-execution setup and record the first failing checkpoint. Do not start a source rebuild until that gap is demonstrated.

Native macro execution, form interaction, COM activation and DAO query execution have **not** been tested by this inspection. No VBA extension changes were made.

## Remote title-bar close handling

The Windows worker now hit-tests the top-level window before converting pointer coordinates to its client area. A left press/release over the same enabled window's title-bar X queues the normal `WM_SYSCOMMAND / SC_CLOSE` request. It does not destroy the window or bypass VBA `QueryClose` cancellation. Dragging off the X, releasing over another window, or losing pointer capture cancels the pending gesture.

Client-area input now activates the clicked control's own top-level window, rather than the main document behind it, and posts mouse messages into the application's queue. Disabled document windows are excluded from pointer targeting while a modal form is open. The real Windows private-desktop regression in `tests/RemoteFrame.PointerTests` opens a disposable native modal form, clicks its child button, checks that the form is active, and verifies that the button closes the form. This validates the input path but does not validate Excel's MSForms implementation or the supplied workbook's VBA/DAO workflow.

Mouse releases now retain the original pressed control and consult its GUI thread's capture window, rather than hit-testing a fresh target for every release. This matters for composite controls which move capture to a parent window. Cancellation sends `WM_CANCELMODE` to the tracking window and clears held-button state. The Windows regression also covers capture redirection, release over a different control, cancellation and subsequent hover without held-button flags. Browser regressions cover lost capture, cancellation, blur, hidden-page changes, missed-release reconciliation and chorded buttons. Preview.5 live checks below cover the supplied MSForms command button; independent DAO data validation remains an acceptance check.

Live read-only desktop inspection found the supplied form (`ThunderDFrame`) at (1920,280)-(2240,520), outside the remote 1280×800 viewport, even though its captured pixels appeared at (0,0). This explained why clicking the displayed form did not hit its native window. Preview.5 normalizes owned titled dialogs into the remote viewport and renders each window into its own surface before compositing at native coordinates. The native regression reproduces both off-screen placement and a print-origin reset. The diagnostic `RemoteFrame.PointerTests --inspect-worker <pid>` lists window rectangles and hit tests without activating or clicking windows; when cross-process desktop handles are unavailable it explicitly lists the RemoteFrame desktops in the current window station.

## Live interaction verification — 2026-10-29

The actual Debug server was rebuilt and restarted with `0.3.0-preview.5`; the deployed managed libraries, worker and helper matched their selected packages and runtime manifest. The user opened the supplied workbook's UserForm through their normal trusted-workbook workflow. In the browser, clicking X closed that form. Clicking the worksheet's `SheetRefreshButton` reopened it; clicking the form's `CommandButton` then hid it. Before/after streamed frames were inspected to verify each transition. The corrected form appeared inside the remote viewport rather than as misplaced pixels at its top-left corner.

This confirms live mouse delivery and the form show/close/hide path for this workbook and installed Excel on this host. It does not independently establish DAO query results, test isolated database behavior, validate save/reopen preservation, or cover other Office versions. No macro trust settings were changed by the agent. The working browser session was left open without saving or closing it.

Rebuild and redistribute the worker payload to use this change; rebuilding only the server does not replace the worker in an existing runtime package. Gesture regression tests run in `RemoteFrame.ApplicationTests`. Live acceptance still needs a disposable workbook with a form: open it through the viewer, click X, verify the form closes and worksheet input resumes, then repeat with a `QueryClose` handler that cancels and verify the form remains open. Keep the existing macro trust settings; no DAO workflow is needed to test X.
