#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include "ExcelCursor.h"

// Upper WPARAM bits tag our private-desktop input; never alter unrelated app messages.
static constexpr ULONGLONG PointerTag = 0x52464d5000000000ULL;
static_assert(sizeof(WPARAM) == sizeof(ULONGLONG), "RemoteFrame input requires a 64-bit worker and application.");
static thread_local DWORD pressedButtons = 0;

extern "C" __declspec(dllexport) LRESULT CALLBACK RemoteFrameQueuedInputProc(int code, WPARAM remove, LPARAM parameter)
{
    if (code >= 0)
    {
        auto* message = reinterpret_cast<MSG*>(parameter);
        const bool pointer = message->message >= WM_MOUSEFIRST && message->message <= WM_MOUSELAST;
        const bool cancel = message->message == WM_CANCELMODE;
        if ((pointer || cancel) && (message->wParam & 0xffffffff00000000ULL) == PointerTag)
        {
            message->wParam = static_cast<DWORD>(message->wParam);
            auto nextButtons = pressedButtons;
            switch (message->message)
            {
            case WM_LBUTTONDOWN: case WM_LBUTTONDBLCLK: nextButtons |= MK_LBUTTON; break;
            case WM_LBUTTONUP: nextButtons &= ~MK_LBUTTON; break;
            case WM_RBUTTONDOWN: case WM_RBUTTONDBLCLK: nextButtons |= MK_RBUTTON; break;
            case WM_RBUTTONUP: nextButtons &= ~MK_RBUTTON; break;
            case WM_MBUTTONDOWN: case WM_MBUTTONDBLCLK: nextButtons |= MK_MBUTTON; break;
            case WM_MBUTTONUP: nextButtons &= ~MK_MBUTTON; break;
            case WM_CANCELMODE: nextButtons = 0; break;
            }
            // A stale move read by a filtered loop must not press a button again after up.
            if (pointer) message->wParam = (message->wParam & ~(MK_LBUTTON | MK_RBUTTON | MK_MBUTTON)) | nextButtons;
            POINT point{ static_cast<short>(LOWORD(message->lParam)), static_cast<short>(HIWORD(message->lParam)) };
            const bool wheel = message->message == WM_MOUSEWHEEL || message->message == WM_MOUSEHWHEEL;
            // Tagged messages carry viewport/screen coordinates, so late window moves
            // cannot invalidate coordinates computed by the worker before dispatch.
            const auto capture = GetCapture();
            // Resolve capture AFTER the preceding press has been processed, not when the
            // worker enqueues a release. Busy UI threads can establish capture much later.
            if (!wheel && capture && GetWindowThreadProcessId(capture, nullptr) == GetCurrentThreadId())
                message->hwnd = capture;
            if (pointer)
            {
                message->pt = point;
                if (!wheel) ScreenToClient(message->hwnd, &point);
                message->lParam = MAKELPARAM(static_cast<short>(point.x), static_cast<short>(point.y));
            }
            if (remove == PM_REMOVE)
            {
                if (pointer) ExcelCursor::Update(message->pt);
                pressedButtons = nextButtons;
                BYTE state[256]{};
                GetKeyboardState(state);
                const auto flags = nextButtons;
                state[VK_LBUTTON] = (flags & MK_LBUTTON) ? 0x80 : 0;
                state[VK_RBUTTON] = (flags & MK_RBUTTON) ? 0x80 : 0;
                state[VK_MBUTTON] = (flags & MK_MBUTTON) ? 0x80 : 0;
                // Keyboard modifiers belong to the existing keyboard protocol. Do not
                // let an older queued hover undo a newer Ctrl+Save modifier command.
                SetKeyboardState(state);
            }
        }
    }
    return CallNextHookEx(nullptr, code, remove, parameter);
}

// Runs only on the GUI threads of a worker-owned Office process.
// Windows modifier state is thread-local; updating it from the C# worker is insufficient.
extern "C" __declspec(dllexport) LRESULT CALLBACK RemoteFrameInputProc(int code, WPARAM wParam, LPARAM lParam)
{
    if (code >= 0)
    {
        const auto* message = reinterpret_cast<const CWPSTRUCT*>(lParam);
        if (message->message == RegisterWindowMessageW(L"RemoteFrame.Office.DisableCursor.v1"))
            ExcelCursor::Disable();
        if (message->message == RegisterWindowMessageW(L"RemoteFrame.Office.SetModifiers.v1"))
            SetPropW(message->hwnd, L"RemoteFrame.ModifierMask", reinterpret_cast<HANDLE>(message->wParam + 1));
        if ((message->message >= WM_KEYFIRST && message->message <= WM_KEYLAST)
            || message->message == RegisterWindowMessageW(L"RemoteFrame.Office.SetModifiers.v1"))
        {
            BYTE state[256]{};
            GetKeyboardState(state);
            const auto stored = reinterpret_cast<ULONG_PTR>(GetPropW(message->hwnd, L"RemoteFrame.ModifierMask"));
            const auto mask = stored ? stored - 1 : 0;
            state[VK_CONTROL] = state[VK_LCONTROL] = (mask & 1) ? 0x80 : 0;
            state[VK_SHIFT] = state[VK_LSHIFT] = (mask & 2) ? 0x80 : 0;
            state[VK_MENU] = state[VK_LMENU] = (mask & 4) ? 0x80 : 0;
            state[VK_RCONTROL] = state[VK_RSHIFT] = state[VK_RMENU] = 0;
            SetKeyboardState(state);
            const ULONG_PTR observed = ((GetKeyState(VK_CONTROL) & 0x8000) ? 1 : 0)
                | ((GetKeyState(VK_SHIFT) & 0x8000) ? 2 : 0) | ((GetKeyState(VK_MENU) & 0x8000) ? 4 : 0);
            SetPropW(message->hwnd, L"RemoteFrame.LastKeyboardMask", reinterpret_cast<HANDLE>(observed + 1));
        }
    }
    return CallNextHookEx(nullptr, code, wParam, lParam);
}
