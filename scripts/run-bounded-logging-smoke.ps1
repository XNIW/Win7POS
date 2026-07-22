[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [ValidateSet("x86")]
    [string]$Platform = "x86"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$candidatePaths = @(
    (Join-Path $repoRoot "tests\Win7POS.Wpf.UiSmokeHarness\bin\$Platform\$Configuration\net48\Win7POS.Wpf.UiSmokeHarness.exe"),
    (Join-Path $repoRoot "tests\Win7POS.Wpf.UiSmokeHarness\bin\$Configuration\net48\Win7POS.Wpf.UiSmokeHarness.exe")
)
$harnessExe = $candidatePaths |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($harnessExe)) {
    throw "Bounded logging WPF smoke harness is missing; build the x86 net48 harness first."
}

$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$dataDir = [System.IO.Path]::GetFullPath((Join-Path $tempRoot (
    "Win7POS.BoundedLogging." + [Guid]::NewGuid().ToString("N"))))
if (-not $dataDir.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to create bounded logging smoke data outside the system temp directory."
}
New-Item -ItemType Directory -Path $dataDir | Out-Null

$quotedDataDir = '"' + $dataDir.Replace('"', '\"') + '"'
$process = Start-Process `
    -FilePath $harnessExe `
    -ArgumentList @("--data-dir", $quotedDataDir, "--bounded-logging-smoke") `
    -PassThru `
    -WindowStyle Hidden
$artifact = Join-Path $dataDir "bounded-logging-smoke.txt"
if (-not $process.WaitForExit(120000)) {
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    throw "Bounded logging smoke exceeded its 120-second timeout. Artifact: $artifact"
}
if ($process.ExitCode -ne 0) {
    throw "Bounded logging smoke failed with exit code $($process.ExitCode). Artifact: $artifact"
}
if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) {
    throw "Bounded logging smoke did not produce its result artifact: $artifact"
}

$result = [System.IO.File]::ReadAllText($artifact).Trim()
if (-not $result.StartsWith("PASS", [StringComparison]::Ordinal)) {
    throw "Bounded logging smoke did not report PASS. Artifact: $artifact"
}

Write-Host $result
Write-Host "BOUNDED_LOGGING_ARTIFACT=$artifact"
