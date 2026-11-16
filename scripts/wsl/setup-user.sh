#!/usr/bin/env bash
set -euo pipefail

sdk_version=""
if [ "$#" -gt 0 ]; then
    sdk_version="$1"
fi

if [ -z "$sdk_version" ]; then
    printf 'The .NET SDK version is required.\n' >&2
    exit 2
fi

curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$HOME/dotnet-install.sh"
bash "$HOME/dotnet-install.sh" --version "$sdk_version" --install-dir "$HOME/.dotnet"

pwsh_version=""
release_json=$(curl -fsSL "https://api.github.com/repos/PowerShell/PowerShell/releases?per_page=100")
patch=$(printf '%s' "$release_json" | grep -o '"tag_name"[[:space:]]*:[[:space:]]*"v7\.6\.[0-9]*"' | grep -o 'v7\.6\.[0-9]*' | sed 's/v7\.6\.//' | sort -n | awk '$1 >= 4' | tail -n 1 || true)

if [ -n "$patch" ]; then
    version="7.6.$patch"
    mkdir -p "$HOME/.local/pwsh"
    curl -fsSL "https://github.com/PowerShell/PowerShell/releases/download/v$version/powershell-$version-linux-x64.tar.gz" -o "$HOME/powershell.tar.gz"
    tar -xzf "$HOME/powershell.tar.gz" -C "$HOME/.local/pwsh"
    chmod +x "$HOME/.local/pwsh/pwsh"
    rm -f "$HOME/powershell.tar.gz"
    pwsh_version=$("$HOME/.local/pwsh/pwsh" -NoProfile -Command '$PSVersionTable.PSVersion.ToString()')
else
    printf 'Warning: no PowerShell 7.6.4 or later release was found; pwsh tests will be skipped.\n'
fi

printf 'bash: %s\n' "$(bash --version | head -n 1)"
printf 'dotnet: %s\n' "$("$HOME/.dotnet/dotnet" --version)"

if [ -n "$pwsh_version" ]; then
    printf 'pwsh: %s\n' "$pwsh_version"
else
    printf 'pwsh: not installed\n'
fi
