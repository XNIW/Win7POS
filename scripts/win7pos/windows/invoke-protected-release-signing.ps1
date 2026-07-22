[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Application", "Installer")]
    [string]$Mode,

    [Parameter(Mandatory = $true)]
    [string]$ArtifactRoot,

    [string]$ApplicationPayloadRoot = "",
    [string]$InstallerPath = "",
    [string]$ApplicationSigningRecordPath = "",

    [Parameter(Mandatory = $true)]
    [string]$SignToolPath,

    [string]$SigningToolchainConfigPath = (Join-Path $PSScriptRoot "release-signing-toolchain.json"),

    [Parameter(Mandatory = $true)]
    [string]$CertificateThumbprint,

    [Parameter(Mandatory = $true)]
    [string]$TimestampUrl,

    [Parameter(Mandatory = $true)]
    [string]$ProductVersion,

    [Parameter(Mandatory = $true)]
    [string]$ReleaseTag,

    [Parameter(Mandatory = $true)]
    [string]$CommitSha,

    [Parameter(Mandatory = $true)]
    [string]$ChecksumManifestPath,

    [Parameter(Mandatory = $true)]
    [string]$UnsignedPayloadManifestPath,

    [Parameter(Mandatory = $true)]
    [string]$SbomPath,

    [Parameter(Mandatory = $true)]
    [string]$ProvenancePath,

    [Parameter(Mandatory = $true)]
    [string]$AttestationPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputSigningRecordPath,

    [Parameter(Mandatory = $true)]
    [string]$EnvironmentName,

    [string]$RequiredEnvironmentName = "win7pos-protected-release",
    [switch]$NonProductionFixture,
    [switch]$SkipFileVersionCheck
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Get-PathInsideRoot {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$RootPrefix
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not [string]::Equals($fullPath, $Root, [StringComparison]::OrdinalIgnoreCase) -and
        -not $fullPath.StartsWith($RootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Signing path escapes ArtifactRoot."
    }
    return $fullPath
}

function Get-RelativePath {
    param([string]$Root, [string]$Path)
    return [IO.Path]::GetRelativePath($Root, $Path).Replace('\', '/')
}

function Invoke-SignToolQuietly {
    param([string[]]$Arguments, [string]$Operation)

    & $SignToolPath @Arguments 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Pinned signtool $Operation failed closed with exit code $LASTEXITCODE."
    }
}

function Assert-CurrentSigningRecord {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedMode,
        [Parameter(Mandatory = $true)][string]$ExpectedThumbprint
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required earlier signing record is missing."
    }
    try {
        $record = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw "Required earlier signing record is invalid JSON."
    }
    $expectedTimestampProtocol = if ($NonProductionFixture) { "NONE-NON-PRODUCTION-FIXTURE" } else { "RFC3161" }
    $expectedTimestampDigest = if ($NonProductionFixture) { "NONE" } else { "SHA-256" }
    if ($record.schema -ne "https://xniw.github.io/Win7POS/schemas/release-signing-record/v1" -or
        $record.mode -ne $ExpectedMode -or
        $record.productVersion -ne $ProductVersion -or
        $record.releaseTag -ne $ReleaseTag -or
        $record.commitSha -ne $CommitSha -or
        [bool]$record.nonProductionFixture -ne [bool]$NonProductionFixture -or
        (($record.certificate.thumbprint -replace '\s', '').ToUpperInvariant()) -ne $ExpectedThumbprint -or
        $record.timestamp.protocol -ne $expectedTimestampProtocol -or
        $record.timestamp.digestAlgorithm -ne $expectedTimestampDigest -or
        ($NonProductionFixture -and -not [string]::IsNullOrEmpty([string]$record.timestamp.url))) {
        throw "Required earlier signing record does not match this release and signer."
    }
    foreach ($fileRecord in @($record.files)) {
        $currentPath = Get-PathInsideRoot -Path (Join-Path $root ([string]$fileRecord.path)) -Root $root -RootPrefix $rootPrefix
        if (-not (Test-Path -LiteralPath $currentPath -PathType Leaf) -or
            (Get-FileHash -LiteralPath $currentPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne
                [string]$fileRecord.signedSha256) {
            throw "An application binary changed after its recorded signing phase."
        }
        $signature = Get-AuthenticodeSignature -LiteralPath $currentPath
        $actualThumbprint = if ($signature.SignerCertificate) {
            ($signature.SignerCertificate.Thumbprint -replace '\s', '').ToUpperInvariant()
        }
        else { "" }
        $timestampStateValid = if ($NonProductionFixture) {
            $null -eq $signature.TimeStamperCertificate
        }
        else {
            $null -ne $signature.TimeStamperCertificate
        }
        $expectedSignatureStatus = if ($NonProductionFixture) {
            [System.Management.Automation.SignatureStatus]::UnknownError
        }
        else { [System.Management.Automation.SignatureStatus]::Valid }
        if ($signature.Status -ne $expectedSignatureStatus -or
            $actualThumbprint -ne $ExpectedThumbprint -or -not $timestampStateValid) {
            throw "An application signature or timestamp is no longer valid before installer signing."
        }
    }
}

if (-not $IsWindows) {
    throw "Protected Authenticode signing is supported only on Windows."
}
if ($ProductVersion -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or
    $ReleaseTag -ne "v$ProductVersion") {
    throw "Protected-release version and tag do not match."
}
if ($CommitSha -notmatch '^[0-9a-fA-F]{40}$') {
    throw "A full exact commit SHA is required for signing."
}
$CommitSha = $CommitSha.ToLowerInvariant()

if ($NonProductionFixture) {
    if ($EnvironmentName -ne "NON-PRODUCTION-SELF-SIGNED-FIXTURE") {
        throw "Fixture signing is permitted only in the explicit NON-PRODUCTION fixture context."
    }
}
elseif ($EnvironmentName -ne $RequiredEnvironmentName) {
    throw "Production signing requires the exact protected GitHub Environment."
}
if ($SkipFileVersionCheck -and -not $NonProductionFixture) {
    throw "File-version validation can be skipped only for the explicit NON-PRODUCTION fixture."
}

$timestampUri = $null
if (-not [Uri]::TryCreate($TimestampUrl, [UriKind]::Absolute, [ref]$timestampUri) -or
    $timestampUri.Scheme -notin @("http", "https")) {
    throw "TimestampUrl must be an absolute RFC3161 HTTP(S) endpoint."
}
if (-not [string]::IsNullOrEmpty($timestampUri.UserInfo) -or
    -not [string]::IsNullOrEmpty($timestampUri.Query) -or
    -not [string]::IsNullOrEmpty($timestampUri.Fragment)) {
    throw "TimestampUrl must not contain credentials, query data or fragments."
}
if (-not $NonProductionFixture -and
    ($timestampUri.Scheme -ne "https" -or $timestampUri.IsLoopback -or
        $timestampUri.DnsSafeHost -in @("localhost", "127.0.0.1", "::1"))) {
    throw "Production signing requires a non-loopback HTTPS timestamp endpoint."
}

if (-not (Test-Path -LiteralPath $SigningToolchainConfigPath -PathType Leaf)) {
    throw "Pinned signing toolchain configuration is missing."
}
$toolchain = Get-Content -LiteralPath $SigningToolchainConfigPath -Raw | ConvertFrom-Json
if (-not (Test-Path -LiteralPath $SignToolPath -PathType Leaf) -or
    (Get-FileHash -LiteralPath $SignToolPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne
        [string]$toolchain.signToolSha256 -or
    [Diagnostics.FileVersionInfo]::GetVersionInfo($SignToolPath).ProductVersion -ne
        [string]$toolchain.signToolProductVersion) {
    throw "Pinned signtool path, hash or version validation failed."
}
$toolSignature = Get-AuthenticodeSignature -LiteralPath $SignToolPath
if ($toolSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
    $toolSignature.SignerCertificate.Subject -ne [string]$toolchain.expectedSignerSubject) {
    throw "Pinned signtool Microsoft signature validation failed."
}

$normalizedThumbprint = ($CertificateThumbprint -replace '\s', '').ToUpperInvariant()
if ($normalizedThumbprint -notmatch '^[0-9A-F]{40}$') {
    throw "Certificate thumbprint must be one exact SHA-1 certificate-store identifier."
}
$certificatePath = "Cert:\CurrentUser\My\$normalizedThumbprint"
if (-not (Test-Path -LiteralPath $certificatePath -PathType Leaf)) {
    throw "The requested signing certificate is not installed in CurrentUser/My."
}
$certificate = Get-Item -LiteralPath $certificatePath
if (-not $certificate.HasPrivateKey -or
    $certificate.NotBefore.ToUniversalTime() -gt [DateTime]::UtcNow -or
    $certificate.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow) {
    throw "The requested signing certificate lacks a usable private key or validity window."
}
$codeSigningEku = @($certificate.Extensions | Where-Object { $_.Oid.Value -eq "2.5.29.37" } |
        ForEach-Object { $_.EnhancedKeyUsages } |
        ForEach-Object { $_ } |
        Where-Object { $_.Value -eq "1.3.6.1.5.5.7.3.3" })
if ($codeSigningEku.Count -eq 0) {
    throw "The requested certificate is not authorized for code signing."
}

$root = [IO.Path]::GetFullPath($ArtifactRoot).TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "ArtifactRoot does not exist."
}
$rootPrefix = $root + [IO.Path]::DirectorySeparatorChar
$outputRecordPath = Get-PathInsideRoot -Path $OutputSigningRecordPath -Root $root -RootPrefix $rootPrefix
$unsignedChecksumIndex = @{}

$resolvedChecksumManifestPath = Get-PathInsideRoot -Path $ChecksumManifestPath -Root $root -RootPrefix $rootPrefix
$resolvedUnsignedPayloadManifestPath = Get-PathInsideRoot -Path $UnsignedPayloadManifestPath -Root $root -RootPrefix $rootPrefix
$resolvedSbomPath = Get-PathInsideRoot -Path $SbomPath -Root $root -RootPrefix $rootPrefix
$resolvedProvenancePath = Get-PathInsideRoot -Path $ProvenancePath -Root $root -RootPrefix $rootPrefix
$resolvedAttestationPath = Get-PathInsideRoot -Path $AttestationPath -Root $root -RootPrefix $rootPrefix
foreach ($resolvedMetadataPath in @(
        $resolvedChecksumManifestPath, $resolvedUnsignedPayloadManifestPath, $resolvedSbomPath,
        $resolvedProvenancePath, $resolvedAttestationPath)) {
    if (-not (Test-Path -LiteralPath $resolvedMetadataPath -PathType Leaf) -or
        (Get-Item -LiteralPath $resolvedMetadataPath).Length -eq 0) {
        throw "Signing requires checksums, unsigned manifest, SBOM, provenance and attestation."
    }
}

if ($Mode -eq "Application") {
    & (Join-Path $PSScriptRoot "test-protected-release-artifacts.ps1") `
        -ArtifactRoot $root `
        -ChecksumManifestPath $resolvedChecksumManifestPath `
        -UnsignedPayloadManifestPath $resolvedUnsignedPayloadManifestPath `
        -SbomPath $resolvedSbomPath `
        -ProvenancePath $resolvedProvenancePath `
        -AttestationPath $resolvedAttestationPath `
        -ExpectedStage unsigned `
        -CommitSha $CommitSha `
        -ProductVersion $ProductVersion `
        -ReleaseTag $ReleaseTag

    $unsignedChecksums = Get-Content -LiteralPath $resolvedChecksumManifestPath -Raw | ConvertFrom-Json
    foreach ($entry in @($unsignedChecksums.files)) {
        $unsignedChecksumIndex[[string]$entry.path] = [string]$entry.sha256
    }

    if ([string]::IsNullOrWhiteSpace($ApplicationPayloadRoot)) {
        throw "Application mode requires ApplicationPayloadRoot."
    }
    $payloadRoot = Get-PathInsideRoot -Path $ApplicationPayloadRoot -Root $root -RootPrefix $rootPrefix
    if (-not (Test-Path -LiteralPath $payloadRoot -PathType Container)) {
        throw "Application payload root does not exist."
    }
    $targets = @(Get-ChildItem -LiteralPath $payloadRoot -Recurse -File | Where-Object {
            $_.Name -match '^Win7POS\..+\.(exe|dll)$'
        } | Sort-Object -Property FullName)
    foreach ($requiredApplicationFile in @("Win7POS.Wpf.exe", "Win7POS.Core.dll", "Win7POS.Data.dll")) {
        if (-not ($targets | Where-Object { $_.Name -eq $requiredApplicationFile })) {
            throw "Application payload is missing a required project-owned binary."
        }
    }
}
else {
    if ([string]::IsNullOrWhiteSpace($InstallerPath) -or
        [string]::IsNullOrWhiteSpace($ApplicationSigningRecordPath)) {
        throw "Installer mode requires InstallerPath and ApplicationSigningRecordPath."
    }
    $resolvedApplicationRecord = Get-PathInsideRoot -Path $ApplicationSigningRecordPath -Root $root -RootPrefix $rootPrefix
    Assert-CurrentSigningRecord -Path $resolvedApplicationRecord -ExpectedMode "Application" -ExpectedThumbprint $normalizedThumbprint
    $resolvedInstaller = Get-PathInsideRoot -Path $InstallerPath -Root $root -RootPrefix $rootPrefix
    if (-not (Test-Path -LiteralPath $resolvedInstaller -PathType Leaf) -or
        [IO.Path]::GetFileName($resolvedInstaller) -ne "Win7POS-$ProductVersion-Setup.exe") {
        throw "Installer mode requires the exact versioned Win7POS installer filename."
    }
    $targets = @(Get-Item -LiteralPath $resolvedInstaller)
}

foreach ($target in $targets) {
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($target.FullName)
    if (-not $SkipFileVersionCheck -and $version.FileVersion -ne "$ProductVersion.0") {
        throw "A signing target does not match the authoritative file version."
    }
    if (($target.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Signing refuses reparse-point targets."
    }
}

$stagingRoot = Join-Path $root (".signing-stage-{0}" -f [Guid]::NewGuid().ToString("N"))
[IO.Directory]::CreateDirectory($stagingRoot) | Out-Null
$records = [Collections.Generic.List[object]]::new()
try {
    foreach ($target in $targets) {
        $relativePath = Get-RelativePath -Root $root -Path $target.FullName
        $stagedPath = Join-Path $stagingRoot $relativePath
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($stagedPath)) | Out-Null
        [IO.File]::Copy($target.FullName, $stagedPath, $true)
        $unsignedHash = (Get-FileHash -LiteralPath $stagedPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($Mode -eq "Application" -and
            (-not $unsignedChecksumIndex.ContainsKey($relativePath) -or
                $unsignedChecksumIndex[$relativePath] -ne $unsignedHash)) {
            throw "An application signing target is not bound by the validated unsigned checksum manifest."
        }

        if ($NonProductionFixture) {
            Invoke-SignToolQuietly -Operation "NON-PRODUCTION fixture sign" -Arguments @(
                "sign", "/fd", "SHA256", "/sha1", $normalizedThumbprint,
                "/d", "Win7POS NON-PRODUCTION Fixture", $stagedPath)
        }
        else {
            Invoke-SignToolQuietly -Operation "sign" -Arguments @(
                "sign", "/fd", "SHA256", "/sha1", $normalizedThumbprint,
                "/tr", $TimestampUrl, "/td", "SHA256", "/d", "Win7POS", $stagedPath)
            Invoke-SignToolQuietly -Operation "signature/timestamp verification" -Arguments @(
                "verify", "/pa", "/all", "/tw", $stagedPath)
        }

        $signature = Get-AuthenticodeSignature -LiteralPath $stagedPath
        $actualThumbprint = if ($signature.SignerCertificate) {
            ($signature.SignerCertificate.Thumbprint -replace '\s', '').ToUpperInvariant()
        }
        else { "" }
        $timestampStateValid = if ($NonProductionFixture) {
            $null -eq $signature.TimeStamperCertificate
        }
        else {
            $null -ne $signature.TimeStamperCertificate
        }
        $expectedSignatureStatus = if ($NonProductionFixture) {
            [System.Management.Automation.SignatureStatus]::UnknownError
        }
        else { [System.Management.Automation.SignatureStatus]::Valid }
        if ($signature.Status -ne $expectedSignatureStatus -or
            $actualThumbprint -ne $normalizedThumbprint -or -not $timestampStateValid) {
            throw "Post-sign signer, signature or RFC3161 timestamp validation failed."
        }

        $signedHash = (Get-FileHash -LiteralPath $stagedPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($signedHash -eq $unsignedHash) {
            throw "Signing did not change the target Authenticode image."
        }
        $records.Add([pscustomobject][ordered]@{
                path = $relativePath
                unsignedSha256 = $unsignedHash
                signedSha256 = $signedHash
                signerThumbprint = $actualThumbprint
                timestampSignerThumbprint = if ($signature.TimeStamperCertificate) {
                    $signature.TimeStamperCertificate.Thumbprint.ToUpperInvariant()
                }
                else { "" }
            }) | Out-Null
    }

    foreach ($record in $records) {
        $sourcePath = Join-Path $stagingRoot ([string]$record.path)
        $destinationPath = Join-Path $root ([string]$record.path)
        [IO.File]::Copy($sourcePath, $destinationPath, $true)
        if ((Get-FileHash -LiteralPath $destinationPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne
            [string]$record.signedSha256) {
            throw "A signed target failed its final copy verification."
        }
    }
}
finally {
    if (Test-Path -LiteralPath $stagingRoot -PathType Container) {
        [IO.Directory]::Delete($stagingRoot, $true)
    }
}

[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputRecordPath)) | Out-Null
$recordDocument = [pscustomobject][ordered]@{
    schema = "https://xniw.github.io/Win7POS/schemas/release-signing-record/v1"
    mode = $Mode
    nonProductionFixture = [bool]$NonProductionFixture
    productVersion = $ProductVersion
    releaseTag = $ReleaseTag
    commitSha = $CommitSha
    digestAlgorithm = "SHA-256"
    signingTool = [pscustomobject][ordered]@{
        package = [string]$toolchain.packageId
        version = [string]$toolchain.version
        executableSha256 = [string]$toolchain.signToolSha256
    }
    certificate = [pscustomobject][ordered]@{
        thumbprint = $normalizedThumbprint
        subject = $certificate.Subject
        validationPolicy = if ($NonProductionFixture) {
            "SELF-SIGNED-UNTRUSTED-NON-PRODUCTION-FIXTURE"
        }
        else { "WINDOWS-TRUSTED-PRODUCTION" }
    }
    timestamp = [pscustomobject][ordered]@{
        protocol = if ($NonProductionFixture) { "NONE-NON-PRODUCTION-FIXTURE" } else { "RFC3161" }
        digestAlgorithm = if ($NonProductionFixture) { "NONE" } else { "SHA-256" }
        url = if ($NonProductionFixture) { "" } else { $TimestampUrl }
    }
    files = @($records)
}
[IO.File]::WriteAllText(
    $outputRecordPath,
    (($recordDocument | ConvertTo-Json -Depth 20) + "`n"),
    [Text.UTF8Encoding]::new($false))

Write-Host "Protected signing phase '$Mode': PASS ($($records.Count) files)."
