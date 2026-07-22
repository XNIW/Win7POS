[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ToolDirectory,

    [string]$ConfigPath = (Join-Path $PSScriptRoot "release-signing-toolchain.json"),
    [string]$GitHubEnvironmentFile = "",
    [switch]$AsJson
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Get-Sha512Base64 {
    param([Parameter(Mandatory = $true)][string]$Path)

    $algorithm = [System.Security.Cryptography.SHA512]::Create()
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        return [Convert]::ToBase64String($algorithm.ComputeHash($stream))
    }
    finally {
        $stream.Dispose()
        $algorithm.Dispose()
    }
}

function Assert-PinnedSignTool {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Config
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Pinned signtool executable is missing."
    }

    $actualHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not [string]::Equals($actualHash, [string]$Config.signToolSha256, [StringComparison]::Ordinal)) {
        throw "Pinned signtool SHA-256 validation failed."
    }

    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path).ProductVersion
    if (-not [string]::Equals($version, [string]$Config.signToolProductVersion, [StringComparison]::Ordinal)) {
        throw "Pinned signtool product version validation failed."
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate -or
        -not [string]::Equals(
            $signature.SignerCertificate.Subject,
            [string]$Config.expectedSignerSubject,
            [StringComparison]::Ordinal)) {
        throw "Pinned signtool Authenticode validation failed."
    }
}

if (-not $IsWindows) {
    throw "Pinned signtool installation is supported only on a Windows build machine."
}

if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
    throw "Signing toolchain configuration is missing."
}

try {
    $config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
}
catch {
    throw "Signing toolchain configuration is invalid JSON: $($_.Exception.Message)"
}

foreach ($requiredProperty in @(
        "packageId", "version", "downloadUrl", "packageSize", "packageSha256",
        "packageSha512Base64", "signToolRelativePath", "signToolSha256",
        "signToolProductVersion", "expectedSignerSubject")) {
    if ([string]::IsNullOrWhiteSpace([string]$config.$requiredProperty)) {
        throw "Signing toolchain configuration is missing '$requiredProperty'."
    }
}

if ($config.packageId -ne "Microsoft.Windows.SDK.BuildTools" -or
    $config.version -notmatch '^10\.0\.[0-9]+\.[0-9]+$') {
    throw "Signing toolchain package identity or version is not an accepted exact pin."
}

$downloadUri = [Uri]$config.downloadUrl
if ($downloadUri.Scheme -ne "https" -or
    $downloadUri.DnsSafeHost -ne "api.nuget.org" -or
    -not $downloadUri.AbsolutePath.EndsWith(
        "/$($config.packageId.ToLowerInvariant())/$($config.version)/$($config.packageId.ToLowerInvariant()).$($config.version).nupkg",
        [StringComparison]::Ordinal)) {
    throw "Signing toolchain download URL must be the exact official NuGet flat-container package."
}

if ($config.packageSha256 -notmatch '^[0-9a-f]{64}$' -or
    $config.packageSha512Base64 -notmatch '^[A-Za-z0-9+/]{86}==$' -or
    $config.signToolSha256 -notmatch '^[0-9a-f]{64}$' -or
    [long]$config.packageSize -le 0) {
    throw "Signing toolchain hashes or size are not valid immutable pins."
}

$toolRoot = [IO.Path]::GetFullPath($ToolDirectory)
[IO.Directory]::CreateDirectory($toolRoot) | Out-Null
$installRoot = Join-Path $toolRoot ("{0}-{1}" -f $config.packageId, $config.version)
$relativeSignTool = ([string]$config.signToolRelativePath).Replace('/', [IO.Path]::DirectorySeparatorChar)
$signToolPath = Join-Path $installRoot $relativeSignTool

if (Test-Path -LiteralPath $installRoot -PathType Container) {
    Assert-PinnedSignTool -Path $signToolPath -Config $config
}
else {
    $downloadRoot = Join-Path $toolRoot "downloads"
    [IO.Directory]::CreateDirectory($downloadRoot) | Out-Null
    $packagePath = Join-Path $downloadRoot ("{0}.{1}.nupkg" -f $config.packageId, $config.version)
    $downloadPath = "$packagePath.download"

    if (Test-Path -LiteralPath $downloadPath -PathType Leaf) {
        [IO.File]::Delete($downloadPath)
    }

    $oldProtocol = [Net.ServicePointManager]::SecurityProtocol
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $downloadUri -OutFile $downloadPath -UseBasicParsing
    }
    finally {
        [Net.ServicePointManager]::SecurityProtocol = $oldProtocol
    }

    $downloadInfo = Get-Item -LiteralPath $downloadPath
    if ($downloadInfo.Length -ne [long]$config.packageSize) {
        throw "Signing toolchain package size validation failed."
    }

    $actualSha256 = (Get-FileHash -LiteralPath $downloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not [string]::Equals($actualSha256, [string]$config.packageSha256, [StringComparison]::Ordinal)) {
        throw "Signing toolchain package SHA-256 validation failed."
    }

    $actualSha512 = Get-Sha512Base64 -Path $downloadPath
    if (-not [string]::Equals($actualSha512, [string]$config.packageSha512Base64, [StringComparison]::Ordinal)) {
        throw "Signing toolchain package NuGet SHA-512 validation failed."
    }

    [IO.File]::Move($downloadPath, $packagePath, $true)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stagingRoot = Join-Path $toolRoot (".extract-{0}" -f [Guid]::NewGuid().ToString("N"))
    [IO.Directory]::CreateDirectory($stagingRoot) | Out-Null
    try {
        $stagingPrefix = [IO.Path]::GetFullPath($stagingRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        $archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
        try {
            foreach ($entry in $archive.Entries) {
                $entryPath = [IO.Path]::GetFullPath((Join-Path $stagingRoot $entry.FullName))
                if (-not $entryPath.StartsWith($stagingPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "Signing toolchain package contains an unsafe archive path."
                }
            }
        }
        finally {
            $archive.Dispose()
        }

        [IO.Compression.ZipFile]::ExtractToDirectory($packagePath, $stagingRoot)
        $stagedSignTool = Join-Path $stagingRoot $relativeSignTool
        Assert-PinnedSignTool -Path $stagedSignTool -Config $config
        [IO.Directory]::Move($stagingRoot, $installRoot)
    }
    finally {
        if (Test-Path -LiteralPath $stagingRoot -PathType Container) {
            [IO.Directory]::Delete($stagingRoot, $true)
        }
    }

    Assert-PinnedSignTool -Path $signToolPath -Config $config
}

if (-not [string]::IsNullOrWhiteSpace($GitHubEnvironmentFile)) {
    $writer = [IO.StreamWriter]::new(
        $GitHubEnvironmentFile,
        $true,
        [Text.UTF8Encoding]::new($false))
    try {
        $writer.WriteLine("WIN7POS_SIGNTOOL_EXE=$signToolPath")
        $writer.WriteLine("WIN7POS_SIGNTOOL_VERSION=$($config.version)")
        $writer.WriteLine("WIN7POS_SIGNTOOL_PACKAGE_SHA256=$($config.packageSha256)")
    }
    finally {
        $writer.Dispose()
    }
}

$result = [pscustomobject][ordered]@{
    PackageId = [string]$config.packageId
    Version = [string]$config.version
    PackageSha256 = [string]$config.packageSha256
    SignToolPath = $signToolPath
    SignToolSha256 = [string]$config.signToolSha256
    Verified = $true
}

if ($AsJson) {
    $result | ConvertTo-Json -Compress
}
else {
    $result
}
