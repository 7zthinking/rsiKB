@echo off
setlocal
rem  Batch sign launcher. Real logic lives in sign.ps1.
rem  Double-click this file after any build/publish.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0sign.ps1"
pause
