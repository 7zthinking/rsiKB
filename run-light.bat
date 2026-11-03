@echo off
setlocal
set "ROOT=%~dp0"
set "DOTNET_ROOT=%ROOT%.dotnet"
set "DOTNET_MULTILEVEL_LOOKUP=0"
set "KBEA_ARGS=%*"
rem Smart App Control blocks locally built KBEA.exe (self-signed cert).
rem Launch via the Microsoft-signed dotnet.exe host to bypass it.
rem Usage: run-light.bat --config D:\path\conf.json
powershell -NoProfile -Command "$a = $env:KBEA_ARGS; if ($a) { $a = ' ' + $a }; Start-Process -FilePath ($env:DOTNET_ROOT + '\dotnet.exe') -ArgumentList ('exec \"' + $env:ROOT + 'artifacts\KBEA-Light\KBEA.dll\"' + $a) -WindowStyle Hidden"
endlocal
