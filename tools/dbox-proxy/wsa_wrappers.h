#pragma once

#include <windows.h>

void install_wsa_hooks(HMODULE target_module, const wchar_t* module_name);
