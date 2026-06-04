#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <strsafe.h>

#include "logger.h"

namespace {

HMODULE  g_real_dll       = NULL;
HMODULE  g_self           = NULL;
wchar_t  g_real_dll_path[MAX_PATH] = {0};
wchar_t  g_host_exe[MAX_PATH] = {0};

typedef void* (*generic_fn8)(void*, void*, void*, void*, void*, void*, void*, void*);

generic_fn8 g_real_GetAvailableDevices     = NULL;
generic_fn8 g_real_GetMotionServiceManager = NULL;
generic_fn8 g_real_ResetDevice             = NULL;
generic_fn8 g_real_StartDeviceTest         = NULL;
generic_fn8 g_real_StartLocationTest       = NULL;
generic_fn8 g_real_StopDeviceTest          = NULL;
generic_fn8 g_real_StopLocationTest        = NULL;

void resolve(const char* name, generic_fn8* slot) {
    FARPROC p = GetProcAddress(g_real_dll, name);
    *slot = reinterpret_cast<generic_fn8>(p);
    log_line("  resolve %-26s = %p", name, (void*)p);
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

    StringCchPrintfW(g_real_dll_path, MAX_PATH, L"%s\\dbxService64_real.dll", self_dir);
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

    resolve("GetAvailableDevices",     &g_real_GetAvailableDevices);
    resolve("GetMotionServiceManager", &g_real_GetMotionServiceManager);
    resolve("ResetDevice",             &g_real_ResetDevice);
    resolve("StartDeviceTest",         &g_real_StartDeviceTest);
    resolve("StartLocationTest",       &g_real_StartLocationTest);
    resolve("StopDeviceTest",          &g_real_StopDeviceTest);
    resolve("StopLocationTest",        &g_real_StopLocationTest);
}

void log_call_in(const char* name,
                 void* a1, void* a2, void* a3, void* a4,
                 void* a5, void* a6, void* a7, void* a8) {
    log_line(">> %s(%p, %p, %p, %p, %p, %p, %p, %p)",
             name, a1, a2, a3, a4, a5, a6, a7, a8);
}

void* call_real(const char* name, generic_fn8 fn,
                void* a1, void* a2, void* a3, void* a4,
                void* a5, void* a6, void* a7, void* a8) {
    if (!fn) {
        log_line("<< %s: real fn is NULL, returning NULL", name);
        return NULL;
    }
    void* rv = fn(a1, a2, a3, a4, a5, a6, a7, a8);
    log_line("<< %s returned %p", name, rv);
    return rv;
}

}

#define PROXY_WRAPPER(NAME)                                                   \
extern "C" __declspec(dllexport) void* NAME(                                  \
    void* a1, void* a2, void* a3, void* a4,                                   \
    void* a5, void* a6, void* a7, void* a8) {                                 \
    log_call_in(#NAME, a1, a2, a3, a4, a5, a6, a7, a8);                       \
    return call_real(#NAME, g_real_##NAME, a1, a2, a3, a4, a5, a6, a7, a8);   \
}

PROXY_WRAPPER(GetAvailableDevices)
PROXY_WRAPPER(ResetDevice)
PROXY_WRAPPER(StartDeviceTest)
PROXY_WRAPPER(StartLocationTest)
PROXY_WRAPPER(StopDeviceTest)
PROXY_WRAPPER(StopLocationTest)

extern "C" __declspec(dllexport) void* GetMotionServiceManager(
    void* a1, void* a2, void* a3, void* a4,
    void* a5, void* a6, void* a7, void* a8) {
    log_call_in("GetMotionServiceManager", a1, a2, a3, a4, a5, a6, a7, a8);
    void* rv = call_real("GetMotionServiceManager", g_real_GetMotionServiceManager,
                         a1, a2, a3, a4, a5, a6, a7, a8);
    if (rv) {
        log_vtable("IMotionServiceManager", rv, 32);
    }
    return rv;
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hModule);
        g_self = hModule;
        GetModuleFileNameW(NULL, g_host_exe, MAX_PATH);
        logger_init(g_host_exe);
        log_line("=== dbxService64 proxy DLL_PROCESS_ATTACH ===");
        log_line("host_exe=%S", g_host_exe);
        log_line("pid=%lu", GetCurrentProcessId());

        load_real_dll(hModule);
    } else if (reason == DLL_PROCESS_DETACH) {
        log_line("=== DLL_PROCESS_DETACH ===");
        logger_shutdown();
    }
    return TRUE;
}
