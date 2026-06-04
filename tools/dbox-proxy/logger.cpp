#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shlobj.h>
#include <strsafe.h>
#include <stdio.h>
#include <stdarg.h>

#include "logger.h"

namespace {

HANDLE g_log = INVALID_HANDLE_VALUE;
CRITICAL_SECTION g_lock;
LARGE_INTEGER g_freq;
LARGE_INTEGER g_start;
bool g_lock_init = false;

void ensure_dir(const wchar_t* path) {
    SHCreateDirectoryExW(NULL, path, NULL);
}

}

void logger_init(const wchar_t* host_module_path) {
    InitializeCriticalSection(&g_lock);
    g_lock_init = true;
    QueryPerformanceFrequency(&g_freq);
    QueryPerformanceCounter(&g_start);

    wchar_t local_appdata[MAX_PATH] = {0};
    if (FAILED(SHGetFolderPathW(NULL, CSIDL_LOCAL_APPDATA, NULL, 0, local_appdata))) {
        return;
    }

    wchar_t dir[MAX_PATH];
    if (FAILED(StringCchPrintfW(dir, MAX_PATH, L"%s\\MozaStarCitizen\\dbx-trace", local_appdata))) {
        return;
    }
    ensure_dir(dir);

    SYSTEMTIME st;
    GetLocalTime(&st);

    wchar_t host_module[MAX_PATH] = L"unknown";
    if (host_module_path) {
        const wchar_t* slash = wcsrchr(host_module_path, L'\\');
        const wchar_t* leaf = slash ? slash + 1 : host_module_path;
        StringCchCopyW(host_module, MAX_PATH, leaf);
        wchar_t* dot = wcsrchr(host_module, L'.');
        if (dot) *dot = 0;
    }

    wchar_t file[MAX_PATH];
    StringCchPrintfW(
        file, MAX_PATH,
        L"%s\\dbx-trace-%04u%02u%02u-%02u%02u%02u-%s-%lu.log",
        dir,
        st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond,
        host_module, GetCurrentProcessId());

    g_log = CreateFileW(
        file,
        GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_DELETE,
        NULL,
        CREATE_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        NULL);
}

void logger_shutdown() {
    if (g_lock_init) {
        EnterCriticalSection(&g_lock);
    }
    if (g_log != INVALID_HANDLE_VALUE) {
        FlushFileBuffers(g_log);
        CloseHandle(g_log);
        g_log = INVALID_HANDLE_VALUE;
    }
    if (g_lock_init) {
        LeaveCriticalSection(&g_lock);
        DeleteCriticalSection(&g_lock);
        g_lock_init = false;
    }
}

static double elapsed_ms() {
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    return (double)(now.QuadPart - g_start.QuadPart) * 1000.0 / (double)g_freq.QuadPart;
}

static void write_locked(const char* data, int len) {
    if (g_log == INVALID_HANDLE_VALUE) return;
    EnterCriticalSection(&g_lock);
    DWORD wrote = 0;
    WriteFile(g_log, data, (DWORD)len, &wrote, NULL);
    LeaveCriticalSection(&g_lock);
}

void log_line(const char* fmt, ...) {
    if (g_log == INVALID_HANDLE_VALUE) return;

    char buf[4096];
    int prefix = _snprintf_s(
        buf, sizeof(buf), _TRUNCATE,
        "[%12.3f ms tid=%-5lu] ",
        elapsed_ms(),
        GetCurrentThreadId());
    if (prefix < 0) prefix = 0;

    va_list ap;
    va_start(ap, fmt);
    int written = _vsnprintf_s(buf + prefix, sizeof(buf) - prefix - 2, _TRUNCATE, fmt, ap);
    va_end(ap);
    if (written < 0) written = (int)(sizeof(buf) - prefix - 2);

    int total = prefix + written;
    if (total > (int)sizeof(buf) - 2) total = (int)sizeof(buf) - 2;
    buf[total++] = '\r';
    buf[total++] = '\n';

    write_locked(buf, total);
}

void log_payload(const char* tag, const unsigned char* data, int len) {
    if (g_log == INVALID_HANDLE_VALUE) return;
    if (!data || len < 0) {
        log_line("  %s: (null or invalid len=%d)", tag, len);
        return;
    }

    const int preview = len < 256 ? len : 256;
    char buf[8192];

    int n = _snprintf_s(
        buf, sizeof(buf), _TRUNCATE,
        "  %s len=%d ascii=", tag, len);
    if (n < 0) n = 0;

    for (int i = 0; i < preview && n < (int)sizeof(buf) - 4; i++) {
        unsigned char b = data[i];
        buf[n++] = (b >= 32 && b <= 126) ? (char)b : '.';
    }

    int wrote = _snprintf_s(buf + n, sizeof(buf) - n, _TRUNCATE, "\r\n  hex=");
    if (wrote > 0) n += wrote;

    for (int i = 0; i < preview && n < (int)sizeof(buf) - 6; i++) {
        wrote = _snprintf_s(buf + n, sizeof(buf) - n, _TRUNCATE, "%02X", data[i]);
        if (wrote > 0) n += wrote;
        if (i < preview - 1 && n < (int)sizeof(buf) - 4) buf[n++] = ' ';
    }

    if (n < (int)sizeof(buf) - 2) {
        buf[n++] = '\r';
        buf[n++] = '\n';
    }

    write_locked(buf, n);
}
