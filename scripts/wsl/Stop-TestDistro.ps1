[CmdletBinding()]
param(
    [string]$DistroName = "subordinate-linux"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$env:WSL_UTF8 = "1"
[Console]::OutputEncoding = [Text.Encoding]::UTF8

& wsl.exe --terminate $DistroName

if ($LASTEXITCODE -ne 0) {
    throw "wsl.exe --terminate $DistroName failed with exit code $LASTEXITCODE."
}

$running = & wsl.exe --list --running --quiet

if ($LASTEXITCODE -ne 0) {
    throw "wsl.exe --list --running --quiet failed with exit code $LASTEXITCODE."
}

$runningNames = @($running | Where-Object { $_.Trim() -ne '' })

if ($runningNames.Count -eq 0) {
    & wsl.exe --shutdown

    if ($LASTEXITCODE -ne 0) {
        throw "wsl.exe --shutdown failed with exit code $LASTEXITCODE."
    }
}
else {
    Write-Output "Other WSL distributions are running; the WSL VM was left up."
}
