[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestA,

    [Parameter(Mandatory = $true)]
    [string]$ManifestB,

    [Parameter(Mandatory = $true)]
    [string]$ComparisonEvidencePath,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedCommitSha
)

$ErrorActionPreference = "Stop"
$ExpectedFormat = "win7pos-unsigned-payload-manifest-v1"
$ComparisonFormat = "win7pos-unsigned-payload-comparison-v1"
$RequiredSdkVersion = "10.0.301"

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Read-AndValidateManifest {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string]$CommitSha
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label normalized manifest is missing: $Path"
    }
    try {
        $manifest = [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $Path).Path) | ConvertFrom-Json
    }
    catch {
        throw "$Label normalized manifest is not valid JSON: $($_.Exception.Message)"
    }

    if (-not [string]::Equals($manifest.format, $ExpectedFormat, [StringComparison]::Ordinal)) {
        throw "$Label normalized manifest has unsupported format '$($manifest.format)'."
    }
    if (-not [string]::Equals($manifest.hashAlgorithm, "SHA-256", [StringComparison]::Ordinal)) {
        throw "$Label normalized manifest must use SHA-256."
    }
    if (-not [string]::Equals($manifest.commitSha, $CommitSha, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label normalized manifest commit '$($manifest.commitSha)' does not match '$CommitSha'."
    }
    if (-not [string]::Equals($manifest.platform, "x86", [StringComparison]::Ordinal) -or
        -not [string]::Equals($manifest.targetFramework, "net48", [StringComparison]::Ordinal)) {
        throw "$Label normalized manifest must describe the net48/x86 payload."
    }
    if ($manifest.buildVersion -notmatch '^(?<product>(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*))(?:-dev\.[0-9a-f]{12})?$') {
        throw "$Label normalized manifest has invalid buildVersion '$($manifest.buildVersion)'."
    }
    $expectedProductVersion = $Matches["product"]
    $expectedFourPartVersion = "$expectedProductVersion.0"
    $expectedInformationalVersion = "$($manifest.buildVersion)+sha.$CommitSha"
    $versionMetadata = $manifest.versionMetadata
    if ($null -eq $versionMetadata -or
        -not [string]::Equals($versionMetadata.productVersion, $expectedProductVersion, [StringComparison]::Ordinal) -or
        -not [string]::Equals($versionMetadata.buildVersion, $manifest.buildVersion, [StringComparison]::Ordinal) -or
        -not [string]::Equals($versionMetadata.assemblyVersion, $expectedFourPartVersion, [StringComparison]::Ordinal) -or
        -not [string]::Equals($versionMetadata.fileVersion, $expectedFourPartVersion, [StringComparison]::Ordinal) -or
        -not [string]::Equals($versionMetadata.informationalVersion, $expectedInformationalVersion, [StringComparison]::Ordinal) -or
        -not [string]::Equals($versionMetadata.commitSha, $CommitSha, [StringComparison]::Ordinal) -or
        -not [string]::Equals($versionMetadata.configuration, "Release", [StringComparison]::Ordinal) -or
        -not [string]::Equals($versionMetadata.platform, "x86", [StringComparison]::Ordinal) -or
        -not [string]::Equals($versionMetadata.sdkVersion, $RequiredSdkVersion, [StringComparison]::Ordinal)) {
        throw "$Label normalized manifest has inconsistent VERSION.txt metadata."
    }
    if ($null -eq $manifest.files -or [int]$manifest.fileCount -ne @($manifest.files).Count -or [int]$manifest.fileCount -le 0) {
        throw "$Label normalized manifest has an invalid fileCount/files collection."
    }

    $normalization = @($manifest.normalization)
    if ($normalization.Count -ne 1 -or
        -not [string]::Equals($normalization[0].path, "VERSION.txt", [StringComparison]::Ordinal) -or
        -not [string]::Equals($normalization[0].field, "BuildTimestampUtc", [StringComparison]::Ordinal) -or
        -not [string]::Equals($normalization[0].replacement, "<normalized-build-timestamp-utc>", [StringComparison]::Ordinal)) {
        throw "$Label normalized manifest has an unsupported normalization policy."
    }

    $expectedExclusions = @(
        "APP-FILES.txt",
        "SHA256SUMS.txt",
        "Win7POS-$($manifest.buildVersion)-x86.zip",
        "Win7POS-$($manifest.buildVersion)-Setup.exe"
    )
    $expectedExclusionReasons = @(
        "derived payload inventory",
        "derived checksum inventory",
        "outer ZIP container",
        "outer Inno Setup container"
    )
    $actualExclusions = @($manifest.exclusions)
    if ($actualExclusions.Count -ne $expectedExclusions.Count) {
        throw "$Label normalized manifest has an unsupported exclusion count."
    }
    for ($i = 0; $i -lt $expectedExclusions.Count; $i++) {
        if (-not [string]::Equals($actualExclusions[$i].path, $expectedExclusions[$i], [StringComparison]::Ordinal) -or
            -not [string]::Equals($actualExclusions[$i].reason, $expectedExclusionReasons[$i], [StringComparison]::Ordinal)) {
            throw "$Label normalized manifest has an unsupported exclusion policy at index $i."
        }
    }

    $seenPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    $previousPath = $null
    foreach ($file in @($manifest.files)) {
        if ([string]::IsNullOrWhiteSpace($file.path) -or $file.path.Contains("\")) {
            throw "$Label normalized manifest contains an invalid canonical path '$($file.path)'."
        }
        $leafName = [System.IO.Path]::GetFileName([string]$file.path)
        if ($leafName.EndsWith(".zip", [StringComparison]::OrdinalIgnoreCase) -or
            ($leafName.StartsWith("Win7POS-", [StringComparison]::OrdinalIgnoreCase) -and
                $leafName.EndsWith("-Setup.exe", [StringComparison]::OrdinalIgnoreCase))) {
            throw "$Label normalized manifest contains a prohibited outer ZIP/installer path '$($file.path)'."
        }
        if (-not $seenPaths.Add([string]$file.path)) {
            throw "$Label normalized manifest contains duplicate path '$($file.path)'."
        }
        if ($null -ne $previousPath -and [StringComparer]::Ordinal.Compare($previousPath, [string]$file.path) -ge 0) {
            throw "$Label normalized manifest file entries are not in ordinal path order."
        }
        if ([long]$file.length -lt 0 -or $file.sha256 -notmatch '^[0-9a-f]{64}$') {
            throw "$Label normalized manifest contains invalid length/hash metadata for '$($file.path)'."
        }
        $expectedNormalization = if ([string]::Equals($file.path, "VERSION.txt", [StringComparison]::Ordinal)) {
            "BuildTimestampUtc-only"
        }
        else {
            "none"
        }
        if (-not [string]::Equals($file.normalization, $expectedNormalization, [StringComparison]::Ordinal)) {
            throw "$Label normalized manifest contains an unsupported file normalization for '$($file.path)'."
        }
        $previousPath = [string]$file.path
    }

    foreach ($requiredPath in @("VERSION.txt", "Win7POS.Core.dll", "Win7POS.Data.dll", "Win7POS.Wpf.exe", "Win7POS.Wpf.exe.config")) {
        if (-not $seenPaths.Contains($requiredPath)) {
            throw "$Label normalized manifest is missing required payload path '$requiredPath'."
        }
    }

    $expectedBinaryPaths = @("Win7POS.Core.dll", "Win7POS.Data.dll", "Win7POS.Wpf.exe")
    $binaryVersions = @($manifest.verifiedBinaryVersions)
    if ($binaryVersions.Count -ne $expectedBinaryPaths.Count) {
        throw "$Label normalized manifest has an invalid verifiedBinaryVersions count."
    }
    for ($i = 0; $i -lt $expectedBinaryPaths.Count; $i++) {
        $binary = $binaryVersions[$i]
        if (-not [string]::Equals($binary.path, $expectedBinaryPaths[$i], [StringComparison]::Ordinal) -or
            -not [string]::Equals($binary.assemblyVersion, $expectedFourPartVersion, [StringComparison]::Ordinal) -or
            -not [string]::Equals($binary.fileVersion, $expectedFourPartVersion, [StringComparison]::Ordinal) -or
            -not [string]::Equals($binary.informationalVersion, $expectedInformationalVersion, [StringComparison]::Ordinal)) {
            throw "$Label normalized manifest has invalid verified binary version evidence at index $i."
        }
    }

    return $manifest
}

if ($ExpectedCommitSha -notmatch '^[0-9a-fA-F]{40}$') {
    throw "ExpectedCommitSha must be a full 40-character commit SHA."
}
$ExpectedCommitSha = $ExpectedCommitSha.ToLowerInvariant()

$manifestAObject = Read-AndValidateManifest -Path $ManifestA -Label "Build A" -CommitSha $ExpectedCommitSha
$manifestBObject = Read-AndValidateManifest -Path $ManifestB -Label "Build B" -CommitSha $ExpectedCommitSha

$differences = New-Object System.Collections.Generic.List[object]
if (-not [string]::Equals($manifestAObject.buildVersion, $manifestBObject.buildVersion, [StringComparison]::Ordinal)) {
    $differences.Add([pscustomobject][ordered]@{
        type = "BuildVersionMismatch"
        path = "VERSION.txt"
        buildA = $manifestAObject.buildVersion
        buildB = $manifestBObject.buildVersion
    }) | Out-Null
}

$filesA = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([StringComparer]::Ordinal)
$filesB = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([StringComparer]::Ordinal)
foreach ($file in @($manifestAObject.files)) { $filesA.Add([string]$file.path, $file) }
foreach ($file in @($manifestBObject.files)) { $filesB.Add([string]$file.path, $file) }

$allPaths = New-Object System.Collections.Generic.List[string]
foreach ($path in $filesA.Keys) { $allPaths.Add($path) | Out-Null }
foreach ($path in $filesB.Keys) {
    if (-not $filesA.ContainsKey($path)) { $allPaths.Add($path) | Out-Null }
}
$sortedPaths = $allPaths.ToArray()
[Array]::Sort($sortedPaths, [StringComparer]::Ordinal)

foreach ($path in $sortedPaths) {
    if (-not $filesA.ContainsKey($path)) {
        $differences.Add([pscustomobject][ordered]@{
            type = "UnexpectedInBuildB"
            path = $path
            buildA = $null
            buildB = $filesB[$path].sha256
        }) | Out-Null
        continue
    }
    if (-not $filesB.ContainsKey($path)) {
        $differences.Add([pscustomobject][ordered]@{
            type = "MissingFromBuildB"
            path = $path
            buildA = $filesA[$path].sha256
            buildB = $null
        }) | Out-Null
        continue
    }

    $a = $filesA[$path]
    $b = $filesB[$path]
    if ([long]$a.length -ne [long]$b.length) {
        $differences.Add([pscustomobject][ordered]@{
            type = "LengthMismatch"
            path = $path
            buildA = [long]$a.length
            buildB = [long]$b.length
        }) | Out-Null
    }
    if (-not [string]::Equals($a.sha256, $b.sha256, [StringComparison]::Ordinal)) {
        $differences.Add([pscustomobject][ordered]@{
            type = "HashMismatch"
            path = $path
            buildA = $a.sha256
            buildB = $b.sha256
        }) | Out-Null
    }
    if (-not [string]::Equals($a.normalization, $b.normalization, [StringComparison]::Ordinal)) {
        $differences.Add([pscustomobject][ordered]@{
            type = "NormalizationMismatch"
            path = $path
            buildA = $a.normalization
            buildB = $b.normalization
        }) | Out-Null
    }
}

$passed = $differences.Count -eq 0
$comparison = [pscustomobject][ordered]@{
    format = $ComparisonFormat
    passed = $passed
    commitSha = $ExpectedCommitSha
    buildVersion = $manifestAObject.buildVersion
    payload = "unsigned-net48-x86"
    manifestA = [pscustomobject][ordered]@{
        fileName = [System.IO.Path]::GetFileName($ManifestA)
        sha256 = Get-FileSha256 -Path $ManifestA
        fileCount = [int]$manifestAObject.fileCount
    }
    manifestB = [pscustomobject][ordered]@{
        fileName = [System.IO.Path]::GetFileName($ManifestB)
        sha256 = Get-FileSha256 -Path $ManifestB
        fileCount = [int]$manifestBObject.fileCount
    }
    comparedPathCount = $sortedPaths.Count
    normalizedFields = @("VERSION.txt:BuildTimestampUtc")
    excludedDerivedOrOuterContainers = @(
        "APP-FILES.txt",
        "SHA256SUMS.txt",
        "Win7POS-$($manifestAObject.buildVersion)-x86.zip",
        "Win7POS-$($manifestAObject.buildVersion)-Setup.exe"
    )
    differenceCount = $differences.Count
    differences = $differences.ToArray()
}

$comparisonFullPath = [System.IO.Path]::GetFullPath($ComparisonEvidencePath)
$comparisonParent = Split-Path -Parent $comparisonFullPath
if (-not [string]::IsNullOrWhiteSpace($comparisonParent)) {
    New-Item -ItemType Directory -Force -Path $comparisonParent | Out-Null
}
$json = $comparison | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText($comparisonFullPath, $json + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))

if (-not $passed) {
    $firstDifference = $differences[0]
    throw "Unsigned payload reproducibility mismatch: $($firstDifference.type) at '$($firstDifference.path)'. Evidence: $comparisonFullPath"
}

Write-Host "Unsigned payload manifests match exactly after the single documented timestamp normalization ($($sortedPaths.Count) paths)."
Write-Host "Comparison evidence: $comparisonFullPath"
