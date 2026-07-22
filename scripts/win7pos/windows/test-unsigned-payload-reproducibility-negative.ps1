[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$CommitSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
$BuildVersion = "1.0.0-dev.aaaaaaaaaaaa"
$ProductVersion = "1.0.0"
$AssemblyVersion = "1.0.0.0"
$FileVersion = "1.0.0.0"
$InformationalVersion = "$BuildVersion+sha.$CommitSha"
$Writer = Join-Path $PSScriptRoot "write-reproducibility-payload-manifest.ps1"
$Comparator = Join-Path $PSScriptRoot "compare-reproducibility-payload-manifests.ps1"
$EvidenceValidator = Join-Path $PSScriptRoot "test-reproducibility-evidence.ps1"
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("Win7POS-Repro-Negative-" + [Guid]::NewGuid().ToString("N"))

function Assert-Throws {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [Parameter(Mandatory = $true)][string]$ExpectedMessagePattern,
        [Parameter(Mandatory = $true)][string]$VectorName
    )

    $caught = $null
    try {
        & $Action
    }
    catch {
        $caught = $_
    }
    if ($null -eq $caught) {
        throw "Negative vector '$VectorName' did not fail closed."
    }
    if ($caught.Exception.Message -notmatch $ExpectedMessagePattern) {
        throw "Negative vector '$VectorName' failed for the wrong reason: $($caught.Exception.Message)"
    }
    Write-Host "PASS negative vector: $VectorName"
}

function Write-FixturePayload {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Timestamp,
        [Parameter(Mandatory = $true)][string]$FixtureBinary
    )

    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    Copy-Item -LiteralPath $FixtureBinary -Destination (Join-Path $Root "Win7POS.Wpf.exe")
    [System.IO.File]::WriteAllBytes((Join-Path $Root "Win7POS.Wpf.exe.config"), [byte[]](5, 6, 7))
    Copy-Item -LiteralPath $FixtureBinary -Destination (Join-Path $Root "Win7POS.Core.dll")
    Copy-Item -LiteralPath $FixtureBinary -Destination (Join-Path $Root "Win7POS.Data.dll")
    $versionLines = @(
        "Win7POS Windows x86 release pack",
        "ProductVersion=$ProductVersion",
        "BuildVersion=$BuildVersion",
        "AssemblyVersion=$AssemblyVersion",
        "FileVersion=$FileVersion",
        "InformationalVersion=$InformationalVersion",
        "CommitSHA=$CommitSha",
        "BuildTimestampUtc=$Timestamp",
        "Configuration=Release",
        "Platform=x86",
        "SdkVersion=10.0.301"
    )
    [System.IO.File]::WriteAllLines(
        (Join-Path $Root "VERSION.txt"),
        $versionLines,
        (New-Object System.Text.UTF8Encoding($false)))
}

function New-VersionedFixtureBinary {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$FixtureAssemblyVersion,
        [Parameter(Mandatory = $true)][string]$FixtureFileVersion,
        [Parameter(Mandatory = $true)][string]$FixtureInformationalVersion
    )

    $fixtureRoot = Join-Path $Root $Name
    New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
    $sourcePath = Join-Path $fixtureRoot "Fixture.cs"
    $binaryPath = Join-Path $fixtureRoot "$Name.dll"
    $sourceText = @"
using System.Reflection;
[assembly: AssemblyVersion("$FixtureAssemblyVersion")]
[assembly: AssemblyFileVersion("$FixtureFileVersion")]
[assembly: AssemblyInformationalVersion("$FixtureInformationalVersion")]
public sealed class ReproducibilityFixtureMarker { }
"@
    [System.IO.File]::WriteAllText($sourcePath, $sourceText, (New-Object System.Text.UTF8Encoding($false)))
    $cscPath = @(
        (Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"),
        (Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe")
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($cscPath)) {
        throw "The .NET Framework 4.x C# compiler is required for reproducibility metadata fixtures."
    }
    & $cscPath /nologo /target:library "/out:$binaryPath" $sourcePath
    if ($LASTEXITCODE -ne 0) {
        throw "Could not build versioned reproducibility fixture '$Name'."
    }
    if (-not (Test-Path -LiteralPath $binaryPath -PathType Leaf)) {
        throw "Versioned reproducibility fixture was not produced: $binaryPath"
    }
    return $binaryPath
}

function Write-RunEvidence {
    param([Parameter(Mandatory = $true)][string]$EvidenceRoot)

    $manifestNames = @(
        "build-a.normalized-manifest.json",
        "build-b.normalized-manifest.json",
        "unsigned-payload.normalized-manifest.json"
    )
    $runEvidence = [pscustomobject][ordered]@{
        format = "win7pos-unsigned-payload-reproducibility-run-v1"
        passed = $true
        commitSha = $CommitSha
        ref = "refs/heads/reproducibility-fixture"
        productVersion = $ProductVersion
        buildVersion = $BuildVersion
        sdkVersion = "10.0.301"
        configuration = "Release"
        platform = "x86"
        targetFramework = "net48"
        isolatedCleanBuildCount = 2
        builds = @(
            [pscustomobject][ordered]@{ label = "Build A"; sourceKind = "detached-git-worktree"; commitSha = $CommitSha; cleanBeforeRestore = $true; outputCopiedToIsolatedPayload = $true; payloadIsolationKey = "build-a"; configuration = "Release"; platform = "x86"; targetFramework = "net48" },
            [pscustomobject][ordered]@{ label = "Build B"; sourceKind = "detached-git-worktree"; commitSha = $CommitSha; cleanBeforeRestore = $true; outputCopiedToIsolatedPayload = $true; payloadIsolationKey = "build-b"; configuration = "Release"; platform = "x86"; targetFramework = "net48" }
        )
        cleanup = [pscustomobject][ordered]@{
            worktreesRemoved = $true
            gitWorktreePruned = $true
            temporaryRootPolicy = "system-temp/Win7POS-Repro-*"
            temporaryBuildRootRemoved = $true
            retainedByExplicitRequest = $false
        }
        removedFiles = [pscustomobject][ordered]@{ policy = "PDB debug files only"; buildA = 0; buildB = 0 }
        normalizedFields = @("VERSION.txt:BuildTimestampUtc")
        excludedDerivedOrOuterContainers = @(
            "APP-FILES.txt",
            "SHA256SUMS.txt",
            "Win7POS-$BuildVersion-x86.zip",
            "Win7POS-$BuildVersion-Setup.exe"
        )
        manifests = @(
            [pscustomobject][ordered]@{ fileName = $manifestNames[0]; sha256 = (Get-FileHash -LiteralPath (Join-Path $EvidenceRoot $manifestNames[0]) -Algorithm SHA256).Hash.ToLowerInvariant() },
            [pscustomobject][ordered]@{ fileName = $manifestNames[1]; sha256 = (Get-FileHash -LiteralPath (Join-Path $EvidenceRoot $manifestNames[1]) -Algorithm SHA256).Hash.ToLowerInvariant() },
            [pscustomobject][ordered]@{ fileName = $manifestNames[2]; sha256 = (Get-FileHash -LiteralPath (Join-Path $EvidenceRoot $manifestNames[2]) -Algorithm SHA256).Hash.ToLowerInvariant(); role = "canonical unsigned payload integrity input" }
        )
        comparison = [pscustomobject][ordered]@{
            fileName = "comparison.json"
            sha256 = (Get-FileHash -LiteralPath (Join-Path $EvidenceRoot "comparison.json") -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $EvidenceRoot "run.json"),
        ($runEvidence | ConvertTo-Json -Depth 10) + [Environment]::NewLine,
        (New-Object System.Text.UTF8Encoding($false)))
}

function Copy-EvidenceDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    New-Item -ItemType Directory -Path $Destination | Out-Null
    Copy-Item -Path (Join-Path $Source "*") -Destination $Destination -Recurse -Force
}

$tempFullPath = [System.IO.Path]::GetFullPath($temporaryRoot)
$systemTempPrefix = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
if (-not $tempFullPath.StartsWith($systemTempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not [System.IO.Path]::GetFileName($tempFullPath).StartsWith("Win7POS-Repro-Negative-", [StringComparison]::Ordinal)) {
    throw "Unsafe negative-test temporary path: $tempFullPath"
}

try {
    $fixtureBinary = New-VersionedFixtureBinary `
        -Root $tempFullPath `
        -Name "CorrectVersionFixture" `
        -FixtureAssemblyVersion $AssemblyVersion `
        -FixtureFileVersion $FileVersion `
        -FixtureInformationalVersion $InformationalVersion
    $wrongVersionBinary = New-VersionedFixtureBinary `
        -Root $tempFullPath `
        -Name "WrongVersionFixture" `
        -FixtureAssemblyVersion "2.0.0.0" `
        -FixtureFileVersion $FileVersion `
        -FixtureInformationalVersion $InformationalVersion
    $payloadA = Join-Path $tempFullPath "payload-a"
    $payloadB = Join-Path $tempFullPath "payload-b"
    $passEvidence = Join-Path $tempFullPath "pass-evidence"
    New-Item -ItemType Directory -Force -Path $passEvidence | Out-Null
    Write-FixturePayload -Root $payloadA -Timestamp "2026-07-21T10:00:00Z" -FixtureBinary $fixtureBinary
    Write-FixturePayload -Root $payloadB -Timestamp "2026-07-21T10:00:01Z" -FixtureBinary $fixtureBinary

    $manifestA = Join-Path $passEvidence "build-a.normalized-manifest.json"
    $manifestB = Join-Path $passEvidence "build-b.normalized-manifest.json"
    $comparison = Join-Path $passEvidence "comparison.json"
    $canonical = Join-Path $passEvidence "unsigned-payload.normalized-manifest.json"
    & $Writer -PayloadRoot $payloadA -ManifestPath $manifestA -ExpectedCommitSha $CommitSha -BuildVersion $BuildVersion
    & $Writer -PayloadRoot $payloadB -ManifestPath $manifestB -ExpectedCommitSha $CommitSha -BuildVersion $BuildVersion
    & $Comparator -ManifestA $manifestA -ManifestB $manifestB -ComparisonEvidencePath $comparison -ExpectedCommitSha $CommitSha
    Copy-Item -LiteralPath $manifestA -Destination $canonical
    Write-RunEvidence -EvidenceRoot $passEvidence
    & $EvidenceValidator -EvidenceDirectory $passEvidence -ExpectedCommitSha $CommitSha
    Write-Host "PASS positive control: timestamp-only normalization, binary versions, comparison and run evidence."

    $outerInstallerPayload = Join-Path $tempFullPath "outer-installer-payload"
    Copy-Item -LiteralPath $payloadA -Destination $outerInstallerPayload -Recurse
    $nestedInstallerDirectory = Join-Path $outerInstallerPayload "nested"
    New-Item -ItemType Directory -Path $nestedInstallerDirectory | Out-Null
    [System.IO.File]::WriteAllBytes((Join-Path $nestedInstallerDirectory "Win7POS-$BuildVersion-Setup.exe"), [byte[]](0x4D, 0x5A))
    Assert-Throws -VectorName "outer installer inside payload" -ExpectedMessagePattern "Outer ZIP/installer container" -Action {
        & $Writer -PayloadRoot $outerInstallerPayload -ManifestPath (Join-Path $tempFullPath "outer-installer.json") -ExpectedCommitSha $CommitSha -BuildVersion $BuildVersion
    }

    $outerZipPayload = Join-Path $tempFullPath "outer-zip-payload"
    Copy-Item -LiteralPath $payloadA -Destination $outerZipPayload -Recurse
    [System.IO.File]::WriteAllBytes((Join-Path $outerZipPayload "Win7POS-$BuildVersion-x86.zip"), [byte[]](0x50, 0x4B))
    Assert-Throws -VectorName "outer ZIP inside payload" -ExpectedMessagePattern "Outer ZIP/installer container" -Action {
        & $Writer -PayloadRoot $outerZipPayload -ManifestPath (Join-Path $tempFullPath "outer-zip.json") -ExpectedCommitSha $CommitSha -BuildVersion $BuildVersion
    }

    $invalidTimestampPayload = Join-Path $tempFullPath "invalid-timestamp-payload"
    Write-FixturePayload -Root $invalidTimestampPayload -Timestamp "2026-07-21T10:00:00+00:00" -FixtureBinary $fixtureBinary
    Assert-Throws -VectorName "non-canonical UTC timestamp" -ExpectedMessagePattern "canonical UTC" -Action {
        & $Writer -PayloadRoot $invalidTimestampPayload -ManifestPath (Join-Path $tempFullPath "invalid-timestamp.json") -ExpectedCommitSha $CommitSha -BuildVersion $BuildVersion
    }

    $invalidCalendarTimestampPayload = Join-Path $tempFullPath "invalid-calendar-timestamp-payload"
    Write-FixturePayload -Root $invalidCalendarTimestampPayload -Timestamp "2026-02-30T10:00:00Z" -FixtureBinary $fixtureBinary
    Assert-Throws -VectorName "invalid UTC calendar timestamp" -ExpectedMessagePattern "valid canonical UTC timestamp" -Action {
        & $Writer -PayloadRoot $invalidCalendarTimestampPayload -ManifestPath (Join-Path $tempFullPath "invalid-calendar-timestamp.json") -ExpectedCommitSha $CommitSha -BuildVersion $BuildVersion
    }

    $wrongBinaryVersionPayload = Join-Path $tempFullPath "wrong-binary-version-payload"
    Write-FixturePayload -Root $wrongBinaryVersionPayload -Timestamp "2026-07-21T10:00:00Z" -FixtureBinary $fixtureBinary
    Copy-Item -LiteralPath $wrongVersionBinary -Destination (Join-Path $wrongBinaryVersionPayload "Win7POS.Data.dll") -Force
    Assert-Throws -VectorName "binary version mismatch" -ExpectedMessagePattern "Binary AssemblyVersion mismatch|not a readable managed assembly" -Action {
        & $Writer -PayloadRoot $wrongBinaryVersionPayload -ManifestPath (Join-Path $tempFullPath "wrong-binary-version.json") -ExpectedCommitSha $CommitSha -BuildVersion $BuildVersion
    }

    $forgedComparisonEvidence = Join-Path $tempFullPath "forged-comparison"
    Copy-EvidenceDirectory -Source $passEvidence -Destination $forgedComparisonEvidence
    $forgedComparisonPath = Join-Path $forgedComparisonEvidence "comparison.json"
    $forgedComparison = [System.IO.File]::ReadAllText($forgedComparisonPath) | ConvertFrom-Json
    $forgedComparison.payload = "forged-pass"
    [System.IO.File]::WriteAllText($forgedComparisonPath, ($forgedComparison | ConvertTo-Json -Depth 10) + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))
    $forgedRunPath = Join-Path $forgedComparisonEvidence "run.json"
    $forgedRun = [System.IO.File]::ReadAllText($forgedRunPath) | ConvertFrom-Json
    $forgedRun.comparison.sha256 = (Get-FileHash -LiteralPath $forgedComparisonPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText($forgedRunPath, ($forgedRun | ConvertTo-Json -Depth 10) + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))
    Assert-Throws -VectorName "forged comparison evidence" -ExpectedMessagePattern "independently recalculated comparison" -Action {
        & $EvidenceValidator -EvidenceDirectory $forgedComparisonEvidence -ExpectedCommitSha $CommitSha
    }

    $missingRunEvidence = Join-Path $tempFullPath "missing-run"
    Copy-EvidenceDirectory -Source $passEvidence -Destination $missingRunEvidence
    [System.IO.File]::Delete((Join-Path $missingRunEvidence "run.json"))
    Assert-Throws -VectorName "missing run evidence" -ExpectedMessagePattern "evidence is missing" -Action {
        & $EvidenceValidator -EvidenceDirectory $missingRunEvidence -ExpectedCommitSha $CommitSha
    }

    $tamperedRunEvidence = Join-Path $tempFullPath "tampered-run"
    Copy-EvidenceDirectory -Source $passEvidence -Destination $tamperedRunEvidence
    $tamperedRunPath = Join-Path $tamperedRunEvidence "run.json"
    $tamperedRun = [System.IO.File]::ReadAllText($tamperedRunPath) | ConvertFrom-Json
    $tamperedRun.sdkVersion = "9.9.999"
    [System.IO.File]::WriteAllText($tamperedRunPath, ($tamperedRun | ConvertTo-Json -Depth 10) + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))
    Assert-Throws -VectorName "tampered run invariants" -ExpectedMessagePattern "run invariants" -Action {
        & $EvidenceValidator -EvidenceDirectory $tamperedRunEvidence -ExpectedCommitSha $CommitSha
    }

    $tamperedRunHashEvidence = Join-Path $tempFullPath "tampered-run-hash"
    Copy-EvidenceDirectory -Source $passEvidence -Destination $tamperedRunHashEvidence
    $tamperedRunHashPath = Join-Path $tamperedRunHashEvidence "run.json"
    $tamperedRunHash = [System.IO.File]::ReadAllText($tamperedRunHashPath) | ConvertFrom-Json
    $tamperedRunHash.comparison.sha256 = ("0" * 64)
    [System.IO.File]::WriteAllText($tamperedRunHashPath, ($tamperedRunHash | ConvertTo-Json -Depth 10) + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))
    Assert-Throws -VectorName "tampered run hash binding" -ExpectedMessagePattern "comparison/canonical evidence binding" -Action {
        & $EvidenceValidator -EvidenceDirectory $tamperedRunHashEvidence -ExpectedCommitSha $CommitSha
    }

    $alteredBytes = [System.IO.File]::ReadAllBytes((Join-Path $payloadB "Win7POS.Data.dll"))
    $alteredBytes += [byte]0xFF
    [System.IO.File]::WriteAllBytes((Join-Path $payloadB "Win7POS.Data.dll"), $alteredBytes)
    $alteredManifestB = Join-Path $tempFullPath "altered-b.normalized-manifest.json"
    $alteredComparison = Join-Path $tempFullPath "altered-comparison.json"
    & $Writer -PayloadRoot $payloadB -ManifestPath $alteredManifestB -ExpectedCommitSha $CommitSha -BuildVersion $BuildVersion
    Assert-Throws -VectorName "altered payload" -ExpectedMessagePattern "reproducibility mismatch" -Action {
        & $Comparator -ManifestA $manifestA -ManifestB $alteredManifestB -ComparisonEvidencePath $alteredComparison -ExpectedCommitSha $CommitSha
    }
    $alteredEvidence = [System.IO.File]::ReadAllText($alteredComparison) | ConvertFrom-Json
    if ($alteredEvidence.passed -ne $false -or
        -not (@($alteredEvidence.differences) | Where-Object { $_.type -eq "HashMismatch" -and $_.path -eq "Win7POS.Data.dll" })) {
        throw "Altered payload negative vector did not retain actionable machine-readable mismatch evidence."
    }

    Assert-Throws -VectorName "missing normalized manifest" -ExpectedMessagePattern "manifest is missing" -Action {
        & $Comparator `
            -ManifestA (Join-Path $tempFullPath "does-not-exist.json") `
            -ManifestB $manifestB `
            -ComparisonEvidencePath (Join-Path $tempFullPath "missing-manifest-comparison.json") `
            -ExpectedCommitSha $CommitSha
    }

    $missingCanonical = Join-Path $tempFullPath "missing-canonical"
    Copy-EvidenceDirectory -Source $passEvidence -Destination $missingCanonical
    [System.IO.File]::Delete((Join-Path $missingCanonical "unsigned-payload.normalized-manifest.json"))
    Assert-Throws -VectorName "missing canonical integrity manifest" -ExpectedMessagePattern "evidence is missing" -Action {
        & $EvidenceValidator -EvidenceDirectory $missingCanonical -ExpectedCommitSha $CommitSha
    }

    $missingComparison = Join-Path $tempFullPath "missing-comparison"
    Copy-EvidenceDirectory -Source $passEvidence -Destination $missingComparison
    [System.IO.File]::Delete((Join-Path $missingComparison "comparison.json"))
    Assert-Throws -VectorName "missing comparison evidence" -ExpectedMessagePattern "evidence is missing" -Action {
        & $EvidenceValidator -EvidenceDirectory $missingComparison -ExpectedCommitSha $CommitSha
    }

    Write-Host "Unsigned payload reproducibility negative vectors PASS (13/13)."
}
finally {
    if (Test-Path -LiteralPath $tempFullPath -PathType Container) {
        Remove-Item -LiteralPath $tempFullPath -Recurse -Force
    }
}
