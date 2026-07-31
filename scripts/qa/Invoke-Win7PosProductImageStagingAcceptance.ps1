[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$EvidenceDirectory,

    [string]$Profile = 'asus-staging',

    [string]$DataDirectory =
        'C:\POSData\Win7POSProductImagePhaseBAcceptance',

    [string]$DotnetPath = 'C:\Dev\dotnet10\dotnet.exe'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Assert-ExactSafeDataDirectory {
    param([string]$Path)

    $expected = [System.IO.Path]::GetFullPath(
        'C:\POSData\Win7POSProductImagePhaseBAcceptance').TrimEnd('\')
    $resolved = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not [string]::Equals(
            $resolved,
            $expected,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'product_image_acceptance_data_directory_invalid'
    }
    return $resolved
}

function Invoke-AcceptancePhase {
    param(
        [string]$Harness,
        [string]$Phase,
        [int]$ExpectedExitCode
    )

    & $Harness `
        --data-dir $script:SafeDataDirectory `
        --product-image-staging-acceptance `
        --profile $Profile `
        --acceptance-output $script:SafeEvidenceDirectory `
        --acceptance-phase $Phase
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne $ExpectedExitCode) {
        throw "product_image_acceptance_${Phase}_failed_${exitCode}"
    }
}

function Wait-ForCleanupFence {
    param(
        [string]$ReportPath,
        [DateTimeOffset]$StartedAt
    )

    if (-not (Test-Path -LiteralPath $ReportPath -PathType Leaf)) {
        throw 'product_image_acceptance_report_missing'
    }
    $report = Get-Content -Raw -LiteralPath $ReportPath | ConvertFrom-Json
    $fenceUntil = [DateTimeOffset]::Parse(
        [string]$report.fenceUntil,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind)
    if ($fenceUntil -lt $StartedAt.AddHours(2).AddMinutes(5)) {
        throw 'product_image_acceptance_fence_too_short'
    }
    $cleanupAt = $fenceUntil.AddSeconds(15)
    while ([DateTimeOffset]::UtcNow -lt $cleanupAt) {
        $remaining = $cleanupAt - [DateTimeOffset]::UtcNow
        $sleepSeconds = [Math]::Max(
            1,
            [Math]::Min(45, [int][Math]::Ceiling($remaining.TotalSeconds)))
        Write-Host (
            'TASK150_CLEANUP_WAIT remaining_seconds=' +
            [Math]::Ceiling($remaining.TotalSeconds))
        Start-Sleep -Seconds $sleepSeconds
    }
}

$script:SafeDataDirectory = Assert-ExactSafeDataDirectory -Path $DataDirectory
$script:SafeEvidenceDirectory = [System.IO.Path]::GetFullPath(
    $EvidenceDirectory)
$repoRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\..'))

if (-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
    throw 'product_image_acceptance_dotnet_missing'
}
if (-not (Test-Path -LiteralPath $repoRoot -PathType Container)) {
    throw 'product_image_acceptance_repo_missing'
}

$branch = (git -C $repoRoot branch --show-current).Trim()
$head = (git -C $repoRoot rev-parse HEAD).Trim()
$originMain = (git -C $repoRoot rev-parse origin/main).Trim()
$dirty = git -C $repoRoot status --porcelain
if ($branch -ne 'main' -or $head -ne $originMain -or $dirty) {
    throw 'product_image_acceptance_exact_main_required'
}
if ($head -notmatch '^[0-9a-f]{40}$') {
    throw 'product_image_acceptance_sha_invalid'
}

if (Test-Path -LiteralPath $script:SafeDataDirectory) {
    Remove-Item -LiteralPath $script:SafeDataDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $script:SafeDataDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $script:SafeEvidenceDirectory -Force |
    Out-Null

$env:WIN7POS_ACCEPTANCE_EXACT_MAIN_SHA = $head
$env:WIN7POS_PRODUCT_IMAGE_STORAGE_ORIGIN =
    'https://jpgoimipbothfgkokyvm.supabase.co/'
$env:DOTNET_ROOT_X86 = 'C:\Dev\_codex-tools\dotnet-runtime-10-x86'

& $DotnetPath restore `
    (Join-Path $repoRoot 'tests\Win7POS.Wpf.UiSmokeHarness\Win7POS.Wpf.UiSmokeHarness.csproj') `
    --locked-mode `
    -p:Platform=x86 `
    -p:PlatformTarget=x86
if ($LASTEXITCODE -ne 0) {
    throw 'product_image_acceptance_harness_restore_failed'
}

& $DotnetPath build `
    (Join-Path $repoRoot 'tests\Win7POS.Wpf.UiSmokeHarness\Win7POS.Wpf.UiSmokeHarness.csproj') `
    -c Release `
    -p:Platform=x86 `
    -p:PlatformTarget=x86 `
    --no-restore
if ($LASTEXITCODE -ne 0) {
    throw 'product_image_acceptance_harness_build_failed'
}

$harness = Join-Path $repoRoot (
    'tests\Win7POS.Wpf.UiSmokeHarness\bin\x86\Release\net48\' +
    'Win7POS.Wpf.UiSmokeHarness.exe')
if (-not (Test-Path -LiteralPath $harness -PathType Leaf)) {
    throw 'product_image_acceptance_harness_missing'
}

$reportPath = Join-Path $script:SafeEvidenceDirectory (
    'product-image-staging-result.json')
$startedAt = [DateTimeOffset]::UtcNow
try {
    Invoke-AcceptancePhase `
        -Harness $harness `
        -Phase 'prepare' `
        -ExpectedExitCode 75
    Invoke-AcceptancePhase `
        -Harness $harness `
        -Phase 'resume' `
        -ExpectedExitCode 76
    Invoke-AcceptancePhase `
        -Harness $harness `
        -Phase 'cache-restart' `
        -ExpectedExitCode 77
    Wait-ForCleanupFence -ReportPath $reportPath -StartedAt $startedAt
    Invoke-AcceptancePhase `
        -Harness $harness `
        -Phase 'cleanup' `
        -ExpectedExitCode 0
}
catch {
    $phaseFailure = $_
    $cleanupFailure = $null
    if (Test-Path -LiteralPath $reportPath -PathType Leaf) {
        $checkpoint = Get-Content -Raw -LiteralPath $reportPath |
            ConvertFrom-Json
        if ($checkpoint.cleanupComplete -ne $true) {
            try {
                Wait-ForCleanupFence `
                    -ReportPath $reportPath `
                    -StartedAt $startedAt
                Invoke-AcceptancePhase `
                    -Harness $harness `
                    -Phase 'cleanup' `
                    -ExpectedExitCode 0
            }
            catch {
                $cleanupFailure = $_
            }
        }
    }
    if ($null -ne $cleanupFailure) {
        throw (
            'product_image_acceptance_failed_and_cleanup_requires_recovery: ' +
            $phaseFailure.Exception.Message + '; cleanup=' +
            $cleanupFailure.Exception.Message)
    }
    throw $phaseFailure
}

$final = Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json
if ($final.passed -ne $true -or
    [int]$final.dbResiduals -ne 0 -or
    [int]$final.storageResiduals -ne 0 -or
    [int]$final.activeActorSessionResiduals -ne 0 -or
    [int]$final.signedUrlPersistenceCount -ne 0 -or
    $final.sharedSnapshotUnchanged -ne $true -or
    $final.immutableAuditPreserved -ne $true -or
    $final.runProfileRemoved -ne $true) {
    throw 'product_image_acceptance_terminal_result_invalid'
}

Remove-Item -LiteralPath $script:SafeDataDirectory -Recurse -Force
if (Test-Path -LiteralPath $script:SafeDataDirectory) {
    throw 'product_image_acceptance_local_cleanup_failed'
}

[pscustomobject]@{
    exactMainSha = $head
    passed = $true
    evidenceDirectory = $script:SafeEvidenceDirectory
    localRuntimeResiduals = 0
    dbResiduals = 0
    storageResiduals = 0
    activeActorSessionResiduals = 0
} | ConvertTo-Json -Depth 4
