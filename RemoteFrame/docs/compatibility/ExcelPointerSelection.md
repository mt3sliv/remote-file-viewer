# Excel cell selection on the private desktop

## Reproduced behavior

In installed Excel, drag a range, then click an unrelated cell such as B12.
Intermittently the plain click selected B1:B12. An input trace showed the correct
target window and coordinates, a released left button, and no held Shift or Ctrl.
The capture window had also released native mouse capture.

Excel's GetCursorPos call returned false on the inactive private desktop, even
though GetMessagePos contained the correct remote point. A temporary per-process
cursor adapter handled 50 cursor queries during repeated drag/click checks; the
single-cell selections worked with it enabled. Removing the adapter reproduced
the upward selection with the same sequence.

## Fix in 0.3.0-preview.7

The queued-input helper updates a virtual screen position only when consuming a
tagged RemoteFrame pointer event. Only the worker-owned EXCEL.EXE import of
GetCursorPos is adapted, including Excel's delayed import. Other application
executables keep their native cursor API. The shared host cursor and input
desktop are never changed. The original import is restored during orderly
worker disposal. The callback is pinned until the isolated Excel process exits
so retained function pointers cannot call unloaded code.

The earlier preview.6 fix remains necessary: resolve capture when the GUI thread
dequeues each pointer event, and track button transitions so an older queued move
cannot reassert a released button. These are separate causes of similar symptoms.

## Regression coverage

- `tests/TestPointerInput.ps1`: regular and delayed cursor imports, signed screen
  coordinates, position updates, restoration, other executable exclusion, busy
  native capture, stale moves after release, cancellation, intentional dragging,
  off-screen UserForms, modal buttons and title-bar close.
- `tests/RemoteFrame.StreamingTests/viewer.test.cjs`: browser pointer cancellation,
  blur, missed release, button chords and stale-frame handling.
- `tests/TestInstalledExcel.ps1`: disposable installed-Excel workbook, capture,
  resize, typing and save.
- Distribution build: bundled Writer and Calc capture, resize, typing and save;
  engine inventory and packaged runtime hash verification.

The Windows-specific change does not alter the public input protocol or the
managed Linux rendering path. Linux execution was not tested on this Windows host.

## Packaged demo verification (2026-10-29)

The Debug demo was rebuilt against aggregate package `0.3.0-preview.7`, with zero
build warnings/errors. `TestDemoRuntime.ps1` verified the deployed managed
libraries, worker and input helper against both package contents and fresh worker
hashes. Installed Excel ran from that demo's packaged worker directory.

Live browser checks dragged D6:D10, E7:E11 and B15:B19, then clicked B12, C14 and
F12 respectively. Each ordinary click left only its target cell selected. Three
more ordinary clicks also ended with B12 alone selected. Intentional range
selection continued to work. These checks changed selection only, without saving
or editing workbook contents. Screenshot: `artifacts/excel-pointer-preview7-verified.png`.
