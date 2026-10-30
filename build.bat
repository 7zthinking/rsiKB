@echo off
setlocal
set "ROOT=%~dp0"
set "DOTNET=%ROOT%.dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"
set "DOTNET_CLI_HOME=%ROOT%.dotnet-home"
set "NUGET_PACKAGES=%ROOT%.nuget\packages"
"%DOTNET%" restore "%ROOT%src\KBEA.Core\KBEA.Core.csproj" || exit /b 1
"%DOTNET%" restore "%ROOT%src\KBEA.App\KBEA.App.csproj" || exit /b 1
"%DOTNET%" restore "%ROOT%tests\KBEA.Tests\KBEA.Tests.csproj" || exit /b 1
"%DOTNET%" build "%ROOT%KBEA.sln" --no-restore --configuration Release
endlocal
