#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9]+$')][string]$RunId,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedCommitSha,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $OutputDirectory) { throw 'OutputDirectory must be new.' }
$runText = & gh run view $RunId --repo XNIW/Win7POS --json headSha,status,conclusion,workflowName,url
if ($LASTEXITCODE -ne 0) { throw 'Cannot read GitHub run.' }
$run = $runText | ConvertFrom-Json
if ($run.headSha -cne $ExpectedCommitSha -or $run.status -ne 'completed' -or
    $run.conclusion -ne 'success' -or $run.workflowName -ne 'Release Pack') {
    throw 'A successful Release Pack run on the exact requested SHA is required.'
}
$metadataText = & gh api "repos/XNIW/Win7POS/actions/runs/$RunId/artifacts"
if ($LASTEXITCODE -ne 0) { throw 'Cannot read artifact metadata.' }
$metadata = $metadataText | ConvertFrom-Json
$packs = @($metadata.artifacts | Where-Object { $_.name -like 'Win7POS-*-ReleasePack-x86' })
if ($packs.Count -ne 1) { throw 'Expected exactly one ReleasePack artifact.' }
$version = $packs[0].name.Substring(8).Replace('-ReleasePack-x86','')
$names = @("Win7POS-$version-ReleasePack-x86", "Win7POS-$version-Setup", "Win7POS-$version-dist")
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$runText | Set-Content (Join-Path $OutputDirectory 'run.json')
$metadataText | Set-Content (Join-Path $OutputDirectory 'artifacts.json')
$archives = @()
foreach ($name in $names) {
    $artifacts = @($metadata.artifacts | Where-Object name -CEQ $name)
    if ($artifacts.Count -ne 1 -or $artifacts[0].expired -or
        $artifacts[0].digest -cnotmatch '^sha256:[0-9a-f]{64}$') { throw "Invalid artifact: $name" }
    $artifact = $artifacts[0]
    $archive = Join-Path $OutputDirectory "$name.github.zip"
    # PowerShell 7.4+ preserves the native byte stream; do not decode/re-encode ZIPs.
    & gh api "repos/XNIW/Win7POS/actions/artifacts/$($artifact.id)/zip" > $archive
    if ($LASTEXITCODE -ne 0) { throw "Download failed: $name" }
    $digest = 'sha256:' + (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($digest -cne $artifact.digest) { throw "GitHub archive digest mismatch: $name" }
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $OutputDirectory $name)
    $archives += [pscustomobject]@{ name=$name; id=$artifact.id; digest=$digest }
}
$pack = Join-Path $OutputDirectory $names[0]
$setup = Join-Path $pack "Win7POS-$version-Setup.exe"
$payloadZip = Join-Path $pack "Win7POS-$version-x86.zip"
$dist = Join-Path $OutputDirectory $names[2]
$payload = Join-Path $pack 'Win7POS'
Expand-Archive -LiteralPath $payloadZip -DestinationPath $payload
$distFiles = @(Get-ChildItem -LiteralPath $dist -File -Recurse)
$payloadFiles = @(Get-ChildItem -LiteralPath $payload -File -Recurse)
if ($distFiles.Count -ne $payloadFiles.Count) { throw 'dist and payload file counts differ.' }
foreach ($file in $payloadFiles) {
    $relative = $file.FullName.Substring($payload.Length + 1)
    if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $dist $relative)).Hash) {
        throw "dist and payload differ: $relative"
    }
}
if ((Get-FileHash -LiteralPath $setup).Hash -ne
    (Get-FileHash -LiteralPath (Join-Path (Join-Path $OutputDirectory $names[1]) "Win7POS-$version-Setup.exe")).Hash) {
    throw 'Setup and ReleasePack installers differ.'
}
$integrity = Join-Path $pack 'release-evidence'
$checksum = Get-Content -LiteralPath (Join-Path $integrity 'release-checksums.json') -Raw | ConvertFrom-Json
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'test-protected-release-artifacts.ps1') `
    -ArtifactRoot $pack -ChecksumManifestPath (Join-Path $integrity 'release-checksums.json') `
    -UnsignedPayloadManifestPath (Join-Path $integrity 'unsigned-payload-manifest.json') `
    -SbomPath (Join-Path $pack "Win7POS-$version.cdx.json") `
    -ProvenancePath (Join-Path $integrity 'release-provenance.json') `
    -AttestationPath (Join-Path $integrity 'release-attestation.intoto.jsonl') `
    -ExpectedStage development-unsigned -CommitSha $ExpectedCommitSha `
    -ProductVersion $checksum.productVersion -BuildVersion $version -ReleaseTag ''
if ($LASTEXITCODE -ne 0) { throw 'Downloaded ReleasePack failed canonical integrity verification.' }
$signatures = @(@($payloadFiles | Where-Object Extension -In '.exe','.dll') + @(Get-Item -LiteralPath $setup) |
    ForEach-Object { [pscustomobject]@{ file=$_.FullName.Substring($pack.Length + 1);
        status=[string](Get-AuthenticodeSignature -LiteralPath $_.FullName).Status } })
[ordered]@{
    status='PASS'; runId=$RunId; commitSha=$ExpectedCommitSha; buildVersion=$version;
    archives=$archives; payloadFiles=$payloadFiles.Count;
    setupSha256=(Get-FileHash -LiteralPath $setup).Hash.ToLowerInvariant();
    payloadZipSha256=(Get-FileHash -LiteralPath $payloadZip).Hash.ToLowerInvariant();
    signatures=$signatures; installation='NOT_EXECUTED'
} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutputDirectory 'download-verification.json')
Write-Output "DOWNLOADED_RELEASE_PACK=PASS sha=$ExpectedCommitSha files=$($payloadFiles.Count)"
