#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string.h>

#include "iat_hook.h"

void* hook_iat(HMODULE module, const char* import_dll, const char* func_name, void* new_fn) {
    if (!module || !import_dll || !func_name || !new_fn) return NULL;

    BYTE* base = reinterpret_cast<BYTE*>(module);
    IMAGE_DOS_HEADER* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return NULL;

    IMAGE_NT_HEADERS* nt = reinterpret_cast<IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return NULL;

    IMAGE_DATA_DIRECTORY& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (dir.VirtualAddress == 0 || dir.Size == 0) return NULL;

    IMAGE_IMPORT_DESCRIPTOR* desc = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base + dir.VirtualAddress);

    for (; desc->Name; ++desc) {
        const char* dll_name = reinterpret_cast<const char*>(base + desc->Name);
        if (_stricmp(dll_name, import_dll) != 0) continue;

        ULONG_PTR* iat = reinterpret_cast<ULONG_PTR*>(base + desc->FirstThunk);
        ULONG_PTR int_rva = desc->OriginalFirstThunk ? desc->OriginalFirstThunk : desc->FirstThunk;
        ULONG_PTR* int_ = reinterpret_cast<ULONG_PTR*>(base + int_rva);

        for (size_t i = 0; int_[i]; ++i) {
            if (IMAGE_SNAP_BY_ORDINAL(int_[i])) continue;
            IMAGE_IMPORT_BY_NAME* name = reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(base + int_[i]);
            if (strcmp(reinterpret_cast<const char*>(name->Name), func_name) != 0) continue;

            DWORD old_protect = 0;
            if (!VirtualProtect(&iat[i], sizeof(void*), PAGE_READWRITE, &old_protect)) return NULL;
            void* prev = reinterpret_cast<void*>(iat[i]);
            iat[i] = reinterpret_cast<ULONG_PTR>(new_fn);
            DWORD discard;
            VirtualProtect(&iat[i], sizeof(void*), old_protect, &discard);
            return prev;
        }
    }
    return NULL;
}
