[CmdletBinding()]
param(
    [string]$Configuration = "Debug",
    [string]$LiveConfig = (Join-Path $env:USERPROFILE ".subordinate\config.json")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$subordinate = Join-Path $repositoryRoot "src/Subordinate/bin/$Configuration/net11.0/subordinate.exe"
$tui = Join-Path $repositoryRoot "src/Subordinate.Tui/bin/$Configuration/net11.0/subordinate-tui.exe"
$log = Join-Path $env:TEMP "subordinate-tui-live.log"
$failed = $false
$tempRoot = $null

function Write-Result {
    param([string]$Line)

    Write-Output $Line
}

function Get-FirstLine {
    param([string]$Text)

    if ([string]::IsNullOrEmpty($Text)) {
        return ""
    }

    return ($Text -split "\r?\n" | Select-Object -First 1)
}

function Limit-Text {
    param(
        [string]$Text,
        [int]$Length = 300
    )

    if ($Text.Length -le $Length) {
        return $Text
    }

    return $Text.Substring(0, $Length) + "..."
}

if (-not (Test-Path -LiteralPath $LiveConfig -PathType Leaf)) {
    Write-Result "LIVE SKIP: the live configuration was not found at $LiveConfig."
    exit 0
}

$config = Get-Content -Raw -LiteralPath $LiveConfig | ConvertFrom-Json
$defaultAlias = [string]$config.default_model
$modelProperty = $config.models.PSObject.Properties[$defaultAlias]

if ($null -eq $modelProperty) {
    Write-Result "LIVE SKIP: the configuration has no default model."
    exit 0
}

$providerProperty = $config.providers.PSObject.Properties[[string]$modelProperty.Value.provider]

if ($null -eq $providerProperty) {
    Write-Result "LIVE SKIP: the default model names an unknown provider."
    exit 0
}

$keyName = [string]$providerProperty.Value.api_key_env

if ([string]::IsNullOrEmpty($keyName)) {
    Write-Result "LIVE SKIP: the configuration does not name an API key variable."
    exit 0
}

if ([string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($keyName))) {
    Write-Result "LIVE SKIP: $keyName is not set."
    exit 0
}

if (-not (Test-Path -LiteralPath $subordinate -PathType Leaf)) {
    Write-Result "LIVE FAIL start: $subordinate was not found."
    exit 1
}

if (-not (Test-Path -LiteralPath $tui -PathType Leaf)) {
    Write-Result "LIVE FAIL feed: $tui was not found."
    exit 1
}

try {
    $tempRoot = Join-Path $env:TEMP ("st-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
    $tempHome = Join-Path $tempRoot "home"
    $tempWork = Join-Path $tempRoot "work"
    [System.IO.Directory]::CreateDirectory($tempHome) | Out-Null
    [System.IO.Directory]::CreateDirectory($tempWork) | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $tempWork "alpha.txt"), "alpha`n", (New-Object System.Text.UTF8Encoding($false)))
    [System.IO.File]::WriteAllText((Join-Path $tempWork "beta.txt"), "beta`n", (New-Object System.Text.UTF8Encoding($false)))

    $config | Add-Member -NotePropertyName shells -NotePropertyValue ([pscustomobject]@{}) -Force
    $monitorProperty = $config.PSObject.Properties["monitor"]

    if ($null -ne $monitorProperty -and $null -ne $monitorProperty.Value) {
        $monitorProperty.Value.PSObject.Properties.Remove("socket_path")
        $monitorProperty.Value | Add-Member -NotePropertyName enabled -NotePropertyValue $true -Force
    }
    else {
        $config | Add-Member -NotePropertyName monitor -NotePropertyValue ([pscustomobject]@{ enabled = $true }) -Force
    }

    $configText = $config | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText((Join-Path $tempHome "config.json"), $configText, (New-Object System.Text.UTF8Encoding($false)))
    $env:SUBORDINATE_HOME = $tempHome

    $startOutput = & $subordinate start --name live-tui --workdir $tempWork --policy readonly --max-turns 6 --prompt "Use the shell tool to list the files, read both files, then summarize them in two sentences." 2>&1
    $startExit = $LASTEXITCODE

    $feedOutput = & $tui --print --wait-seconds 8 2>&1
    $feedExit = $LASTEXITCODE

    $detailOutput = & $tui --print --session live-tui --wait-seconds 4 2>&1
    $detailExit = $LASTEXITCODE

    $waitOutput = & $subordinate wait --name live-tui --workdir $tempWork --timeout-seconds 300 2>&1
    $waitExit = $LASTEXITCODE

    $finalOutput = & $tui --print --session live-tui 2>&1
    $finalExit = $LASTEXITCODE

    $logText = @(
        "START (exit $startExit):",
        ($startOutput -join "`n"),
        "",
        "FEED (exit $feedExit):",
        ($feedOutput -join "`n"),
        "",
        "DETAIL DURING RUN (exit $detailExit):",
        ($detailOutput -join "`n"),
        "",
        "WAIT (exit $waitExit):",
        ($waitOutput -join "`n"),
        "",
        "FINAL (exit $finalExit):",
        ($finalOutput -join "`n")
    ) -join "`n"
    [System.IO.File]::WriteAllText($log, $logText, (New-Object System.Text.UTF8Encoding($false)))

    $feedText = $feedOutput -join "`n"
    $feedHeader = Get-FirstLine -Text $feedText

    if ($startExit -eq 0 -and $feedExit -eq 0 -and $feedHeader.Contains("1 runs seen")) {
        Write-Result "LIVE PASS feed"
    }
    else {
        $failed = $true
        Write-Result ("LIVE FAIL feed: " + (Limit-Text -Text $feedHeader))
    }

    $finalText = $finalOutput -join "`n"

    if ($waitExit -eq 0 -and $finalExit -eq 0 -and $finalText.Contains("-- assistant") -and $finalText -match "live-tui\s+finished") {
        Write-Result "LIVE PASS history"
    }
    else {
        $failed = $true
        Write-Result ("LIVE FAIL history: wait exit $waitExit; " + (Limit-Text -Text (Get-FirstLine -Text $finalText)))
    }
}
finally {
    if ($null -ne $tempRoot) {
        try {
            & $subordinate stop --name live-tui --workdir (Join-Path $tempRoot "work") 2>&1 | Out-Null
        }
        catch {
        }

        try {
            Remove-Item -LiteralPath $tempRoot -Recurse -Force
        }
        catch {
        }
    }
}

if ($failed) {
    exit 1
}

exit 0
