param(
    [string]$ReleasePackSource = "",
    [switch]$WriteManifests
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$fail = $false

function Fail([string]$message) {
    Write-Host "FAIL: $message" -ForegroundColor Red
    $script:fail = $true
}

function Pass([string]$message) {
    Write-Host "PASS: $message" -ForegroundColor Green
}

function Resolve-Source([string]$source) {
    if ([string]::IsNullOrWhiteSpace($source)) {
        return $null
    }

    if ([System.IO.Path]::IsPathRooted($source)) {
        return $source
    }

    return (Join-Path $repoRoot $source)
}

function Get-RelativePath([string]$root, [string]$path) {
    return $path.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')
}

$requiredFiles = @(
    "Win7POS.Wpf.exe",
    "Win7POS.Wpf.exe.config",
    "Win7POS.Core.dll",
    "Win7POS.Data.dll",
    "ClosedXML.dll",
    "DocumentFormat.OpenXml.dll",
    "ExcelDataReader.dll",
    "ExcelDataReader.DataSet.dll",
    "PdfSharp-gdi.dll",
    "System.Drawing.Common.dll",
    "System.IO.Packaging.dll",
    "System.Text.Encoding.CodePages.dll",
    "e_sqlite3.dll",
    "SQLitePCLRaw.batteries_v2.dll",
    "SQLitePCLRaw.core.dll",
    "SQLitePCLRaw.provider.e_sqlite3.dll",
    "zxing.dll",
    "zxing.presentation.dll",
    "ZXing.Windows.Compatibility.dll",
    "VERSION.txt",
    "README_RUN.txt",
    "RELEASE_CHECKLIST.txt",
    "set-admin-web-staging-url.bat"
)

$forbiddenFiles = @(
    "Win7POS.Cli.exe",
    "Win7POS.Cli.dll"
)

if ([string]::IsNullOrWhiteSpace($ReleasePackSource)) {
    Pass "ReleasePack completeness checker loaded"
    Pass "Required files: $($requiredFiles -join ', ')"
    Pass "Forbidden runtime files: $($forbiddenFiles -join ', ')"
    Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
    exit 0
}

$source = Resolve-Source $ReleasePackSource
if (-not (Test-Path $source)) {
    Fail "ReleasePack source missing: $ReleasePackSource"
}

$tempDir = $null
$root = $source
if (-not $fail -and -not (Test-Path $source -PathType Container)) {
    if ([System.IO.Path]::GetExtension($source) -notmatch "\.zip") {
        Fail "ReleasePack source must be a folder or zip: $ReleasePackSource"
    }
    else {
        $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("win7pos-pack-" + [Guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
        Expand-Archive -Path $source -DestinationPath $tempDir -Force
        $root = $tempDir
    }
}

if (-not $fail) {
    foreach ($name in $requiredFiles) {
        $found = Get-ChildItem -Path $root -Recurse -File -Filter $name -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($null -eq $found) {
            Fail "ReleasePack missing required file: $name"
        }
        else {
            Pass "ReleasePack contains $name"
        }
    }

    $cliFolder = Join-Path $root "cli"
    if (Test-Path $cliFolder) {
        Fail "ReleasePack must not bundle CLI diagnostics under runtime folder: cli"
    }
    else {
        Pass "ReleasePack does not bundle CLI diagnostics folder"
    }

    foreach ($name in $forbiddenFiles) {
        $found = Get-ChildItem -Path $root -Recurse -File -Filter $name -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($null -ne $found) {
            Fail "ReleasePack contains forbidden CLI runtime file: $name"
        }
        else {
            Pass "ReleasePack does not contain $name"
        }
    }

    if ($WriteManifests) {
        if ($tempDir) {
            Fail "-WriteManifests requires a folder source, not a zip"
        }
        else {
            $files = Get-ChildItem -Path $root -Recurse -File -ErrorAction Stop |
                Where-Object { $_.Name -ne "APP-FILES.txt" -and $_.Name -ne "SHA256SUMS.txt" } |
                Sort-Object FullName

            $appFiles = $files | ForEach-Object { Get-RelativePath $root $_.FullName }
            [System.IO.File]::WriteAllLines((Join-Path $root "APP-FILES.txt"), $appFiles, [System.Text.Encoding]::UTF8)

            $hashLines = foreach ($file in $files) {
                $relative = Get-RelativePath $root $file.FullName
                $hash = Get-FileHash -Algorithm SHA256 -Path $file.FullName
                "$($hash.Hash.ToLowerInvariant())  $relative"
            }
            [System.IO.File]::WriteAllLines((Join-Path $root "SHA256SUMS.txt"), $hashLines, [System.Text.Encoding]::UTF8)
            Pass "ReleasePack manifests written: APP-FILES.txt, SHA256SUMS.txt"
        }
    }
}

if ($tempDir -and (Test-Path $tempDir)) {
    Remove-Item -Path $tempDir -Recurse -Force
}

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
