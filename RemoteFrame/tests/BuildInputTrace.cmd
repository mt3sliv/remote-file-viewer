@echo off
setlocal
set "RF_VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq delims=" %%I in (`"%RF_VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "RF_VSPATH=%%I"
if not defined RF_VSPATH exit /b 1
call "%RF_VSPATH%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b %errorlevel%
if /I "%~2"=="cursor" (
cl /nologo /LD /O2 /MT /Fo"%~1\CursorProbe.obj" "%~dp0RemoteFrame.PointerTests\CursorProbe.cpp" /link /OUT:"%~1\RemoteFrame.CursorProbe.dll" /IMPLIB:"%~1\CursorProbe.lib" user32.lib
) else (
cl /nologo /LD /O2 /MT /Fo"%~1\InputTrace.obj" "%~dp0RemoteFrame.PointerTests\InputTrace.cpp" /link /OUT:"%~1\RemoteFrame.InputTrace.dll" /IMPLIB:"%~1\InputTrace.lib" user32.lib
)
exit /b %errorlevel%
