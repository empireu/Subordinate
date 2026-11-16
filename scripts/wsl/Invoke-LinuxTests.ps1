[CmdletBinding()]
param(
    [string]$DistroName = "subordinate-linux",
    [string]$UserName = "tester",
    [string]$LogFile = (Join-Path $env:TEMP "subordinate-linux.log"),
    [switch]$Smoke,
    [switch]$Live,
    [string]$LiveConfig,
    [switch]$KeepRunning
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

if (-not [string]::IsNullOrEmpty($LiveConfig)) {
    $Live = $true
}

if ($Live) {
    $Smoke = $true
}

$failed = $false

Set-Content -Path $LogFile -Value "" -Encoding utf8

$wslRepository = ConvertTo-WslPath -WindowsPath $repositoryRoot
$linuxRepository = "/home/$UserName/subordinate"

Write-Output "Log: $LogFile"

try {
    $syncArguments = @(
        "-d", $DistroName, "-u", $UserName, "--exec", "rsync", "-a", "--delete",
        "--exclude=bin/", "--exclude=obj/", "--exclude=.git/", "--exclude=.idea/", "--exclude=.vs/", "--exclude=diary/",
        "$wslRepository/", "$linuxRepository/"
    )
    & wsl.exe @syncArguments *>> $LogFile

    if ($LASTEXITCODE -ne 0) {
        $failed = $true
    }

    & wsl.exe -d $DistroName -u $UserName --exec bash "$linuxRepository/scripts/test-linux.sh" *>> $LogFile

    if ($LASTEXITCODE -ne 0) {
        $failed = $true
    }

    if ($Smoke) {
        $smokeArguments = @(
            "-d", $DistroName, "-u", $UserName, "--exec", "bash",
            "$linuxRepository/scripts/smoke.sh",
            "--binary", "/home/$UserName/.subordinate/bin/subordinate"
        )

        if ($Live) {
            $configPath = $null

            if (-not [string]::IsNullOrEmpty($LiveConfig)) {
                $configPath = $LiveConfig
            }
            elseif (-not [string]::IsNullOrEmpty($env:SUBORDINATE_HOME)) {
                $configPath = Join-Path $env:SUBORDINATE_HOME "config.json"
            }
            else {
                $configPath = Join-Path $env:USERPROFILE ".subordinate\config.json"
            }

            if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
                throw "The live configuration was not found at $configPath."
            }

            $config = Get-Content -Raw -LiteralPath $configPath | ConvertFrom-Json
            $keyNames = @()
            $providersProperty = $config.PSObject.Properties["providers"]

            if ($null -ne $providersProperty) {
                foreach ($provider in $providersProperty.Value.PSObject.Properties) {
                    $apiKeyEnvProperty = $provider.Value.PSObject.Properties["api_key_env"]

                    if ($null -ne $apiKeyEnvProperty -and -not [string]::IsNullOrEmpty($apiKeyEnvProperty.Value)) {
                        $keyNames += [string]$apiKeyEnvProperty.Value
                    }
                }
            }

            if ($keyNames.Count -gt 0) {
                $entries = ($keyNames | ForEach-Object { "$_/u" }) -join ":"

                if ([string]::IsNullOrEmpty($env:WSLENV)) {
                    $env:WSLENV = $entries
                }
                else {
                    $env:WSLENV = $env:WSLENV + ":" + $entries
                }
            }

            $smokeArguments += @("--live-config", (ConvertTo-WslPath -WindowsPath $configPath))
        }

        & wsl.exe @smokeArguments *>> $LogFile

        if ($LASTEXITCODE -ne 0) {
            $failed = $true
        }
    }
}
finally {
    if (-not $KeepRunning) {
        & (Join-Path $PSScriptRoot "Stop-TestDistro.ps1") -DistroName $DistroName

        if ($LASTEXITCODE -ne 0) {
            $failed = $true
        }
    }
}

Select-String -Path $LogFile -Pattern '^SMOKE |Passed!|Failed!|^\s+Failed |: error |Build FAILED' | ForEach-Object {
    $_.Line
}

if ($failed) {
    exit 1
}

exit 0
