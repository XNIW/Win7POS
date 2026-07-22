[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ToolDirectory,
    [string]$DotNetExe = "dotnet",
    [string]$ToolsConfigPath = (Join-Path $PSScriptRoot "..\eng\supply-chain\tools.json"),
    [switch]$ValidateConfigOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Read-JsonFile([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required JSON file not found: $path"
    }

    try {
        return [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $path).Path) | ConvertFrom-Json
    }
    catch {
        throw "Invalid JSON in '$path': $($_.Exception.Message)"
    }
}

function Assert-Hash([string]$label, [string]$value) {
    if ($value -cnotmatch '^[0-9a-f]{64}$') {
        throw "$label must be exactly 64 lowercase SHA-256 hex characters."
    }
}

function Assert-ToolConfig($config) {
    if ([int]$config.schemaVersion -ne 1) {
        throw "Unsupported supply-chain tool config schemaVersion."
    }

    if ([string]$config.cycloneDx.packageId -cne "CycloneDX" -or
        [string]$config.cycloneDx.version -cne "6.2.0") {
        throw "CycloneDX must remain pinned to package CycloneDX 6.2.0."
    }
    if ([string]$config.gitleaks.version -cne "8.30.1") {
        throw "Gitleaks must remain pinned to 8.30.1."
    }
    if ([string]$config.gitleaks.executable -cne "gitleaks.exe" -or
        [IO.Path]::IsPathRooted([string]$config.gitleaks.executable) -or
        [IO.Path]::GetFileName([string]$config.gitleaks.executable) -cne
            [string]$config.gitleaks.executable) {
        throw "Gitleaks executable must be the exact plain file name gitleaks.exe."
    }

    $cycloneUri = [Uri][string]$config.cycloneDx.url
    if ($cycloneUri.Scheme -cne "https" -or
        $cycloneUri.Host -cne "api.nuget.org" -or
        $cycloneUri.AbsolutePath -cne "/v3-flatcontainer/cyclonedx/6.2.0/cyclonedx.6.2.0.nupkg") {
        throw "CycloneDX URL must be the exact official NuGet flat-container asset."
    }

    $gitleaksUri = [Uri][string]$config.gitleaks.url
    if ($gitleaksUri.Scheme -cne "https" -or
        $gitleaksUri.Host -cne "github.com" -or
        $gitleaksUri.AbsolutePath -cne "/gitleaks/gitleaks/releases/download/v8.30.1/gitleaks_8.30.1_windows_x64.zip") {
        throw "Gitleaks URL must be the exact official GitHub release asset."
    }

    Assert-Hash "CycloneDX sha256" ([string]$config.cycloneDx.sha256)
    Assert-Hash "Gitleaks sha256" ([string]$config.gitleaks.sha256)
    foreach ($item in @($config.cycloneDx, $config.gitleaks)) {
        if ([int64]$item.size -le 0) {
            throw "Pinned tool asset size must be positive."
        }
        if ([string]::IsNullOrWhiteSpace([string]$item.fileName) -or
            [System.IO.Path]::GetFileName([string]$item.fileName) -cne [string]$item.fileName) {
            throw "Pinned tool fileName must be a plain file name."
        }
    }
}

function Assert-Asset([string]$path, $tool, [string]$label) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "$label asset is missing: $path"
    }
    $file = Get-Item -LiteralPath $path
    if ([int64]$file.Length -ne [int64]$tool.size) {
        throw "$label asset size mismatch: expected $($tool.size), actual $($file.Length)."
    }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -cne [string]$tool.sha256) {
        throw "$label SHA-256 mismatch: expected $($tool.sha256), actual $actual."
    }
}

function Invoke-Download([Uri]$uri, [string]$destination) {
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $uri -OutFile $destination
    }
    catch {
        throw "Failed to download '$uri': $($_.Exception.Message)"
    }
}

$configPath = (Resolve-Path -LiteralPath $ToolsConfigPath).Path
$config = Read-JsonFile $configPath
Assert-ToolConfig $config
if ($ValidateConfigOnly) {
    Write-Host "PASS: pinned supply-chain tool configuration is valid"
    exit 0
}

$dotnetCommand = Get-Command $DotNetExe -ErrorAction Stop
$dotnetPath = $dotnetCommand.Source
$sdkVersion = (& $dotnetPath --version).Trim()
if ($LASTEXITCODE -ne 0 -or $sdkVersion -cne "10.0.301") {
    throw "Supply-chain tools require the pinned .NET SDK 10.0.301; actual '$sdkVersion'."
}

$toolRoot = [System.IO.Path]::GetFullPath($ToolDirectory)
$markerPath = Join-Path $toolRoot "supply-chain-toolchain.json"
$downloads = Join-Path $toolRoot "downloads"
$cyclonePackage = Join-Path $downloads ([string]$config.cycloneDx.fileName)
$gitleaksArchive = Join-Path $downloads ([string]$config.gitleaks.fileName)

if (Test-Path -LiteralPath $toolRoot) {
    $entries = @(Get-ChildItem -LiteralPath $toolRoot -Force)
    if ($entries.Count -gt 0 -and -not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        throw "ToolDirectory is non-empty without a verified marker: $toolRoot"
    }
}
else {
    New-Item -ItemType Directory -Path $toolRoot | Out-Null
}

if (Test-Path -LiteralPath $markerPath -PathType Leaf) {
    $marker = Read-JsonFile $markerPath
    $configHash = (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ([string]$marker.configSha256 -cne $configHash) {
        throw "Existing toolchain marker does not match the committed tool configuration."
    }
    Assert-Asset $cyclonePackage $config.cycloneDx "CycloneDX"
    Assert-Asset $gitleaksArchive $config.gitleaks "Gitleaks"
    $cycloneDll = Join-Path $toolRoot ([string]$marker.cycloneDx.commandDll)
    $gitleaksExe = Join-Path $toolRoot ([string]$marker.gitleaks.executable)
    if (-not (Test-Path -LiteralPath $cycloneDll -PathType Leaf) -or
        -not (Test-Path -LiteralPath $gitleaksExe -PathType Leaf)) {
        throw "Existing pinned tool binaries are incomplete."
    }
    $cycloneVersion = (& $dotnetPath $cycloneDll --version).Trim()
    $gitleaksVersion = (& $gitleaksExe version).Trim()
    if ($cycloneVersion -notlike "6.2.0+*" -or $gitleaksVersion -cne "8.30.1") {
        throw "Existing pinned tool version verification failed."
    }
    Write-Host "PASS: existing CycloneDX 6.2.0 and Gitleaks 8.30.1 toolchain verified"
    exit 0
}

New-Item -ItemType Directory -Force -Path $downloads | Out-Null
Invoke-Download ([Uri][string]$config.cycloneDx.url) $cyclonePackage
Assert-Asset $cyclonePackage $config.cycloneDx "CycloneDX"
Invoke-Download ([Uri][string]$config.gitleaks.url) $gitleaksArchive
Assert-Asset $gitleaksArchive $config.gitleaks "Gitleaks"

$feed = Join-Path $toolRoot "cyclonedx-feed"
$cycloneTool = Join-Path $toolRoot "cyclonedx"
New-Item -ItemType Directory -Path $feed, $cycloneTool | Out-Null
Copy-Item -LiteralPath $cyclonePackage -Destination (Join-Path $feed ([string]$config.cycloneDx.fileName))
$escapedFeed = [Security.SecurityElement]::Escape($feed)
$nugetConfig = Join-Path $toolRoot "cyclonedx-only.NuGet.Config"
$nugetXml = "<?xml version=`"1.0`" encoding=`"utf-8`"?><configuration><packageSources><clear/><add key=`"pinned-local`" value=`"$escapedFeed`" /></packageSources></configuration>"
[System.IO.File]::WriteAllText($nugetConfig, $nugetXml, [Text.UTF8Encoding]::new($false))

& $dotnetPath tool install CycloneDX --version 6.2.0 --tool-path $cycloneTool --configfile $nugetConfig --no-cache
if ($LASTEXITCODE -ne 0) {
    throw "Pinned CycloneDX tool installation failed with exit code $LASTEXITCODE."
}
$cycloneDll = @(Get-ChildItem -LiteralPath (Join-Path $cycloneTool ".store") -Filter CycloneDX.dll -Recurse |
    Where-Object { $_.FullName -match '[\\/]tools[\\/]net10\.0[\\/]any[\\/]CycloneDX\.dll$' })
if ($cycloneDll.Count -ne 1) {
    throw "Expected exactly one CycloneDX net10.0 command assembly; found $($cycloneDll.Count)."
}
$cycloneVersion = (& $dotnetPath $cycloneDll[0].FullName --version).Trim()
if ($LASTEXITCODE -ne 0 -or $cycloneVersion -notlike "6.2.0+*") {
    throw "CycloneDX executable version mismatch: '$cycloneVersion'."
}

$gitleaksTool = Join-Path $toolRoot "gitleaks"
New-Item -ItemType Directory -Path $gitleaksTool | Out-Null
Expand-Archive -LiteralPath $gitleaksArchive -DestinationPath $gitleaksTool
$gitleaksExe = Join-Path $gitleaksTool ([string]$config.gitleaks.executable)
if (-not (Test-Path -LiteralPath $gitleaksExe -PathType Leaf)) {
    throw "Gitleaks executable missing after verified archive extraction."
}
$gitleaksVersion = (& $gitleaksExe version).Trim()
if ($LASTEXITCODE -ne 0 -or $gitleaksVersion -cne "8.30.1") {
    throw "Gitleaks executable version mismatch: '$gitleaksVersion'."
}

$relativeCycloneDll = [System.IO.Path]::GetRelativePath($toolRoot, $cycloneDll[0].FullName)
$relativeGitleaksExe = [System.IO.Path]::GetRelativePath($toolRoot, $gitleaksExe)
$toolMarker = [ordered]@{
    schemaVersion = 1
    configSha256 = (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash.ToLowerInvariant()
    dotnetSdk = $sdkVersion
    cycloneDx = [ordered]@{
        version = [string]$config.cycloneDx.version
        packageSha256 = [string]$config.cycloneDx.sha256
        commandDll = $relativeCycloneDll
    }
    gitleaks = [ordered]@{
        version = [string]$config.gitleaks.version
        archiveSha256 = [string]$config.gitleaks.sha256
        executable = $relativeGitleaksExe
    }
}
[System.IO.File]::WriteAllText(
    $markerPath,
    ($toolMarker | ConvertTo-Json -Depth 6),
    [Text.UTF8Encoding]::new($false))

Write-Host "PASS: CycloneDX 6.2.0 package verified and installed from its pinned local feed"
Write-Host "PASS: Gitleaks 8.30.1 archive verified and installed"
Write-Host "Toolchain marker: $markerPath"
