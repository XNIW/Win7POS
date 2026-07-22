[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$failed = $false

function Fail([string]$Message) {
    Write-Host "FAIL: $Message" -ForegroundColor Red
    $script:failed = $true
}

function Pass([string]$Message) {
    Write-Host "PASS: $Message" -ForegroundColor Green
}

function Read-RequiredText([string]$RelativePath) {
    $path = Join-Path $repoRoot $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Fail "Required release supply-chain file is missing: $RelativePath"
        return ""
    }
    return [System.IO.File]::ReadAllText($path)
}

function Require-Pattern([string]$Label, [string]$Text, [string]$Pattern) {
    if ($Text -notmatch $Pattern) { Fail $Label } else { Pass $Label }
}

$requiredFiles = @(
    "eng\supply-chain\tools.json",
    "eng\supply-chain\license-policy.json",
    "eng\supply-chain\gitleaks.toml",
    ".gitleaksignore",
    "scripts\install-pinned-supply-chain-tools.ps1",
    "scripts\invoke-nuget-supply-chain-gates.ps1",
    "scripts\new-cyclonedx-sbom.ps1",
    "scripts\invoke-gitleaks-scans.ps1",
    "scripts\test-supply-chain-gates.ps1",
    "scripts\check-codeql-sarif.ps1",
    "scripts\win7pos\windows\test-unsigned-payload-reproducibility.ps1",
    "scripts\win7pos\windows\write-release-integrity-metadata.ps1",
    "scripts\win7pos\windows\test-protected-release-artifacts.ps1",
    "scripts\win7pos\windows\invoke-protected-release-signing.ps1",
    "scripts\win7pos\windows\test-release-signing-negative.ps1",
    "scripts\win7pos\windows\release-signing-toolchain.json",
    "docs\RELEASE_SUPPLY_CHAIN.md"
)
foreach ($relativePath in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $relativePath) -PathType Leaf)) {
        Fail "Required release supply-chain file is missing: $relativePath"
    }
}

try {
    $tools = Get-Content -LiteralPath (Join-Path $repoRoot "eng\supply-chain\tools.json") -Raw | ConvertFrom-Json
    if ($tools.schemaVersion -ne 1 -or
        $tools.cycloneDx.version -ne "6.2.0" -or
        $tools.cycloneDx.url -ne "https://api.nuget.org/v3-flatcontainer/cyclonedx/6.2.0/cyclonedx.6.2.0.nupkg" -or
        $tools.cycloneDx.size -ne 9424542 -or
        $tools.cycloneDx.sha256 -ne "3473525381eee02a649b75bbf5b4f0ffa66112f62917aff798fed196e290a884" -or
        $tools.gitleaks.version -ne "8.30.1" -or
        $tools.gitleaks.url -ne "https://github.com/gitleaks/gitleaks/releases/download/v8.30.1/gitleaks_8.30.1_windows_x64.zip" -or
        $tools.gitleaks.size -ne 8438883 -or
        $tools.gitleaks.sha256 -ne "d29144deff3a68aa93ced33dddf84b7fdc26070add4aa0f4513094c8332afc4e" -or
        $tools.gitleaks.executable -cne "gitleaks.exe") {
        Fail "CycloneDX and Gitleaks pins must match the reviewed official artifacts"
    }
    else { Pass "CycloneDX 6.2.0 and Gitleaks 8.30.1 are version/hash pinned" }
}
catch { Fail "Supply-chain tool pin configuration is invalid JSON" }

try {
    $signing = Get-Content -LiteralPath (Join-Path $repoRoot "scripts\win7pos\windows\release-signing-toolchain.json") -Raw | ConvertFrom-Json
    if ($signing.packageId -ne "Microsoft.Windows.SDK.BuildTools" -or
        $signing.version -ne "10.0.26100.7705" -or
        $signing.packageSize -ne 22582767 -or
        $signing.packageSha256 -ne "48a81375752f9f1ff56a34062084b426bfe412a5a8072e1c99b6a4be0e774841" -or
        $signing.signToolSha256 -ne "431ee314c83988cacda86606356fd321b75ae0093481b97e3b738e99c412f2a0" -or
        $signing.signToolProductVersion -ne "10.0.26100.7705" -or
        $signing.expectedSignerSubject -notmatch '^CN=Microsoft Corporation,') {
        Fail "signtool must come from the exact reviewed Microsoft package and executable"
    }
    else { Pass "Microsoft signtool package, executable, version and signer are pinned" }
}
catch { Fail "Signing toolchain pin configuration is invalid JSON" }

try {
    $licensePolicy = Get-Content -LiteralPath (Join-Path $repoRoot "eng\supply-chain\license-policy.json") -Raw | ConvertFrom-Json
    $allowed = @($licensePolicy.allowedExpressions | Sort-Object)
    $expectedAllowed = @("Apache-2.0", "LicenseRef-SQLite-Public-Domain", "MIT", "MPL-2.0")
    $packages = @($licensePolicy.licenseGroups | ForEach-Object { @($_.packages) })
    $uniquePackages = @($packages | Sort-Object -Unique)
    if ($licensePolicy.schemaVersion -ne 1 -or
        ($allowed -join "|") -ne ($expectedAllowed -join "|") -or
        $packages.Count -ne 99 -or $uniquePackages.Count -ne 99 -or
        @($packages | Where-Object { $_ -notmatch '^[^@]+@\d+\.\d+\.' }).Count -ne 0) {
        Fail "License policy must contain 99 unique exact package/version mappings and only reviewed expressions"
    }
    else { Pass "License policy contains 99 exact mappings with a closed reviewed allowlist" }
}
catch { Fail "License policy is invalid JSON" }

$ignoreText = Read-RequiredText ".gitleaksignore"
$ignoreLines = @($ignoreText -split '\r?\n' | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_) -and $_ -notmatch '^\s*#'
    })
$invalidIgnoreLines = @($ignoreLines | Where-Object {
        $_ -notmatch '^[0-9a-f]{40}:[^:*?]+:[a-z0-9-]+:[0-9]+$'
    })
$perf2bFalsePositive = "18a6e065d445445b201d5203807a06455311a862:tests/Win7POS.Core.Tests/Data/ProductQueryPlanTests.cs:generic-api-key:376"
if ($ignoreLines.Count -ne 9 -or
    $invalidIgnoreLines.Count -ne 0 -or
    $ignoreLines -cnotcontains $perf2bFalsePositive) {
    Fail "Gitleaks history exceptions must be nine reviewed exact commit/path/rule/line fingerprints"
}
else { Pass "Gitleaks history exceptions are nine reviewed exact fingerprints with no wildcard" }

$securityWorkflow = Read-RequiredText ".github\workflows\security-supply-chain.yml"
$releaseWorkflow = Read-RequiredText ".github\workflows\release-pack.yml"
$protectedWorkflow = Read-RequiredText ".github\workflows\protected-release.yml"
$runbook = Read-RequiredText "docs\RELEASE_SUPPLY_CHAIN.md"
$nugetGate = Read-RequiredText "scripts\invoke-nuget-supply-chain-gates.ps1"
$signerScript = Read-RequiredText "scripts\win7pos\windows\invoke-protected-release-signing.ps1"
$signingFixture = Read-RequiredText "scripts\win7pos\windows\test-release-signing-negative.ps1"

Require-Pattern "NuGet audits ignore runner-global sources and use only the approved HTTPS feed" $nugetGate '(?s)\$NuGetAuditSource\s*=\s*"https://api\.nuget\.org/v3/index\.json".*Invoke-DotNetReport.*"--source",\s*\$NuGetAuditSource.*Invoke-DotNetReport.*"--source",\s*\$NuGetAuditSource'

foreach ($required in @(
        @{ Label = "Security workflow performs vulnerable/deprecated/license gates"; Pattern = 'invoke-nuget-supply-chain-gates\.ps1' },
        @{ Label = "Security workflow produces CycloneDX SBOM"; Pattern = 'new-cyclonedx-sbom\.ps1' },
        @{ Label = "Security workflow scans working tree and full history"; Pattern = 'invoke-gitleaks-scans\.ps1' },
        @{ Label = "Security workflow compares two clean unsigned builds"; Pattern = 'test-unsigned-payload-reproducibility\.ps1' },
        @{ Label = "Security workflow records development checksums/provenance"; Pattern = 'StageName\s+development-unsigned[\s\S]*test-protected-release-artifacts\.ps1' },
        @{ Label = "Security workflow executes signing negative fixtures"; Pattern = 'test-release-signing-negative\.ps1' },
        @{ Label = "Security workflow uses one exact PR-head or event ref for version, repro and provenance"; Pattern = 'WIN7POS_EXACT_REF:\s*\$\{\{[\s\S]*refs/heads/\{0\}[\s\S]*resolve-release-version\.ps1[^\r\n]*-Ref\s+\$env:WIN7POS_EXACT_REF[\s\S]*test-unsigned-payload-reproducibility\.ps1[^\r\n]*-Ref\s+\$env:WIN7POS_EXACT_REF[\s\S]*-RepositoryRef\s+\$env:WIN7POS_EXACT_REF' },
        @{ Label = "CodeQL uses the reviewed full-SHA v4.37.2 action"; Pattern = 'github/codeql-action/(?:init|analyze)@e0647621c2984b5ed2f768cb892365bf2a616ad1\s+#\s+v4\.37\.2' },
        @{ Label = "CodeQL evidence is attributed to the exact PR head"; Pattern = 'refs/pull/\{0\}/head[\s\S]*sha:\s*\$\{\{\s*github\.event\.pull_request\.head\.sha\s*\|\|' },
        @{ Label = "CodeQL output is checked fail closed"; Pattern = 'check-codeql-sarif\.ps1' }
    )) {
    Require-Pattern $required.Label $securityWorkflow $required.Pattern
}
if ($securityWorkflow -match '\$\{\{\s*secrets\.' -or $securityWorkflow -match '(?m)^\s*environment:') {
    Fail "PR/branch security workflow must not reference signing secrets or protected environments"
}
else { Pass "PR/branch security workflow is unsigned and secret-free" }

foreach ($required in @(
        @{ Label = "Release Pack runs supply-chain gates"; Pattern = 'invoke-nuget-supply-chain-gates\.ps1[\s\S]*new-cyclonedx-sbom\.ps1[\s\S]*invoke-gitleaks-scans\.ps1' },
        @{ Label = "Release Pack runs two-build reproducibility"; Pattern = 'test-unsigned-payload-reproducibility\.ps1' },
        @{ Label = "Release Pack proves the published third payload matches canonical"; Pattern = 'write-reproducibility-payload-manifest\.ps1[\s\S]*compare-reproducibility-payload-manifests\.ps1[\s\S]*Published release payload differs' },
        @{ Label = "Release Pack publishes SBOM and integrity evidence"; Pattern = 'write-release-integrity-metadata\.ps1[\s\S]*release-evidence' }
    )) {
    Require-Pattern $required.Label $releaseWorkflow $required.Pattern
}
if ($releaseWorkflow -match '\$\{\{\s*secrets\.' -or $releaseWorkflow -match '(?m)^\s*environment:') {
    Fail "Ordinary Release Pack must remain unsigned and secret-free"
}
else { Pass "Ordinary Release Pack remains unsigned and secret-free" }
if ($releaseWorkflow -match '(?m)^\s+tags:\s*$' -or
    $releaseWorkflow -notmatch 'Reject tag invocation outside protected release[\s\S]*github\.ref_type\s*==\s*''tag''[\s\S]*Protected Release workflow' -or
    $releaseWorkflow -notmatch '\$stage\s*=\s*"development-unsigned"[\s\S]*\$releaseTag\s*=\s*""' -or
    $releaseWorkflow -match '\$isReleaseTag') {
    Fail "Ordinary Release Pack must be branch-only, reject dispatched tags and emit only development-unsigned evidence"
}
else { Pass "Ordinary Release Pack cannot publish a release-tag artifact" }

if ($protectedWorkflow -match '(?m)^\s*(workflow_dispatch|pull_request|branches):' -or
    $protectedWorkflow -notmatch '(?ms)^on:\s*\r?\n\s+push:\s*\r?\n\s+tags:\s*\r?\n\s+-\s+"v\*\.\*\.\*"') {
    Fail "Protected release must be tag-only with no manual, branch or PR trigger"
}
else { Pass "Protected release is tag-only" }

$signStart = $protectedWorkflow.IndexOf("  sign-protected-release:", [StringComparison]::Ordinal)
$attestStart = $protectedWorkflow.IndexOf("  attest-signed-release:", [StringComparison]::Ordinal)
if ($signStart -lt 0 -or $attestStart -le $signStart) {
    Fail "Protected release trust-boundary jobs are missing or unordered"
}
else {
    $unsignedBlock = $protectedWorkflow.Substring(0, $signStart)
    $signBlock = $protectedWorkflow.Substring($signStart, $attestStart - $signStart)
    $attestBlock = $protectedWorkflow.Substring($attestStart)
    if ($unsignedBlock -match '\$\{\{\s*secrets\.' -or $unsignedBlock -match '(?m)^\s*environment:') {
        Fail "Unsigned protected-release build must not receive signing material"
    }
    else { Pass "Unsigned protected-release build has no environment or signing secrets" }
    if ($signBlock -notmatch '(?m)^\s+environment:\s+win7pos-protected-release\s*$' -or
        $signBlock -match '(?m)^\s+(id-token|attestations|artifact-metadata):\s+write\s*$') {
        Fail "Signing job must use only the protected environment with contents read"
    }
    else { Pass "Signing job alone uses the protected environment and no OIDC write permission" }
    if ($attestBlock -notmatch '(?m)^\s+id-token:\s+write\s*$' -or
        $attestBlock -notmatch '(?m)^\s+attestations:\s+write\s*$' -or
        $attestBlock -match '\$\{\{\s*secrets\.' -or
        $attestBlock -match '(?m)^\s*environment:') {
        Fail "Attestation job must have OIDC/attestation permission without environment secrets"
    }
    else { Pass "OIDC attestation is isolated from the signing environment" }
}

$secretReferences = @([regex]::Matches($protectedWorkflow, '\$\{\{\s*secrets\.([A-Z0-9_]+)\s*\}\}') |
    ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
if (($secretReferences -join '|') -ne 'WIN7POS_SIGNING_PFX_B64|WIN7POS_SIGNING_PFX_PASSWORD') {
    Fail "Protected workflow may reference only the two documented protected-environment secrets"
}
else { Pass "Protected workflow references only the two signing secrets" }

foreach ($required in @(
        @{ Label = "Protected environment reviewer/tag/ruleset policy is verified"; Pattern = 'required_reviewers[\s\S]*deployment-branch-policies[\s\S]*rulesets' },
        @{ Label = "Protected tag resolves to exact HEAD contained in origin/main"; Pattern = 'tagCommitSha[\s\S]*headSha\s+-ne\s+\$expectedSha[\s\S]*merge-base\s+--is-ancestor\s+\$expectedSha\s+refs/remotes/origin/main' },
        @{ Label = "Protected tag creator types are least privilege"; Pattern = '\$allowedBypassActorTypes\s*=\s*@\("Integration",\s*"Team",\s*"User"\)' },
        @{ Label = "Protected tag ruleset has exact scope and one approved creator"; Pattern = 'WIN7POS_RELEASE_TAG_BYPASS_ACTOR_ID[\s\S]*WIN7POS_RELEASE_TAG_BYPASS_ACTOR_TYPE[\s\S]*\$includes\.Count\s+-eq\s+1[\s\S]*refs/tags/v\*\.\*\.\*[\s\S]*\$excludes\.Count\s+-eq\s+0[\s\S]*\$bypassActors\.Count\s+-eq\s+1[\s\S]*actor_id[\s\S]*actor_type[\s\S]*bypass_mode[\s\S]*"always"' },
        @{ Label = "Protected release proves the candidate payload matches canonical"; Pattern = 'write-reproducibility-payload-manifest\.ps1[\s\S]*compare-reproducibility-payload-manifests\.ps1[\s\S]*candidate differs' },
        @{ Label = "Application is signed before Inno installer compilation"; Pattern = 'Mode\s+Application[\s\S]*ISCC_EXE[\s\S]*Mode\s+Installer' },
        @{ Label = "Production signatures are verified fail closed"; Pattern = 'RequireProductionSignatures' },
        @{ Label = "GitHub provenance and SBOM attestations use pinned actions/attest v4.2.0"; Pattern = 'actions/attest@f7c74d28b9d84cb8768d0b8ca14a4bac6ef463e6\s+#\s+v4\.2\.0' },
        @{ Label = "Platform attestations are verified against signer/source"; Pattern = 'gh\s+attestation\s+verify[\s\S]*--signer-workflow[\s\S]*--source-digest[\s\S]*--source-ref' },
        @{ Label = "Expected signer identity crosses from the protected job without secrets"; Pattern = 'expected-signer-thumbprint:\s*\$\{\{\s*steps\.import-signing-material\.outputs\.expected-signer-thumbprint\s*\}\}[\s\S]*EXPECTED_SIGNER_THUMBPRINT:\s*\$\{\{\s*needs\.sign-protected-release\.outputs\.expected-signer-thumbprint\s*\}\}' },
        @{ Label = "Attestation job reruns signed closed-world validation with exact GitHub bundles"; Pattern = 'github-provenance\.sigstore\.json[\s\S]*github-sbom\.sigstore\.json[\s\S]*test-protected-release-artifacts\.ps1[\s\S]*SigningRecordPath\s+@\(\(Join-Path\s+\$signingRoot\s+"application-signing-record\.json"\),\s*\(Join-Path\s+\$signingRoot\s+"installer-signing-record\.json"\)\)[\s\S]*ExpectedSignerThumbprint\s+\$env:EXPECTED_SIGNER_THUMBPRINT[\s\S]*RequireProductionSignatures[\s\S]*PostMetadataArtifactPath\s+@\(\$githubProvenanceBundle,\s*\$githubSbomBundle\)' },
        @{ Label = "PFX import snapshots the store and captures every returned certificate"; Pattern = 'beforeCertificates\s*=\s*@\(Get-CurrentSigningStoreCertificates\)[\s\S]*importedCertificates\s*=\s*@\(Import-PfxCertificate' },
        @{ Label = "PFX preflight is ephemeral and rejects every pre-existing thumbprint collision"; Pattern = 'X509KeyStorageFlags\]::EphemeralKeySet[\s\S]*preflightThumbprints[\s\S]*-in\s+\$beforeThumbprints[\s\S]*collides with a pre-existing certificate-store entry' },
        @{ Label = "PFX password SecureString is disposed after import"; Pattern = 'Import-PfxCertificate[\s\S]*\$password\.Dispose\(\)[\s\S]*\$password\s*=\s*\$null' },
        @{ Label = "Only the post-import store delta is marked as workflow-owned JSON"; Pattern = 'ConvertTo-Json\s+-InputObject\s+@\(\$normalized\)[\s\S]*ownedCertificates\s*=\s*@\(\$afterCertificates[\s\S]*-notin\s+\$beforeThumbprints[\s\S]*Write-OwnedCertificateMarker\s+-Thumbprints\s+\$ownedThumbprints' },
        @{ Label = "Exactly one owned expected private-key leaf is required"; Pattern = 'expectedLeafCertificates\s*=\s*@\([\s\S]*expectedThumbprint[\s\S]*ownedPrivateKeyCertificates\s*=\s*@\([\s\S]*expectedLeafCertificates\.Count\s+-ne\s+1[\s\S]*ownedPrivateKeyCertificates\.Count\s+-ne\s+1' },
        @{ Label = "Private keys and public chain certificates use distinct cleanup paths"; Pattern = 'storedCertificate\.HasPrivateKey[\s\S]*Remove-Item\s+-Path\s+\$certificatePath\s+-DeleteKey[\s\S]*else\s*\{[\s\S]*Remove-Item\s+-Path\s+\$certificatePath\s+-Force' },
        @{ Label = "Owned certificate JSON and PFX receive unconditional verified cleanup"; Pattern = 'if:\s+always\(\)[\s\S]*win7pos-imported-certs\.json[\s\S]*win7pos-release-signing\.pfx[\s\S]*remainingOwnedCertificates' },
        @{ Label = "Import marker and outcome are independently required and cleaned"; Pattern = 'WIN7POS_IMPORT_STEP_OUTCOME[\s\S]*win7pos-import-outcome\.json[\s\S]*owned certificate marker is missing[\s\S]*import outcome marker is missing[\s\S]*\$markerThumbprints\s*\+\s*\$outcomeOwnedThumbprints\s*\+\s*\$derivedOwnedThumbprints' },
        @{ Label = "Store-delta cleanup requires a structurally valid outcome snapshot"; Pattern = 'outcomePropertyNames[\s\S]*beforeThumbprints[\s\S]*ownedThumbprints[\s\S]*\$outcomeIsValid\s*=\s*\$true[\s\S]*if\s*\(\$outcomeIsValid\)[\s\S]*\$derivedOwnedThumbprints' }
    )) {
    Require-Pattern $required.Label $protectedWorkflow $required.Pattern
}

$protectedRootCoverageCalls = @([regex]::Matches(
        $protectedWorkflow,
        '(?m)^\s+-ArtifactPath\s+@\(\$dist\)\s+`\s*$'))
if ($protectedRootCoverageCalls.Count -ne 2) {
    Fail "Both protected-release integrity metadata stages must cover the full dist root"
}
else { Pass "Both protected-release integrity metadata stages cover the full dist root" }

if ($protectedWorkflow -match '\{40,64\}' -or
    $protectedWorkflow -notmatch "thumbprintPattern\s*=\s*'\^\[0-9A-F\]\{40\}\$'") {
    Fail "Protected signing thumbprints must use the exact 40-hex SHA-1 certificate form"
}
else { Pass "Protected signing uses exact 40-hex certificate thumbprints" }

if ($protectedWorkflow -match 'ownedThumbprints\s*\+=\s*\$expectedThumbprint') {
    Fail "Cleanup must never claim or remove a pre-existing expected certificate"
}
else { Pass "Cleanup is limited to certificates absent from the pre-import snapshot" }

if ($runbook -notmatch 'REAL_PROTECTED_TAG_SIGNING=BLOCKED_EXTERNAL' -or
    $runbook -notmatch 'NON-PRODUCTION' -or
    $runbook -notmatch 'Win7POS_SIGNING_PFX_B64' -or
    $runbook -notmatch 'win7pos-protected-release' -or
    $runbook -notmatch 'WIN7POS_RELEASE_TAG_BYPASS_ACTOR_ID' -or
    $runbook -notmatch 'WIN7POS_RELEASE_TAG_BYPASS_ACTOR_TYPE' -or
    $runbook -notmatch 'workflow_dispatch` tag ref') {
    Fail "Release runbook must preserve the real-signing blocker and manual protected settings"
}
else { Pass "Release runbook keeps production signing BLOCKED_EXTERNAL and documents protected settings" }

if ($signerScript -notmatch 'NONE-NON-PRODUCTION-FIXTURE' -or
    $signerScript -notmatch 'WINDOWS-TRUSTED-PRODUCTION' -or
    $signerScript -notmatch 'Production signing requires a non-loopback HTTPS timestamp endpoint' -or
    $signingFixture -notmatch 'Mode\s+Application[\s\S]*Mode\s+Installer[\s\S]*NON-PRODUCTION two-phase signing wiring') {
    Fail "NON-PRODUCTION two-phase fixture and trusted RFC3161 production policy must remain distinct"
}
else { Pass "Two-phase fixture is explicit while production stays Windows-trusted and RFC3161-only" }

if ($failed) {
    Write-Host ""
    Write-Host "=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
