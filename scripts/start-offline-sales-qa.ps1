[CmdletBinding()]
param(
    [string]$DataDir = "",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipBuild,
    [switch]$SeedOnly
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$platform = "x86"
$harnessProject = Join-Path $repoRoot "tests\Win7POS.Wpf.UiSmokeHarness\Win7POS.Wpf.UiSmokeHarness.csproj"
$harnessExe = Join-Path $repoRoot ("tests\Win7POS.Wpf.UiSmokeHarness\bin\{0}\{1}\net48\Win7POS.Wpf.UiSmokeHarness.exe" -f $platform, $Configuration)
$appExe = Join-Path $repoRoot ("src\Win7POS.Wpf\bin\{0}\{1}\net48\Win7POS.Wpf.exe" -f $platform, $Configuration)

function Find-CompatibleDotnet {
    $candidates = New-Object System.Collections.Generic.List[string]
    if ($env:WIN7POS_DOTNET_EXE) { $candidates.Add($env:WIN7POS_DOTNET_EXE) | Out-Null }
    if ($env:DOTNET_ROOT) { $candidates.Add((Join-Path $env:DOTNET_ROOT "dotnet.exe")) | Out-Null }

    $fromPath = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($fromPath) { $candidates.Add($fromPath.Source) | Out-Null }

    $candidates.Add("C:\Dev\dotnet10\dotnet.exe") | Out-Null
    if ($env:USERPROFILE) {
        $candidates.Add((Join-Path $env:USERPROFILE ".dotnet10\dotnet.exe")) | Out-Null
    }

    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        Push-Location $repoRoot
        try {
            $version = (& $candidate --version 2>$null | Select-Object -First 1)
            if ($version -eq "10.0.301") {
                return (Resolve-Path -LiteralPath $candidate).Path
            }
        }
        finally {
            Pop-Location
        }
    }

    throw "Compatible .NET SDK 10.0.301 not found. Set WIN7POS_DOTNET_EXE."
}

function Get-CanonicalLocalQaPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or -not [System.IO.Path]::IsPathRooted($Path)) {
        throw "DataDir must be an absolute path on a local drive. UNC and device paths are not allowed."
    }

    $canonicalPath = [System.IO.Path]::GetFullPath($Path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $driveRoot = [System.IO.Path]::GetPathRoot($canonicalPath)
    if ([string]::IsNullOrWhiteSpace($driveRoot) -or
        $driveRoot -notmatch '^[A-Za-z]:\\$') {
        throw "DataDir must be on a local drive-letter path. UNC and device paths are not allowed."
    }

    try {
        $drive = New-Object -TypeName System.IO.DriveInfo -ArgumentList $driveRoot
        if (-not $drive.IsReady -or $drive.DriveType -ne [System.IO.DriveType]::Fixed) {
            throw "DataDir drive must be a ready local fixed drive: $driveRoot"
        }
    }
    catch {
        if ($_.Exception.Message -like "DataDir drive must be*") { throw }
        throw "Unable to verify that DataDir uses a local fixed drive: $driveRoot"
    }

    $qaBase = [System.IO.Path]::GetFullPath(
        (Join-Path $driveRoot "POSData\Win7POS-QA")).TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)
    $qaPrefix = $qaBase + [System.IO.Path]::DirectorySeparatorChar
    if ([string]::Equals($canonicalPath, $qaBase, [StringComparison]::OrdinalIgnoreCase) -or
        -not $canonicalPath.StartsWith($qaPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Offline sales QA requires a data directory canonically contained below $qaBase."
    }

    return [pscustomobject]@{
        DataDir = $canonicalPath
        QaBase = $qaBase
        DriveRoot = $driveRoot
    }
}

function Assert-NoExistingReparsePointAncestor {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CanonicalPath
    )

    $driveRoot = [System.IO.Path]::GetPathRoot($CanonicalPath)
    $relativePath = $CanonicalPath.Substring($driveRoot.Length)
    $segments = $relativePath.Split(
        [char[]]@(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar),
        [System.StringSplitOptions]::RemoveEmptyEntries)
    $current = $driveRoot
    $pathsToCheck = New-Object System.Collections.Generic.List[string]
    $pathsToCheck.Add($current) | Out-Null
    foreach ($segment in $segments) {
        $current = Join-Path $current $segment
        $pathsToCheck.Add($current) | Out-Null
    }

    foreach ($candidate in $pathsToCheck) {
        try {
            $attributes = [System.IO.File]::GetAttributes($candidate)
        }
        catch [System.IO.FileNotFoundException] {
            break
        }
        catch [System.IO.DirectoryNotFoundException] {
            break
        }
        catch {
            throw "Unable to verify DataDir ancestor attributes: $candidate"
        }

        if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Offline sales QA does not allow an existing reparse-point ancestor: $candidate"
        }
        if (($attributes -band [System.IO.FileAttributes]::Directory) -eq 0) {
            throw "Offline sales QA data path has a non-directory ancestor: $candidate"
        }
    }
}

if ([string]::IsNullOrWhiteSpace($DataDir)) {
    $driveRoot = [System.IO.Path]::GetPathRoot($repoRoot)
    $qaBase = Join-Path $driveRoot "POSData\Win7POS-QA"
    $DataDir = Join-Path $qaBase ("Offline-Sales-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
}

$validatedPath = Get-CanonicalLocalQaPath -Path $DataDir
$fullDataDir = $validatedPath.DataDir
Assert-NoExistingReparsePointAncestor -CanonicalPath $fullDataDir

if (Test-Path -LiteralPath $fullDataDir -PathType Container) {
    if (Get-ChildItem -LiteralPath $fullDataDir -Force | Select-Object -First 1) {
        throw "Offline sales QA requires a new or empty data directory: $fullDataDir"
    }
}

if (-not $SeedOnly -and (Get-Process -Name "Win7POS.Wpf" -ErrorAction SilentlyContinue)) {
    throw "Close the current Win7POS window before launching the isolated offline QA instance."
}

New-Item -ItemType Directory -Path $fullDataDir -Force | Out-Null
$postCreatePath = Get-CanonicalLocalQaPath -Path (Resolve-Path -LiteralPath $fullDataDir).Path
if (-not [string]::Equals($postCreatePath.DataDir, $fullDataDir, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Offline sales QA DataDir changed during creation."
}
Assert-NoExistingReparsePointAncestor -CanonicalPath $postCreatePath.DataDir

if (-not $SkipBuild) {
    $dotnet = Find-CompatibleDotnet
    Push-Location $repoRoot
    try {
        & $dotnet build $harnessProject -c $Configuration -p:Platform=$platform -p:PlatformTarget=$platform
        if ($LASTEXITCODE -ne 0) {
            throw "Offline sales QA build failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

if (-not (Test-Path -LiteralPath $harnessExe -PathType Leaf)) {
    throw "UI smoke harness not found: $harnessExe"
}
if (-not (Test-Path -LiteralPath $appExe -PathType Leaf)) {
    throw "Win7POS executable not found: $appExe"
}

$seedArguments = @(
    "--data-dir",
    ('"{0}"' -f $fullDataDir),
    "--offline-sales-sandbox"
)
$seedProcess = Start-Process `
    -FilePath $harnessExe `
    -ArgumentList $seedArguments `
    -WorkingDirectory (Split-Path -Parent $harnessExe) `
    -WindowStyle Hidden `
    -Wait `
    -PassThru
if ($seedProcess.ExitCode -ne 0) {
    throw "Offline sales QA seed failed with exit code $($seedProcess.ExitCode)."
}

$requiredSeedFiles = @(
    "pos.db",
    "pos-trusted-device.json",
    "QA-OFFLINE-SANDBOX.txt"
)
foreach ($fileName in $requiredSeedFiles) {
    $path = Join-Path $fullDataDir $fileName
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Offline sales QA seed did not create $fileName."
    }
}

if ($SeedOnly) {
    [pscustomobject]@{
        Mode = "SeedOnly"
        DataDir = $fullDataDir
        LeaseHours = 12
        HardwareEnabled = $false
    }
    return
}

$previousDataDir = $env:WIN7POS_DATA_DIR
$previousSafeStart = $env:WIN7POS_SAFE_START
$previousAdminWeb = $env:WIN7POS_ADMIN_WEB_BASE_URL
try {
    $env:WIN7POS_DATA_DIR = $fullDataDir
    $env:WIN7POS_SAFE_START = "1"
    $env:WIN7POS_ADMIN_WEB_BASE_URL = "http://127.0.0.1:9"
    $appProcess = Start-Process `
        -FilePath $appExe `
        -ArgumentList "--safe-start" `
        -WorkingDirectory (Split-Path -Parent $appExe) `
        -PassThru
}
finally {
    if ($null -eq $previousDataDir) { Remove-Item Env:WIN7POS_DATA_DIR -ErrorAction SilentlyContinue }
    else { $env:WIN7POS_DATA_DIR = $previousDataDir }
    if ($null -eq $previousSafeStart) { Remove-Item Env:WIN7POS_SAFE_START -ErrorAction SilentlyContinue }
    else { $env:WIN7POS_SAFE_START = $previousSafeStart }
    if ($null -eq $previousAdminWeb) { Remove-Item Env:WIN7POS_ADMIN_WEB_BASE_URL -ErrorAction SilentlyContinue }
    else { $env:WIN7POS_ADMIN_WEB_BASE_URL = $previousAdminWeb }
}

[pscustomobject]@{
    Mode = "OfflineSalesQa"
    ProcessId = $appProcess.Id
    DataDir = $fullDataDir
    LeaseHours = 12
    OnlineEndpoint = "loopback-only"
    HardwareEnabled = $false
}
