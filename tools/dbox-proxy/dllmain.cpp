#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <psapi.h>
#include <strsafe.h>

#include "logger.h"
#include "iat_hook.h"
#include "wsa_wrappers.h"

namespace {

HMODULE  g_real_dll       = NULL;
HMODULE  g_self           = NULL;
wchar_t  g_real_dll_path[MAX_PATH] = {0};
wchar_t  g_host_exe[MAX_PATH] = {0};

typedef void* (*fn_GetEventHandler)();
fn_GetEventHandler g_real_GetEventHandler = NULL;

void hook_all_dbox_modules() {
    HMODULE modules[1024];
    DWORD needed = 0;
    HANDLE proc = GetCurrentProcess();
    if (!EnumProcessModules(proc, modules, sizeof(modules), &needed)) {
        log_line("EnumProcessModules failed err=%lu", GetLastError());
        return;
    }
    DWORD count = needed / sizeof(HMODULE);
    if (count > 1024) count = 1024;
    log_line("EnumProcessModules: %lu modules", count);

    int dbx_count = 0;
    for (DWORD i = 0; i < count; i++) {
        if (modules[i] == g_self) continue;

        wchar_t base_name[MAX_PATH] = {0};
        if (GetModuleBaseNameW(proc, modules[i], base_name, MAX_PATH) == 0) continue;
        if (_wcsnicmp(base_name, L"dbx", 3) != 0) continue;

        wchar_t full_path[MAX_PATH] = {0};
        GetModuleFileNameExW(proc, modules[i], full_path, MAX_PATH);
        log_line("dbx module: name=%S base=%p path=%S", base_name, modules[i], full_path);
        install_wsa_hooks(modules[i], base_name);
        dbx_count++;
    }
    log_line("Total dbx* modules hooked: %d", dbx_count);
}

void load_real_dll(HMODULE self) {
    wchar_t self_path[MAX_PATH] = {0};
    if (GetModuleFileNameW(self, self_path, MAX_PATH) == 0) {
        log_line("GetModuleFileNameW(self) failed err=%lu", GetLastError());
        return;
    }
    log_line("self_path=%S", self_path);

    wchar_t self_dir[MAX_PATH] = {0};
    StringCchCopyW(self_dir, MAX_PATH, self_path);
    wchar_t* sep = wcsrchr(self_dir, L'\\');
    if (!sep) {
        log_line("could not derive self directory");
        return;
    }
    *sep = 0;

    StringCchPrintfW(g_real_dll_path, MAX_PATH, L"%s\\dbxLive64_real.dll", self_dir);
    log_line("loading real dll: %S", g_real_dll_path);

    g_real_dll = LoadLibraryExW(
        g_real_dll_path,
        NULL,
        LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!g_real_dll) {
        log_line("LoadLibraryExW failed err=%lu", GetLastError());
        return;
    }
    log_line("real_dll loaded base=%p", g_real_dll);

    g_real_GetEventHandler = reinterpret_cast<fn_GetEventHandler>(
        GetProcAddress(g_real_dll, "GetEventHandler"));
    log_line("real GetEventHandler=%p", (void*)g_real_GetEventHandler);
}

}

extern "C" __declspec(dllexport) void* GetEventHandler() {
    log_line(">> GetEventHandler called");
    if (!g_real_GetEventHandler) {
        log_line("<< GetEventHandler real_fn is NULL, returning NULL");
        return NULL;
    }
    void* result = g_real_GetEventHandler();
    log_line("<< GetEventHandler returned %p", result);
    if (result) {
        void** vtable = *reinterpret_cast<void***>(result);
        log_line("   object vtable=%p", vtable);
        if (vtable) {
            for (int i = 0; i < 24; i++) {
                __try {
                    void* slot = vtable[i];
                    if (!slot) { log_line("   vtable[%2d] = NULL (end)", i); break; }
                    log_line("   vtable[%2d] = %p", i, slot);
                } __except (EXCEPTION_EXECUTE_HANDLER) {
                    log_line("   vtable[%2d] = (read failed)", i);
                    break;
                }
            }
        }
    }
    return result;
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hModule);
        g_self = hModule;
        GetModuleFileNameW(NULL, g_host_exe, MAX_PATH);
        logger_init(g_host_exe);
        log_line("=== dbxLive64 proxy DLL_PROCESS_ATTACH ===");
        log_line("host_exe=%S", g_host_exe);
        log_line("pid=%lu", GetCurrentProcessId());

        load_real_dll(hModule);

        if (g_real_dll) {
            hook_all_dbox_modules();
        } else {
            log_line("skipping hook install: real dll not loaded");
        }
    } else if (reason == DLL_PROCESS_DETACH) {
        log_line("=== DLL_PROCESS_DETACH ===");
        logger_shutdown();
    }
    return TRUE;
}
