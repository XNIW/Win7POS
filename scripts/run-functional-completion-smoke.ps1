[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [ValidateSet('x86')][string]$Platform = 'x86',
    [string]$HarnessDirectory = ''
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $HarnessDirectory) {
    $HarnessDirectory = Join-Path $repoRoot "tests\Win7POS.Wpf.UiSmokeHarness\bin\$Platform\$Configuration\net48"
}
$exe = Join-Path $HarnessDirectory 'Win7POS.Wpf.UiSmokeHarness.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Build the x86 WPF harness first.' }

$previousTestEndpoint = $env:WIN7POS_ADMIN_WEB_BASE_URL
try {
$env:WIN7POS_ADMIN_WEB_BASE_URL = 'http://127.0.0.1:9'
$visualData = Join-Path ([IO.Path]::GetTempPath()) ('Win7POS.Functional.Visual.' + [Guid]::NewGuid().ToString('N'))
$visualOutput = $visualData + '-screenshots'
$process = Start-Process -FilePath $exe -ArgumentList @('--data-dir', ('"' + $visualData + '"'), '--ui-ux-final-closeout', '--output-dir', ('"' + $visualOutput + '"')) -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(120000)) {
    Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
    throw "Functional visual regression timed out. Evidence: $visualData"
}
$visualResult = Join-Path $visualOutput 'ui-ux-final-closeout.txt'
if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $visualResult)) {
    if (Test-Path -LiteralPath (Join-Path $visualData 'harness-error.txt')) { Get-Content (Join-Path $visualData 'harness-error.txt') }
    throw "Functional visual regression failed. Evidence: $visualData"
}
Get-Content -LiteralPath $visualResult
if (-not ([IO.File]::ReadAllText($visualResult).StartsWith('PASS'))) { throw "Visual regression did not pass: $visualResult" }
Write-Host "FUNCTIONAL_VISUAL_ARTIFACT=$visualOutput"
$scenarios = @('IMPORT_completion','REFUND_completion','REVERSAL_authorization','F06_reversed_preview_delete_close','F01_export_gate','F07_settings_rollback','HARDWARE_settings_scanner','CUSTOMERDISPLAY_polish','CUSTOMERDISPLAY_completion','STOCK_fractional_input','F02_F03_workflow_stable_identity_batch_dispatcher','F05_same_clock_recovery_restart_active_cart','F08_discount_preview_lifetime','INTEGRATED_login_sale_retry_receipt_restart','LOAD_sync_backup_x86','P109_product_editor_late_render_close','PERF_visual_lifetime_and_public_commands')
foreach ($scenario in $scenarios) {
$dataDir = Join-Path ([IO.Path]::GetTempPath()) ('Win7POS.Functional.' + [Guid]::NewGuid().ToString('N'))
$process = Start-Process -FilePath $exe -ArgumentList @('--data-dir', ('"' + $dataDir + '"'), '--functional-completion-smoke', '--scenario', $scenario) -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(60000)) {
    Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
    throw "Functional completion smoke timed out. Evidence: $dataDir"
}
$result = Join-Path $dataDir 'functional-completion.txt'
if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result }
if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $result)) {
    foreach ($diagnosticName in @('cart-regression-error.txt','harness-error.txt')) {
        $diagnosticPath = Join-Path $dataDir $diagnosticName
        if (Test-Path -LiteralPath $diagnosticPath -PathType Leaf) {
            Write-Host "FUNCTIONAL_FAILURE_DIAGNOSTIC=$diagnosticName"
            Get-Content -LiteralPath $diagnosticPath
        }
    }
    throw "Functional completion smoke failed (exit=$($process.ExitCode)). Evidence: $dataDir"
}
if (-not ([IO.File]::ReadAllText($result).StartsWith('PASS'))) { throw "Functional completion smoke did not pass: $result" }
Write-Host "FUNCTIONAL_COMPLETION_ARTIFACT=$result"

}
}
finally {
    $env:WIN7POS_ADMIN_WEB_BASE_URL = $previousTestEndpoint
}
