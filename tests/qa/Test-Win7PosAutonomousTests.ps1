#requires -Version 7.4
[CmdletBinding()]
param([switch]$Runtime)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source = Join-Path $repoRoot 'scripts/qa/Invoke-Win7PosAutonomousTests.ps1'
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Autonomous runner syntax errors' }
# Load definitions only: never execute runner top-level, Git, builds, WPF or staging.
$definitions = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $true))
foreach ($definition in $definitions) { . ([scriptblock]::Create($definition.Extent.Text)) }
function Assert-QaTest([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Must-Fail([scriptblock]$Action, [string]$Pattern) {
    $message = ''
    try { & $Action } catch { $message = $_.Exception.Message }
    Assert-QaTest ($message -match $Pattern) "Expected $Pattern, received $message"
}
$root = Join-Path ([IO.Path]::GetTempPath()) ('Win7POS.QA.Test.' + [Guid]::NewGuid().ToString('N'))
foreach ($folder in @([Environment]::GetFolderPath('CommonApplicationData'), [Environment]::GetFolderPath('LocalApplicationData'), [IO.Path]::GetTempPath())) {
    if ($folder) { Must-Fail { Assert-QaOutputRoot (Join-Path $folder 'Win7POS/qa-must-be-refused') $repoRoot } 'qa_output_overlaps_checkout_or_store_data' }
}
$previousDataOverride = $env:WIN7POS_DATA_DIR
try {
    $storeOverride = Join-Path $root 'separate store'
    $env:WIN7POS_DATA_DIR = '  ' + $storeOverride + '  '
    Must-Fail { Assert-QaOutputRoot (Join-Path $storeOverride 'qa-must-be-refused') $repoRoot } 'qa_output_overlaps_checkout_or_store_data'
} finally { $env:WIN7POS_DATA_DIR = $previousDataOverride }
New-Item -ItemType Directory -Path $root | Out-Null
$unicode = Join-Path $root 'spazi e qualità'
Assert-QaTest ((Assert-QaPath $unicode $root) -ceq $unicode) 'Unicode/spaces path changed'
Must-Fail { Assert-QaPath (Join-Path $root '../outside') $root } 'qa_path_outside_root'
Assert-QaTest (-not (Test-QaWithin ($root + '-other') $root)) 'Sibling prefix accepted as owned data'
Must-Fail { Assert-QaPath 'relative-path' } 'qa_path_must_be_absolute'
$smokeWork = New-QaWorkDirectories $repoRoot $root
$smokeFixture = Join-Path $smokeWork.temporaryDirectory 'synthetic/main-diagnostics'
New-Item -ItemType Directory -Path $smokeFixture | Out-Null
$safeSmokeFiles = @('harness-error.txt','bounded-logging-smoke.txt','authorization-lease-smoke.txt.stdout.txt','authorization-lease-smoke.txt.stderr.txt')
foreach ($name in ($safeSmokeFiles + @('synthetic.db','trusted-device-profile.json','trust-store.dat'))) { [IO.File]::WriteAllText((Join-Path $smokeFixture $name),'synthetic') }
$smokeDestination = Join-Path $unicode 'smoke diagnostics'
$exportedSmoke = @(Export-QaSmokeEvidence $smokeWork.workDirectory $smokeDestination)
$exportedFiles = @(Get-ChildItem -LiteralPath $smokeDestination -Recurse -File)
Assert-QaTest ($exportedSmoke.Count -eq $safeSmokeFiles.Count -and $exportedFiles.Count -eq $safeSmokeFiles.Count -and @($exportedFiles | Where-Object Name -cnotin $safeSmokeFiles).Count -eq 0) 'Smoke export copied database, trust files or unknown names'
Assert-QaTest ((Test-Path -LiteralPath (Join-Path $smokeFixture 'synthetic.db')) -and (Test-Path -LiteralPath (Join-Path $smokeFixture 'trusted-device-profile.json'))) 'Smoke export removed source data'
[IO.File]::WriteAllBytes((Join-Path $smokeFixture 'product-image-profile-smoke.txt'), [byte[]]::new(2MB+1))
Must-Fail { Export-QaSmokeEvidence $smokeWork.workDirectory (Join-Path $unicode 'oversize diagnostics') } 'qa_smoke_export_file_size_limit'
if ($IsWindows) {
    $longBase = Join-Path $root ('long-base-' + ('x' * 100))
    Must-Fail { New-QaWorkDirectories $repoRoot $root $longBase } 'qa_temporary_base_too_long'
    Assert-QaTest (-not (Test-Path -LiteralPath $longBase)) 'Overlong temporary base was mutated before preflight failure'
}

$creation = [DateTime]::UtcNow
$process = [pscustomobject]@{ HasExited=$false; Id=42; StartTime=$creation; MainModule=[pscustomobject]@{ FileName=(Join-Path $root 'owned.exe') } }
$process | Add-Member -MemberType ScriptMethod -Name Refresh -Value { }
$identity = @{ pid=42; creationTicks=$creation.Ticks; executable=$process.MainModule.FileName }
Assert-QaTest (Test-QaProcessIdentity $process $identity) 'Owned identity rejected'
$identity.creationTicks++
Assert-QaTest (-not (Test-QaProcessIdentity $process $identity)) 'Reused PID accepted'
$identity.creationTicks--; $identity.executable += '.foreign'
Assert-QaTest (-not (Test-QaProcessIdentity $process $identity)) 'Foreign executable accepted'
$script:imageReads = 0
$pendingImage = [pscustomobject]@{ HasExited=$false; Id=43; StartTime=$creation }
$pendingImage | Add-Member -MemberType ScriptMethod -Name Refresh -Value { }
$pendingImage | Add-Member -MemberType ScriptProperty -Name MainModule -Value {
    $script:imageReads++
    if ($script:imageReads -gt 1) { return [pscustomobject]@{ FileName=(Join-Path $root 'owned.exe') } }
    return $null
}
$observed = Get-QaProcessIdentity $pendingImage (Join-Path $root 'owned.exe')
Assert-QaTest ($script:imageReads -eq 2 -and $observed.pid -eq 43) 'Initial missing MainModule must refresh before identity is accepted'
Assert-QaTest ((Get-QaProcessOutcome $true 0 $false $false).status -ceq 'PASS') 'Successful child rejected'
Assert-QaTest ((Get-QaProcessOutcome $true 7 $false $false).status -ceq 'FAIL') 'Nonzero child passed'
Assert-QaTest ((Get-QaProcessOutcome $true 0 $true $false).status -ceq 'TIMEOUT') 'Late zero exit erased timeout'
Assert-QaTest ((Get-QaProcessOutcome $false $null $false $false).status -ceq 'FAIL') 'Missing process passed'
Assert-QaTest ((Get-QaProcessOutcome $true 0 $false $true).status -ceq 'NOT_RUN') 'Cancellation erased'
Assert-QaTest (-not (Test-QaUiAvailable $true $false)) 'Missing UI accepted'

$state = [pscustomobject]@{ Exists=$false; Valid=$false; BaseUrlHost=''; ProfileBindingSha256='' }
Assert-QaTest (-not (Test-QaStagingProfile $state)) 'Missing credentials accepted'
$state.Exists=$true; $state.Valid=$true; $state.ProfileBindingSha256='a' * 64
$state.BaseUrlHost='production.example.com'
Assert-QaTest (-not (Test-QaStagingProfile $state)) 'Forbidden endpoint accepted'
$state.BaseUrlHost='merchandise-control-admin-web-staging.merchandise-control-admin-web.workers.dev'
Assert-QaTest (Test-QaStagingProfile $state) 'Canonical validated vault summary rejected'

$coreDir = Join-Path $root 'core'; New-Item -ItemType Directory -Path $coreDir | Out-Null
$coreStep = New-QaStep core fake @() @() 1 'trx'
'<TestRun><ResultSummary><Counters total="0" executed="0" passed="0" failed="0" /></ResultSummary></TestRun>' | Set-Content -LiteralPath (Join-Path $coreDir 'core.trx')
Must-Fail { Assert-QaEvidence $coreStep $root } 'zero_tests'
'<TestRun><ResultSummary><Counters total="2" executed="2" passed="2" failed="0" /></ResultSummary></TestRun>' | Set-Content -LiteralPath (Join-Path $coreDir 'core.trx')
Assert-QaEvidence $coreStep $root

$functionalDir = Join-Path $root 'functional'; New-Item -ItemType Directory -Path $functionalDir | Out-Null
$functionalStep = New-QaStep functional fake @() @() 1 'functional'
$functionalPath = Join-Path $functionalDir 'functional-results.json'
@{ schemaVersion=1; results=@() } | ConvertTo-Json | Set-Content -LiteralPath $functionalPath
Must-Fail { Assert-QaEvidence $functionalStep $root } 'incomplete_functional_tests'
$expected = @('FUNCTIONAL_VISUAL','IMPORT_completion','IMPORT_RECOVERY_completion','PRICE_HISTORY_completion','REFUND_completion','REVERSAL_authorization','F06_reversed_preview_delete_close','F01_export_gate','F07_settings_rollback','HARDWARE_settings_scanner','CUSTOMERDISPLAY_polish','CUSTOMERDISPLAY_completion','STOCK_fractional_input','F02_F03_workflow_stable_identity_batch_dispatcher','F05_same_clock_recovery_restart_active_cart','F08_discount_preview_lifetime','INTEGRATED_login_sale_retry_receipt_restart','LOAD_sync_backup_x86','P109_product_editor_late_render_close','PERF_visual_lifetime_and_public_commands')
$records = @($expected | ForEach-Object { [pscustomobject]@{ name=$_; status='PASS'; exitCode=0 } })
@{ schemaVersion=1; results=$records } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $functionalPath
Assert-QaEvidence $functionalStep $root
$records[-1].name='UNKNOWN'
@{ schemaVersion=1; results=$records } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $functionalPath
Must-Fail { Assert-QaEvidence $functionalStep $root } 'functional_scenario_missing'
$records[-1].name=$records[0].name
@{ schemaVersion=1; results=$records } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $functionalPath
Must-Fail { Assert-QaEvidence $functionalStep $root } 'incomplete_functional_tests'

$runtimeDefinition = @($definitions | Where-Object Name -eq 'Invoke-QaProcess')[0].Extent.Text
if ($Runtime) {
    # Only fresh synthetic PowerShell children. No WPF, real credentials or external endpoints.
    $shell = (Get-Process -Id $PID).Path
    $environmentBefore = @{}
    foreach ($key in @('WIN7POS_ADMIN_WEB_BASE_URL','WIN7POS_DATA_DIR','WIN7POS_QA_TEMP_BASE','TEMP','TMP','TMPDIR')) { $environmentBefore[$key] = [Environment]::GetEnvironmentVariable($key) }
    foreach ($case in @(@{ name='success'; command='exit 0'; expected='PASS'; timeout=15 }, @{ name='nonzero'; command='exit 7'; expected='FAIL'; timeout=15 }, @{ name='timeout'; command='Start-Sleep -Seconds 30'; expected='TIMEOUT'; timeout=2 })) {
        $directory = Join-Path $unicode $case.name; New-Item -ItemType Directory -Path $directory | Out-Null
        $result = Invoke-QaProcess -Executable $shell -Arguments @('-NoProfile','-Command',$case.command) -Directory $directory -WorkingDirectory $root -TimeoutSeconds $case.timeout
        Assert-QaTest ($result.status -ceq $case.expected) "Synthetic runtime case $($case.name): $($result.status) / $($result.code) / $($result.errorType)"
        Assert-QaTest ($result.cleanupSucceeded -eq $true -and $result.processRemaining -eq 0) ('Owned process cleanup was not verified: ' + $case.name)
        if ($IsWindows -and $case.name -eq 'timeout') { Assert-QaTest ($result.activeProcessesAtCleanup -ge 2) 'Timeout did not exercise an owned PowerShell descendant' }
        $processReceipt = Get-Content -LiteralPath (Join-Path $directory 'process-result.json') -Raw | ConvertFrom-Json
        Assert-QaTest ($processReceipt.cleanupSucceeded -eq $true -and $processReceipt.processRemaining -eq 0 -and $processReceipt.code -ceq $result.code) 'Process cleanup receipt lost the original outcome or active count'
        if ($null -ne $result.identity) {
            $remaining = Get-Process -Id $result.identity.pid -ErrorAction SilentlyContinue
            if ($null -ne $remaining) {
                try { Assert-QaTest (-not (Test-QaProcessIdentity $remaining $result.identity)) ('Owned process remained: ' + $case.name) }
                finally { $remaining.Dispose() }
            }
        }
    }
    $directory = Join-Path $root 'missing'; New-Item -ItemType Directory -Path $directory | Out-Null
    $result = Invoke-QaProcess -Executable (Join-Path $root 'missing.exe') -Arguments @() -Directory $directory -WorkingDirectory $root -TimeoutSeconds 1
    Assert-QaTest ($result.status -ceq 'FAIL' -and $null -eq $result.identity) 'Missing executable passed or acquired ownership'
    $longOutput = Join-Path $unicode ('evidence-' + ('x' * [Math]::Max(20,180-$unicode.Length)))
    New-Item -ItemType Directory -Path $longOutput | Out-Null
    $environmentProbe = Join-Path $root 'environment-probe.ps1'
    $environmentResult = Join-Path $longOutput 'child-environment.json'
    @'
param($OutputPath)
[ordered]@{ temp=$env:TEMP; tmp=$env:TMP; tmpdir=$env:TMPDIR; runtimeTemp=[IO.Path]::GetTempPath();
    data=$env:WIN7POS_DATA_DIR; baseDirectory=$env:WIN7POS_QA_TEMP_BASE } | ConvertTo-Json |
    Set-Content -LiteralPath $OutputPath -Encoding utf8
'@ | Set-Content -LiteralPath $environmentProbe
    $longResult = Invoke-QaProcess -Executable $shell -Arguments @('-NoProfile','-File',$environmentProbe,$environmentResult) -Directory $longOutput -WorkingDirectory $root -TimeoutSeconds 15
    Assert-QaTest ($longResult.status -ceq 'PASS') ('Long output runtime probe failed: ' + $longResult.code)
    $childEnvironment = Get-Content -LiteralPath $environmentResult -Raw | ConvertFrom-Json
    $workReceipt = Get-Content -LiteralPath (Join-Path $longResult.workDirectory 'qa-work.json') -Raw | ConvertFrom-Json
    Assert-QaTest ($childEnvironment.temp -ceq $longResult.temporaryDirectory -and $childEnvironment.tmp -ceq $longResult.temporaryDirectory -and $childEnvironment.tmpdir -ceq $longResult.temporaryDirectory -and $childEnvironment.runtimeTemp.TrimEnd('\','/') -ceq $longResult.temporaryDirectory.TrimEnd('\','/') -and $childEnvironment.data -ceq $longResult.dataDirectory) 'Child temporary/data environment did not match report locations'
    Assert-QaTest (-not (Test-QaWithin $longResult.temporaryDirectory $longOutput) -and (Test-QaWithin $longResult.temporaryDirectory $longResult.workDirectory) -and (Test-QaWithin $longResult.dataDirectory $longResult.workDirectory)) 'Work directories remain nested below long evidence output'
    Assert-QaTest ($workReceipt.evidenceDirectory -ceq $longOutput -and $workReceipt.workDirectory -ceq $longResult.workDirectory -and $workReceipt.baseDirectory -ceq $childEnvironment.baseDirectory) 'Short work directory lost its run/evidence mapping'
    Assert-QaTest ([IO.Path]::GetFileName($longResult.workDirectory) -cmatch '^W7Q\.[A-Za-z0-9_-]{22}$') 'Work identity must preserve a full 128-bit UUID in a short encoding'
    if ($IsWindows) {
        Assert-QaTest ($longResult.temporaryDirectory.Length -le 60 -and $longResult.temporaryDirectory -ceq $longResult.workDirectory) 'Child temporary directory exceeded the measured customer-display fixture reserve'
        $automationDestination = [IO.Path]::Combine($longResult.temporaryDirectory,'Win7POS.AutoTests',('a' * 32),'new-destination')
        Assert-QaTest ($automationDestination.Length + 104 -lt 240) 'Short TEMP still violates the existing BackupAutomationDestination contract'
    }
    $cancelBefore = Join-Path $root 'cancel-before.flag'; [IO.File]::WriteAllText($cancelBefore,'stop')
    $directory = Join-Path $unicode 'cancel-before'; New-Item -ItemType Directory -Path $directory | Out-Null
    $result = Invoke-QaProcess -Executable $shell -Arguments @('-NoProfile','-Command','exit 0') -Directory $directory -WorkingDirectory $root -TimeoutSeconds 15 -CancellationFile $cancelBefore -Staging $true
    Assert-QaTest ($result.status -ceq 'NOT_RUN' -and $result.code -ceq 'cancelled' -and $null -eq $result.identity) 'Pre-existing cancellation started a staging child'
    Assert-QaTest (-not (Test-Path -LiteralPath (Join-Path $directory 'command.json'))) 'Pre-existing cancellation prepared a command'
    $cancelDuring = Join-Path $root 'cancel-during.flag'
    $cancelScript = Join-Path $root 'cancel-during.ps1'
    @'
param($CancellationFile)
Start-Sleep -Milliseconds 200
[IO.File]::WriteAllText($CancellationFile,'stop')
Start-Sleep -Seconds 30
'@ | Set-Content -LiteralPath $cancelScript
    $directory = Join-Path $unicode 'cancel-during'; New-Item -ItemType Directory -Path $directory | Out-Null
    $result = Invoke-QaProcess -Executable $shell -Arguments @('-NoProfile','-File',$cancelScript,$cancelDuring) -Directory $directory -WorkingDirectory $root -TimeoutSeconds 15 -CancellationFile $cancelDuring
    Assert-QaTest ($result.status -ceq 'NOT_RUN' -and $result.code -ceq 'cancelled' -and $null -ne $result.identity -and $result.elapsedMs -lt 15000) 'Cancellation during supervision was ignored'
    Assert-QaTest ($result.cleanupSucceeded -eq $true -and $result.processRemaining -eq 0) 'Cancelled process Job did not reach zero active processes'
    if ($IsWindows) { Assert-QaTest ($result.activeProcessesAtCleanup -ge 2) 'Cancellation did not exercise an owned PowerShell descendant' }
    $remaining = Get-Process -Id $result.identity.pid -ErrorAction SilentlyContinue
    if ($null -ne $remaining) {
        try { Assert-QaTest (-not (Test-QaProcessIdentity $remaining $result.identity)) 'Cancelled owned process remained' }
        finally { $remaining.Dispose() }
    }
    foreach ($key in $environmentBefore.Keys) { Assert-QaTest ([Environment]::GetEnvironmentVariable($key) -ceq $environmentBefore[$key]) "Caller environment changed: $key" }
}

$script:calls = [Collections.Generic.List[string]]::new()
$mockWorkDirectory = Join-Path $root 'mock-work'
New-Item -ItemType Directory -Path $mockWorkDirectory | Out-Null
[IO.File]::WriteAllText((Join-Path $mockWorkDirectory 'harness-error.txt'),'synthetic')
function Invoke-QaProcess {
    param($Executable,$Arguments,$Directory,$WorkingDirectory,$TimeoutSeconds,$CancellationFile,$Staging)
    $script:calls.Add($Executable)
    $fails=$Executable -in @('functional-completion','cleanup-failure')
    [pscustomobject]@{ status=$(if ($fails) { 'FAIL' } else { 'PASS' }); code='mock_exit'; exitCode=$(if ($fails) { 7 } else { 0 }); identity=$null; elapsedMs=1; workDirectory=(Join-Path $root 'mock-work'); temporaryDirectory=(Join-Path $root 'mock-work/temp'); dataDirectory=(Join-Path $root 'mock-work/data'); cleanupSucceeded=($Executable -ne 'cleanup-failure'); processRemaining=$(if ($Executable -eq 'cleanup-failure') { 1 } else { 0 }); cleanupError=$(if ($Executable -eq 'cleanup-failure') { 'synthetic_job_still_active' } else { $null }) }
}
$windows = @(New-QaPlan 'Windows' $root 'synthetic-pwsh')
Assert-QaTest (@($windows | Where-Object { $_.name -eq 'restore' -and $_.arguments -contains '--locked-mode' }).Count -eq 1) 'Locked restore absent'
Assert-QaTest (@($windows | Where-Object { $_.name -eq 'core' -and $_.timeout -eq 3600 }).Count -eq 1) 'Core timeout must cover the measured 35m56s baseline'
$smokes = @($windows | Where-Object { $_.name -in @('product-image-profile','functional-completion','authorization-lease','bounded-logging','product-paging-dispatcher') })
Assert-QaTest ($smokes.Count -eq 5) 'Canonical Windows smoke missing'
$mapping = @{
    'product-image-profile'='scripts/run-product-image-profile-smoke.ps1'
    'functional-completion'='scripts/run-functional-completion-smoke.ps1'
    'authorization-lease'='scripts/run-authorization-lease-smoke.ps1'
    'bounded-logging'='scripts/run-bounded-logging-smoke.ps1'
    'product-paging-dispatcher'='scripts/run-product-paging-dispatcher-smoke.ps1'
}
foreach ($smoke in $smokes) { Assert-QaTest ($smoke.arguments[2] -ceq $mapping[$smoke.name]) ('Canonical runner mapping changed: ' + $smoke.name) }
Assert-QaTest (@($smokes | Where-Object { ($_.arguments -contains '-KeepData') -ne ($_.name -in @('product-image-profile','authorization-lease')) }).Count -eq 0) 'KeepData was omitted or added to an unsupported runner'
$plan = @((New-QaStep 'build-harness' 'build-harness' @()))
foreach ($step in $smokes) { $step.executable=$step.name; $step.evidence=''; $plan += $step }
$rows = @(Invoke-QaPlan $plan $root $root)
Assert-QaTest (@($rows | Where-Object status -eq 'FAIL').Count -eq 1) 'Expected single functional failure'
Assert-QaTest ($script:calls.Count -eq 6 -and $rows[-1].status -ceq 'PASS') 'Functional FAIL blocked independent smoke'
Must-Fail { Invoke-QaPlan @() $root $root } 'zero_tests'
$dependentPlan = @((New-QaStep 'functional-completion' 'functional-completion' @()), (New-QaStep 'dependent' 'must-not-launch' @() @('functional-completion')), (New-QaStep 'independent' 'independent' @()))
$dependentRows = @(Invoke-QaPlan $dependentPlan $root $root)
Assert-QaTest ($dependentRows[1].status -ceq 'NOT_RUN' -and $dependentRows[2].status -ceq 'PASS') 'Dependencies or independent collection broken'
$cleanupReport = @{ status='FAIL'; runId='cleanup-stop'; sourceSha=('a' * 40); suite='Local'; results=@(); firstError=$null; reportError=$null }
$cleanupPlan = @((New-QaStep 'original-failure' 'functional-completion' @()), (New-QaStep 'cleanup-failure' 'cleanup-failure' @()), (New-QaStep 'independent-after-cleanup-failure' 'must-not-launch' @()))
$callsBeforeCleanup = $script:calls.Count
$cleanupRows = @(Invoke-QaPlan $cleanupPlan $root $root '' $cleanupReport)
Assert-QaTest ($script:calls.Count -eq $callsBeforeCleanup + 2 -and $cleanupRows[2].status -ceq 'NOT_RUN' -and $cleanupRows[2].code -ceq 'qa_previous_process_cleanup_failed') 'Cleanup failure launched an independent next test'
Assert-QaTest ($cleanupRows[1].cleanupSucceeded -eq $false -and $cleanupRows[1].processRemaining -eq 1 -and $cleanupReport.firstError.name -ceq 'original-failure' -and $cleanupReport.firstError.exitCode -eq 7) 'Cleanup failure erased first error or its remaining-process receipt'
$stepBlocked = 'blocked-step-directory'
[IO.File]::WriteAllText((Join-Path $root $stepBlocked),'file prevents directory creation')
$checkpointReport = @{ status='FAIL'; runId='checkpoint'; sourceSha=('a' * 40); suite='Local'; results=@(); firstError=$null; reportError=$null }
$checkpointPlan = @((New-QaStep 'functional-completion' 'functional-completion' @()), (New-QaStep $stepBlocked 'must-not-launch' @()), (New-QaStep 'after-blocked-directory' 'independent' @()))
$checkpointRows = @(Invoke-QaPlan $checkpointPlan $root $root '' $checkpointReport)
Assert-QaTest ($checkpointRows.Count -eq 3 -and $checkpointRows[1].status -ceq 'FAIL' -and $checkpointRows[2].status -ceq 'PASS') 'Step directory failure lost completed rows or blocked independent collection'
$checkpointDisk = Get-Content -LiteralPath (Join-Path $root 'qa-results.json') -Raw | ConvertFrom-Json
Assert-QaTest ($checkpointDisk.status -ceq 'FAIL' -and @($checkpointDisk.results).Count -eq 3 -and $checkpointDisk.firstError.name -ceq 'functional-completion' -and $checkpointDisk.firstError.exitCode -eq 7) 'Progress checkpoint lost original failure'
Assert-QaTest ($checkpointDisk.results[0].workDirectory -ceq (Join-Path $root 'mock-work') -and $checkpointDisk.results[0].temporaryDirectory -ceq (Join-Path $root 'mock-work/temp') -and $checkpointDisk.results[0].dataDirectory -ceq (Join-Path $root 'mock-work/data')) 'Progress report lost short work locations'
$cancel = Join-Path $root 'cancel'; [IO.File]::WriteAllText($cancel,'stop')
$cancelled = @(Invoke-QaPlan @((New-QaStep 'cancelled' 'must-not-launch' @())) $root $root $cancel)
Assert-QaTest ($cancelled[0].status -ceq 'NOT_RUN' -and $cancelled[0].code -ceq 'cancelled') 'Cancellation launched work'
$exportDefinition = @($definitions | Where-Object Name -eq 'Export-QaSmokeEvidence')[0].Extent.Text
function Export-QaSmokeEvidence { param($WorkDirectory,$DestinationDirectory) throw 'qa_synthetic_export_failed' }
$exportFailureReport = @{ status='FAIL'; runId='smoke-export'; sourceSha=('a' * 40); suite='Windows'; results=@(); firstError=$null; reportError=$null }
$exportFailurePlan = @((New-QaStep 'bounded-logging' 'functional-completion' @()), (New-QaStep 'product-paging-dispatcher' 'independent' @()))
$exportFailureRows = @(Invoke-QaPlan $exportFailurePlan $root $root '' $exportFailureReport)
Assert-QaTest ($exportFailureRows[0].status -ceq 'FAIL' -and $exportFailureRows[0].code -ceq 'mock_exit' -and $exportFailureRows[0].exitCode -eq 7 -and $exportFailureRows[0].evidenceExportError.code -ceq 'qa_synthetic_export_failed') 'Smoke export erased the child failure'
Assert-QaTest ($exportFailureRows[1].status -ceq 'FAIL' -and $exportFailureRows[1].code -ceq 'qa_smoke_evidence_export_failed' -and $exportFailureReport.firstError.name -ceq 'bounded-logging' -and $exportFailureReport.firstError.exitCode -eq 7) 'Smoke export failure passed or erased the first failure'
. ([scriptblock]::Create($exportDefinition))

$report = @{ status='FAIL'; runId='synthetic'; sourceSha=('a' * 40); suite='Local'; results=$dependentRows; firstError=@{ code='original_failure' } }
function Set-Content { param($LiteralPath,$Encoding,[Parameter(ValueFromPipeline)]$Value) process { throw 'synthetic_unwritable_report' } }
Must-Fail { Write-QaReports $report $root } 'synthetic_unwritable_report'
Assert-QaTest ($report.firstError.code -ceq 'original_failure') 'Export failure erased first error'
$failedExportReport = @{ status='FAIL'; runId='checkpoint-export'; sourceSha=('a' * 40); suite='Local'; results=@(); firstError=$null; reportError=$null }
$failedExportRows = @(Invoke-QaPlan $dependentPlan $root $root '' $failedExportReport)
Assert-QaTest ($failedExportRows.Count -eq 3 -and $failedExportReport.results.Count -eq 3 -and $failedExportReport.firstError.name -ceq 'functional-completion' -and $failedExportReport.firstError.exitCode -eq 7 -and $failedExportReport.reportError.code -ceq 'qa_report_write_failed' -and $failedExportReport.status -ceq 'FAIL') 'Checkpoint export erased first error or completed rows'
Remove-Item Function:/Set-Content
# Verify dirty source binding without invoking Git or reading a checkout.
$script:mockDiff='first-change'
function Invoke-QaGit {
    param($RepoRoot,$Arguments)
    if ($Arguments[0] -eq 'rev-parse') { return ('a' * 40) }
    if ($Arguments[0] -eq 'diff') { return $script:mockDiff }
    if ($Arguments[0] -eq 'status') { return ' M scripts/synthetic.ps1' }
    if ($Arguments[0] -eq 'ls-files') { return @() }
    throw 'Unexpected Git mock command'
}
$sourceBefore = Get-QaSourceState $root
$script:mockDiff='second-change'
$sourceAfter = Get-QaSourceState $root
Assert-QaTest ($sourceBefore.dirty -and $sourceBefore.trackedDiffSha256 -cne $sourceAfter.trackedDiffSha256 -and $sourceBefore.fingerprintSha256 -cne $sourceAfter.fingerprintSha256) 'Dirty sources were attributed to HEAD alone'
Remove-Item Function:/Invoke-QaGit
# Restore the real function definition so later tests cannot inherit this mock.
. ([scriptblock]::Create($runtimeDefinition))
# The functional runner owns a direct WPF child. Exercise its identity race and
# cleanup only with in-memory objects; never launch WPF from this contract test.
$functionalSource = Join-Path $repoRoot 'scripts/run-functional-completion-smoke.ps1'
$functionalAst = [Management.Automation.Language.Parser]::ParseFile($functionalSource, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Functional runner syntax errors' }
foreach ($name in @('Read-FunctionalChildImage','Get-FunctionalChildImage','Stop-FunctionalChild','New-FunctionalDataRoot','Copy-FunctionalEvidence')) {
    $definition = @($functionalAst.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true))
    Assert-QaTest ($definition.Count -eq 1) "Missing functional identity helper: $name"
    . ([scriptblock]::Create($definition[0].Extent.Text))
}
$functionalBase = if ($env:WIN7POS_QA_TEMP_BASE) { $env:WIN7POS_QA_TEMP_BASE } else { [IO.Path]::GetTempPath() }
$functionalDataRoot = New-FunctionalDataRoot $functionalBase $repoRoot
$functionalData = Join-Path $functionalDataRoot 'case-00'
New-Item -ItemType Directory -Path $functionalData | Out-Null
$functionalEvidence = Join-Path $unicode 'functional evidence'
New-Item -ItemType Directory -Path $functionalEvidence | Out-Null
Assert-QaTest ($functionalData.Length -le 89 -and -not (Test-QaWithin $functionalData $functionalEvidence)) 'Functional SQLite data inherited the report path length'
$functionalDiagnostics = @('functional-completion.txt','measurements.csv','sync-backup-load.json','capture.png')
$functionalPrivateFiles = @('entry.json','pos-trusted-device.json','trusted-device-profile.json','synthetic.db','memory.dmp','token.dat')
foreach ($name in ($functionalDiagnostics + $functionalPrivateFiles)) { [IO.File]::WriteAllText((Join-Path $functionalData $name),'synthetic') }
Copy-FunctionalEvidence $functionalData $functionalEvidence
$functionalExported = @(Get-ChildItem -LiteralPath $functionalEvidence -File)
Assert-QaTest ($functionalExported.Count -eq $functionalDiagnostics.Count -and @($functionalExported | Where-Object Name -cnotin $functionalDiagnostics).Count -eq 0) 'Functional diagnostic export copied trust profiles or unrecognized data'
Assert-QaTest (@($functionalPrivateFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $functionalData $_)) }).Count -eq 0) 'Functional diagnostic export removed private source data'
# Execute only the real export try/catch with a failing copy. No runner entrypoint
# or child process runs: this checks the persisted classification after cleanup.
$functionalCopyDefinition = @($functionalAst.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Copy-FunctionalEvidence' }, $true))[0].Extent.Text
$functionalExportCalls = @($functionalAst.FindAll({ param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Copy-FunctionalEvidence' }, $true))
Assert-QaTest ($functionalExportCalls.Count -eq 1) 'Expected one functional evidence export call'
$functionalExportTry = $functionalExportCalls[0].Parent
while ($null -ne $functionalExportTry -and $functionalExportTry -isnot [Management.Automation.Language.TryStatementAst]) { $functionalExportTry = $functionalExportTry.Parent }
Assert-QaTest ($null -ne $functionalExportTry -and $functionalExportTry.CatchClauses.Count -eq 1) 'Missing functional export error handling'
$functionalExportAction = [scriptblock]::Create($functionalExportTry.Extent.Text)
function Copy-FunctionalEvidence {
    param($DataDirectory,$EvidenceDirectory)
    if ($script:functionalExportShouldFail) { throw 'synthetic_functional_export_failure' }
}
try {
    foreach ($primaryStatus in @('TIMEOUT','FAIL','NOT_RUN','PASS')) {
        foreach ($copyFails in @($true,$false)) {
            $script:functionalExportShouldFail = $copyFails
            $primaryError = if ($primaryStatus -eq 'PASS') { $null } else { 'original_' + $primaryStatus }
            $primaryExitCode = if ($primaryStatus -eq 'PASS') { 0 } elseif ($primaryStatus -eq 'FAIL') { 7 } else { $null }
            $record = [ordered]@{ status=$primaryStatus; exitCode=$primaryExitCode; error=$primaryError; evidenceExportError=$null }
            $firstError = $primaryError
            $cleanupConfirmed = $true; $dataDir = $functionalData; $evidenceDir = $functionalEvidence
            . $functionalExportAction
            $expectedStatus = if ($copyFails -and $primaryStatus -eq 'PASS') { 'FAIL' } else { $primaryStatus }
            $expectedExportError = if ($copyFails) { 'synthetic_functional_export_failure' } else { $null }
            $expectedError = if ($primaryError) { $primaryError } else { $expectedExportError }
            Assert-QaTest ($record.status -ceq $expectedStatus) "Functional export erased primary $primaryStatus (copyFails=$copyFails)"
            Assert-QaTest ($record.evidenceExportError -ceq $expectedExportError) 'Functional export did not retain its separate secondary error'
            Assert-QaTest ($record.error -ceq $expectedError -and $firstError -ceq $expectedError -and $record.exitCode -eq $primaryExitCode) 'Functional export changed the original error or exit code'
        }
    }
} finally { . ([scriptblock]::Create($functionalCopyDefinition)) }
$unboundedFunctionalWaits = @($functionalAst.FindAll({ param($n) $n -is [Management.Automation.Language.InvokeMemberExpressionAst] -and $n.Member.Value -eq 'WaitForExit' -and ($null -eq $n.Arguments -or $n.Arguments.Count -eq 0) }, $true))
Assert-QaTest ($unboundedFunctionalWaits.Count -eq 0) 'Functional supervisor contains an unbounded process/output wait'
Must-Fail { New-FunctionalDataRoot ('C:\' + ('long-temporary-base' * 8)) $repoRoot } 'too long'
$functionalObserver = @($functionalAst.EndBlock.Statements | Where-Object { $_.Extent.Text -like '*Read-FunctionalChildImage $observer*' })
Assert-QaTest ($functionalObserver.Count -eq 1 -and $functionalObserver[0].Extent.StartOffset -lt $functionalAst.Extent.Text.IndexOf('foreach ($name in $cases)')) 'Process observer initialization moved inside the timed WPF cases'
if ($Runtime -and $IsWindows) {
    $own = [Diagnostics.Process]::GetCurrentProcess()
    try { Assert-QaTest ((Read-FunctionalChildImage $own) -ceq $own.MainModule.FileName) 'Native process image differs from owned synthetic host' }
    finally { $own.Dispose() }
    # Same Windows redirection as the functional runner, with a synthetic child
    # only. Both final writes must already be visible after the bounded wait.
    $outputProbe = Join-Path $unicode 'functional-output-probe.ps1'
    @'
for ($i = 0; $i -lt 512; $i++) {
    [Console]::Out.WriteLine('stdout ' + $i)
    [Console]::Error.WriteLine('stderr ' + $i)
}
[Console]::Out.WriteLine('FINAL_STDOUT_SENTINEL')
[Console]::Error.WriteLine('FINAL_STDERR_SENTINEL')
'@ | Set-Content -LiteralPath $outputProbe -Encoding utf8
    $probeOut = Join-Path $unicode 'functional-stdout.log'
    $probeErr = Join-Path $unicode 'functional-stderr.log'
    $probeProcess = Start-Process -FilePath $shell -ArgumentList @('-NoProfile','-File',('"' + $outputProbe + '"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput $probeOut -RedirectStandardError $probeErr
    try {
        $probeCreated = $probeProcess.StartTime.ToUniversalTime().ToString('O')
        $null = $probeProcess.Handle
        Assert-QaTest ($probeProcess.WaitForExit(15000)) 'Synthetic output probe exceeded its finite process budget'
        Assert-QaTest ($probeProcess.ExitCode -eq 0) 'Synthetic output probe failed'
        Assert-QaTest ([IO.File]::ReadAllText($probeOut).EndsWith('FINAL_STDOUT_SENTINEL' + [Environment]::NewLine) -and [IO.File]::ReadAllText($probeErr).EndsWith('FINAL_STDERR_SENTINEL' + [Environment]::NewLine)) 'Bounded Windows wait returned before redirected final output was available'
    } finally {
        try { Stop-FunctionalChild $probeProcess $probeCreated }
        finally { $probeProcess.Dispose() }
    }
}
$script:imageReads = 0
function Read-FunctionalChildImage { param($Process) $script:imageReads++; if ($script:imageReads -eq 1) { return $null }; if ($script:imageReads -eq 2) { return 'wrong-loader-image' }; return 'C:\owned.exe' }
$functionalProcess = [pscustomobject]@{ HasExited=$false; StartTime=[DateTime]::UtcNow; killed=$false }
$functionalProcess | Add-Member ScriptMethod Refresh { }
$functionalProcess | Add-Member ScriptMethod Kill { $this.killed=$true; $this.HasExited=$true }
$functionalProcess | Add-Member ScriptMethod WaitForExit { param($milliseconds) return $true }
Assert-QaTest ((Get-FunctionalChildImage $functionalProcess 'C:\owned.exe') -ceq 'C:\owned.exe') 'Functional image race was not observed to completion'
Stop-FunctionalChild $functionalProcess ($functionalProcess.StartTime.ToUniversalTime().ToString('O'))
Assert-QaTest $functionalProcess.killed 'Functional owned child stranded after an image race'
$functionalProcess.HasExited=$false; $functionalProcess.killed=$false
Must-Fail { Stop-FunctionalChild $functionalProcess ([DateTime]::UtcNow.AddDays(-1).ToString('O')) } 'identity changed'
Assert-QaTest (-not $functionalProcess.killed) 'Functional cleanup targeted a reused creation identity'
Write-Output ('WIN7POS_AUTONOMOUS_TESTS=PASS runtime=' + [bool]$Runtime + ' evidence=' + $root)
