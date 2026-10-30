@echo off
setlocal
set "ROOT=%~dp0"
set "DOTNET=%ROOT%.dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"
set "DOTNET_CLI_HOME=%ROOT%.dotnet-home"
set "NUGET_PACKAGES=%ROOT%.nuget\packages"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
"%DOTNET%" run --project "%ROOT%src\KBEA.App\KBEA.App.csproj" --configuration Release -p:SingleAssembly=true -- %*
endlocal
