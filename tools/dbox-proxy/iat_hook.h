#pragma once

#include <windows.h>

void* hook_iat(HMODULE module, const char* import_dll, const char* func_name, void* new_fn);
