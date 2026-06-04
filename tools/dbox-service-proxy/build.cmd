@echo off
setlocal enableextensions

set "SCRIPT_DIR=%~dp0"
pushd "%SCRIPT_DIR%"

set "VS_ROOT=C:\Program Files\Microsoft Visual Studio\2022\Community"
set "VCVARS=%VS_ROOT%\VC\Auxiliary\Build\vcvars64.bat"

where cl.exe >nul 2>nul
if errorlevel 1 (
    if not exist "%VCVARS%" (
        echo Could not find cl.exe or vcvars64.bat at: %VCVARS%
        exit /b 1
    )
    call "%VCVARS%" >nul || exit /b 1
)

if not exist out mkdir out

cl.exe /nologo /O2 /MT /EHsc /W3 /std:c++17 ^
    /DWIN32 /D_WIN32 /D_WINDLL /DUNICODE /D_UNICODE ^
    /Foout\ ^
    dllmain.cpp logger.cpp ^
    /link /DLL /DEF:dbox-service-proxy.def ^
    /OUT:out\dbxService64.dll ^
    /IMPLIB:out\dbxService64.lib ^
    /PDB:out\dbxService64.pdb ^
    /SUBSYSTEM:WINDOWS ^
    /MACHINE:X64 ^
    shell32.lib advapi32.lib user32.lib ole32.lib
if errorlevel 1 (
    echo Build failed.
    popd
    exit /b 1
)

echo.
echo Build OK: %SCRIPT_DIR%out\dbxService64.dll
echo.

popd
endlocal
