#define WIN32_LEAN_AND_MEAN
#include <windows.h>

// Runs only on the GUI threads of the worker-owned LibreOffice process.
// Windows modifier state is thread-local; updating it from the C# worker is insufficient.
extern "C" __declspec(dllexport) LRESULT CALLBACK RemoteFrameInputProc(int code, WPARAM wParam, LPARAM lParam)
{
    if (code >= 0)
    {
        const auto* message = reinterpret_cast<const CWPSTRUCT*>(lParam);
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
