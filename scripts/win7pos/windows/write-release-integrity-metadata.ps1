[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactRoot,

    [Parameter(Mandatory = $true)]
    [string[]]$ArtifactPath,

    [Parameter(Mandatory = $true)]
    [string]$UnsignedPayloadManifestPath,

    [Parameter(Mandatory = $true)]
    [string]$SbomPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [Parameter(Mandatory = $true)]
    [ValidateSet("development-unsigned", "unsigned", "signed")]
    [string]$StageName,

    [string]$FilePrefix = "release",

    [Parameter(Mandatory = $true)]
    [string]$CommitSha,

    [Parameter(Mandatory = $true)]
    [string]$ProductVersion,

    [string]$BuildVersion = "",

    [Parameter(Mandatory = $true)]
    [AllowEmptyString()]
    [string]$ReleaseTag,

    [Parameter(Mandatory = $true)]
    [string]$RepositoryUri,

    [Parameter(Mandatory = $true)]
    [string]$RepositoryRef,

    [Parameter(Mandatory = $true)]
    [string]$WorkflowName,

    [Parameter(Mandatory = $true)]
    [string]$WorkflowRunId,

    [Parameter(Mandatory = $true)]
    [string]$WorkflowRunAttempt,

    [Parameter(Mandatory = $true)]
    [string]$WorkflowRunUrl,

    [Parameter(Mandatory = $true)]
    [string]$BuilderId
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Get-FullPathInsideRoot {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$RootPrefix
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not [string]::Equals($fullPath, $Root, [StringComparison]::OrdinalIgnoreCase) -and
        -not $fullPath.StartsWith($RootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release integrity input is outside ArtifactRoot."
    }
    return $fullPath
}

function Get-RelativeReleasePath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    return [IO.Path]::GetRelativePath($Root, $Path).Replace('\', '/')
}

function Get-FileDigestRecord {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $item = Get-Item -LiteralPath $Path
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Release integrity metadata refuses reparse-point artifacts."
    }

    return [pscustomobject][ordered]@{
        path = Get-RelativeReleasePath -Root $Root -Path $item.FullName
        sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        size = [long]$item.Length
    }
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

$semVerPattern = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'
if ($CommitSha -notmatch '^[0-9a-fA-F]{40}$') {
    throw "A full exact commit SHA is required."
}
$CommitSha = $CommitSha.ToLowerInvariant()
if ($ProductVersion -notmatch $semVerPattern) {
    throw "ProductVersion must be an exact MAJOR.MINOR.PATCH semantic version."
}
if ([string]::IsNullOrWhiteSpace($BuildVersion)) {
    $BuildVersion = $ProductVersion
}
if ($StageName -eq "development-unsigned") {
    $expectedDevelopmentVersion = "$ProductVersion-dev.$($CommitSha.Substring(0, 12))"
    if (-not [string]::IsNullOrEmpty($ReleaseTag) -or $BuildVersion -ne $expectedDevelopmentVersion) {
        throw "Development integrity evidence requires no release tag and the exact SHA-derived build version."
    }
}
elseif ($ReleaseTag -ne "v$ProductVersion" -or $BuildVersion -ne $ProductVersion) {
    throw "Protected release evidence requires an exact vMAJOR.MINOR.PATCH tag and matching build version."
}
if ($FilePrefix -notmatch '^[a-z0-9][a-z0-9.-]*$') {
    throw "FilePrefix must use lowercase letters, digits, dots or hyphens."
}
if ($WorkflowRunId -notmatch '^[0-9]+$' -or $WorkflowRunAttempt -notmatch '^[0-9]+$') {
    throw "Workflow run ID and attempt must be numeric exact-run identifiers."
}
foreach ($uriValue in @($RepositoryUri, $WorkflowRunUrl, $BuilderId)) {
    $uri = $null
    if (-not [Uri]::TryCreate($uriValue, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne "https") {
        throw "Repository, workflow run and builder identifiers must be absolute HTTPS URIs."
    }
}
if (-not $WorkflowRunUrl.Contains("/$WorkflowRunId", [StringComparison]::Ordinal)) {
    throw "WorkflowRunUrl does not contain the exact workflow run ID."
}

$root = [IO.Path]::GetFullPath($ArtifactRoot).TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "ArtifactRoot does not exist."
}
$rootItem = Get-Item -LiteralPath $root -Force
if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Release integrity metadata refuses a reparse-point ArtifactRoot."
}
$rootPrefix = $root + [IO.Path]::DirectorySeparatorChar
$outputRoot = Get-FullPathInsideRoot -Path $OutputDirectory -Root $root -RootPrefix $rootPrefix
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null

$unsignedManifest = Get-FullPathInsideRoot -Path $UnsignedPayloadManifestPath -Root $root -RootPrefix $rootPrefix
$sbom = Get-FullPathInsideRoot -Path $SbomPath -Root $root -RootPrefix $rootPrefix
foreach ($requiredPath in @($unsignedManifest, $sbom)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf) -or
        (Get-Item -LiteralPath $requiredPath).Length -eq 0) {
        throw "Required unsigned manifest or SBOM is missing or empty."
    }
}

$checksumPath = Join-Path $outputRoot "$FilePrefix-checksums.json"
$provenancePath = Join-Path $outputRoot "$FilePrefix-provenance.json"
$attestationPath = Join-Path $outputRoot "$FilePrefix-attestation.intoto.jsonl"
$excludedOutputs = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($outputPath in @($checksumPath, $provenancePath, $attestationPath)) {
    [void]$excludedOutputs.Add([IO.Path]::GetFullPath($outputPath))
}

$candidateFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($path in @($ArtifactPath) + @($unsignedManifest, $sbom)) {
    $fullPath = Get-FullPathInsideRoot -Path $path -Root $root -RootPrefix $rootPrefix
    if (Test-Path -LiteralPath $fullPath -PathType Container) {
        $directory = Get-Item -LiteralPath $fullPath -Force
        if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Release integrity metadata refuses reparse-point artifact directories."
        }
        foreach ($item in Get-ChildItem -LiteralPath $fullPath -Recurse -Force) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Release integrity metadata refuses reparse-point artifacts."
            }
            if ($item.PSIsContainer) {
                continue
            }
            $checkedPath = Get-FullPathInsideRoot -Path $item.FullName -Root $root -RootPrefix $rootPrefix
            if (-not $excludedOutputs.Contains($checkedPath)) {
                [void]$candidateFiles.Add($checkedPath)
            }
        }
    }
    elseif (Test-Path -LiteralPath $fullPath -PathType Leaf) {
        if (-not $excludedOutputs.Contains($fullPath)) {
            [void]$candidateFiles.Add($fullPath)
        }
    }
    else {
        throw "A declared release artifact does not exist."
    }
}

if ($candidateFiles.Count -eq 0) {
    throw "At least one concrete release artifact is required."
}

# ArtifactRoot is the exact tree that will be published. ArtifactPath remains an
# explicit declaration of intended inputs, but it may not omit any file from that
# tree. The only exclusions are the three metadata outputs written below; they
# cannot be members of their own checksum/provenance graph.
$publishedFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($item in Get-ChildItem -LiteralPath $root -Recurse -Force) {
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Release integrity metadata refuses reparse points in the published artifact tree."
    }
    if ($item.PSIsContainer) {
        continue
    }
    $publishedPath = Get-FullPathInsideRoot -Path $item.FullName -Root $root -RootPrefix $rootPrefix
    if (-not $excludedOutputs.Contains($publishedPath)) {
        [void]$publishedFiles.Add($publishedPath)
    }
}
if ($candidateFiles.Count -ne $publishedFiles.Count -or
    @($publishedFiles | Where-Object { -not $candidateFiles.Contains($_) }).Count -ne 0) {
    throw "ArtifactPath does not cover the complete published ArtifactRoot tree."
}

$fileRecords = @($candidateFiles | ForEach-Object {
        Get-FileDigestRecord -Root $root -Path $_
    } | Sort-Object -Property path)

$checksumDocument = [pscustomobject][ordered]@{
    schema = "https://xniw.github.io/Win7POS/schemas/release-checksums/v1"
    algorithm = "SHA-256"
    stage = $StageName
    productVersion = $ProductVersion
    buildVersion = $BuildVersion
    releaseTag = $ReleaseTag
    commitSha = $CommitSha
    files = $fileRecords
}
Write-Utf8NoBom -Path $checksumPath -Content (($checksumDocument | ConvertTo-Json -Depth 20) + "`n")
$checksumRecord = Get-FileDigestRecord -Root $root -Path $checksumPath

$subjects = @($fileRecords + $checksumRecord | Sort-Object -Property path | ForEach-Object {
        [pscustomobject][ordered]@{
            name = $_.path
            digest = [pscustomobject][ordered]@{ sha256 = $_.sha256 }
        }
    })
$unsignedManifestRecord = Get-FileDigestRecord -Root $root -Path $unsignedManifest
$sbomRecord = Get-FileDigestRecord -Root $root -Path $sbom

$statement = [pscustomobject][ordered]@{
    _type = "https://in-toto.io/Statement/v1"
    subject = $subjects
    predicateType = "https://slsa.dev/provenance/v1"
    predicate = [pscustomobject][ordered]@{
        buildDefinition = [pscustomobject][ordered]@{
            buildType = "https://github.com/XNIW/Win7POS/blob/main/docs/RELEASE_SUPPLY_CHAIN.md#protected-release"
            externalParameters = [pscustomobject][ordered]@{
                productVersion = $ProductVersion
                buildVersion = $BuildVersion
                releaseTag = $ReleaseTag
                repositoryRef = $RepositoryRef
                stage = $StageName
            }
            internalParameters = [pscustomobject][ordered]@{
                workflowName = $WorkflowName
                workflowRunId = $WorkflowRunId
                workflowRunAttempt = $WorkflowRunAttempt
            }
            resolvedDependencies = @(
                [pscustomobject][ordered]@{
                    uri = "$RepositoryUri@$CommitSha"
                    digest = [pscustomobject][ordered]@{ gitCommit = $CommitSha }
                }
            )
        }
        runDetails = [pscustomobject][ordered]@{
            builder = [pscustomobject][ordered]@{ id = $BuilderId }
            metadata = [pscustomobject][ordered]@{
                invocationId = $WorkflowRunUrl
            }
            byproducts = @(
                [pscustomobject][ordered]@{
                    name = $unsignedManifestRecord.path
                    digest = [pscustomobject][ordered]@{ sha256 = $unsignedManifestRecord.sha256 }
                },
                [pscustomobject][ordered]@{
                    name = $sbomRecord.path
                    digest = [pscustomobject][ordered]@{ sha256 = $sbomRecord.sha256 }
                }
            )
        }
    }
}

$prettyStatement = $statement | ConvertTo-Json -Depth 30
$compactStatement = $statement | ConvertTo-Json -Depth 30 -Compress
Write-Utf8NoBom -Path $provenancePath -Content ($prettyStatement + "`n")
Write-Utf8NoBom -Path $attestationPath -Content ($compactStatement + "`n")

[pscustomobject][ordered]@{
    Stage = $StageName
    Checksums = $checksumPath
    Provenance = $provenancePath
    Attestation = $attestationPath
    SubjectCount = $subjects.Count
    CommitSha = $CommitSha
    ProductVersion = $ProductVersion
    BuildVersion = $BuildVersion
}
