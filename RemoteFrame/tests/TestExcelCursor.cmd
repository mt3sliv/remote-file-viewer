@echo off
setlocal
set "RF_VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq delims=" %%I in (`"%RF_VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "RF_VSPATH=%%I"
if not defined RF_VSPATH exit /b 1
call "%RF_VSPATH%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++17 /O2 /MT /Fo"%~1\ExcelCursorTests.obj" "%~dp0RemoteFrame.PointerTests\ExcelCursorTests.cpp" /link /OUT:"%~1\EXCEL.EXE" user32.lib
if errorlevel 1 exit /b %errorlevel%
"%~1\EXCEL.EXE"
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++17 /O2 /MT /Fo"%~1\ExcelCursorTests.obj" "%~dp0RemoteFrame.PointerTests\ExcelCursorTests.cpp" /link /OUT:"%~1\EXCEL.EXE" /DELAYLOAD:user32.dll user32.lib delayimp.lib
if errorlevel 1 exit /b %errorlevel%
"%~1\EXCEL.EXE"
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++17 /O2 /MT /Fo"%~1\ExcelCursorTests.obj" "%~dp0RemoteFrame.PointerTests\ExcelCursorTests.cpp" /link /OUT:"%~1\OtherOffice.exe" user32.lib
if errorlevel 1 exit /b %errorlevel%
"%~1\OtherOffice.exe" excluded
exit /b %errorlevel%
