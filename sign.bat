@echo off
setlocal EnableDelayedExpansion
rem ============================================================
rem  KBEA.exe code signing (CN=7zThinking)
rem  Usage: double-click, or run after build/publish.
rem  Re-run any time the exe is rebuilt.
rem ============================================================

set "SIGNTOOL=C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"
set "THUMB=1583576BBF01ADB342EDC1CB9BCA018E7FC71381"
set "EXE=%~dp0artifacts\KBEA-Light\KBEA.exe"
set "DLL=%~dp0artifacts\KBEA-Light\KBEA.dll"

if not exist "%SIGNTOOL%" (
    echo [ERROR] signtool not found: !SIGNTOOL!
    echo Install Windows SDK or edit SIGNTOOL path in this file.
    pause
    exit /b 1
)

if not exist "%EXE%" (
    echo [ERROR] KBEA.exe not found: !EXE!
    echo Run the light build first, then re-run this file.
    pause
    exit /b 1
)

echo Signing: !EXE!
echo Signing: !DLL!
echo.
"%SIGNTOOL%" sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /sha1 %THUMB% "%EXE%" "%DLL%"
if errorlevel 1 (
    echo.
    echo [ERROR] Signing failed. Check the error above.
    pause
    exit /b 1
)

echo.
echo Verifying...
"%SIGNTOOL%" verify /pa "%EXE%" "%DLL%"

echo.
echo Done. Signed as CN=7zThinking (thumbprint %THUMB%).
pause
