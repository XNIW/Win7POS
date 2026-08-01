[CmdletBinding()]
param(
    [string]$HarnessPath = ''
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ([string]::IsNullOrWhiteSpace($HarnessPath)) {
    $HarnessPath = Join-Path $repoRoot (
        'tests\Win7POS.Wpf.UiSmokeHarness\bin\x86\Release\net48\' +
        'Win7POS.Wpf.UiSmokeHarness.exe')
}
$HarnessPath = [IO.Path]::GetFullPath($HarnessPath)
if (-not (Test-Path -LiteralPath $HarnessPath -PathType Leaf)) {
    throw 'product_image_acceptance_lock_test_harness_missing'
}

$dataPath = 'C:\POSData\Win7POS-QA\ProductImagePhaseBAcceptance'
$evidencePath = Join-Path ([IO.Path]::GetTempPath()) (
    'Win7POS-ProductImageAcceptance-LockTest-' +
    [Guid]::NewGuid().ToString('N'))
$expectedDataPath = [IO.Path]::GetFullPath($dataPath).TrimEnd('\')
$expectedEvidencePath = [IO.Path]::GetFullPath($evidencePath).TrimEnd('\')
$runnerTokenVariable = 'WIN7POS_PRODUCT_IMAGE_ACCEPTANCE_RUNNER_TOKEN'
$previousRunnerToken =
    [Environment]::GetEnvironmentVariable($runnerTokenVariable)

if (Test-Path -LiteralPath $expectedDataPath) {
    throw 'product_image_acceptance_lock_test_data_not_empty'
}
if (Test-Path -LiteralPath $expectedEvidencePath) {
    throw 'product_image_acceptance_lock_test_evidence_not_empty'
}

function Remove-LockTestArtifacts {
    foreach ($target in @(
        @{ Path = $dataPath; Expected = $expectedDataPath },
        @{ Path = $evidencePath; Expected = $expectedEvidencePath })) {
        $resolved = [IO.Path]::GetFullPath([string]$target.Path).TrimEnd('\')
        if (-not [string]::Equals(
                $resolved,
                [string]$target.Expected,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'product_image_acceptance_lock_test_cleanup_target_invalid'
        }
        if (Test-Path -LiteralPath $resolved) {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}

function Invoke-LockProbe {
    param(
        [string]$ExpectedError
    )

    $arguments = @(
        '--data-dir', $dataPath,
        '--product-image-staging-acceptance',
        '--profile', 'definitely-missing-lock-test-profile',
        '--acceptance-output', $evidencePath,
        '--acceptance-phase', 'prepare')
    $process = Start-Process `
        -FilePath $HarnessPath `
        -ArgumentList $arguments `
        -WindowStyle Hidden `
        -Wait `
        -PassThru
    $exitCode = $process.ExitCode
    $process.Dispose()
    if ($exitCode -eq 0) {
        throw 'product_image_acceptance_lock_probe_unexpected_success'
    }
    $errorPath = Join-Path $dataPath 'harness-error.txt'
    if (-not (Test-Path -LiteralPath $errorPath -PathType Leaf)) {
        throw 'product_image_acceptance_lock_probe_error_missing'
    }
    $detail = Get-Content -Raw -LiteralPath $errorPath
    if ($detail -notmatch [regex]::Escape($ExpectedError)) {
        throw (
            'product_image_acceptance_lock_probe_wrong_error: ' +
            $ExpectedError)
    }
    Remove-LockTestArtifacts
}

function Initialize-TestRunnerHandshake {
    [IO.Directory]::CreateDirectory($dataPath) | Out-Null
    $token = [byte[]]::new(32)
    $payload = [byte[]]::new(40)
    $protected = $null
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
        [IO.File]::WriteAllBytes(
            (Join-Path $dataPath 'product-image-acceptance-runner.dpapi'),
            $protected)
        return -join ($token | ForEach-Object { $_.ToString('x2') })
    }
    finally {
        if ($null -ne $protected) {
            [Array]::Clear($protected, 0, $protected.Length)
        }
        [Array]::Clear($payload, 0, $payload.Length)
        [Array]::Clear($token, 0, $token.Length)
    }
}

try {
    [Environment]::SetEnvironmentVariable(
        $runnerTokenVariable,
        ('0' * 64))
    Invoke-LockProbe `
        -ExpectedError 'product_image_acceptance_runner_handshake_missing'
    [Environment]::SetEnvironmentVariable($runnerTokenVariable, $null)

    $orchestrator = [Threading.Mutex]::new(
        $false,
        'Global\Win7POS.ProductImagePhaseBAcceptance.v1')
    [void]$orchestrator.WaitOne()
    try {
        $runnerToken = Initialize-TestRunnerHandshake
        [Environment]::SetEnvironmentVariable(
            $runnerTokenVariable,
            $runnerToken)
        Invoke-LockProbe `
            -ExpectedError 'shared_profile_unavailable'
        [Environment]::SetEnvironmentVariable($runnerTokenVariable, $null)
        Invoke-LockProbe `
            -ExpectedError 'product_image_acceptance_already_running'
    }
    finally {
        $orchestrator.ReleaseMutex()
        $orchestrator.Dispose()
    }

    $phase = [Threading.Mutex]::new(
        $false,
        'Global\Win7POS.ProductImagePhaseBAcceptance.Phase.v1')
    [void]$phase.WaitOne()
    try {
        Invoke-LockProbe `
            -ExpectedError 'product_image_acceptance_phase_already_running'
    }
    finally {
        $phase.ReleaseMutex()
        $phase.Dispose()
    }
}
finally {
    [Environment]::SetEnvironmentVariable(
        $runnerTokenVariable,
        $previousRunnerToken)
    Remove-LockTestArtifacts
}

Write-Host 'Product image acceptance lock probes passed (4/4).'
