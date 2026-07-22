[CmdletBinding()]
param(
    [string]$ToolchainConfigPath = "",
    [string]$InstallerPath = "",
    [string]$IsccPath = "",
    [switch]$SkipAuthenticode
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ToolchainConfigPath)) {
    $ToolchainConfigPath = Join-Path $PSScriptRoot "inno-setup-toolchain.json"
}
if (-not (Test-Path -LiteralPath $ToolchainConfigPath -PathType Leaf)) {
    throw "Pinned Inno Setup toolchain configuration is missing: $ToolchainConfigPath"
}

try {
    $config = [System.IO.File]::ReadAllText($ToolchainConfigPath) | ConvertFrom-Json
}
catch {
    throw "Pinned Inno Setup toolchain configuration is invalid JSON: $($_.Exception.Message)"
}

if ($config.version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw "Pinned Inno Setup version must be an exact MAJOR.MINOR.PATCH value."
}
$tagVersion = $config.version.Replace('.', '_')
$expectedUrl = "https://github.com/jrsoftware/issrc/releases/download/is-$tagVersion/innosetup-$($config.version).exe"
if (-not [string]::Equals($config.downloadUrl, $expectedUrl, [StringComparison]::Ordinal)) {
    throw "Pinned Inno Setup URL must be the official immutable JRSoftware GitHub release URL for version $($config.version)."
}
if ($config.installerSha256 -notmatch '^[0-9a-f]{64}$') {
    throw "Pinned Inno Setup installer SHA-256 must be 64 lowercase hexadecimal characters."
}
if ($config.installerSize -ne 10592232) {
    throw "Pinned Inno Setup installer size must match the reviewed 6.7.3 release asset."
}
if ([string]::IsNullOrWhiteSpace($config.expectedSignerSubject)) {
    throw "Pinned Inno Setup signer subject must not be empty."
}

if (-not [string]::IsNullOrWhiteSpace($InstallerPath)) {
    if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf)) {
        throw "Inno Setup installer is missing: $InstallerPath"
    }
    $installerHash = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not [string]::Equals($installerHash, $config.installerSha256, [StringComparison]::Ordinal)) {
        throw "Inno Setup installer SHA-256 mismatch: expected $($config.installerSha256), found $installerHash."
    }
    $installerSize = (Get-Item -LiteralPath $InstallerPath).Length
    if ($installerSize -ne $config.installerSize) {
        throw "Inno Setup installer size mismatch: expected $($config.installerSize), found $installerSize."
    }

    if (-not $SkipAuthenticode) {
        if (-not $IsWindows) {
            throw "Authenticode verification of the Inno Setup installer requires Windows."
        }
        $signature = Get-AuthenticodeSignature -LiteralPath $InstallerPath
        if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
            throw "Inno Setup installer Authenticode signature is not valid: $($signature.Status)."
        }
        $subject = $signature.SignerCertificate.Subject
        if ($subject -notlike "$($config.expectedSignerSubject)*") {
            throw "Inno Setup installer signer mismatch: expected '$($config.expectedSignerSubject)', found '$subject'."
        }
    }
}

if (-not [string]::IsNullOrWhiteSpace($IsccPath)) {
    if (-not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
        throw "Pinned Inno Setup compiler is missing: $IsccPath"
    }
    $resolvedIscc = (Resolve-Path -LiteralPath $IsccPath).Path
    $markerPath = Join-Path (Split-Path -Parent $resolvedIscc) "win7pos-inno-toolchain.json"
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        throw "Pinned Inno Setup compiler provenance marker is missing: $markerPath"
    }
    try {
        $marker = [System.IO.File]::ReadAllText($markerPath) | ConvertFrom-Json
    }
    catch {
        throw "Pinned Inno Setup compiler provenance marker is invalid JSON: $($_.Exception.Message)"
    }
    $compilerHash = (Get-FileHash -LiteralPath $resolvedIscc -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not [string]::Equals($marker.version, $config.version, [StringComparison]::Ordinal) -or
        -not [string]::Equals($marker.downloadUrl, $config.downloadUrl, [StringComparison]::Ordinal) -or
        -not [string]::Equals($marker.installerSha256, $config.installerSha256, [StringComparison]::Ordinal) -or
        $marker.installerSize -ne $config.installerSize -or
        -not [string]::Equals($marker.compilerSha256, $compilerHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Pinned Inno Setup compiler provenance marker does not match the reviewed toolchain or compiler bytes."
    }

    $versionParts = @($config.version.Split('.') | ForEach-Object { [int]$_ })
    $probeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("win7pos-iscc-probe-" + [Guid]::NewGuid().ToString("N"))
    $probeOutput = Join-Path $probeRoot "output"
    $probeScript = Join-Path $probeRoot "compiler-version-probe.iss"
    New-Item -ItemType Directory -Force -Path $probeOutput | Out-Null
    $probeLines = @(
        "#if VER != EncodeVer($($versionParts[0]),$($versionParts[1]),$($versionParts[2]))",
        "  #error Compiler version does not match the pinned Win7POS toolchain",
        "#endif",
        "[Setup]",
        "AppName=Win7POS compiler version probe",
        "AppVersion=1.0.0",
        "DefaultDirName={tmp}\Win7POSCompilerProbe",
        "Uninstallable=no",
        "OutputDir=$probeOutput",
        "OutputBaseFilename=compiler-version-probe"
    )
    [System.IO.File]::WriteAllLines($probeScript, $probeLines, (New-Object System.Text.UTF8Encoding($false)))
    try {
        $probeLog = & $resolvedIscc "/Qp" $probeScript 2>&1
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $probeOutput "compiler-version-probe.exe") -PathType Leaf)) {
            $firstError = ($probeLog | Where-Object { $_ -match '(?i)error' } | Select-Object -First 1) -as [string]
            throw "Installed Inno Setup compiler is not exact version $($config.version). $firstError"
        }
    }
    finally {
        if (Test-Path -LiteralPath $probeRoot -PathType Container) {
            Remove-Item -LiteralPath $probeRoot -Recurse -Force
        }
    }
}

[pscustomobject][ordered]@{
    Version = $config.version
    DownloadUrl = $config.downloadUrl
    InstallerSha256 = $config.installerSha256
    InstallerSize = $config.installerSize
    InstallerVerified = -not [string]::IsNullOrWhiteSpace($InstallerPath)
    CompilerVerified = -not [string]::IsNullOrWhiteSpace($IsccPath)
}
