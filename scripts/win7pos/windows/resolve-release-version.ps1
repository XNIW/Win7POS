[CmdletBinding()]
param(
    [string]$RepoRoot = "",
    [string]$VersionPropsPath = "",
    [string]$CommitSha = "",
    [string]$Ref = "",
    [switch]$AsJson,
    [string]$GitHubEnvironmentFile = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path
}
else {
    $RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
}

if ([string]::IsNullOrWhiteSpace($VersionPropsPath)) {
    $VersionPropsPath = Join-Path $RepoRoot "Directory.Build.props"
}
elseif (-not [System.IO.Path]::IsPathRooted($VersionPropsPath)) {
    $VersionPropsPath = Join-Path $RepoRoot $VersionPropsPath
}

if (-not (Test-Path -LiteralPath $VersionPropsPath -PathType Leaf)) {
    throw "Authoritative version source is missing: $VersionPropsPath"
}

try {
    [xml]$versionXml = [System.IO.File]::ReadAllText($VersionPropsPath)
}
catch {
    throw "Authoritative version source is not valid XML: $($_.Exception.Message)"
}

$versionNodes = @($versionXml.SelectNodes("//*[local-name()='Win7PosVersion']"))
if ($versionNodes.Count -ne 1) {
    throw "Directory.Build.props must contain exactly one Win7PosVersion value."
}
$productVersion = $versionNodes[0].InnerText.Trim()
$semVerPattern = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'
if ($productVersion -notmatch $semVerPattern) {
    throw "Win7PosVersion must be a canonical MAJOR.MINOR.PATCH value; found '$productVersion'."
}

function Get-GitValue([string[]]$Arguments) {
    try {
        $value = & git -C $RepoRoot @Arguments 2>$null
        if ($LASTEXITCODE -eq 0) {
            return (($value | Select-Object -First 1) -as [string]).Trim()
        }
    }
    catch { }
    return ""
}

if ([string]::IsNullOrWhiteSpace($CommitSha)) {
    # The checked-out HEAD is authoritative; GITHUB_SHA can be a synthetic PR merge SHA.
    $CommitSha = Get-GitValue @("rev-parse", "HEAD")
    if ([string]::IsNullOrWhiteSpace($CommitSha) -and $env:GITHUB_SHA) {
        $CommitSha = $env:GITHUB_SHA
    }
}
if ($CommitSha -notmatch '^[0-9a-fA-F]{40}$') {
    throw "A full 40-character exact commit SHA is required to resolve the build version."
}
$CommitSha = $CommitSha.ToLowerInvariant()

if ([string]::IsNullOrWhiteSpace($Ref)) {
    if ($env:GITHUB_REF) {
        $Ref = $env:GITHUB_REF
    }
    else {
        $Ref = Get-GitValue @("rev-parse", "--abbrev-ref", "HEAD")
    }
}
if ([string]::IsNullOrWhiteSpace($Ref)) {
    throw "A branch or tag ref is required to resolve the build version."
}

$isRelease = $false
$releaseTag = ""
if ($Ref -match '^refs/tags/(.+)$') {
    $releaseTag = $Matches[1]
    if ($releaseTag -notmatch '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw "Tag builds must use the exact protected release format vMAJOR.MINOR.PATCH; found '$releaseTag'."
    }
    $tagVersion = $releaseTag.Substring(1)
    if (-not [string]::Equals($tagVersion, $productVersion, [StringComparison]::Ordinal)) {
        throw "Release tag '$releaseTag' does not match authoritative version 'v$productVersion'."
    }
    $isRelease = $true
}

$shortSha = $CommitSha.Substring(0, 12)
$buildVersion = if ($isRelease) { $productVersion } else { "$productVersion-dev.$shortSha" }
$informationalVersion = "$buildVersion+sha.$CommitSha"
$assemblyVersion = "$productVersion.0"
$installerBaseFilename = "Win7POS-$buildVersion-Setup"

$result = [pscustomobject][ordered]@{
    ProductVersion = $productVersion
    BuildVersion = $buildVersion
    AssemblyVersion = $assemblyVersion
    FileVersion = $assemblyVersion
    InformationalVersion = $informationalVersion
    InstallerBaseFilename = $installerBaseFilename
    CommitSha = $CommitSha
    ShortSha = $shortSha
    Ref = $Ref
    ReleaseTag = $releaseTag
    IsRelease = $isRelease
}

if (-not [string]::IsNullOrWhiteSpace($GitHubEnvironmentFile)) {
    $environmentLines = @(
        "Win7PosBuildVersion=$buildVersion",
        "Win7PosInformationalVersion=$informationalVersion",
        "Win7PosCommitSha=$CommitSha",
        "WIN7POS_EXACT_SHA=$CommitSha",
        "WIN7POS_PRODUCT_VERSION=$productVersion",
        "WIN7POS_BUILD_VERSION=$buildVersion",
        "WIN7POS_ASSEMBLY_VERSION=$assemblyVersion",
        "WIN7POS_FILE_VERSION=$assemblyVersion",
        "WIN7POS_INFORMATIONAL_VERSION=$informationalVersion",
        "WIN7POS_INSTALLER_BASE_FILENAME=$installerBaseFilename",
        "WIN7POS_RELEASE_TAG=$releaseTag"
    )
    $environmentWriter = New-Object System.IO.StreamWriter(
        $GitHubEnvironmentFile,
        $true,
        (New-Object System.Text.UTF8Encoding($false)))
    try {
        foreach ($environmentLine in $environmentLines) {
            $environmentWriter.WriteLine($environmentLine)
        }
    }
    finally {
        $environmentWriter.Dispose()
    }
}

if ($AsJson) {
    $result | ConvertTo-Json -Compress
}
else {
    $result
}
