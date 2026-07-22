[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$EvidenceDirectory,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedCommitSha,

    [string]$ManifestAFileName = "build-a.normalized-manifest.json",
    [string]$ManifestBFileName = "build-b.normalized-manifest.json",
    [string]$CanonicalManifestFileName = "unsigned-payload.normalized-manifest.json",
    [string]$ComparisonFileName = "comparison.json",
    [string]$RunFileName = "run.json"
)

$ErrorActionPreference = "Stop"

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-ExactStringArray {
    param(
        [Parameter(Mandatory = $true)][object[]]$Actual,
        [Parameter(Mandatory = $true)][string[]]$Expected,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ($Actual.Count -ne $Expected.Count) {
        throw "$Label has an unexpected item count."
    }
    for ($i = 0; $i -lt $Expected.Count; $i++) {
        if (-not [string]::Equals([string]$Actual[$i], $Expected[$i], [StringComparison]::Ordinal)) {
            throw "$Label has an unexpected value at index $i."
        }
    }
}

if ($ExpectedCommitSha -notmatch '^[0-9a-fA-F]{40}$') {
    throw "ExpectedCommitSha must be a full 40-character commit SHA."
}
$ExpectedCommitSha = $ExpectedCommitSha.ToLowerInvariant()

if (-not (Test-Path -LiteralPath $EvidenceDirectory -PathType Container)) {
    throw "Reproducibility evidence directory is missing: $EvidenceDirectory"
}
$evidenceRoot = (Resolve-Path -LiteralPath $EvidenceDirectory).Path

$manifestAPath = Join-Path $evidenceRoot $ManifestAFileName
$manifestBPath = Join-Path $evidenceRoot $ManifestBFileName
$canonicalManifestPath = Join-Path $evidenceRoot $CanonicalManifestFileName
$comparisonPath = Join-Path $evidenceRoot $ComparisonFileName
$runPath = Join-Path $evidenceRoot $RunFileName
foreach ($requiredEvidence in @($manifestAPath, $manifestBPath, $canonicalManifestPath, $comparisonPath, $runPath)) {
    if (-not (Test-Path -LiteralPath $requiredEvidence -PathType Leaf)) {
        throw "Required reproducibility evidence is missing: $requiredEvidence"
    }
}

try {
    $manifestA = [System.IO.File]::ReadAllText($manifestAPath) | ConvertFrom-Json
    $manifestB = [System.IO.File]::ReadAllText($manifestBPath) | ConvertFrom-Json
    $canonicalManifest = [System.IO.File]::ReadAllText($canonicalManifestPath) | ConvertFrom-Json
    $comparison = [System.IO.File]::ReadAllText($comparisonPath) | ConvertFrom-Json
    $run = [System.IO.File]::ReadAllText($runPath) | ConvertFrom-Json
}
catch {
    throw "Reproducibility evidence is not valid JSON: $($_.Exception.Message)"
}

if (-not [string]::Equals($manifestA.format, "win7pos-unsigned-payload-manifest-v1", [StringComparison]::Ordinal) -or
    -not [string]::Equals($manifestB.format, "win7pos-unsigned-payload-manifest-v1", [StringComparison]::Ordinal) -or
    -not [string]::Equals($canonicalManifest.format, "win7pos-unsigned-payload-manifest-v1", [StringComparison]::Ordinal)) {
    throw "Reproducibility evidence contains an unsupported normalized manifest format."
}
if (-not [string]::Equals($comparison.format, "win7pos-unsigned-payload-comparison-v1", [StringComparison]::Ordinal)) {
    throw "Reproducibility comparison evidence has an unsupported format."
}
if (-not [string]::Equals($run.format, "win7pos-unsigned-payload-reproducibility-run-v1", [StringComparison]::Ordinal)) {
    throw "Reproducibility run evidence has an unsupported format."
}
foreach ($commit in @($manifestA.commitSha, $manifestB.commitSha, $canonicalManifest.commitSha, $comparison.commitSha, $run.commitSha)) {
    if (-not [string]::Equals($commit, $ExpectedCommitSha, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Reproducibility evidence commit '$commit' does not match exact commit '$ExpectedCommitSha'."
    }
}

$recalculatedComparisonPath = Join-Path $evidenceRoot (".recalculated-comparison-" + [Guid]::NewGuid().ToString("N") + ".json")
try {
    $comparator = Join-Path $PSScriptRoot "compare-reproducibility-payload-manifests.ps1"
    & $comparator `
        -ManifestA $manifestAPath `
        -ManifestB $manifestBPath `
        -ComparisonEvidencePath $recalculatedComparisonPath `
        -ExpectedCommitSha $ExpectedCommitSha
    if (-not (Test-Path -LiteralPath $recalculatedComparisonPath -PathType Leaf)) {
        throw "Independent reproducibility comparison did not produce evidence."
    }
    $recordedComparisonHash = Get-FileSha256 -Path $comparisonPath
    $recalculatedComparisonHash = Get-FileSha256 -Path $recalculatedComparisonPath
    if (-not [string]::Equals($recordedComparisonHash, $recalculatedComparisonHash, [StringComparison]::Ordinal) -or
        (Get-Item -LiteralPath $comparisonPath).Length -ne (Get-Item -LiteralPath $recalculatedComparisonPath).Length) {
        throw "Recorded comparison.json does not exactly match the independently recalculated comparison."
    }
}
finally {
    if (Test-Path -LiteralPath $recalculatedComparisonPath -PathType Leaf) {
        [System.IO.File]::Delete($recalculatedComparisonPath)
    }
}

if ($comparison.passed -ne $true -or [int]$comparison.differenceCount -ne 0 -or @($comparison.differences).Count -ne 0) {
    throw "Reproducibility comparison evidence does not record a clean PASS."
}
if ([int]$manifestA.fileCount -le 0 -or
    [int]$manifestA.fileCount -ne @($manifestA.files).Count -or
    [int]$manifestB.fileCount -ne @($manifestB.files).Count -or
    [int]$manifestA.fileCount -ne [int]$manifestB.fileCount -or
    [int]$comparison.comparedPathCount -ne [int]$manifestA.fileCount) {
    throw "Reproducibility evidence file counts are missing or inconsistent."
}

$manifestAHash = Get-FileSha256 -Path $manifestAPath
$manifestBHash = Get-FileSha256 -Path $manifestBPath
$canonicalManifestHash = Get-FileSha256 -Path $canonicalManifestPath
$comparisonHash = Get-FileSha256 -Path $comparisonPath
$runHash = Get-FileSha256 -Path $runPath
if (-not [string]::Equals($canonicalManifestHash, $manifestAHash, [StringComparison]::Ordinal)) {
    throw "Canonical unsigned payload manifest is not byte-identical to the validated Build A manifest."
}
if (-not [string]::Equals($comparison.manifestA.fileName, $ManifestAFileName, [StringComparison]::Ordinal) -or
    -not [string]::Equals($comparison.manifestA.sha256, $manifestAHash, [StringComparison]::Ordinal) -or
    [int]$comparison.manifestA.fileCount -ne [int]$manifestA.fileCount) {
    throw "Build A manifest evidence does not match the comparison record."
}
if (-not [string]::Equals($comparison.manifestB.fileName, $ManifestBFileName, [StringComparison]::Ordinal) -or
    -not [string]::Equals($comparison.manifestB.sha256, $manifestBHash, [StringComparison]::Ordinal) -or
    [int]$comparison.manifestB.fileCount -ne [int]$manifestB.fileCount) {
    throw "Build B manifest evidence does not match the comparison record."
}

$expectedBuildVersion = [string]$manifestA.buildVersion
if ($expectedBuildVersion -notmatch '^(?<product>(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*))(?:-dev\.[0-9a-f]{12})?$') {
    throw "Reproducibility manifest build version is invalid."
}
$expectedProductVersion = $Matches["product"]
if ($run.passed -ne $true -or
    -not [string]::Equals($run.productVersion, $expectedProductVersion, [StringComparison]::Ordinal) -or
    -not [string]::Equals($run.buildVersion, $expectedBuildVersion, [StringComparison]::Ordinal) -or
    -not [string]::Equals($run.sdkVersion, "10.0.301", [StringComparison]::Ordinal) -or
    -not [string]::Equals($run.configuration, "Release", [StringComparison]::Ordinal) -or
    -not [string]::Equals($run.platform, "x86", [StringComparison]::Ordinal) -or
    -not [string]::Equals($run.targetFramework, "net48", [StringComparison]::Ordinal) -or
    [int]$run.isolatedCleanBuildCount -ne 2) {
    throw "Reproducibility run invariants (two clean Release/net48/x86 builds with SDK 10.0.301) are missing or inconsistent."
}
if ([string]::IsNullOrWhiteSpace([string]$run.ref) -or [string]$run.ref -notmatch '^refs\/(heads|tags)\/[^\s]+$') {
    throw "Reproducibility run ref is missing or non-canonical."
}

$expectedBuildLabels = @("Build A", "Build B")
$expectedIsolationKeys = @("build-a", "build-b")
$builds = @($run.builds)
if ($builds.Count -ne 2) {
    throw "Reproducibility run must record exactly two isolated builds."
}
for ($i = 0; $i -lt 2; $i++) {
    $build = $builds[$i]
    if (-not [string]::Equals($build.label, $expectedBuildLabels[$i], [StringComparison]::Ordinal) -or
        -not [string]::Equals($build.sourceKind, "detached-git-worktree", [StringComparison]::Ordinal) -or
        -not [string]::Equals($build.commitSha, $ExpectedCommitSha, [StringComparison]::Ordinal) -or
        $build.cleanBeforeRestore -ne $true -or
        $build.outputCopiedToIsolatedPayload -ne $true -or
        -not [string]::Equals($build.payloadIsolationKey, $expectedIsolationKeys[$i], [StringComparison]::Ordinal) -or
        -not [string]::Equals($build.configuration, "Release", [StringComparison]::Ordinal) -or
        -not [string]::Equals($build.platform, "x86", [StringComparison]::Ordinal) -or
        -not [string]::Equals($build.targetFramework, "net48", [StringComparison]::Ordinal)) {
        throw "Reproducibility isolated-build evidence is invalid at index $i."
    }
}

if ($run.cleanup.worktreesRemoved -ne $true -or
    $run.cleanup.gitWorktreePruned -ne $true -or
    -not [string]::Equals($run.cleanup.temporaryRootPolicy, "system-temp/Win7POS-Repro-*", [StringComparison]::Ordinal) -or
    (($run.cleanup.temporaryBuildRootRemoved -eq $true) -eq ($run.cleanup.retainedByExplicitRequest -eq $true))) {
    throw "Reproducibility cleanup evidence is missing or inconsistent."
}
if (-not [string]::Equals($run.removedFiles.policy, "PDB debug files only", [StringComparison]::Ordinal) -or
    [int]$run.removedFiles.buildA -lt 0 -or [int]$run.removedFiles.buildB -lt 0) {
    throw "Reproducibility removed-file policy is missing or inconsistent."
}
Assert-ExactStringArray -Actual @($run.normalizedFields) -Expected @("VERSION.txt:BuildTimestampUtc") -Label "Reproducibility normalization policy"
$expectedExclusions = @(
    "APP-FILES.txt",
    "SHA256SUMS.txt",
    "Win7POS-$expectedBuildVersion-x86.zip",
    "Win7POS-$expectedBuildVersion-Setup.exe"
)
Assert-ExactStringArray -Actual @($run.excludedDerivedOrOuterContainers) -Expected $expectedExclusions -Label "Reproducibility exclusion policy"

$expectedManifestNames = @($ManifestAFileName, $ManifestBFileName, $CanonicalManifestFileName)
$expectedManifestHashes = @($manifestAHash, $manifestBHash, $canonicalManifestHash)
$runManifests = @($run.manifests)
if ($runManifests.Count -ne 3) {
    throw "Reproducibility run must bind exactly three manifest files."
}
for ($i = 0; $i -lt 3; $i++) {
    if (-not [string]::Equals($runManifests[$i].fileName, $expectedManifestNames[$i], [StringComparison]::Ordinal) -or
        -not [string]::Equals($runManifests[$i].sha256, $expectedManifestHashes[$i], [StringComparison]::Ordinal)) {
        throw "Reproducibility run manifest hash binding is invalid at index $i."
    }
}
if (-not [string]::Equals($runManifests[2].role, "canonical unsigned payload integrity input", [StringComparison]::Ordinal) -or
    -not [string]::Equals($run.comparison.fileName, $ComparisonFileName, [StringComparison]::Ordinal) -or
    -not [string]::Equals($run.comparison.sha256, $comparisonHash, [StringComparison]::Ordinal)) {
    throw "Reproducibility run comparison/canonical evidence binding is invalid."
}

$validation = [pscustomobject][ordered]@{
    format = "win7pos-reproducibility-evidence-validation-v1"
    passed = $true
    commitSha = $ExpectedCommitSha
    buildVersion = $comparison.buildVersion
    comparedPathCount = [int]$comparison.comparedPathCount
    manifestA = [pscustomobject][ordered]@{ fileName = $ManifestAFileName; sha256 = $manifestAHash }
    manifestB = [pscustomobject][ordered]@{ fileName = $ManifestBFileName; sha256 = $manifestBHash }
    canonicalManifest = [pscustomobject][ordered]@{ fileName = $CanonicalManifestFileName; sha256 = $canonicalManifestHash }
    comparison = [pscustomobject][ordered]@{ fileName = $ComparisonFileName; sha256 = $comparisonHash }
    run = [pscustomobject][ordered]@{ fileName = $RunFileName; sha256 = $runHash }
}
$validationPath = Join-Path $evidenceRoot "evidence-validation.json"
$validationJson = $validation | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText($validationPath, $validationJson + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Reproducibility evidence is complete and internally consistent: $validationPath"
