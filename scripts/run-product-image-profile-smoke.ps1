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
    throw "Product-image profile WPF smoke harness is missing; build the x86 net48 harness first."
}

$tempRoot = [System.IO.Path]::GetFullPath(
    [System.IO.Path]::GetTempPath())
$dataDir = [System.IO.Path]::GetFullPath((Join-Path $tempRoot (
    "Win7POS.ProductImageProfile." + [Guid]::NewGuid().ToString("N"))))
if (-not $dataDir.StartsWith(
        $tempRoot,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to create product-image profile smoke data outside the system temp directory."
}
New-Item -ItemType Directory -Path $dataDir | Out-Null

try {
    $quotedDataDir = '"' + $dataDir.Replace('"', '\"') + '"'
    $process = Start-Process `
        -FilePath $harnessExe `
        -ArgumentList @(
            "--data-dir",
            $quotedDataDir,
            "--product-image-profile-smoke") `
        -PassThru `
        -WindowStyle Hidden
    $artifact = Join-Path $dataDir "product-image-profile-smoke.txt"
    $harnessError = Join-Path $dataDir "harness-error.txt"
    if (-not $process.WaitForExit(180000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        [void]$process.WaitForExit(10000)
        throw "Product-image profile smoke exceeded its 180-second timeout."
    }
    if ($process.ExitCode -ne 0) {
        $detail = if (Test-Path -LiteralPath $harnessError -PathType Leaf) {
            [System.IO.File]::ReadAllText($harnessError).Trim()
        } else {
            "harness-error.txt was not produced"
        }
        throw "Product-image profile smoke failed with exit code $($process.ExitCode): $detail"
    }
    if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) {
        throw "Product-image profile smoke did not produce its result artifact."
    }

    $result = [System.IO.File]::ReadAllText($artifact).Trim()
    if (-not $result.StartsWith("PASS", [StringComparison]::Ordinal) -or
        $result.IndexOf(
            "net48_request_serialization=true",
            [StringComparison]::Ordinal) -lt 0 -or
        $result.IndexOf(
            "net48_json_stringify_response=true",
            [StringComparison]::Ordinal) -lt 0 -or
        $result.IndexOf(
            "net48_storage_error_mapping=true",
            [StringComparison]::Ordinal) -lt 0 -or
        $result.IndexOf(
            "staging_diagnostic_redacted=true",
            [StringComparison]::Ordinal) -lt 0) {
        throw "Product-image profile smoke did not report every net48 contract PASS marker."
    }

    Write-Host $result
}
finally {
    if (Test-Path -LiteralPath $dataDir -PathType Container) {
        Remove-Item -LiteralPath $dataDir -Recurse -Force
    }
}
