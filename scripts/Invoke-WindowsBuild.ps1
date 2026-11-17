[CmdletBinding()]
param(
    [switch]$Test,
    [switch]$Smoke,
    [string]$Configuration = "Debug"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$buildLog = Join-Path $env:TEMP "subordinate-build.log"
$testLog = Join-Path $env:TEMP "subordinate-test.log"
$smokeLog = Join-Path $env:TEMP "subordinate-smoke-windows.log"

$env:SubordinateDeploying = "true"

function Get-DeployDirectory {
    $homeDirectory = $env:SUBORDINATE_HOME

    if ([string]::IsNullOrEmpty($homeDirectory)) {
        $homeDirectory = Join-Path $env:USERPROFILE ".subordinate"
    }

    return Join-Path $homeDirectory "bin"
}

function Get-DeployFingerprint {
    param([string]$Directory)

    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        return ""
    }

    $root = (Resolve-Path -LiteralPath $Directory).Path
    $entries = @(Get-ChildItem -LiteralPath $Directory -Recurse -File -Force | ForEach-Object {
        [pscustomobject]@{
            RelativePath = $_.FullName.Substring($root.Length).TrimStart([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
            Length = $_.Length
            LastWriteTimeUtc = $_.LastWriteTimeUtc.ToString("o")
        }
    } | Sort-Object RelativePath)

    return ($entries | ConvertTo-Json -Compress -Depth 3)
}

function Write-LogMatches {
    param(
        [string]$Path,
        [string]$Pattern,
        [int]$Last
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    Select-String -Path $Path -Pattern $Pattern | Select-Object -Last $Last | ForEach-Object {
        $_.Line
    }
}

$deployDirectory = Get-DeployDirectory
$deployFingerprintBefore = Get-DeployFingerprint -Directory $deployDirectory
$buildFailed = $false
$testsFailed = $false
$smokeFailed = $false
$guardFailed = $false

Push-Location $repositoryRoot

try {
    & dotnet build Subordinate.slnx -c $Configuration -p:SubordinateDeploying=true *> $buildLog

    if ($LASTEXITCODE -ne 0) {
        $buildFailed = $true
    }

    if ($Test) {
        & dotnet test tests/Subordinate.Tests -c $Configuration -p:SubordinateDeploying=true *> $testLog

        if ($LASTEXITCODE -ne 0) {
            $testsFailed = $true
        }
    }

    if ($Smoke) {
        $gitBash = Join-Path $env:ProgramFiles "Git\bin\bash.exe"

        if (-not (Test-Path -LiteralPath $gitBash -PathType Leaf)) {
            throw "Git Bash was not found at $gitBash."
        }

        $smokeScript = (Join-Path $repositoryRoot "scripts/smoke.sh").Replace('\', '/')
        $binaryPath = (Join-Path $repositoryRoot "src/Subordinate/bin/$Configuration/net11.0/subordinate.exe").Replace('\', '/')
        $tuiBinaryPath = (Join-Path $repositoryRoot "src/Subordinate.Tui/bin/$Configuration/net11.0/subordinate-tui.exe").Replace('\', '/')
        & $gitBash $smokeScript --binary $binaryPath --tui-binary $tuiBinaryPath *> $smokeLog

        if ($LASTEXITCODE -ne 0) {
            $smokeFailed = $true
        }
    }
}
finally {
    Pop-Location
}

$deployFingerprintAfter = Get-DeployFingerprint -Directory $deployDirectory

if ($deployFingerprintBefore -ne $deployFingerprintAfter) {
    $guardFailed = $true
    Write-Output "DEPLOY GUARD FAILED: $deployDirectory changed during the build. Stop and tell the owner."
}

Write-Output "Build log: $buildLog"

if ($Test) {
    Write-Output "Test log: $testLog"
}

if ($Smoke) {
    Write-Output "Smoke log: $smokeLog"
}

Write-Output "Build summary:"
Write-LogMatches -Path $buildLog -Pattern ': error |: warning |Build succeeded|Build FAILED|Warning\(s\)|Error\(s\)' -Last 30

if ($Test) {
    Write-Output "Test summary:"
    Write-LogMatches -Path $testLog -Pattern '^\s+Failed |Passed!|Failed!' -Last 40
}

if ($Smoke) {
    Write-Output "Smoke summary:"
    Write-LogMatches -Path $smokeLog -Pattern '^SMOKE ' -Last 100
}

if ($buildFailed -or $testsFailed -or $smokeFailed -or $guardFailed) {
    exit 1
}

exit 0
