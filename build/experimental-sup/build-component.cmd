@echo off
setlocal
for %%I in ("%~dp0..\..") do set "MANI_EXPERIMENT_ROOT=%%~fI"
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\VsDevCmd.bat" -no_logo -arch=x64 -host_arch=x64
if errorlevel 1 exit /b 1
set "PATH=%VCToolsInstallDir%bin\Hostx64\x64;%PATH%;%MANI_EXPERIMENT_ROOT%\artifacts\m5\sup-engine-ab\tools\msys64\usr\bin"
set "MSYS2_ARG_CONV_EXCL=-Fo;-Fe;-I;-LIBPATH:"
set "MANI_SCRIPT_PATH=%MANI_EXPERIMENT_ROOT:\=/%/build/experimental-sup/build-component.sh"
"%MANI_EXPERIMENT_ROOT%\artifacts\m5\sup-engine-ab\tools\msys64\usr\bin\bash.exe" --noprofile --norc "%MANI_SCRIPT_PATH%" %1
exit /b %errorlevel%
