[CmdletBinding()]
param(
    [string]$DistroName = "subordinate-linux",
    [string]$UserName = "tester"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$env:WSL_UTF8 = "1"
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

function ConvertTo-WslPath {
    param([string]$WindowsPath)

    $full = [System.IO.Path]::GetFullPath($WindowsPath)
    $drive = $full.Substring(0, 1).ToLowerInvariant()
    $rest = $full.Substring(2).Replace('\', '/')
    return "/mnt/$drive$rest"
}

function Invoke-Wsl {
    param([string[]]$Arguments)

    & wsl.exe @Arguments

    if ($LASTEXITCODE -ne 0) {
        throw "wsl.exe $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

$listOutput = & wsl.exe --list --verbose

if ($LASTEXITCODE -ne 0) {
    throw "wsl.exe --list --verbose failed with exit code $LASTEXITCODE."
}

$previousDefault = $null
$distroExists = $false

foreach ($line in $listOutput) {
    if ($line -match '^\s*\*\s*(\S+)') {
        $previousDefault = $Matches[1]
    }

    if ($line -match '^\s*\*?\s*(\S+)') {
        if ($Matches[1] -eq $DistroName) {
            $distroExists = $true
        }
    }
}

try {
    if (-not $distroExists) {
        $online = & wsl.exe --list --online

        if ($LASTEXITCODE -ne 0) {
            throw "wsl.exe --list --online failed with exit code $LASTEXITCODE."
        }

        if (-not ($online -match 'Ubuntu-24\.04')) {
            throw "Ubuntu-24.04 is not offered by wsl --list --online. Ask the owner to install it."
        }

        & wsl.exe --install Ubuntu-24.04 --name $DistroName --no-launch

        if ($LASTEXITCODE -ne 0) {
            throw "wsl.exe --install failed with exit code $LASTEXITCODE. If it asked for elevation or a reboot, stop and ask the owner."
        }
    }

    $wslRepository = ConvertTo-WslPath -WindowsPath $repositoryRoot
    $rootScript = "$wslRepository/scripts/wsl/setup-root.sh"
    $null | & wsl.exe -d $DistroName -u root --exec bash $rootScript $UserName

    if ($LASTEXITCODE -ne 0) {
        throw "setup-root.sh failed with exit code $LASTEXITCODE."
    }

    Invoke-Wsl -Arguments @("--terminate", $DistroName)

    $sdkVersion = $null
    Push-Location $repositoryRoot

    try {
        $sdkVersion = & dotnet --version

        if ($LASTEXITCODE -ne 0) {
            throw "dotnet --version failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }

    $userScript = "$wslRepository/scripts/wsl/setup-user.sh"
    $null | & wsl.exe -d $DistroName -u $UserName --exec bash $userScript $sdkVersion

    if ($LASTEXITCODE -ne 0) {
        throw "setup-user.sh failed with exit code $LASTEXITCODE."
    }
}
finally {
    if ($previousDefault -and $previousDefault -ne $DistroName) {
        try {
            Invoke-Wsl -Arguments @("--set-default", $previousDefault)
        }
        catch {
            Write-Warning "The previous default distro could not be restored: $_"
        }
    }

    try {
        & (Join-Path $PSScriptRoot "Stop-TestDistro.ps1") -DistroName $DistroName

        if ($LASTEXITCODE -ne 0) {
            throw "Stop-TestDistro.ps1 failed with exit code $LASTEXITCODE."
        }
    }
    catch {
        Write-Warning "The test distro could not be stopped: $_"
    }
}
