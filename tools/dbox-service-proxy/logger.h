#pragma once

#include <windows.h>

void logger_init(const wchar_t* host_module_path);
void logger_shutdown();
void log_line(const char* fmt, ...);
void log_vtable(const char* tag, void* obj, int slot_count);
