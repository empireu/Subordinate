#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd "$(dirname "$0")" && pwd)
repository_root=$(dirname "$script_directory")
cd "$repository_root"

if [ -x "$HOME/.dotnet/dotnet" ]; then
    export DOTNET_ROOT="$HOME/.dotnet"
    export PATH="$HOME/.dotnet:$PATH"
fi

if [ -x "$HOME/.local/pwsh/pwsh" ]; then
    export SUBORDINATE_TEST_PWSH="$HOME/.local/pwsh/pwsh"
fi

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

printf 'uname: %s\n' "$(uname -sr)"
printf 'bash: %s\n' "$(bash --version | head -n 1)"
printf 'dotnet: %s\n' "$(dotnet --version)"

dotnet build Subordinate.slnx
dotnet test tests/Subordinate.Tests
