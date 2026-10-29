#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>

// Temporary, read-only tracing for one worker-owned GUI thread. No message is modified.
extern "C" __declspec(dllexport) LRESULT CALLBACK RemoteFrameTraceInputProc(int code, WPARAM remove, LPARAM value)
{
    auto* message = reinterpret_cast<MSG*>(value);
    MSG before{};
    bool trace = code >= 0 && remove == PM_REMOVE && message->message >= WM_MOUSEFIRST && message->message <= WM_MOUSELAST;
    if (trace) before = *message;
    const auto result = CallNextHookEx(nullptr, code, remove, value);
    if (trace)
    {
        POINT cursor{};
        const auto cursorOk = GetCursorPos(&cursor);
        char line[512];
        const auto length = sprintf_s(line,"hwnd=%p capture=%p id=%04x tag=%08x original=%d,%d final=%d,%d pt=%ld,%ld flags=%04x left=%04x shift=%04x ctrl=%04x cursorOK=%d cursor=%ld,%ld\r\n",
            message->hwnd,GetCapture(),before.message,static_cast<unsigned>(before.wParam >> 32),
            static_cast<short>(LOWORD(before.lParam)),static_cast<short>(HIWORD(before.lParam)),
            static_cast<short>(LOWORD(message->lParam)),static_cast<short>(HIWORD(message->lParam)),
            message->pt.x,message->pt.y,static_cast<unsigned>(message->wParam),
            static_cast<unsigned short>(GetKeyState(VK_LBUTTON)),static_cast<unsigned short>(GetKeyState(VK_SHIFT)),
            static_cast<unsigned short>(GetKeyState(VK_CONTROL)),cursorOk,cursor.x,cursor.y);
        wchar_t temp[MAX_PATH], path[MAX_PATH];
        GetTempPathW(MAX_PATH,temp);
        swprintf_s(path,L"%lsRemoteFrame-input-%lu.log",temp,GetCurrentProcessId());
        const auto file = CreateFileW(path,FILE_APPEND_DATA,FILE_SHARE_READ|FILE_SHARE_WRITE,nullptr,OPEN_ALWAYS,FILE_ATTRIBUTE_NORMAL,nullptr);
        if (file != INVALID_HANDLE_VALUE) { DWORD written; WriteFile(file,line,length,&written,nullptr); CloseHandle(file); }
    }
    return result;
}
