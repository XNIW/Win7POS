[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$EvidenceDirectory,

    [string]$Profile = 'asus-staging',

    [string]$DataDirectory =
        'C:\POSData\Win7POS-QA\ProductImagePhaseBAcceptance',

    [string]$DotnetPath = 'C:\Dev\dotnet10\dotnet.exe',

    [switch]$PreflightOnly
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if ($Profile -cnotmatch '^[A-Za-z0-9_-]{3,64}$') {
    throw 'product_image_acceptance_profile_invalid'
}
$script:AcceptanceMutex = [System.Threading.Mutex]::new(
    $false,
    'Global\Win7POS.ProductImagePhaseBAcceptance.v1')
$script:AcceptanceMutexHeld = $false
$script:RunnerTokenVariable =
    'WIN7POS_PRODUCT_IMAGE_ACCEPTANCE_RUNNER_TOKEN'
$script:PreviousRunnerToken =
    [Environment]::GetEnvironmentVariable($script:RunnerTokenVariable)
$script:RunnerTokenActive = $false
$script:RunnerHandshakePath = $null

try {
    try {
        $script:AcceptanceMutexHeld = $script:AcceptanceMutex.WaitOne(0)
    }
    catch [System.Threading.AbandonedMutexException] {
        $script:AcceptanceMutexHeld = $true
    }
    if (-not $script:AcceptanceMutexHeld) {
        throw 'product_image_acceptance_already_running'
    }

function Initialize-RunnerHandshake {
    $path = Join-Path $script:SafeDataDirectory (
        'product-image-acceptance-runner.dpapi')
    if (Test-Path -LiteralPath $path) {
        [IO.File]::Delete($path)
    }
    $token = [byte[]]::new(32)
    $payload = [byte[]]::new(40)
    $protected = $null
    $temporary = $path + '.tmp-' + [Guid]::NewGuid().ToString('N')
    try {
        $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
        try {
            $generator.GetBytes($token)
        }
        finally {
            $generator.Dispose()
        }
        [Text.Encoding]::ASCII.GetBytes('PIB1').CopyTo($payload, 0)
        [BitConverter]::GetBytes([int]$PID).CopyTo($payload, 4)
        $token.CopyTo($payload, 8)
        $protected = [Security.Cryptography.ProtectedData]::Protect(
            $payload,
            $null,
            [Security.Cryptography.DataProtectionScope]::CurrentUser)
        $stream = [IO.FileStream]::new(
            $temporary,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None,
            4096,
            [IO.FileOptions]::WriteThrough)
        try {
            $stream.Write($protected, 0, $protected.Length)
            $stream.Flush($true)
        }
        finally {
            $stream.Dispose()
        }
        [IO.File]::Move($temporary, $path)
        $tokenHex = -join ($token | ForEach-Object { $_.ToString('x2') })
        [Environment]::SetEnvironmentVariable(
            $script:RunnerTokenVariable,
            $tokenHex)
        $script:RunnerTokenActive = $true
        $script:RunnerHandshakePath = $path
    }
    finally {
        if (Test-Path -LiteralPath $temporary) {
            [IO.File]::Delete($temporary)
        }
        if ($null -ne $protected) {
            [Array]::Clear($protected, 0, $protected.Length)
        }
        [Array]::Clear($payload, 0, $payload.Length)
        [Array]::Clear($token, 0, $token.Length)
    }
}

function Assert-ExactSafeDataDirectory {
    param([string]$Path)

    $expected = [System.IO.Path]::GetFullPath(
        'C:\POSData\Win7POS-QA\ProductImagePhaseBAcceptance').TrimEnd('\')
    $resolved = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not [string]::Equals(
            $resolved,
            $expected,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'product_image_acceptance_data_directory_invalid'
    }
    return $resolved
}

function Assert-NoReparsePath {
    param(
        [string]$Path,
        [string]$FailureCode
    )

    $candidate = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $existing = $candidate
    while (-not (Test-Path -LiteralPath $existing -PathType Container)) {
        if (Test-Path -LiteralPath $existing) { throw $FailureCode }
        $existing = [System.IO.Path]::GetDirectoryName($existing)
        if ([string]::IsNullOrWhiteSpace($existing)) { throw $FailureCode }
    }
    $current = [System.IO.DirectoryInfo]::new($existing)
    while ($null -ne $current) {
        if (($current.Attributes -band
                [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw $FailureCode
        }
        $current = $current.Parent
    }
    if (Test-Path -LiteralPath $candidate -PathType Container) {
        $pending = [System.Collections.Generic.Stack[string]]::new()
        $pending.Push($candidate)
        while ($pending.Count -gt 0) {
            Get-ChildItem -LiteralPath $pending.Pop() -Force |
                ForEach-Object {
                    if (($_.Attributes -band
                            [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                        throw $FailureCode
                    }
                    if ($_.PSIsContainer) {
                        $pending.Push($_.FullName)
                    }
                }
        }
    }
    return $candidate
}

function Test-PathOverlap {
    param([string]$Left, [string]$Right)

    $leftFull = [System.IO.Path]::GetFullPath($Left).TrimEnd('\')
    $rightFull = [System.IO.Path]::GetFullPath($Right).TrimEnd('\')
    return [string]::Equals(
            $leftFull,
            $rightFull,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        $leftFull.StartsWith(
            $rightFull + '\',
            [System.StringComparison]::OrdinalIgnoreCase) -or
        $rightFull.StartsWith(
            $leftFull + '\',
            [System.StringComparison]::OrdinalIgnoreCase)
}

function Invoke-AcceptancePhase {
    param(
        [string]$Harness,
        [string]$Phase,
        [int[]]$ExpectedExitCodes
    )

    $arguments = @(
        '--data-dir', ('"' + $script:SafeDataDirectory + '"'),
        '--product-image-staging-acceptance',
        '--profile', ('"' + $Profile + '"'),
        '--acceptance-output', ('"' + $script:SafeEvidenceDirectory + '"'),
        '--acceptance-phase', ('"' + $Phase + '"'))
    $process = Start-Process `
        -FilePath $Harness `
        -ArgumentList $arguments `
        -WindowStyle Hidden `
        -Wait `
        -PassThru
    try {
        $exitCode = $process.ExitCode
    }
    finally {
        $process.Dispose()
    }
    if ($exitCode -notin $ExpectedExitCodes) {
        throw "product_image_acceptance_${Phase}_failed_${exitCode}"
    }
    return $exitCode
}

function Wait-ForCleanupFence {
    param(
        [string]$ReportPath
    )

    if (-not (Test-Path -LiteralPath $ReportPath -PathType Leaf)) {
        throw 'product_image_acceptance_report_missing'
    }
    $report = Get-Content -Raw -LiteralPath $ReportPath | ConvertFrom-Json
    $runStartedAt = [DateTimeOffset]::Parse(
        [string]$report.startedAt,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind)
    $fenceUntil = [DateTimeOffset]::Parse(
        [string]$report.fenceUntil,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind)
    if ($fenceUntil -lt $runStartedAt.AddHours(2).AddMinutes(5)) {
        throw 'product_image_acceptance_fence_too_short'
    }
    if ($fenceUntil -gt $runStartedAt.AddHours(3)) {
        throw 'product_image_acceptance_fence_too_long'
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

function Assert-TerminalCleanupReport {
    param(
        [string]$ReportPath,
        [string]$ExpectedSha
    )

    if (-not (Test-Path -LiteralPath $ReportPath -PathType Leaf)) {
        throw 'product_image_acceptance_terminal_report_missing'
    }
    $terminal = Get-Content -Raw -LiteralPath $ReportPath | ConvertFrom-Json
    if ($terminal.cleanupComplete -ne $true -or
        $terminal.cleanupPending -ne $false -or
        -not [string]::Equals(
            [string]$terminal.exactMainSha,
            $ExpectedSha,
            [System.StringComparison]::Ordinal) -or
        [int]$terminal.dbResiduals -ne 0 -or
        [int]$terminal.storageResiduals -ne 0 -or
        [int]$terminal.activeActorSessionResiduals -ne 0 -or
        [int]$terminal.signedUrlPersistenceCount -ne 0 -or
        $terminal.sharedSnapshotUnchanged -ne $true -or
        $terminal.immutableAuditPreserved -ne $true -or
        $terminal.runProfileRemoved -ne $true) {
        throw 'product_image_acceptance_terminal_result_invalid'
    }
    return $terminal
}

function Assert-TerminalAcceptanceReport {
    param(
        [string]$ReportPath,
        [string]$ExpectedSha
    )

    $terminal = Assert-TerminalCleanupReport `
        -ReportPath $ReportPath `
        -ExpectedSha $ExpectedSha
    if ($terminal.passed -ne $true) {
        throw 'product_image_acceptance_matrix_incomplete'
    }
    return $terminal
}

$script:SafeDataDirectory = Assert-ExactSafeDataDirectory -Path $DataDirectory
$repoRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\..'))
$script:SafeDataDirectory = Assert-NoReparsePath `
    -Path $script:SafeDataDirectory `
    -FailureCode 'product_image_acceptance_data_reparse_point'
$script:SafeEvidenceDirectory = Assert-NoReparsePath `
    -Path $EvidenceDirectory `
    -FailureCode 'product_image_acceptance_evidence_reparse_point'
if (Test-PathOverlap `
        -Left $script:SafeEvidenceDirectory `
        -Right $script:SafeDataDirectory) {
    throw 'product_image_acceptance_evidence_overlaps_data'
}
if (Test-PathOverlap `
        -Left $script:SafeEvidenceDirectory `
        -Right $repoRoot) {
    throw 'product_image_acceptance_evidence_overlaps_repo'
}

if (-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
    throw 'product_image_acceptance_dotnet_missing'
}
if (-not (Test-Path -LiteralPath $repoRoot -PathType Container)) {
    throw 'product_image_acceptance_repo_missing'
}

$branchOutput = git -C $repoRoot branch --show-current
$branch = if ($null -eq $branchOutput) {
    [string]::Empty
}
else {
    ([string]$branchOutput).Trim()
}
$head = (git -C $repoRoot rev-parse HEAD).Trim()
$originMain = (git -C $repoRoot rev-parse origin/main).Trim()
$dirty = git -C $repoRoot status --porcelain
$isExactMainCheckout = $branch -eq 'main' -or
    [string]::IsNullOrWhiteSpace($branch)
if (-not $isExactMainCheckout -or $head -ne $originMain -or $dirty) {
    throw 'product_image_acceptance_exact_main_required'
}
if ($head -notmatch '^[0-9a-f]{40}$') {
    throw 'product_image_acceptance_sha_invalid'
}
if ($PreflightOnly) {
    [pscustomobject]@{
        exactMainSha = $head
        checkoutMode = if ([string]::IsNullOrWhiteSpace($branch)) {
            'detached'
        }
        else {
            'main'
        }
        preflightPassed = $true
    } | ConvertTo-Json -Compress
    return
}

$statePath = Join-Path $script:SafeDataDirectory (
    'product-image-acceptance-state.dpapi')
$priorCheckpoint = Test-Path -LiteralPath $statePath -PathType Leaf
if (-not $priorCheckpoint -and
    (Test-Path -LiteralPath $script:SafeEvidenceDirectory -PathType Container) -and
    $null -ne (Get-ChildItem `
        -LiteralPath $script:SafeEvidenceDirectory `
        -Force | Select-Object -First 1)) {
    throw 'product_image_acceptance_evidence_not_empty'
}
if ((Test-Path -LiteralPath $script:SafeDataDirectory) -and
    -not $priorCheckpoint) {
    [void](Assert-NoReparsePath `
        -Path $script:SafeDataDirectory `
        -FailureCode 'product_image_acceptance_data_reparse_point')
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

Initialize-RunnerHandshake

$reportPath = Join-Path $script:SafeEvidenceDirectory (
    'product-image-staging-result.json')

function Invoke-CheckpointCleanup {
    if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) {
        return $false
    }
    $cleanupExit = Invoke-AcceptancePhase `
        -Harness $harness `
        -Phase 'cleanup' `
        -ExpectedExitCodes @(0, 77)
    if ($cleanupExit -eq 77) {
        Wait-ForCleanupFence -ReportPath $reportPath
        $cleanupExit = Invoke-AcceptancePhase `
            -Harness $harness `
            -Phase 'cleanup' `
            -ExpectedExitCodes @(0)
    }
    if ($cleanupExit -ne 0) {
        throw 'product_image_acceptance_checkpoint_cleanup_incomplete'
    }
    [void](Assert-TerminalCleanupReport `
        -ReportPath $reportPath `
        -ExpectedSha $head)
    return $true
}

if ($priorCheckpoint) {
    try {
        [void](Invoke-CheckpointCleanup)
    }
    catch {
        throw (
            'product_image_acceptance_prior_checkpoint_recovery_failed: ' +
            $_.Exception.Message)
    }
    [void](Assert-TerminalCleanupReport `
        -ReportPath $reportPath `
        -ExpectedSha $head)
    [void](Assert-NoReparsePath `
        -Path $script:SafeDataDirectory `
        -FailureCode 'product_image_acceptance_data_reparse_point')
    Remove-Item -LiteralPath $script:SafeDataDirectory -Recurse -Force
    throw 'product_image_acceptance_prior_checkpoint_recovered_rerun_required'
}

try {
    Invoke-AcceptancePhase `
        -Harness $harness `
        -Phase 'prepare' `
        -ExpectedExitCodes @(75)
    Invoke-AcceptancePhase `
        -Harness $harness `
        -Phase 'resume' `
        -ExpectedExitCodes @(76)
    Invoke-AcceptancePhase `
        -Harness $harness `
        -Phase 'cache-restart' `
        -ExpectedExitCodes @(77)
    Wait-ForCleanupFence -ReportPath $reportPath
    Invoke-AcceptancePhase `
        -Harness $harness `
        -Phase 'cleanup' `
        -ExpectedExitCodes @(0)
}
catch {
    $phaseFailure = $_
    $cleanupFailure = $null
    if (Test-Path -LiteralPath $statePath -PathType Leaf) {
        try {
            [void](Invoke-CheckpointCleanup)
            [void](Assert-NoReparsePath `
                -Path $script:SafeDataDirectory `
                -FailureCode 'product_image_acceptance_data_reparse_point')
            Remove-Item `
                -LiteralPath $script:SafeDataDirectory `
                -Recurse `
                -Force
        }
        catch {
            $cleanupFailure = $_
        }
    }
    elseif (Test-Path -LiteralPath $script:SafeDataDirectory -PathType Container) {
        try {
            [void](Assert-NoReparsePath `
                -Path $script:SafeDataDirectory `
                -FailureCode 'product_image_acceptance_data_reparse_point')
            Remove-Item `
                -LiteralPath $script:SafeDataDirectory `
                -Recurse `
                -Force
        }
        catch {
            $cleanupFailure = $_
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

[void](Assert-TerminalAcceptanceReport `
    -ReportPath $reportPath `
    -ExpectedSha $head)
[void](Assert-NoReparsePath `
    -Path $script:SafeDataDirectory `
    -FailureCode 'product_image_acceptance_data_reparse_point')
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
}
finally {
    if ($script:RunnerTokenActive) {
        [Environment]::SetEnvironmentVariable(
            $script:RunnerTokenVariable,
            $script:PreviousRunnerToken)
    }
    if (-not [string]::IsNullOrWhiteSpace($script:RunnerHandshakePath) -and
        (Test-Path -LiteralPath $script:RunnerHandshakePath -PathType Leaf)) {
        [IO.File]::Delete($script:RunnerHandshakePath)
    }
    if ($script:AcceptanceMutexHeld) {
        $script:AcceptanceMutex.ReleaseMutex()
    }
    $script:AcceptanceMutex.Dispose()
}
