#pragma once
#include <windows.h>
#include <string.h>

// GetCursorPos fails on an inactive private desktop. Excel also queries it while
// processing posted clicks, so give only its executable the remote screen point.
// Never move the shared desktop cursor or change the input desktop.
namespace ExcelCursor
{
    inline SRWLOCK gate = SRWLOCK_INIT;
    inline PVOID* slot;
    inline PVOID savedSlot;
    inline BOOL (WINAPI* original)(LPPOINT);
    inline volatile LONG64 position;
    inline volatile LONG enabled;
    inline bool attempted;

    inline BOOL WINAPI Read(LPPOINT point)
    {
        if (point && InterlockedCompareExchange(&enabled, 0, 0))
        {
            const auto packed = InterlockedCompareExchange64(&position, 0, 0);
            point->x = static_cast<LONG>(packed);
            point->y = static_cast<LONG>(packed >> 32);
            return TRUE;
        }
        return original ? original(point) : FALSE;
    }

    inline bool Patch(BYTE* base, DWORD size, DWORD namesRva, DWORD addressesRva)
    {
        for (SIZE_T index = 0; namesRva && addressesRva; ++index)
        {
            const auto offset = index * sizeof(IMAGE_THUNK_DATA64);
            if (namesRva >= size || addressesRva >= size || offset + sizeof(IMAGE_THUNK_DATA64) > size - namesRva
                || offset + sizeof(IMAGE_THUNK_DATA64) > size - addressesRva) return false;
            const auto name = reinterpret_cast<IMAGE_THUNK_DATA64*>(base + namesRva) + index;
            const auto address = reinterpret_cast<IMAGE_THUNK_DATA64*>(base + addressesRva) + index;
            if (!name->u1.AddressOfData) return false;
            if (IMAGE_SNAP_BY_ORDINAL64(name->u1.Ordinal)) continue;
            const auto rva = name->u1.AddressOfData;
            constexpr SIZE_T length = sizeof("GetCursorPos");
            if (rva >= size || sizeof(WORD) + length > size - rva) continue;
            if (memcmp(base + rva + sizeof(WORD), "GetCursorPos", length) != 0) continue;
            const auto candidate = reinterpret_cast<PVOID*>(&address->u1.Function);
            HMODULE self;
            // Office can retain an imported function pointer beyond hook removal.
            // Keep its code mapped until this worker-owned process exits.
            if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
                reinterpret_cast<LPCWSTR>(Read), &self)) return false;
            DWORD protection;
            if (!VirtualProtect(candidate, sizeof(PVOID), PAGE_READWRITE, &protection)) return false;
            savedSlot = InterlockedExchangePointer(candidate, reinterpret_cast<PVOID>(Read));
            slot = candidate;
            DWORD ignored;
            VirtualProtect(candidate, sizeof(PVOID), protection, &ignored);
            InterlockedExchange(&enabled, 1);
            return true;
        }
        return false;
    }

    inline void Update(POINT point)
    {
        InterlockedExchange64(&position, static_cast<ULONGLONG>(static_cast<DWORD>(point.x))
            | (static_cast<ULONGLONG>(static_cast<DWORD>(point.y)) << 32));
        AcquireSRWLockExclusive(&gate);
        if (!attempted)
        {
            attempted = true;
            wchar_t path[MAX_PATH];
            const auto length = GetModuleFileNameW(nullptr, path, MAX_PATH);
            const auto file = length && length < MAX_PATH ? wcsrchr(path, L'\\') : nullptr;
            if (file && _wcsicmp(file + 1, L"EXCEL.EXE") == 0)
            {
                original = reinterpret_cast<decltype(original)>(GetProcAddress(GetModuleHandleW(L"user32.dll"), "GetCursorPos"));
                const auto base = reinterpret_cast<BYTE*>(GetModuleHandleW(nullptr));
                const auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
                const auto nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
                const auto size = nt->OptionalHeader.SizeOfImage;
                const auto imports = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
                for (DWORD offset = 0; original && imports.VirtualAddress && offset + sizeof(IMAGE_IMPORT_DESCRIPTOR) <= imports.Size; offset += sizeof(IMAGE_IMPORT_DESCRIPTOR))
                {
                    if (imports.VirtualAddress >= size || offset + sizeof(IMAGE_IMPORT_DESCRIPTOR) > size - imports.VirtualAddress) break;
                    const auto item = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base + imports.VirtualAddress + offset);
                    if (!item->Name || Patch(base, size, item->OriginalFirstThunk, item->FirstThunk)) break;
                }
                const auto delayed = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_DELAY_IMPORT];
                for (DWORD offset = 0; original && !slot && delayed.VirtualAddress && offset + sizeof(IMAGE_DELAYLOAD_DESCRIPTOR) <= delayed.Size; offset += sizeof(IMAGE_DELAYLOAD_DESCRIPTOR))
                {
                    if (delayed.VirtualAddress >= size || offset + sizeof(IMAGE_DELAYLOAD_DESCRIPTOR) > size - delayed.VirtualAddress) break;
                    const auto item = reinterpret_cast<IMAGE_DELAYLOAD_DESCRIPTOR*>(base + delayed.VirtualAddress + offset);
                    if (!item->DllNameRVA) break;
                    if (item->Attributes.RvaBased && Patch(base, size, item->ImportNameTableRVA, item->ImportAddressTableRVA)) break;
                }
            }
        }
        ReleaseSRWLockExclusive(&gate);
    }

    inline void Disable()
    {
        AcquireSRWLockExclusive(&gate);
        InterlockedExchange(&enabled, 0);
        attempted = true;
        DWORD protection;
        if (slot && VirtualProtect(slot, sizeof(PVOID), PAGE_READWRITE, &protection))
        {
            InterlockedCompareExchangePointer(slot, savedSlot, reinterpret_cast<PVOID>(Read));
            DWORD ignored;
            VirtualProtect(slot, sizeof(PVOID), protection, &ignored);
        }
        ReleaseSRWLockExclusive(&gate);
    }
}
