#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export PROGRAMDATA='C:\ProgramData'
export PROGRAMFILES='C:\Program Files'
export COMMONPROGRAMFILES='C:\Program Files\Common Files'
export DOTNET_CLI_HOME="$ROOT/.dotnet-home"
export NUGET_PACKAGES="$ROOT/.nuget/packages"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
exec env 'PROGRAMFILES(X86)=C:\Program Files (x86)' 'COMMONPROGRAMFILES(X86)=C:\Program Files (x86)\Common Files' "$ROOT/.dotnet/dotnet.exe" "$@"
