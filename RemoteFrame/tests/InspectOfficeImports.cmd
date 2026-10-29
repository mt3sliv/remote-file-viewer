@echo off
setlocal
set "RF_VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq delims=" %%I in (`"%RF_VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "RF_VSPATH=%%I"
if not defined RF_VSPATH exit /b 1
call "%RF_VSPATH%\VC\Auxiliary\Build\vcvars64.bat" >nul
if /I "%~2"=="full" (dumpbin /imports "%~1") else (dumpbin /imports "%~1" | findstr /I "GetCursorPos GetPhysicalCursorPos GetAsyncKeyState GetKeyState")
