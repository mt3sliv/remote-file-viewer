@echo off
setlocal
set "RF_VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%RF_VSWHERE%" exit /b 1
for /f "usebackq delims=" %%I in (`"%RF_VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "RF_VSPATH=%%I"
if not defined RF_VSPATH exit /b 1
call "%RF_VSPATH%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++17 /LD /O2 /MT /Fo"%~1\InputHook.obj" "%~dp0InputHook.cpp" /link /OUT:"%~1\RemoteFrame.InputHook.dll" /IMPLIB:"%~1\InputHook.lib" user32.lib
exit /b %errorlevel%
