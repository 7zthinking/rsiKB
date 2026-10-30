@echo off
setlocal
set "ROOT=%~dp0"
set "DOTNET=%ROOT%.dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"
set "DOTNET_CLI_HOME=%ROOT%.dotnet-home"
set "NUGET_PACKAGES=%ROOT%.nuget\packages"
"%DOTNET%" test "%ROOT%tests\KBEA.Tests\KBEA.Tests.csproj" --configuration Release
endlocal
