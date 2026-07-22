[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SignToolPath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if (-not $IsWindows) {
    throw "Signing fixture tests require Windows."
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path
$writer = Join-Path $PSScriptRoot "write-release-integrity-metadata.ps1"
$validator = Join-Path $PSScriptRoot "test-protected-release-artifacts.ps1"
$signer = Join-Path $PSScriptRoot "invoke-protected-release-signing.ps1"
$toolchain = Join-Path $PSScriptRoot "release-signing-toolchain.json"
$commitSha = "0123456789abcdef0123456789abcdef01234567"
$version = "1.0.0"
$releaseTag = "v$version"
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ("win7pos-signing-negative-{0}" -f [Guid]::NewGuid().ToString("N"))
$fixtureCertificate = $null

function Write-FixtureText {
    param([string]$Path, [string]$Content)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Invoke-ExpectedFailure {
    param(
        [string]$Name,
        [scriptblock]$Action,
        [string]$ExpectedMessagePattern = ""
    )
    $failed = $false
    $failureMessage = ""
    try {
        & $Action
    }
    catch {
        $failed = $true
        $failureMessage = $_.Exception.Message
    }
    if (-not $failed) {
        throw "Negative vector unexpectedly passed: $Name"
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedMessagePattern) -and
        $failureMessage -notmatch $ExpectedMessagePattern) {
        throw "Negative vector '$Name' failed for the wrong reason: $failureMessage"
    }
    Write-Host "PASS negative vector: $Name"
}

try {
    [IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    $publishRoot = Join-Path $fixtureRoot "publish"
    $scratchRoot = Join-Path $fixtureRoot "scratch"
    $payloadRoot = Join-Path $publishRoot "Win7POS"
    $unsignedRoot = Join-Path $publishRoot "unsigned"
    [IO.Directory]::CreateDirectory($payloadRoot) | Out-Null
    [IO.Directory]::CreateDirectory($unsignedRoot) | Out-Null
    [IO.Directory]::CreateDirectory($scratchRoot) | Out-Null

    $fixtureSource = @"
using System.Reflection;
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]
namespace Win7POSSigningFixture { public sealed class Marker { } }
"@
    $corePath = Join-Path $payloadRoot "Win7POS.Core.dll"
    Add-Type -TypeDefinition $fixtureSource -OutputAssembly $corePath -OutputType Library
    [IO.File]::Copy($corePath, (Join-Path $payloadRoot "Win7POS.Data.dll"), $true)
    [IO.File]::Copy($corePath, (Join-Path $payloadRoot "Win7POS.Wpf.exe"), $true)
    Write-FixtureText -Path (Join-Path $payloadRoot "fixture-data.bin") -Content "unsigned fixture payload"
    $fixtureInstaller = Join-Path $publishRoot "Win7POS-$version-Setup.exe"
    [IO.File]::Copy($corePath, $fixtureInstaller, $true)

    $unsignedManifest = Join-Path $unsignedRoot "unsigned-payload-manifest.json"
    $sbomPath = Join-Path $unsignedRoot "sbom.cdx.json"
    $payloadPrefix = $payloadRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $fixtureManifestFiles = @(Get-ChildItem -LiteralPath $payloadRoot -Recurse -File | ForEach-Object {
            $relativePath = $_.FullName.Substring($payloadPrefix.Length).Replace('\', '/')
            [pscustomobject][ordered]@{
                path = $relativePath
                length = [long]$_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                normalization = "none"
            }
        } | Sort-Object -Property path)
    $fixtureUnsignedManifest = [pscustomobject][ordered]@{
        format = "win7pos-unsigned-payload-manifest-v1"
        hashAlgorithm = "SHA-256"
        commitSha = $commitSha
        buildVersion = $version
        platform = "x86"
        targetFramework = "net48"
        fileCount = $fixtureManifestFiles.Count
        files = $fixtureManifestFiles
    }
    Write-FixtureText -Path $unsignedManifest -Content (($fixtureUnsignedManifest | ConvertTo-Json -Depth 8) + "`n")
    Write-FixtureText -Path $sbomPath -Content '{"bomFormat":"CycloneDX","specVersion":"1.6","version":1,"components":[]}'

    & $writer `
        -ArtifactRoot $publishRoot `
        -ArtifactPath @($publishRoot) `
        -UnsignedPayloadManifestPath $unsignedManifest `
        -SbomPath $sbomPath `
        -OutputDirectory $unsignedRoot `
        -StageName unsigned `
        -FilePrefix "unsigned-release" `
        -CommitSha $commitSha `
        -ProductVersion $version `
        -ReleaseTag $releaseTag `
        -RepositoryUri "https://github.com/XNIW/Win7POS" `
        -RepositoryRef "refs/tags/$releaseTag" `
        -WorkflowName "NON-PRODUCTION signing fixture" `
        -WorkflowRunId "123" `
        -WorkflowRunAttempt "1" `
        -WorkflowRunUrl "https://github.com/XNIW/Win7POS/actions/runs/123" `
        -BuilderId "https://github.com/XNIW/Win7POS/.github/workflows/protected-release.yml"

    $checksums = Join-Path $unsignedRoot "unsigned-release-checksums.json"
    $provenance = Join-Path $unsignedRoot "unsigned-release-provenance.json"
    $attestation = Join-Path $unsignedRoot "unsigned-release-attestation.intoto.jsonl"
    $validationArguments = @{
        ArtifactRoot = $publishRoot
        ChecksumManifestPath = $checksums
        UnsignedPayloadManifestPath = $unsignedManifest
        SbomPath = $sbomPath
        ProvenancePath = $provenance
        AttestationPath = $attestation
        ExpectedStage = "unsigned"
        CommitSha = $commitSha
        ProductVersion = $version
        ReleaseTag = $releaseTag
    }
    & $validator @validationArguments

    $undeclaredArtifact = Join-Path $publishRoot "undeclared-release-file.bin"
    try {
        Write-FixtureText -Path $undeclaredArtifact -Content "not represented by the checksum manifest"
        Invoke-ExpectedFailure "undeclared published artifact" {
            & $validator @validationArguments
        } 'absent from the checksum manifest'
    }
    finally {
        Remove-Item -LiteralPath $undeclaredArtifact -Force -ErrorAction SilentlyContinue
    }

    Invoke-ExpectedFailure "missing SBOM" {
        $arguments = $validationArguments.Clone()
        $arguments.SbomPath = Join-Path $unsignedRoot "missing-sbom.json"
        & $validator @arguments
    }
    Invoke-ExpectedFailure "missing provenance" {
        $arguments = $validationArguments.Clone()
        $arguments.ProvenancePath = Join-Path $unsignedRoot "missing-provenance.json"
        & $validator @arguments
    }
    Invoke-ExpectedFailure "missing attestation" {
        $arguments = $validationArguments.Clone()
        $arguments.AttestationPath = Join-Path $unsignedRoot "missing-attestation.intoto.jsonl"
        & $validator @arguments
    }
    Invoke-ExpectedFailure "version mismatch" {
        $arguments = $validationArguments.Clone()
        $arguments.ProductVersion = "1.0.1"
        $arguments.ReleaseTag = "v1.0.1"
        & $validator @arguments
    }

    $payloadData = Join-Path $payloadRoot "fixture-data.bin"
    $originalPayload = [IO.File]::ReadAllBytes($payloadData)
    try {
        [IO.File]::WriteAllText($payloadData, "altered fixture payload", [Text.UTF8Encoding]::new($false))
        Invoke-ExpectedFailure "altered payload" {
            & $validator @validationArguments
        }
    }
    finally {
        [IO.File]::WriteAllBytes($payloadData, $originalPayload)
    }

    $developmentRoot = Join-Path $publishRoot "development"
    $developmentVersion = "$version-dev.$($commitSha.Substring(0, 12))"
    & $writer `
        -ArtifactRoot $publishRoot `
        -ArtifactPath @($publishRoot) `
        -UnsignedPayloadManifestPath $unsignedManifest `
        -SbomPath $sbomPath `
        -OutputDirectory $developmentRoot `
        -StageName development-unsigned `
        -FilePrefix "build" `
        -CommitSha $commitSha `
        -ProductVersion $version `
        -BuildVersion $developmentVersion `
        -ReleaseTag "" `
        -RepositoryUri "https://github.com/XNIW/Win7POS" `
        -RepositoryRef "refs/heads/fixture" `
        -WorkflowName "NON-PRODUCTION development fixture" `
        -WorkflowRunId "124" `
        -WorkflowRunAttempt "1" `
        -WorkflowRunUrl "https://github.com/XNIW/Win7POS/actions/runs/124" `
        -BuilderId "https://github.com/XNIW/Win7POS/.github/workflows/security-supply-chain.yml"

    $developmentValidation = @{
        ArtifactRoot = $publishRoot
        ChecksumManifestPath = (Join-Path $developmentRoot "build-checksums.json")
        UnsignedPayloadManifestPath = $unsignedManifest
        SbomPath = $sbomPath
        ProvenancePath = (Join-Path $developmentRoot "build-provenance.json")
        AttestationPath = (Join-Path $developmentRoot "build-attestation.intoto.jsonl")
        ExpectedStage = "development-unsigned"
        CommitSha = $commitSha
        ProductVersion = $version
        BuildVersion = $developmentVersion
        ReleaseTag = ""
    }
    & $validator @developmentValidation
    Invoke-ExpectedFailure "development build version mismatch" {
        $arguments = $developmentValidation.Clone()
        $arguments.BuildVersion = "$version-dev.ffffffffffff"
        & $validator @arguments
    }
    Invoke-ExpectedFailure "development evidence with release tag" {
        $arguments = $developmentValidation.Clone()
        $arguments.ReleaseTag = $releaseTag
        & $validator @arguments
    }
    Remove-Item -LiteralPath $developmentRoot -Recurse -Force

    Invoke-ExpectedFailure "missing signing certificate" {
        & $signer `
            -Mode Application `
            -ArtifactRoot $publishRoot `
            -ApplicationPayloadRoot $payloadRoot `
            -SignToolPath $SignToolPath `
            -CertificateThumbprint "0000000000000000000000000000000000000000" `
            -TimestampUrl "https://127.0.0.1:1/rfc3161" `
            -ProductVersion $version `
            -ReleaseTag $releaseTag `
            -CommitSha $commitSha `
            -ChecksumManifestPath $checksums `
            -UnsignedPayloadManifestPath $unsignedManifest `
            -SbomPath $sbomPath `
            -ProvenancePath $provenance `
            -AttestationPath $attestation `
            -OutputSigningRecordPath (Join-Path $publishRoot "signing\missing-cert.json") `
            -EnvironmentName "NON-PRODUCTION-SELF-SIGNED-FIXTURE" `
            -NonProductionFixture
    } 'not installed in CurrentUser/My'

    $fixtureCertificate = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject "CN=Win7POS NON-PRODUCTION Signing Fixture" `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -NotAfter ([DateTime]::UtcNow.AddHours(2))

    Invoke-ExpectedFailure "invalid RFC3161 timestamp endpoint" {
        & $signer `
            -Mode Application `
            -ArtifactRoot $publishRoot `
            -ApplicationPayloadRoot $payloadRoot `
            -SignToolPath $SignToolPath `
            -CertificateThumbprint $fixtureCertificate.Thumbprint `
            -TimestampUrl "https://127.0.0.1:1/rfc3161" `
            -ProductVersion $version `
            -ReleaseTag $releaseTag `
            -CommitSha $commitSha `
            -ChecksumManifestPath $checksums `
            -UnsignedPayloadManifestPath $unsignedManifest `
            -SbomPath $sbomPath `
            -ProvenancePath $provenance `
            -AttestationPath $attestation `
            -OutputSigningRecordPath (Join-Path $publishRoot "signing\invalid-timestamp.json") `
            -EnvironmentName "win7pos-protected-release"
    } 'Production signing requires a non-loopback HTTPS timestamp endpoint'

    # This deliberately corrupted Authenticode specimen is test scratch data,
    # never a member of the closed-world release tree.
    $signatureFixture = Join-Path $scratchRoot "Win7POS.SignatureFixture.dll"
    [IO.File]::Copy($corePath, $signatureFixture, $true)
    & $SignToolPath sign /fd SHA256 /sha1 $fixtureCertificate.Thumbprint $signatureFixture 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not create the NON-PRODUCTION self-signed bad-signature fixture."
    }
    $stream = [IO.File]::Open($signatureFixture, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $position = [Math]::Min([long]1024, $stream.Length - 1)
        $stream.Position = $position
        $value = $stream.ReadByte()
        $stream.Position = $position
        $stream.WriteByte([byte]($value -bxor 0x01))
    }
    finally {
        $stream.Dispose()
    }
    if ((Get-AuthenticodeSignature -LiteralPath $signatureFixture).Status -ne
        [System.Management.Automation.SignatureStatus]::HashMismatch) {
        throw "Tampered Authenticode fixture did not produce HashMismatch."
    }
    Write-Host "PASS negative vector: bad Authenticode signature"

    $signingRoot = Join-Path $publishRoot "signing"
    $applicationRecord = Join-Path $signingRoot "application-signing-record.json"
    $installerRecord = Join-Path $signingRoot "installer-signing-record.json"
    $fixtureTimestampMarker = "https://non-production.invalid/no-timestamp"
    & $signer `
        -Mode Application `
        -ArtifactRoot $publishRoot `
        -ApplicationPayloadRoot $payloadRoot `
        -SignToolPath $SignToolPath `
        -CertificateThumbprint $fixtureCertificate.Thumbprint `
        -TimestampUrl $fixtureTimestampMarker `
        -ProductVersion $version `
        -ReleaseTag $releaseTag `
        -CommitSha $commitSha `
        -ChecksumManifestPath $checksums `
        -UnsignedPayloadManifestPath $unsignedManifest `
        -SbomPath $sbomPath `
        -ProvenancePath $provenance `
        -AttestationPath $attestation `
        -OutputSigningRecordPath $applicationRecord `
        -EnvironmentName "NON-PRODUCTION-SELF-SIGNED-FIXTURE" `
        -NonProductionFixture `
        -SkipFileVersionCheck
    if ($LASTEXITCODE -ne 0) { throw "NON-PRODUCTION application-signing wiring failed." }

    & $signer `
        -Mode Installer `
        -ArtifactRoot $publishRoot `
        -InstallerPath $fixtureInstaller `
        -ApplicationSigningRecordPath $applicationRecord `
        -SignToolPath $SignToolPath `
        -CertificateThumbprint $fixtureCertificate.Thumbprint `
        -TimestampUrl $fixtureTimestampMarker `
        -ProductVersion $version `
        -ReleaseTag $releaseTag `
        -CommitSha $commitSha `
        -ChecksumManifestPath $checksums `
        -UnsignedPayloadManifestPath $unsignedManifest `
        -SbomPath $sbomPath `
        -ProvenancePath $provenance `
        -AttestationPath $attestation `
        -OutputSigningRecordPath $installerRecord `
        -EnvironmentName "NON-PRODUCTION-SELF-SIGNED-FIXTURE" `
        -NonProductionFixture `
        -SkipFileVersionCheck
    if ($LASTEXITCODE -ne 0) { throw "NON-PRODUCTION installer-signing wiring failed." }

    $writeSignedFixtureMetadata = {
        & $writer `
            -ArtifactRoot $publishRoot `
            -ArtifactPath @($publishRoot) `
            -UnsignedPayloadManifestPath $unsignedManifest `
            -SbomPath $sbomPath `
            -OutputDirectory $publishRoot `
            -StageName signed `
            -FilePrefix "release" `
            -CommitSha $commitSha `
            -ProductVersion $version `
            -ReleaseTag $releaseTag `
            -RepositoryUri "https://github.com/XNIW/Win7POS" `
            -RepositoryRef "refs/tags/$releaseTag" `
            -WorkflowName "NON-PRODUCTION signing fixture" `
            -WorkflowRunId "123" `
            -WorkflowRunAttempt "1" `
            -WorkflowRunUrl "https://github.com/XNIW/Win7POS/actions/runs/123" `
            -BuilderId "https://github.com/XNIW/Win7POS/.github/workflows/protected-release.yml" | Out-Null
    }
    & $writeSignedFixtureMetadata
    $signedValidationArguments = $validationArguments.Clone()
    $signedValidationArguments.ArtifactRoot = $publishRoot
    $signedValidationArguments.ChecksumManifestPath = Join-Path $publishRoot "release-checksums.json"
    $signedValidationArguments.ProvenancePath = Join-Path $publishRoot "release-provenance.json"
    $signedValidationArguments.AttestationPath = Join-Path $publishRoot "release-attestation.intoto.jsonl"
    $signedValidationArguments.ExpectedStage = "signed"
    $signedValidationArguments.SigningRecordPath = @($applicationRecord, $installerRecord)
    $signedValidationArguments.ExpectedSignerThumbprint = $fixtureCertificate.Thumbprint
    & $validator @signedValidationArguments
    Write-Host "PASS NON-PRODUCTION two-phase signing wiring and final integrity vector"

    Invoke-ExpectedFailure "wrong expected signer thumbprint" {
        $arguments = $signedValidationArguments.Clone()
        $arguments.ExpectedSignerThumbprint = "0000000000000000000000000000000000000000"
        & $validator @arguments
    } 'other than the explicitly expected signer'
    Invoke-ExpectedFailure "non-SHA1 expected signer thumbprint length" {
        $arguments = $signedValidationArguments.Clone()
        $arguments.ExpectedSignerThumbprint = "0" * 64
        & $validator @arguments
    } 'exactly 40 hexadecimal characters'
    Invoke-ExpectedFailure "missing Installer signing record" {
        $arguments = $signedValidationArguments.Clone()
        $arguments.SigningRecordPath = @($applicationRecord)
        & $validator @arguments
    } 'exactly one Application and one Installer'

    $originalApplicationRecordBytes = [IO.File]::ReadAllBytes($applicationRecord)
    try {
        $tamperedApplicationRecord = [IO.File]::ReadAllText($applicationRecord) | ConvertFrom-Json
        @($tamperedApplicationRecord.files)[0].unsignedSha256 = "0" * 64
        Write-FixtureText `
            -Path $applicationRecord `
            -Content (($tamperedApplicationRecord | ConvertTo-Json -Depth 20) + "`n")
        & $writeSignedFixtureMetadata
        Invoke-ExpectedFailure "tampered application pre-sign hash" {
            & $validator @signedValidationArguments
        } 'pre-sign hash is not bound to the canonical unsigned payload manifest'
    }
    finally {
        [IO.File]::WriteAllBytes($applicationRecord, $originalApplicationRecordBytes)
        & $writeSignedFixtureMetadata
    }

    $originalInstallerRecordBytes = [IO.File]::ReadAllBytes($installerRecord)
    try {
        $wrongTargetInstallerRecord = [IO.File]::ReadAllText($installerRecord) | ConvertFrom-Json
        @($wrongTargetInstallerRecord.files)[0].path = "Win7POS/Win7POS.Core.dll"
        Write-FixtureText `
            -Path $installerRecord `
            -Content (($wrongTargetInstallerRecord | ConvertTo-Json -Depth 20) + "`n")
        & $writeSignedFixtureMetadata
        Invoke-ExpectedFailure "Installer record targeting an application binary" {
            & $validator @signedValidationArguments
        } 'target only the exact versioned root installer'
    }
    finally {
        [IO.File]::WriteAllBytes($installerRecord, $originalInstallerRecordBytes)
        & $writeSignedFixtureMetadata
    }

    $lateApplicationBinary = Join-Path $payloadRoot "Win7POS.Late.dll"
    try {
        [IO.File]::Copy((Join-Path $payloadRoot "Win7POS.Core.dll"), $lateApplicationBinary, $true)
        & $writeSignedFixtureMetadata
        Invoke-ExpectedFailure "late checksummed Win7POS application binary" {
            & $validator @signedValidationArguments
        } 'canonical unsigned payload project-binary set is not an exact match'
    }
    finally {
        Remove-Item -LiteralPath $lateApplicationBinary -Force -ErrorAction SilentlyContinue
        & $writeSignedFixtureMetadata
    }

    $deletedApplicationBinary = Join-Path $payloadRoot "Win7POS.Data.dll"
    $deletedApplicationBinaryBytes = [IO.File]::ReadAllBytes($deletedApplicationBinary)
    $applicationRecordBeforeDeletionBytes = [IO.File]::ReadAllBytes($applicationRecord)
    try {
        Remove-Item -LiteralPath $deletedApplicationBinary -Force
        $applicationRecordWithoutDeletedBinary =
            [IO.File]::ReadAllText($applicationRecord) | ConvertFrom-Json
        $applicationRecordWithoutDeletedBinary.files = @(
            $applicationRecordWithoutDeletedBinary.files | Where-Object {
                [string]$_.path -ne "Win7POS/Win7POS.Data.dll"
            })
        Write-FixtureText `
            -Path $applicationRecord `
            -Content (($applicationRecordWithoutDeletedBinary | ConvertTo-Json -Depth 20) + "`n")
        & $writeSignedFixtureMetadata
        Invoke-ExpectedFailure "canonical application binary deleted after signing and removed from record" {
            & $validator @signedValidationArguments
        } 'canonical unsigned payload project-binary set is not an exact match'
    }
    finally {
        [IO.File]::WriteAllBytes($deletedApplicationBinary, $deletedApplicationBinaryBytes)
        [IO.File]::WriteAllBytes($applicationRecord, $applicationRecordBeforeDeletionBytes)
        & $writeSignedFixtureMetadata
    }

    $unexpectedRootExecutable = Join-Path $publishRoot "Win7POS-Unexpected.exe"
    try {
        [IO.File]::Copy($fixtureInstaller, $unexpectedRootExecutable, $true)
        & $writeSignedFixtureMetadata
        Invoke-ExpectedFailure "additional checksummed root executable" {
            & $validator @signedValidationArguments
        } 'exactly one root executable'
    }
    finally {
        Remove-Item -LiteralPath $unexpectedRootExecutable -Force -ErrorAction SilentlyContinue
        & $writeSignedFixtureMetadata
    }

    $signedUndeclaredArtifact = Join-Path $publishRoot "undeclared-after-signing.bin"
    try {
        Write-FixtureText -Path $signedUndeclaredArtifact -Content "not represented by final checksums"
        Invoke-ExpectedFailure "undeclared final signed artifact" {
            & $validator @signedValidationArguments
        } 'absent from the checksum manifest'
    }
    finally {
        Remove-Item -LiteralPath $signedUndeclaredArtifact -Force -ErrorAction SilentlyContinue
    }
    & $validator @signedValidationArguments
    Write-Host "PASS positive control: signed fixture remains valid after fail-closed mutation vectors"

    $signerText = Get-Content -LiteralPath $signer -Raw
    if ($signerText -match '(?i)PfxPassword|CertificatePassword|/p\b') {
        throw "Signing interface must not accept or pass certificate passwords."
    }
    Write-Host "PASS secret-safety vector: signer has no certificate-password interface"
}
finally {
    $cleanupFailure = $null
    try {
        if ($null -ne $fixtureCertificate) {
            $fixtureThumbprint = (($fixtureCertificate.Thumbprint -as [string]) -replace '\s', '').ToUpperInvariant()
            if ($fixtureThumbprint -notmatch '^[0-9A-F]{40}$') {
                throw "NON-PRODUCTION fixture certificate has an invalid cleanup thumbprint."
            }
            $fixtureCertificatePath = "Cert:\CurrentUser\My\$fixtureThumbprint"
            if (Test-Path -LiteralPath $fixtureCertificatePath -PathType Leaf) {
                Remove-Item -Path $fixtureCertificatePath -DeleteKey -Force -ErrorAction Stop
            }
            if (Test-Path -LiteralPath $fixtureCertificatePath -PathType Leaf) {
                throw "NON-PRODUCTION fixture certificate remains after private-key cleanup."
            }
        }
    }
    catch {
        $cleanupFailure = $_
    }

    try {
        $resolvedFixtureRoot = [IO.Path]::GetFullPath($fixtureRoot)
        $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedFixtureRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($resolvedFixtureRoot) -notmatch '^win7pos-signing-negative-[0-9a-f]{32}$') {
            throw "Refusing unsafe signing fixture cleanup."
        }
        if (Test-Path -LiteralPath $resolvedFixtureRoot -PathType Container) {
            [IO.Directory]::Delete($resolvedFixtureRoot, $true)
        }
    }
    catch {
        if ($null -eq $cleanupFailure) {
            $cleanupFailure = $_
        }
    }
    if ($null -ne $cleanupFailure) {
        throw "NON-PRODUCTION signing fixture cleanup failed closed: $($cleanupFailure.Exception.Message)"
    }
}

Write-Host "Release signing negative fixture tests: PASS."
