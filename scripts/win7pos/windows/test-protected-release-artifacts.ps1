[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactRoot,

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
    [ValidateSet("development-unsigned", "unsigned", "signed")]
    [string]$ExpectedStage,

    [Parameter(Mandatory = $true)]
    [string]$CommitSha,

    [Parameter(Mandatory = $true)]
    [string]$ProductVersion,

    [string]$BuildVersion = "",

    [Parameter(Mandatory = $true)]
    [AllowEmptyString()]
    [string]$ReleaseTag,

    [string[]]$SigningRecordPath = @(),
    [string]$ExpectedSignerThumbprint = "",
    [switch]$RequireProductionSignatures,

    [string[]]$PostMetadataArtifactPath = @()
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
        throw "Protected-release artifact path escapes ArtifactRoot."
    }
    return $fullPath
}

function Read-RequiredJson {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or
        (Get-Item -LiteralPath $Path).Length -eq 0) {
        throw "A required release metadata artifact is missing or empty."
    }
    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw "A required release metadata artifact is invalid JSON: $($_.Exception.Message)"
    }
}

function Get-RelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )
    return [IO.Path]::GetRelativePath($Root, $Path).Replace('\', '/')
}

function Get-ExactSha1Thumbprint {
    param(
        [AllowEmptyString()][string]$Value,
        [Parameter(Mandatory = $true)][string]$Label,
        [switch]$AllowEmpty
    )

    if ([string]::IsNullOrEmpty($Value)) {
        if ($AllowEmpty) {
            return ""
        }
        throw "$Label is required."
    }
    if ($Value -notmatch '^[0-9A-Fa-f]{40}$') {
        throw "$Label must be exactly 40 hexadecimal characters."
    }
    return $Value.ToUpperInvariant()
}

if ($CommitSha -notmatch '^[0-9a-fA-F]{40}$') {
    throw "A full exact commit SHA is required."
}
$CommitSha = $CommitSha.ToLowerInvariant()
if ($ProductVersion -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw "Protected-release product version is invalid."
}
if ([string]::IsNullOrWhiteSpace($BuildVersion)) {
    $BuildVersion = $ProductVersion
}
if ($ExpectedStage -eq "development-unsigned") {
    $expectedDevelopmentVersion = "$ProductVersion-dev.$($CommitSha.Substring(0, 12))"
    if (-not [string]::IsNullOrEmpty($ReleaseTag) -or $BuildVersion -ne $expectedDevelopmentVersion) {
        throw "Development evidence identity is invalid."
    }
}
elseif ($ReleaseTag -ne "v$ProductVersion" -or $BuildVersion -ne $ProductVersion) {
    throw "Protected-release version and tag do not match."
}
$signingRecordValues = @($SigningRecordPath)
$signerIdentityRequired = $ExpectedStage -eq "signed" -or
    $RequireProductionSignatures -or $signingRecordValues.Count -ne 0
$normalizedExpectedThumbprint = if ($signerIdentityRequired) {
    Get-ExactSha1Thumbprint -Value $ExpectedSignerThumbprint -Label "Expected signer thumbprint"
}
elseif ([string]::IsNullOrEmpty($ExpectedSignerThumbprint)) { "" }
else {
    Get-ExactSha1Thumbprint -Value $ExpectedSignerThumbprint -Label "Expected signer thumbprint"
}
if ($RequireProductionSignatures -and $ExpectedStage -ne "signed") {
    throw "Production signature validation is valid only for the signed release stage."
}

$root = [IO.Path]::GetFullPath($ArtifactRoot).TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "ArtifactRoot does not exist."
}
$rootItem = Get-Item -LiteralPath $root -Force
if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Protected-release validation refuses a reparse-point ArtifactRoot."
}
$rootPrefix = $root + [IO.Path]::DirectorySeparatorChar

$checksumPath = Get-PathInsideRoot -Path $ChecksumManifestPath -Root $root -RootPrefix $rootPrefix
$unsignedManifestPath = Get-PathInsideRoot -Path $UnsignedPayloadManifestPath -Root $root -RootPrefix $rootPrefix
$resolvedSbomPath = Get-PathInsideRoot -Path $SbomPath -Root $root -RootPrefix $rootPrefix
$resolvedProvenancePath = Get-PathInsideRoot -Path $ProvenancePath -Root $root -RootPrefix $rootPrefix
$resolvedAttestationPath = Get-PathInsideRoot -Path $AttestationPath -Root $root -RootPrefix $rootPrefix

$postMetadataRelativePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$allowedPostMetadataRelativePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($allowedPostMetadataRelativePath in @(
        "attestations/github-provenance.sigstore.json",
        "attestations/github-sbom.sigstore.json")) {
    [void]$allowedPostMetadataRelativePaths.Add($allowedPostMetadataRelativePath)
}
foreach ($postMetadataPathValue in @($PostMetadataArtifactPath)) {
    if ($ExpectedStage -ne "signed" -or -not $RequireProductionSignatures) {
        throw "Post-metadata artifacts are allowed only for final production-signed validation."
    }
    $postMetadataPath = Get-PathInsideRoot -Path $postMetadataPathValue -Root $root -RootPrefix $rootPrefix
    if (-not (Test-Path -LiteralPath $postMetadataPath -PathType Leaf) -or
        (Get-Item -LiteralPath $postMetadataPath).Length -eq 0) {
        throw "An explicitly allowed post-metadata artifact is missing or empty."
    }
    $postMetadataRelativePath = Get-RelativePath -Root $root -Path $postMetadataPath
    if (-not $allowedPostMetadataRelativePaths.Contains($postMetadataRelativePath) -or
        -not $postMetadataRelativePaths.Add($postMetadataRelativePath)) {
        throw "Post-metadata artifacts must be the two exact GitHub attestation bundle paths."
    }
}
if ($postMetadataRelativePaths.Count -ne 0 -and
    ($postMetadataRelativePaths.Count -ne $allowedPostMetadataRelativePaths.Count -or
        @($allowedPostMetadataRelativePaths | Where-Object {
                -not $postMetadataRelativePaths.Contains($_)
            }).Count -ne 0)) {
    throw "Both exact GitHub attestation bundles are required when post-metadata artifacts are declared."
}

foreach ($requiredFile in @(
        $checksumPath, $unsignedManifestPath, $resolvedSbomPath,
        $resolvedProvenancePath, $resolvedAttestationPath)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf) -or
        (Get-Item -LiteralPath $requiredFile).Length -eq 0) {
        throw "A required checksum, unsigned manifest, SBOM, provenance or attestation artifact is missing or empty."
    }
}

$checksumDocument = Read-RequiredJson -Path $checksumPath
if ($checksumDocument.schema -ne "https://xniw.github.io/Win7POS/schemas/release-checksums/v1" -or
    $checksumDocument.algorithm -ne "SHA-256" -or
    $checksumDocument.stage -ne $ExpectedStage -or
    $checksumDocument.productVersion -ne $ProductVersion -or
    $checksumDocument.buildVersion -ne $BuildVersion -or
    $checksumDocument.releaseTag -ne $ReleaseTag -or
    $checksumDocument.commitSha -ne $CommitSha) {
    throw "Release checksum manifest identity does not match the requested release."
}

$checksumFiles = @($checksumDocument.files)
if ($checksumFiles.Count -eq 0) {
    throw "Release checksum manifest contains no files."
}
$checksumIndex = @{}
foreach ($entry in $checksumFiles) {
    if ($entry.path -notmatch '^[^\\/].*' -or
        $entry.path -match '(^|/)\.\.(/|$)' -or
        $entry.sha256 -notmatch '^[0-9a-f]{64}$' -or
        [long]$entry.size -lt 0 -or
        $checksumIndex.ContainsKey([string]$entry.path)) {
        throw "Release checksum manifest contains an invalid or duplicate file record."
    }
    $filePath = Get-PathInsideRoot -Path (Join-Path $root ([string]$entry.path)) -Root $root -RootPrefix $rootPrefix
    if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
        throw "A checksummed release artifact is missing."
    }
    $file = Get-Item -LiteralPath $filePath
    $actualHash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($file.Length -ne [long]$entry.size -or $actualHash -ne [string]$entry.sha256) {
        throw "A release artifact differs from its SHA-256 manifest."
    }
    $checksumIndex[[string]$entry.path] = [string]$entry.sha256
}

$metadataExceptionIndex = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($metadataPath in @($checksumPath, $resolvedProvenancePath, $resolvedAttestationPath)) {
    $metadataRelativePath = Get-RelativePath -Root $root -Path $metadataPath
    if ($checksumIndex.ContainsKey($metadataRelativePath)) {
        throw "Checksum, provenance and attestation outputs may not checksum themselves."
    }
    [void]$metadataExceptionIndex.Add($metadataRelativePath)
}
foreach ($postMetadataRelativePath in $postMetadataRelativePaths) {
    if ($checksumIndex.ContainsKey($postMetadataRelativePath)) {
        throw "A post-metadata artifact must not also appear in the pre-existing checksum manifest."
    }
    [void]$metadataExceptionIndex.Add($postMetadataRelativePath)
}

$publishedFileIndex = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($item in Get-ChildItem -LiteralPath $root -Recurse -Force) {
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Protected-release validation refuses reparse points in the published artifact tree."
    }
    if ($item.PSIsContainer) {
        continue
    }
    $publishedPath = Get-PathInsideRoot -Path $item.FullName -Root $root -RootPrefix $rootPrefix
    $publishedRelativePath = Get-RelativePath -Root $root -Path $publishedPath
    if (-not $publishedFileIndex.Add($publishedRelativePath)) {
        throw "The published artifact tree contains a duplicate canonical path."
    }
    if (-not $checksumIndex.ContainsKey($publishedRelativePath) -and
        -not $metadataExceptionIndex.Contains($publishedRelativePath)) {
        throw "The published artifact tree contains a file that is absent from the checksum manifest."
    }
}
$expectedPublishedFileCount = $checksumIndex.Count + $metadataExceptionIndex.Count
if ($publishedFileIndex.Count -ne $expectedPublishedFileCount -or
    @($checksumIndex.Keys | Where-Object { -not $publishedFileIndex.Contains($_) }).Count -ne 0 -or
    @($metadataExceptionIndex | Where-Object { -not $publishedFileIndex.Contains($_) }).Count -ne 0) {
    throw "The published artifact tree is not a closed-world match for its checksum manifest."
}

$unsignedRelative = Get-RelativePath -Root $root -Path $unsignedManifestPath
$sbomRelative = Get-RelativePath -Root $root -Path $resolvedSbomPath
if (-not $checksumIndex.ContainsKey($unsignedRelative) -or -not $checksumIndex.ContainsKey($sbomRelative)) {
    throw "Unsigned payload manifest and SBOM must both be covered by the checksum manifest."
}

$sbom = Read-RequiredJson -Path $resolvedSbomPath
$bomFormat = if ($sbom.PSObject.Properties["bomFormat"]) { [string]$sbom.bomFormat } else { "" }
$specVersion = if ($sbom.PSObject.Properties["specVersion"]) { [string]$sbom.specVersion } else { "" }
$spdxVersion = if ($sbom.PSObject.Properties["spdxVersion"]) { [string]$sbom.spdxVersion } else { "" }
$isCycloneDx = $bomFormat -eq "CycloneDX" -and -not [string]::IsNullOrWhiteSpace($specVersion)
$isSpdx = -not [string]::IsNullOrWhiteSpace($spdxVersion) -and
    $spdxVersion.StartsWith("SPDX-", [StringComparison]::Ordinal)
if (-not $isCycloneDx -and -not $isSpdx) {
    throw "SBOM is neither a recognizable CycloneDX nor SPDX JSON document."
}

$unsignedPayloadHashIndex = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
$canonicalApplicationPathIndex = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$expectedApplicationPathIndex = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$expectedInstallerRelativePath = "Win7POS-$ProductVersion-Setup.exe"
if ($ExpectedStage -eq "signed") {
    $unsignedPayloadManifest = Read-RequiredJson -Path $unsignedManifestPath
    $unsignedPayloadFiles = @($unsignedPayloadManifest.files)
    if ($unsignedPayloadManifest.format -ne "win7pos-unsigned-payload-manifest-v1" -or
        $unsignedPayloadManifest.hashAlgorithm -ne "SHA-256" -or
        $unsignedPayloadManifest.commitSha -ne $CommitSha -or
        $unsignedPayloadManifest.buildVersion -ne $BuildVersion -or
        $unsignedPayloadManifest.platform -ne "x86" -or
        $unsignedPayloadManifest.targetFramework -ne "net48" -or
        [int]$unsignedPayloadManifest.fileCount -ne $unsignedPayloadFiles.Count -or
        $unsignedPayloadFiles.Count -eq 0) {
        throw "Canonical unsigned payload manifest identity or file inventory is invalid."
    }
    foreach ($unsignedPayloadFile in $unsignedPayloadFiles) {
        $unsignedRelativePath = [string]$unsignedPayloadFile.path
        $unsignedSha256 = [string]$unsignedPayloadFile.sha256
        if ([string]::IsNullOrWhiteSpace($unsignedRelativePath) -or
            $unsignedRelativePath.Contains("\") -or
            [IO.Path]::IsPathRooted($unsignedRelativePath) -or
            $unsignedRelativePath -match '(^|/)\.\.?(?:/|$)' -or
            $unsignedSha256 -notmatch '^[0-9a-f]{64}$' -or
            $unsignedPayloadHashIndex.ContainsKey($unsignedRelativePath)) {
            throw "Canonical unsigned payload manifest contains an invalid or duplicate file record."
        }
        $unsignedPayloadHashIndex.Add($unsignedRelativePath, $unsignedSha256)
        $unsignedLeafName = @($unsignedRelativePath -split '/')[-1]
        if ($unsignedLeafName -match '^Win7POS\..+\.(exe|dll)$' -and
            -not $canonicalApplicationPathIndex.Add("Win7POS/$unsignedRelativePath")) {
            throw "Canonical unsigned payload manifest contains a duplicate project-binary path."
        }
    }

    $applicationPayloadRoot = Join-Path $root "Win7POS"
    if (-not (Test-Path -LiteralPath $applicationPayloadRoot -PathType Container)) {
        throw "Signed validation requires the Win7POS application payload directory."
    }
    foreach ($applicationFile in @(Get-ChildItem -LiteralPath $applicationPayloadRoot -Recurse -File | Where-Object {
                $_.Name -match '^Win7POS\..+\.(exe|dll)$'
            })) {
        $applicationRelativePath = Get-RelativePath -Root $root -Path $applicationFile.FullName
        if (-not $expectedApplicationPathIndex.Add($applicationRelativePath)) {
            throw "Signed application payload contains a duplicate canonical project-binary path."
        }
    }
    if ($expectedApplicationPathIndex.Count -eq 0) {
        throw "Signed validation found no project-owned Win7POS application binaries."
    }
    if ($canonicalApplicationPathIndex.Count -eq 0 -or
        $canonicalApplicationPathIndex.Count -ne $expectedApplicationPathIndex.Count -or
        @($canonicalApplicationPathIndex | Where-Object {
                -not $expectedApplicationPathIndex.Contains($_)
            }).Count -ne 0 -or
        @($expectedApplicationPathIndex | Where-Object {
                -not $canonicalApplicationPathIndex.Contains($_)
            }).Count -ne 0) {
        throw "Canonical unsigned payload project-binary set is not an exact match for the current signed application payload."
    }

    $rootExecutables = @(Get-ChildItem -LiteralPath $root -File | Where-Object {
            $_.Extension -eq ".exe"
        })
    if ($rootExecutables.Count -ne 1 -or
        (Get-RelativePath -Root $root -Path $rootExecutables[0].FullName) -ne $expectedInstallerRelativePath) {
        throw "Signed validation requires exactly one root executable: the exact versioned Win7POS installer."
    }
}

$provenance = Read-RequiredJson -Path $resolvedProvenancePath
$attestation = Read-RequiredJson -Path $resolvedAttestationPath
if (($provenance | ConvertTo-Json -Depth 40 -Compress) -ne
    ($attestation | ConvertTo-Json -Depth 40 -Compress)) {
    throw "In-toto attestation does not exactly match the SLSA provenance statement."
}
if ($provenance._type -ne "https://in-toto.io/Statement/v1" -or
    $provenance.predicateType -ne "https://slsa.dev/provenance/v1" -or
    $provenance.predicate.buildDefinition.externalParameters.productVersion -ne $ProductVersion -or
    $provenance.predicate.buildDefinition.externalParameters.buildVersion -ne $BuildVersion -or
    $provenance.predicate.buildDefinition.externalParameters.releaseTag -ne $ReleaseTag -or
    $provenance.predicate.buildDefinition.externalParameters.stage -ne $ExpectedStage) {
    throw "SLSA provenance identity is invalid."
}

$dependencies = @($provenance.predicate.buildDefinition.resolvedDependencies)
if (-not ($dependencies | Where-Object { $_.digest.gitCommit -eq $CommitSha })) {
    throw "SLSA provenance is not tied to the exact commit SHA."
}
$subjectIndex = @{}
foreach ($subject in @($provenance.subject)) {
    if ([string]::IsNullOrWhiteSpace([string]$subject.name) -or
        $subject.digest.sha256 -notmatch '^[0-9a-f]{64}$' -or
        $subjectIndex.ContainsKey([string]$subject.name)) {
        throw "SLSA provenance contains an invalid or duplicate subject."
    }
    $subjectIndex[[string]$subject.name] = [string]$subject.digest.sha256
}
foreach ($entry in $checksumFiles) {
    if (-not $subjectIndex.ContainsKey([string]$entry.path) -or
        $subjectIndex[[string]$entry.path] -ne [string]$entry.sha256) {
        throw "SLSA provenance subjects do not cover the checksum manifest artifacts."
    }
}
$checksumRelative = Get-RelativePath -Root $root -Path $checksumPath
$checksumHash = (Get-FileHash -LiteralPath $checksumPath -Algorithm SHA256).Hash.ToLowerInvariant()
if (-not $subjectIndex.ContainsKey($checksumRelative) -or $subjectIndex[$checksumRelative] -ne $checksumHash) {
    throw "SLSA provenance does not cover its checksum manifest."
}
if ($subjectIndex.Count -ne ($checksumIndex.Count + 1)) {
    throw "SLSA provenance contains subjects outside the closed checksum set."
}

$byproducts = @($provenance.predicate.runDetails.byproducts)
foreach ($requiredByproduct in @(
        [pscustomobject]@{ path = $unsignedRelative; hash = $checksumIndex[$unsignedRelative] },
        [pscustomobject]@{ path = $sbomRelative; hash = $checksumIndex[$sbomRelative] })) {
    if (-not ($byproducts | Where-Object {
                $_.name -eq $requiredByproduct.path -and $_.digest.sha256 -eq $requiredByproduct.hash
            })) {
        throw "SLSA provenance is missing a required unsigned-manifest or SBOM binding."
    }
}

$signingModeCounts = @{
    Application = 0
    Installer = 0
}
$signingRecordIndex = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$applicationSignedFileIndex = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$installerSignedFileIndex = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($recordPathValue in $signingRecordValues) {
    $recordPath = Get-PathInsideRoot -Path $recordPathValue -Root $root -RootPrefix $rootPrefix
    $recordRelativePath = Get-RelativePath -Root $root -Path $recordPath
    if (-not $signingRecordIndex.Add($recordRelativePath)) {
        throw "A signing record path was supplied more than once."
    }
    if ($ExpectedStage -eq "signed" -and -not $checksumIndex.ContainsKey($recordRelativePath)) {
        throw "Final checksums do not cover a signing record."
    }
    $record = Read-RequiredJson -Path $recordPath
    if ($record.schema -ne "https://xniw.github.io/Win7POS/schemas/release-signing-record/v1" -or
        $record.mode -notin @("Application", "Installer") -or
        $record.productVersion -ne $ProductVersion -or
        $record.releaseTag -ne $ReleaseTag -or
        $record.commitSha -ne $CommitSha -or
        $record.digestAlgorithm -ne "SHA-256") {
        throw "A signing record does not match the requested release."
    }
    if ($RequireProductionSignatures -and [bool]$record.nonProductionFixture) {
        throw "NON-PRODUCTION fixture signatures cannot satisfy production signing."
    }
    $recordThumbprint = Get-ExactSha1Thumbprint `
        -Value ([string]$record.certificate.thumbprint) `
        -Label "Signing-record certificate thumbprint"
    if ($recordThumbprint -ne $normalizedExpectedThumbprint) {
        throw "A signing record uses a signer other than the explicitly expected signer."
    }
    $isNonProductionFixture = [bool]$record.nonProductionFixture
    if ($isNonProductionFixture) {
        if ($record.certificate.validationPolicy -ne "SELF-SIGNED-UNTRUSTED-NON-PRODUCTION-FIXTURE") {
            throw "A NON-PRODUCTION fixture record lacks its explicit untrusted validation policy."
        }
        if ($record.timestamp.protocol -ne "NONE-NON-PRODUCTION-FIXTURE" -or
            $record.timestamp.digestAlgorithm -ne "NONE" -or
            -not [string]::IsNullOrEmpty([string]$record.timestamp.url)) {
            throw "A NON-PRODUCTION fixture record has invalid no-timestamp metadata."
        }
    }
    elseif ($record.certificate.validationPolicy -ne "WINDOWS-TRUSTED-PRODUCTION" -or
        $record.timestamp.protocol -ne "RFC3161" -or
        $record.timestamp.digestAlgorithm -ne "SHA-256" -or
        [string]::IsNullOrWhiteSpace([string]$record.timestamp.url)) {
        throw "A production signing record lacks the required RFC3161/SHA-256 timestamp policy."
    }
    $recordFiles = @($record.files)
    if ($recordFiles.Count -eq 0) {
        throw "A signing record contains no signed files."
    }
    if ($ExpectedStage -eq "signed" -and $record.mode -eq "Installer" -and $recordFiles.Count -ne 1) {
        throw "Installer signing record must contain exactly one signed file."
    }

    $recordFileIndex = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($fileRecord in $recordFiles) {
        if ([string]::IsNullOrWhiteSpace([string]$fileRecord.path) -or
            -not $recordFileIndex.Add([string]$fileRecord.path)) {
            throw "A signing record contains an invalid or duplicate signed-file path."
        }
        $recordedRelativePath = [string]$fileRecord.path
        if ($ExpectedStage -eq "signed" -and $record.mode -eq "Application") {
            if (-not $applicationSignedFileIndex.Add($recordedRelativePath) -or
                -not $expectedApplicationPathIndex.Contains($recordedRelativePath) -or
                -not $recordedRelativePath.StartsWith("Win7POS/", [StringComparison]::OrdinalIgnoreCase)) {
                throw "Application signing record does not exactly cover a current project-owned Win7POS binary."
            }
            $unsignedManifestRelativePath = $recordedRelativePath.Substring("Win7POS/".Length)
            if (-not $unsignedPayloadHashIndex.ContainsKey($unsignedManifestRelativePath) -or
                [string]$fileRecord.unsignedSha256 -ne $unsignedPayloadHashIndex[$unsignedManifestRelativePath]) {
                throw "Application signing record pre-sign hash is not bound to the canonical unsigned payload manifest."
            }
        }
        elseif ($ExpectedStage -eq "signed" -and $record.mode -eq "Installer") {
            if (-not $installerSignedFileIndex.Add($recordedRelativePath) -or
                $recordedRelativePath -ne $expectedInstallerRelativePath) {
                throw "Installer signing record must target only the exact versioned root installer."
            }
        }
        $signedPath = Get-PathInsideRoot -Path (Join-Path $root ([string]$fileRecord.path)) -Root $root -RootPrefix $rootPrefix
        if (-not (Test-Path -LiteralPath $signedPath -PathType Leaf)) {
            throw "A signed release artifact is missing."
        }
        $actualSignedHash = (Get-FileHash -LiteralPath $signedPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualSignedHash -ne [string]$fileRecord.signedSha256 -or
            $actualSignedHash -eq [string]$fileRecord.unsignedSha256) {
            throw "A signed release artifact hash is invalid or was not changed by signing."
        }
        if ($ExpectedStage -eq "signed" -and
            (-not $checksumIndex.ContainsKey([string]$fileRecord.path) -or
                $checksumIndex[[string]$fileRecord.path] -ne $actualSignedHash)) {
            throw "Final checksums do not cover a signed release artifact."
        }

        $signature = Get-AuthenticodeSignature -LiteralPath $signedPath
        $actualThumbprint = if ($signature.SignerCertificate) {
            Get-ExactSha1Thumbprint `
                -Value ([string]$signature.SignerCertificate.Thumbprint) `
                -Label "Authenticode signer thumbprint"
        }
        else { "" }
        $recordedFileThumbprint = Get-ExactSha1Thumbprint `
            -Value ([string]$fileRecord.signerThumbprint) `
            -Label "Signed-file signer thumbprint"
        $actualTimestampThumbprint = if ($signature.TimeStamperCertificate) {
            Get-ExactSha1Thumbprint `
                -Value ([string]$signature.TimeStamperCertificate.Thumbprint) `
                -Label "Authenticode timestamp signer thumbprint"
        }
        else { "" }
        $recordedTimestampThumbprint = Get-ExactSha1Thumbprint `
            -Value ([string]$fileRecord.timestampSignerThumbprint) `
            -Label "Recorded timestamp signer thumbprint" `
            -AllowEmpty
        $timestampStateValid = if ($isNonProductionFixture) {
            [string]::IsNullOrEmpty($actualTimestampThumbprint) -and
                [string]::IsNullOrEmpty($recordedTimestampThumbprint)
        }
        else {
            -not [string]::IsNullOrWhiteSpace($actualTimestampThumbprint) -and
                $actualTimestampThumbprint -eq $recordedTimestampThumbprint
        }
        $expectedSignatureStatus = if ($isNonProductionFixture) {
            [System.Management.Automation.SignatureStatus]::UnknownError
        }
        else { [System.Management.Automation.SignatureStatus]::Valid }
        if ($signature.Status -ne $expectedSignatureStatus -or
            $actualThumbprint -ne $recordThumbprint -or
            $recordedFileThumbprint -ne $recordThumbprint -or -not $timestampStateValid) {
            throw "Authenticode signer, signature or timestamp validation failed."
        }
    }
    $signingModeCounts[[string]$record.mode]++
}

if ($ExpectedStage -eq "signed" -and
    ($signingRecordIndex.Count -ne 2 -or
        $signingModeCounts.Application -ne 1 -or $signingModeCounts.Installer -ne 1)) {
    throw "Signed validation requires exactly one Application and one Installer signing record."
}
if ($ExpectedStage -eq "signed") {
    if ($applicationSignedFileIndex.Count -ne $expectedApplicationPathIndex.Count -or
        @($expectedApplicationPathIndex | Where-Object {
                -not $applicationSignedFileIndex.Contains($_)
            }).Count -ne 0) {
        throw "Application signing record is not an exact complete match for the current project-binary set."
    }
    if ($installerSignedFileIndex.Count -ne 1 -or
        -not $installerSignedFileIndex.Contains($expectedInstallerRelativePath)) {
        throw "Installer signing record does not contain the exact versioned root installer."
    }
    if (@($applicationSignedFileIndex | Where-Object {
                $installerSignedFileIndex.Contains($_)
            }).Count -ne 0) {
        throw "Application and Installer signing record file sets must be disjoint."
    }
}

Write-Host "Protected release artifacts: PASS ($ExpectedStage, $($checksumFiles.Count) checksummed files, $($signingRecordIndex.Count) signing phases)."
