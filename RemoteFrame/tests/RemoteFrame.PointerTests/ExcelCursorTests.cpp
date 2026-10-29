#define WIN32_LEAN_AND_MEAN
#include "../../packaging/native/ExcelCursor.h"
#include <stdio.h>

int main(int argc, char**)
{
    // Named EXCEL.EXE to exercise the exact production import adapter. The delayed
    // build also tests the import table used by installed Excel, before resolution.
    POINT expected{321, 456}, actual{};
    if (!LoadLibraryW(L"user32.dll")) return 4;
    ExcelCursor::Update(expected);
    if (argc > 1)
    {
        if (ExcelCursor::slot || ExcelCursor::enabled) return 5;
        puts("PASS: Other application executables retain their native cursor import.");
        return 0;
    }
    if (!ExcelCursor::slot || !GetCursorPos(&actual) || actual.x != expected.x || actual.y != expected.y) return 1;
    ExcelCursor::Update(POINT{-123, 789});
    if (!GetCursorPos(&actual) || actual.x != -123 || actual.y != 789) return 2;
    ExcelCursor::Disable();
    if (*ExcelCursor::slot != ExcelCursor::savedSlot || ExcelCursor::enabled) return 3;
    puts("PASS: Excel cursor import, signed coordinates, update and restoration.");
    return 0;
}
