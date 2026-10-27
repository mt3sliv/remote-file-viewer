@echo off
setlocal
call "%ProgramFiles%\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b %errorlevel%
cl /nologo /LD /O2 /MT /Fo"%~1\InputHook.obj" "%~dp0InputHook.cpp" /link /OUT:"%~1\RemoteFrame.InputHook.dll" /IMPLIB:"%~1\InputHook.lib" user32.lib
exit /b %errorlevel%
