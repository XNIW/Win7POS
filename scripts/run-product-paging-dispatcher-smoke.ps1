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
$harnessExe = $candidatePaths | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($harnessExe)) {
    throw "Product paging WPF smoke harness is missing; build the x86 net48 harness first."
}

$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$dataDir = [System.IO.Path]::GetFullPath((Join-Path $tempRoot ("Win7POS.ProductPagingDispatcher." + [Guid]::NewGuid().ToString("N"))))
if (-not $dataDir.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to create product paging smoke data outside the system temp directory."
}
New-Item -ItemType Directory -Path $dataDir | Out-Null

$process = Start-Process `
    -FilePath $harnessExe `
    -ArgumentList @("--data-dir", $dataDir, "--product-paging-dispatcher-smoke") `
    -Wait `
    -PassThru `
    -WindowStyle Hidden
$artifact = Join-Path $dataDir "product-paging-dispatcher-smoke.txt"
if ($process.ExitCode -ne 0) {
    throw "Product paging dispatcher smoke failed with exit code $($process.ExitCode). Artifact: $artifact"
}
if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) {
    throw "Product paging dispatcher smoke did not produce its result artifact: $artifact"
}

$result = [System.IO.File]::ReadAllText($artifact).Trim()
if (-not $result.StartsWith("PASS", [StringComparison]::Ordinal)) {
    throw "Product paging dispatcher smoke did not report PASS. Artifact: $artifact"
}

Write-Host $result
Write-Host "PRODUCT_PAGING_DISPATCHER_ARTIFACT=$artifact"
