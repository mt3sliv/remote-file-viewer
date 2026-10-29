#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string.h>

// Temporary cursor-position compatibility probe, scoped to this isolated Excel process.
static PVOID* slot;
static PVOID savedSlot;
static BOOL (WINAPI* original)(LPPOINT);
static volatile LONG64 position;
static volatile LONG calls;
static volatile LONG enabled;
static bool stopped;

static BOOL WINAPI VirtualCursor(LPPOINT point)
{
    if (point && InterlockedCompareExchange(&enabled,0,0))
    {
        const auto packed = InterlockedCompareExchange64(&position,0,0);
        point->x = static_cast<LONG>(packed); point->y = static_cast<LONG>(packed >> 32);
        InterlockedIncrement(&calls);
        return TRUE;
    }
    return original ? original(point) : FALSE;
}
static void Restore()
{
    InterlockedExchange(&enabled,0);
    if (!slot) return;
    DWORD protection;
    if (VirtualProtect(slot,sizeof(PVOID),PAGE_READWRITE,&protection))
    {
        if (*slot == reinterpret_cast<PVOID>(VirtualCursor)) InterlockedExchangePointer(slot,savedSlot);
        DWORD ignored; VirtualProtect(slot,sizeof(PVOID),protection,&ignored);
    }
}
static bool Patch(BYTE* base, IMAGE_THUNK_DATA64* names, IMAGE_THUNK_DATA64* addresses)
{
    for (; names->u1.AddressOfData; ++names, ++addresses)
    {
        if (IMAGE_SNAP_BY_ORDINAL64(names->u1.Ordinal)) continue;
        const auto name = reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(base + names->u1.AddressOfData);
        if (strcmp(reinterpret_cast<char*>(name->Name),"GetCursorPos") != 0) continue;
        slot = reinterpret_cast<PVOID*>(&addresses->u1.Function);
        original = reinterpret_cast<decltype(original)>(GetProcAddress(GetModuleHandleW(L"user32.dll"),"GetCursorPos"));
        HMODULE self;
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
            reinterpret_cast<LPCWSTR>(VirtualCursor),&self);
        DWORD protection;
        if (VirtualProtect(slot,sizeof(PVOID),PAGE_READWRITE,&protection))
        {
            savedSlot = InterlockedExchangePointer(slot,reinterpret_cast<PVOID>(VirtualCursor));
            DWORD ignored; VirtualProtect(slot,sizeof(PVOID),protection,&ignored);
            InterlockedExchange(&enabled,1);
        }
        return true;
    }
    return false;
}
static void Install()
{
    if (slot) return;
    const auto base = reinterpret_cast<BYTE*>(GetModuleHandleW(nullptr));
    const auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return;
    const auto nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
    const auto directory = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (directory.VirtualAddress)
    {
    auto imports = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base + directory.VirtualAddress);
    for (; imports->Name; ++imports)
    {
        if (!imports->OriginalFirstThunk) continue;
        auto names = reinterpret_cast<IMAGE_THUNK_DATA64*>(base + imports->OriginalFirstThunk);
        auto addresses = reinterpret_cast<IMAGE_THUNK_DATA64*>(base + imports->FirstThunk);
        if (Patch(base,names,addresses)) return;
    }
    }
    const auto delayedDirectory = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_DELAY_IMPORT];
    if (delayedDirectory.VirtualAddress)
    {
        auto delayed = reinterpret_cast<IMAGE_DELAYLOAD_DESCRIPTOR*>(base + delayedDirectory.VirtualAddress);
        for (; delayed->DllNameRVA; ++delayed)
        {
            if (!delayed->Attributes.RvaBased) continue;
            if (Patch(base,reinterpret_cast<IMAGE_THUNK_DATA64*>(base + delayed->ImportNameTableRVA),
                reinterpret_cast<IMAGE_THUNK_DATA64*>(base + delayed->ImportAddressTableRVA))) return;
        }
    }
}
extern "C" __declspec(dllexport) LRESULT CALLBACK RemoteFrameCursorProbeProc(int code, WPARAM remove, LPARAM value)
{
    const auto result = CallNextHookEx(nullptr,code,remove,value);
    if (code >= 0 && remove == PM_REMOVE && !stopped)
    {
        const auto message = reinterpret_cast<MSG*>(value);
        if (message->message >= WM_MOUSEFIRST && message->message <= WM_MOUSELAST)
        {
            InterlockedExchange64(&position,static_cast<ULONGLONG>(static_cast<DWORD>(message->pt.x)) |
                (static_cast<ULONGLONG>(static_cast<DWORD>(message->pt.y)) << 32));
            Install();
        }
    }
    return result;
}
extern "C" __declspec(dllexport) LRESULT CALLBACK RemoteFrameCursorControlProc(int code, WPARAM wParam, LPARAM value)
{
    if (code >= 0)
    {
        const auto message = reinterpret_cast<CWPSTRUCT*>(value);
        if (message->message == RegisterWindowMessageW(L"RemoteFrame.CursorProbe.Stop.v1"))
        {
            SetPropW(message->hwnd,L"RemoteFrame.CursorProbe.Installed",reinterpret_cast<HANDLE>(static_cast<ULONG_PTR>(slot && *slot == reinterpret_cast<PVOID>(VirtualCursor))));
            stopped = true; Restore();
            SetPropW(message->hwnd,L"RemoteFrame.CursorProbe.Calls",reinterpret_cast<HANDLE>(static_cast<ULONG_PTR>(calls)));
        }
    }
    return CallNextHookEx(nullptr,code,wParam,value);
}
