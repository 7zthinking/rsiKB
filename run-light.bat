@echo off
rem KBEA dev launcher (Light build).
rem NOTE: keep this file ASCII-only. cmd.exe reads .bat as the system ANSI code page,
rem so non-ASCII comments get garbled and can break command parsing.
rem Usage: run-light.bat --config D:\path\conf.json
setlocal
set "ROOT=%~dp0"
set "DOTNET_ROOT=%ROOT%.dotnet"
set "DOTNET_MULTILEVEL_LOOKUP=0"
if not exist "%ROOT%artifacts\KBEA-Light\KBEA.exe" (
    echo [KBEA] Missing artifacts\KBEA-Light\KBEA.exe
    echo [KBEA] Run publish.ps1 first.
    pause
    exit /b 1
)
start "" "%ROOT%artifacts\KBEA-Light\KBEA.exe" %*
endlocal
