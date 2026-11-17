#requires -Version 7.4
[CmdletBinding()]
param([switch]$Runtime)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Import-Module (Join-Path $repoRoot 'scripts/qa/Win7PosHistoricalSalesValidation.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $repoRoot 'scripts/qa/Win7PosQaPublicEvidence.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $repoRoot 'scripts/qa/Win7PosQaDiagnosticProjection.psm1') -Force -DisableNameChecking
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
$expected = @('FUNCTIONAL_VISUAL','OPERATOR_DIALOG_GEOMETRY','AUTHENTICATED_SHELL_UX','HISTORY_IMPORT_GUIDED_UX','HISTORY_WEB_IMPORT_UX','IMPORT_completion','IMPORT_RECOVERY_completion','PRICE_HISTORY_completion','REFUND_completion','REVERSAL_authorization','F06_reversed_preview_delete_close','F01_export_gate','F07_settings_rollback','HARDWARE_settings_scanner','CUSTOMERDISPLAY_polish','CUSTOMERDISPLAY_completion','STOCK_fractional_input','F02_F03_workflow_stable_identity_batch_dispatcher','F05_same_clock_recovery_restart_active_cart','F08_discount_preview_lifetime','INTEGRATED_login_sale_retry_receipt_restart','LOAD_sync_backup_x86','P109_product_editor_late_render_close','PERF_visual_lifetime_and_public_commands')
$records = @($expected | ForEach-Object { [pscustomobject]@{ name=$_; status='PASS'; exitCode=0 } })
@{ schemaVersion=1; results=$records } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $functionalPath
Assert-QaEvidence $functionalStep $root
$withoutGuided = @($records | Where-Object name -cne 'HISTORY_IMPORT_GUIDED_UX')
@{ schemaVersion=1; results=$withoutGuided } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $functionalPath
Must-Fail { Assert-QaEvidence $functionalStep $root } 'incomplete_functional_tests'
$withoutWeb = @($records | Where-Object name -cne 'HISTORY_WEB_IMPORT_UX')
@{ schemaVersion=1; results=$withoutWeb } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $functionalPath
Must-Fail { Assert-QaEvidence $functionalStep $root } 'incomplete_functional_tests'
@{ schemaVersion=1; results=$records } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $functionalPath
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
foreach ($selection in @(@{mode='All';count=9},@{mode='Smoke';count=6},@{mode='CrashProbe';count=6},@{mode='Load';count=7},@{mode='CloudSync';count=6})) {
    $selected=@(New-QaPlan 'Windows' $root 'synthetic-pwsh' $selection.mode)
    $runners=@($selected|Where-Object {$_.depends -contains 'build-harness'})
    Assert-QaTest ($runners.Count -eq $selection.count) ('Incorrect closed history runner count: '+$selection.mode)
    foreach ($step in $selected) {foreach ($dependency in $step.depends) {Assert-QaTest (@($selected|Where-Object name -ceq $dependency).Count -eq 1) 'Selected plan has an unreachable dependency'}}
}
Assert-QaTest (@($windows|Where-Object {$_.name -ceq 'historical-sales-import-crash' -and $_.timeout -eq 360 -and $_.depends.Count -eq 1 -and $_.depends[0] -ceq 'build-harness'}).Count -eq 1) 'B gate depends on C or lacks the 360 second supervisor budget'
Assert-QaTest (@($windows|Where-Object {$_.name -ceq 'historical-sales-import-cloudsync' -and $_.timeout -eq 90 -and $_.depends.Count -eq 1 -and $_.depends[0] -ceq 'build-harness'}).Count -eq 1) 'Cloud continuity must run independently of the legacy smoke and load'
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
foreach ($name in @('Start-FunctionalChild','Complete-FunctionalChildOutput','Close-FunctionalChildOutput','Read-FunctionalChildImage','Get-FunctionalChildImage','Stop-FunctionalChild','New-FunctionalDataRoot','Copy-FunctionalEvidence')) {
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
$functionalDiagnostics = @('functional-completion.txt','measurements.csv','sync-backup-load.json','capture.png','qa-diagnostics.json')
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
    # Same bounded byte-stream capture as the functional runner, with a synthetic child
    # only. Both final writes must already be visible after the bounded wait.
    $outputProbe = Join-Path $unicode 'functional-output-probe.ps1'
    @'
$probeClock = [Diagnostics.Stopwatch]::StartNew()
[Console]::Out.WriteLine('PROBE_BEGIN utc=' + [DateTime]::UtcNow.ToString('O'))
for ($i = 0; $i -lt 512; $i++) {
    [Console]::Out.WriteLine('stdout ' + $i + ' before_write_ms=' + $probeClock.ElapsedMilliseconds)
    [Console]::Error.WriteLine('stderr ' + $i + ' before_write_ms=' + $probeClock.ElapsedMilliseconds)
}
[Console]::Out.WriteLine('FINAL_STDOUT_SENTINEL')
[Console]::Error.WriteLine('FINAL_STDERR_SENTINEL')
'@ | Set-Content -LiteralPath $outputProbe -Encoding utf8
    $probeOut = Join-Path $unicode 'functional-stdout.log'
    $probeErr = Join-Path $unicode 'functional-stderr.log'
    $outputClock = [Diagnostics.Stopwatch]::StartNew()
    $probeOutput = Start-FunctionalChild $shell @('-NoProfile','-File',('"' + $outputProbe + '"')) $probeOut $probeErr
    $probeProcess = $probeOutput.Process
    try {
        $probeCreated = $probeProcess.StartTime.ToUniversalTime().ToString('O')
        $null = $probeProcess.Handle
        $probeExited = $probeProcess.WaitForExit([Math]::Max(1,15000-[int]$outputClock.ElapsedMilliseconds))
        if ($probeExited) { $probeExited = Complete-FunctionalChildOutput $probeOutput (15000-[int]$outputClock.ElapsedMilliseconds) }
        # Snapshot only this bounded synthetic output, before cleanup. A timeout
        # must distinguish startup, partial writes and process exit, not infer
        # a pipe deadlock or disk latency from WaitForExit(false) alone.
        try {
            $probeProcess.Refresh()
            Write-Host ('FUNCTIONAL_OUTPUT_PROBE exited=' + $probeExited + '; pwsh=' + $PSVersionTable.PSVersion + '; elapsed_ms=' + ([DateTime]::UtcNow - $probeProcess.StartTime.ToUniversalTime()).TotalMilliseconds + '; cpu_ms=' + $probeProcess.TotalProcessorTime.TotalMilliseconds)
            foreach ($probeLog in @($probeOut,$probeErr)) {
                $probeStream = [IO.FileStream]::new($probeLog,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
                try {
                    Assert-QaTest ($probeStream.Length -le 65536) 'Synthetic output exceeded diagnostic bound'
                    $probeReader = [IO.StreamReader]::new($probeStream)
                    try { $probeText = $probeReader.ReadToEnd() } finally { $probeReader.Dispose() }
                } finally { $probeStream.Dispose() }
                $probeLines = @($probeText -split '\r?\n' | Where-Object { $_.Length -gt 0 })
                Write-Host ('FUNCTIONAL_OUTPUT_PROBE file=' + [IO.Path]::GetFileName($probeLog) + '; lines=' + $probeLines.Count + '; first=' + ($probeLines | Select-Object -First 1) + '; last=' + ($probeLines | Select-Object -Last 1))
            }
        } catch { Write-Host ('FUNCTIONAL_OUTPUT_PROBE diagnostic_error=' + $_.Exception.Message) }
        Assert-QaTest $probeExited 'Synthetic output probe exceeded its finite process budget'
        Assert-QaTest ($probeProcess.ExitCode -eq 0) 'Synthetic output probe failed'
        Assert-QaTest ([IO.File]::ReadAllText($probeOut).EndsWith('FINAL_STDOUT_SENTINEL' + [Environment]::NewLine) -and [IO.File]::ReadAllText($probeErr).EndsWith('FINAL_STDERR_SENTINEL' + [Environment]::NewLine)) 'Bounded Windows wait returned before redirected final output was available'
        Assert-QaTest (@([IO.File]::ReadAllLines($probeOut)).Count -eq 514 -and @([IO.File]::ReadAllLines($probeErr)).Count -eq 513) 'Buffered capture lost synthetic output lines'
    } finally {
        try {
            Stop-FunctionalChild $probeProcess $probeCreated
            Assert-QaTest (Complete-FunctionalChildOutput $probeOutput 5000) 'Synthetic output cleanup timed out'
        }
        finally {
            try { Close-FunctionalChildOutput $probeOutput }
            finally { $probeProcess.Dispose() }
        }
    }
    $pendingScript = Join-Path $unicode 'functional-pending-output.ps1'
    "[Console]::Out.WriteLine('PARTIAL_SENTINEL'); Start-Sleep -Seconds 30" | Set-Content -LiteralPath $pendingScript -Encoding utf8
    $pendingOutput = Start-FunctionalChild $shell @('-NoProfile','-File',('"' + $pendingScript + '"')) (Join-Path $unicode 'pending-out.log') (Join-Path $unicode 'pending-err.log')
    $pendingCreated = $pendingOutput.Process.StartTime.ToUniversalTime().ToString('O')
    try {
        Assert-QaTest (-not (Complete-FunctionalChildOutput $pendingOutput 100)) 'Pending child output bypassed its finite deadline'
        Assert-QaTest (-not $pendingOutput.Process.HasExited) 'Output completion stopped a live child without ownership cleanup'
    }
    finally {
        try {
            Stop-FunctionalChild $pendingOutput.Process $pendingCreated
            Assert-QaTest (Complete-FunctionalChildOutput $pendingOutput 5000) 'Stopped synthetic output could not drain'
            Assert-QaTest $pendingOutput.Process.HasExited 'Timed out synthetic child survived cleanup'
        }
        finally {
            try { Close-FunctionalChildOutput $pendingOutput }
            finally { $pendingOutput.Process.Dispose() }
        }
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

# Closed historical receipt contracts: safe partial diagnosis is distinct from PASS.
$historyExpected=[pscustomobject]@{runId='synthetic-history';runNonce=('b'*32);sourceSha=('a'*40);sourceFingerprintSha256=('b'*64);harnessSha256=('c'*64);wpfSha256=('d'*64);fixtureVersion='historical-sales-getnet-v1';fixtureSha256=('e'*64)}
function New-SyntheticHistoryReceipt([string]$Mode) {
    $contract=Get-HistoricalSalesQaContract $Mode
    $results=@(foreach($id in $contract.scenarios.Keys){
        [pscustomobject]@{id=$id;rawStatus='PASS';completed=$true;durationMs=1;assertionResults=@(foreach($assertion in $contract.scenarios[$id]){[pscustomobject]@{id=$assertion;status='PASS';expected=$true;actual=$true}})}
    })
    $receipt=[ordered]@{schemaVersion=$contract.schemaVersion;protocolVersion=$contract.protocolVersion;runId=$historyExpected.runId;runNonce=$historyExpected.runNonce;sourceSha=$historyExpected.sourceSha;sourceFingerprintSha256=$historyExpected.sourceFingerprintSha256;harnessSha256=$historyExpected.harnessSha256;wpfSha256=$historyExpected.wpfSha256;fixtureVersion=$historyExpected.fixtureVersion;fixtureSha256=$historyExpected.fixtureSha256;completed=$true;status='PASS';durationMs=1;process=[pscustomobject]@{pid=123;architecture='x86';clr='4.0.30319';os='6.1.7601'};summary=[pscustomobject]@{expected=$results.Count;executed=$results.Count;passed=$results.Count;failed=0;notRun=0;timedOut=0};results=$results}
    if($Mode -eq 'CrashProbe'){$receipt.workerRawExitCode=79;$receipt.workerRawStatus='FAIL';$receipt.verifierExitCode=0}
    return [pscustomobject]$receipt
}
$validHistory=New-SyntheticHistoryReceipt Smoke
Assert-HistoricalSalesQaPass $validHistory Smoke $historyExpected
$validFoundation=New-SyntheticHistoryReceipt CrashProbe
Assert-HistoricalSalesQaPass $validFoundation CrashProbe $historyExpected
$validFoundation.workerRawExitCode=1
Must-Fail {Assert-HistoricalSalesQaPass $validFoundation CrashProbe $historyExpected} 'qa_history_crash_cause_unproven'
$partialHistory=New-SyntheticHistoryReceipt Smoke
$partialHistory.results=@($partialHistory.results|Select-Object -First 3);$partialHistory.summary.executed=3;$partialHistory.summary.passed=3;$partialHistory.completed=$false;$partialHistory.status='TIMEOUT'
$null=ValidateHistoricalEvidenceStructureAndPrivacy $partialHistory Smoke $historyExpected
Must-Fail {Assert-HistoricalSalesQaPass $partialHistory Smoke $historyExpected} 'qa_history_incomplete_tests'
$unsafeHistory=New-SyntheticHistoryReceipt Smoke
$unsafeHistory|Add-Member NoteProperty rawRows 'PRIVATE_SENTINEL'
Must-Fail {ValidateHistoricalEvidenceStructureAndPrivacy $unsafeHistory Smoke $historyExpected} 'qa_history_receipt_privacy_rejected'
$wrongHistory=New-SyntheticHistoryReceipt Smoke;$wrongHistory.runNonce='wrong'
Must-Fail {ValidateHistoricalEvidenceStructureAndPrivacy $wrongHistory Smoke $historyExpected} 'qa_history_identity_mismatch'
$missingAssert=New-SyntheticHistoryReceipt Smoke;$missingAssert.results[0].assertionResults=@()
Must-Fail {Assert-HistoricalSalesQaPass $missingAssert Smoke $historyExpected} 'qa_history_incomplete_assertions'
$duplicateHistory=New-SyntheticHistoryReceipt Smoke;$duplicateHistory.results[1].id=$duplicateHistory.results[0].id
Must-Fail {ValidateHistoricalEvidenceStructureAndPrivacy $duplicateHistory Smoke $historyExpected} 'qa_history_scenario_invalid'
$duplicatesPath=Join-Path $root 'duplicate-history.json'
[IO.File]::WriteAllText($duplicatesPath,'{"schemaVersion":1,"schemaVersion":2}')
Must-Fail {Read-HistoricalSalesQaJson $duplicatesPath} 'qa_history_duplicate_json_key'
$timeReceipt=New-SyntheticHistoryReceipt Smoke
$timeReceipt|Add-Member NoteProperty startedAtUtc '2026-11-04T23:41:03.0267112+00:00'
$timeReceipt|Add-Member NoteProperty completedAtUtc '2026-11-04T23:41:04.1267112+00:00'
$timePath=Join-Path $root 'timestamp-history.json';$timeReceipt|ConvertTo-Json -Depth 16|Set-Content -LiteralPath $timePath
$timeRoundTrip=Read-HistoricalSalesQaJson $timePath
Assert-QaTest ($timeRoundTrip.startedAtUtc -is [string] -and $timeRoundTrip.startedAtUtc -ceq '2026-11-04T23:41:03.0267112+00:00') 'Receipt timestamp text or submillisecond process identity changed during JSON parsing'
$null=ValidateHistoricalEvidenceStructureAndPrivacy $timeRoundTrip Smoke $historyExpected
# Exercise the exact JsonDocument compatibility path on the current host;
# this is not a claim that a separate PowerShell 7.4 runtime was executed.
$compatPath=Join-Path $root 'strict-json-compatibility.json'
[IO.File]::WriteAllText($compatPath,'{"creationTimeUtc":"2026-11-04T23:41:03.0267112Z","large":9007199254740993,"real":1.25,"empty":[],"one":[null],"nested":[[]],"nothing":null,"flag":true}')
$compat=Read-HistoricalSalesQaJson $compatPath
Assert-QaTest ($compat.creationTimeUtc -is [string] -and $compat.creationTimeUtc -ceq '2026-11-04T23:41:03.0267112Z' -and [DateTimeOffset]::Parse($compat.creationTimeUtc).UtcTicks -eq ([DateTime]::new(2026,11,4,23,41,3,[DateTimeKind]::Utc).Ticks+267112L)) 'Strict reader changed exact seven-digit UTC creation timestamp'
Assert-QaTest ($compat.large -is [long] -and $compat.large -eq 9007199254740993L -and $compat.real -is [double] -and $compat.real -eq 1.25) 'Strict reader lost Int64 precision or real numeric type'
Assert-QaTest ($compat.empty -is [array] -and $compat.empty.Count -eq 0 -and $compat.one -is [array] -and $compat.one.Count -eq 1 -and $null -eq $compat.one[0] -and $compat.nested -is [array] -and $compat.nested.Count -eq 1 -and $compat.nested[0] -is [array] -and $compat.nested[0].Count -eq 0 -and $null -eq $compat.nothing -and $compat.flag -is [bool] -and $compat.flag) 'Strict reader collapsed arrays/null or changed boolean type'
[IO.File]::WriteAllText($compatPath,'{"scope":1,"Scope":2}')
Must-Fail {Read-HistoricalSalesQaJson $compatPath} 'qa_history_duplicate_json_key'
[IO.File]::WriteAllText($compatPath,'{"scope":1,"scope":2}')
Must-Fail {Read-HistoricalSalesQaJson $compatPath} 'qa_history_duplicate_json_key'
[IO.File]::WriteAllText($compatPath,'{"large":9223372036854775808}')
Must-Fail {Read-HistoricalSalesQaJson $compatPath} 'qa_history_integer_out_of_range'
[IO.File]::WriteAllText($compatPath,'{"real":1e400}')
Must-Fail {Read-HistoricalSalesQaJson $compatPath} 'qa_history_invalid_numeric'
[IO.File]::WriteAllText($compatPath,('['*18)+'0'+(']'*18))
Must-Fail {Read-HistoricalSalesQaJson $compatPath} 'qa_history_receipt_depth'
. (Join-Path $PSScriptRoot 'Test-HistoricalSalesMeasurement.ps1') -DefinitionsOnly
$loadHistory=New-HistoricalSalesMeasurementFixture
foreach($field in $historyExpected.PSObject.Properties.Name){$loadHistory.$field=$historyExpected.$field}
Assert-HistoricalSalesQaPass $loadHistory Load $historyExpected
$loadHistory.samples[3].dueTicks+=10
Must-Fail {Assert-HistoricalSalesQaPass $loadHistory Load $historyExpected} 'qa_history_sample_gap'
$loadHistory.samples[3].dueTicks-=10;$loadHistory.environmentValid=$false
Must-Fail {Assert-HistoricalSalesQaPass $loadHistory Load $historyExpected} 'qa_history_measurement_incomplete'
$fakePayload=Join-Path $root 'not-a-net48-payload';$null=New-Item -ItemType Directory -Path $fakePayload
[IO.File]::WriteAllText((Join-Path $fakePayload 'Win7POS.Wpf.exe'),'non-executable synthetic fixture')
Must-Fail {New-QaNet48PayloadBundle $fakePayload (Join-Path $root 'must-not-publish-payload') ('a'*40)} 'qa_payload_x86_required'
Assert-QaTest (-not(Test-Path -LiteralPath (Join-Path $root 'must-not-publish-payload'))) 'Invalid target payload was published'

# Packaging-only synthetic PE markers and opaque DLL bytes; this fixture does
# not claim runtime qualification. Negatives recompute all archive digests, so
# an internally consistent manifest cannot conceal the original missing facade.
$completePayload=Join-Path $root 'synthetic-net48-payload';$null=New-Item -ItemType Directory -Path $completePayload
$runtimePayloadFiles=@(
    'Win7POS.Core.dll','Win7POS.Data.dll','ClosedXML.dll','ClosedXML.Parser.dll','Dapper.dll',
    'DocumentFormat.OpenXml.dll','DocumentFormat.OpenXml.Framework.dll','ExcelDataReader.dll','ExcelDataReader.DataSet.dll',
    'ExcelNumberFormat.dll','Microsoft.Bcl.AsyncInterfaces.dll','Microsoft.Bcl.HashCode.dll','Microsoft.Data.Sqlite.dll',
    'RBush.dll','SixLabors.Fonts.dll','System.Buffers.dll','System.Drawing.Common.dll','System.IO.Packaging.dll',
    'System.Memory.dll','System.Numerics.Vectors.dll','System.Runtime.CompilerServices.Unsafe.dll',
    'System.Text.Encoding.CodePages.dll','System.Threading.Tasks.Extensions.dll','System.ValueTuple.dll',
    'e_sqlite3.dll','SQLitePCLRaw.batteries_v2.dll','SQLitePCLRaw.core.dll','SQLitePCLRaw.provider.e_sqlite3.dll',
    'zxing.dll','zxing.presentation.dll','ZXing.Windows.Compatibility.dll'
)
foreach($name in $runtimePayloadFiles){[IO.File]::WriteAllText((Join-Path $completePayload $name),'synthetic runtime packaging fixture')}
$peBytes=[byte[]]::new(512);$peBytes[0]=0x4d;$peBytes[1]=0x5a
[BitConverter]::GetBytes([int]64).CopyTo($peBytes,0x3c)
[BitConverter]::GetBytes([int]0x4550).CopyTo($peBytes,64)
[BitConverter]::GetBytes([uint16]0x14c).CopyTo($peBytes,68)
[Text.Encoding]::UTF8.GetBytes('.NETFramework,Version=v4.8').CopyTo($peBytes,128)
foreach($name in @('Win7POS.Wpf.exe','Win7POS.Wpf.UiSmokeHarness.exe')){
    [IO.File]::WriteAllBytes((Join-Path $completePayload $name),$peBytes)
    [IO.File]::Copy((Join-Path $repoRoot 'src/Win7POS.Wpf/App.config'),(Join-Path $completePayload ($name+'.config')))
}
$null=New-Item -ItemType Directory -Path (Join-Path $completePayload 'Assets')
[IO.File]::WriteAllText((Join-Path $completePayload 'Assets/sii_qrcode.png'),'synthetic packaging fixture')
$null=New-Item -ItemType Directory -Path (Join-Path $completePayload 'fixtures')
Copy-Item -LiteralPath (Join-Path $repoRoot 'tests/fixtures/historical-sales-getnet-v1') -Destination (Join-Path $completePayload 'fixtures') -Recurse
$payloadPositive=Join-Path $root 'payload-complete'
$positiveManifest=New-QaNet48PayloadBundle $completePayload $payloadPositive ('a'*40)
Assert-QaTest (@($positiveManifest.files|Where-Object path -ceq 'System.ValueTuple.dll').Count -eq 1) 'Complete QA payload omitted the facade'
$null=Test-QaNet48PayloadBundle $payloadPositive ('a'*40)
function Set-SyntheticQaPayloadEntry([string]$Directory,[string]$EntryName,[AllowNull()][object]$Replacement){
    $manifestPath=Join-Path $Directory 'payload-manifest.json'
    $manifest=Read-HistoricalSalesQaJson $manifestPath
    $zipPath=Join-Path $Directory 'Win7POS.QA-net48-x86.zip'
    $archive=[IO.Compression.ZipFile]::Open($zipPath,[IO.Compression.ZipArchiveMode]::Update)
    try{
        $archive.GetEntry($EntryName).Delete()
        $manifest.files=@($manifest.files|Where-Object path -cne $EntryName)
        if($null -ne $Replacement){
            $entry=$archive.CreateEntry($EntryName);$stream=$entry.Open();$bytes=[Text.Encoding]::UTF8.GetBytes($Replacement)
            try{$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}
            $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
            $manifest.files+=@{path=$EntryName;bytes=$bytes.Length;sha256=$hash}
        }
    }finally{$archive.Dispose()}
    $manifest.zipBytes=(Get-Item -LiteralPath $zipPath).Length
    $manifest.zipSha256=(Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifest|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $manifestPath
}
$payloadMissing=Join-Path $root 'payload-missing-facade';$null=New-QaNet48PayloadBundle $completePayload $payloadMissing ('a'*40)
Set-SyntheticQaPayloadEntry $payloadMissing 'System.ValueTuple.dll' $null
Must-Fail {Test-QaNet48PayloadBundle $payloadMissing ('a'*40)} 'qa_payload_required_file_missing'
foreach($configName in @('Win7POS.Wpf.exe.config','Win7POS.Wpf.UiSmokeHarness.exe.config')){
    $payloadRedirect=Join-Path $root ('payload-missing-redirect-'+$configName)
    $null=New-QaNet48PayloadBundle $completePayload $payloadRedirect ('a'*40)
    Set-SyntheticQaPayloadEntry $payloadRedirect $configName '<configuration />'
    Must-Fail {Test-QaNet48PayloadBundle $payloadRedirect ('a'*40)} 'qa_payload_valuetuple_redirect_invalid'
}
[IO.File]::Delete((Join-Path $completePayload 'System.ValueTuple.dll'))
$missingOutput=Join-Path $root 'must-not-publish-missing-facade'
Must-Fail {New-QaNet48PayloadBundle $completePayload $missingOutput ('a'*40)} 'qa_payload_required_file_missing'
Assert-QaTest (-not(Test-Path -LiteralPath $missingOutput)) 'Incomplete QA source was published'

# Current-run-only publication: no raw transcript, paths, command or error object.
$publicRaw=Join-Path $root 'private-run';$null=New-Item -ItemType Directory -Path $publicRaw
$publicParent=Join-Path $root 'public-runs'
$publicReport=[pscustomobject]@{runId='synthetic-public-one';sourceSha=('a'*40);sourceState=[pscustomobject]@{fingerprintSha256=('b'*64)};suite='Windows';status='FAIL';firstError=[pscustomobject]@{name='core';code='mock_exit';path='PRIVATE_SENTINEL';error='PRIVATE_SENTINEL'};results=@([pscustomobject]@{name='core';status='FAIL';code='mock_exit';exitCode=7;elapsedMs=1;error='PRIVATE_SENTINEL';workDirectory='PRIVATE_SENTINEL';tests=@()})}
$bundle=Publish-QaPublicBundle $publicReport $publicRaw $publicParent
$null=Test-QaPublicBundle $bundle.directory $publicReport.runId
$zip=[IO.Compression.ZipFile]::OpenRead($bundle.zipPath)
try{foreach($entry in $zip.Entries){$reader=[IO.StreamReader]::new($entry.Open());try{Assert-QaTest (-not $reader.ReadToEnd().Contains('PRIVATE_SENTINEL')) 'Public ZIP leaked a raw failure or path'}finally{$reader.Dispose()}}}finally{$zip.Dispose()}
Must-Fail {Publish-QaPublicBundle $publicReport $publicRaw $publicParent} 'qa_public_collision'
Must-Fail {Test-QaPublicBundle $bundle.directory 'synthetic-another-run'} 'qa_public_bundle_invalid'
$secondReport=$publicReport|ConvertTo-Json -Depth 10|ConvertFrom-Json
$secondReport.runId='synthetic-public-two'
$unsafeDiagnostic=[pscustomobject]@{stepId='core';code='assertion_failed';expected=$true;actual='PRIVATE_SENTINEL';rawRows='PRIVATE_SENTINEL'}
Must-Fail {Publish-QaPublicBundle $secondReport $publicRaw $publicParent -Diagnostics @($unsafeDiagnostic)} 'qa_public_diagnostic_privacy_rejected'
Assert-QaTest (-not(Test-Path -LiteralPath (Join-Path $publicParent $secondReport.runId))) 'Failed second projection published an incomplete pair'
$null=Test-QaPublicBundle $bundle.directory $publicReport.runId
$consoleWriter=[IO.StringWriter]::new();$previousError=[Console]::Error
try{[Console]::SetError($consoleWriter);Write-QaPublicConsoleEvent -RunId 'PRIVATE_SENTINEL' -Suite Windows -StepId 'PRIVATE_SENTINEL' -Code 'PRIVATE_SENTINEL'}finally{[Console]::SetError($previousError)}
Assert-QaTest (-not$consoleWriter.ToString().Contains('PRIVATE_SENTINEL') -and $consoleWriter.ToString().Contains('qa_unclassified_failure')) 'Public console copied an untrusted fallback field'
$consoleWriter=[IO.StringWriter]::new();$previousError=[Console]::Error
try{[Console]::SetError($consoleWriter);Write-QaPublicConsoleEvent -RunId 'synthetic-fail-code' -Suite Windows -StepId public-evidence -Status FAIL -Code none -PublicationStatus FAIL -PublicationCode none}finally{[Console]::SetError($previousError)}
$consoleDto=$consoleWriter.ToString()|ConvertFrom-Json
Assert-QaTest ($consoleDto.code -ceq 'qa_step_failed' -and $consoleDto.publicationCode -ceq 'qa_public_bundle_failed') 'FAIL console event carried code none'
$consoleWriter=[IO.StringWriter]::new();$previousError=[Console]::Error
try{[Console]::SetError($consoleWriter);Write-QaPublicConsoleEvent -RunId 'synthetic-primary-gates' -Suite Windows -StepId public-evidence -Status FAIL -Code process_exit -PrimaryStepId gates -PublicationStatus PASS}finally{[Console]::SetError($previousError)}
$consoleDto=$consoleWriter.ToString()|ConvertFrom-Json
Assert-QaTest ($consoleDto.rawStatus -ceq 'FAIL' -and $consoleDto.code -ceq 'process_exit' -and $consoleDto.primaryStepId -ceq 'gates' -and $consoleDto.publicationStatus -ceq 'PASS' -and $consoleDto.publicationCode -ceq 'none') 'Successful publication hid the suite primary failure or reported publication failure'

# A malformed typed source is withheld as a partial diagnostic; it cannot
# suppress a usable current-run report or copy its raw sentinel into the ZIP.
$typedRoot=Join-Path $root 'typed-origin';$null=New-Item -ItemType Directory -Path $typedRoot
$typedReport=[pscustomobject]@{runId='synthetic-typed-one';sourceSha=('a'*40);sourceState=[pscustomobject]@{fingerprintSha256=('b'*64)};suite='Windows';status='FAIL';firstError=$null;results=@([pscustomobject]@{name='functional-completion';status='FAIL';code='mock_exit';workDirectory=$typedRoot})}
$harnessPath=Join-Path $repoRoot 'tests/Win7POS.Wpf.UiSmokeHarness/bin/x86/Release/net48/Win7POS.Wpf.UiSmokeHarness.exe'
$harnessHash=if(Test-Path -LiteralPath $harnessPath){(Get-FileHash -LiteralPath $harnessPath -Algorithm SHA256).Hash.ToLowerInvariant()}else{$null}
$typedEvent=[pscustomobject]@{sequence=0;stepId='functional-completion';scenarioId='IMPORT_completion';assertionId='QA_FunctionalCompletionSmoke_L1';status='FAIL';expected=2;actual=1;checkpoint='assertion';code='assertion_failed'}
$typedEnvelope=[pscustomobject]@{schemaVersion='win7pos-qa-diagnostics-v1';runId=$typedReport.runId;sourceFingerprintSha256=('b'*64);harnessSha256=$harnessHash;diagnosticCompleteness='complete';results=@($typedEvent)}
$typedPath=Join-Path $typedRoot 'qa-diagnostics.json'
$typedEnvelope|ConvertTo-Json -Depth 10|Set-Content -LiteralPath $typedPath
$readEvents=@(Get-QaTypedDiagnostics $typedReport $repoRoot)
Assert-QaTest ($readEvents.Count -eq 1 -and $readEvents[0].expected -eq 2 -and $readEvents[0].actual -eq 1 -and $readEvents[0].diagnosticCompleteness -ceq 'complete') 'Typed expected/actual or checkpoint was discarded'
$typedEvent|Add-Member NoteProperty rawRows 'PRIVATE_SENTINEL'
$typedEnvelope|ConvertTo-Json -Depth 10|Set-Content -LiteralPath $typedPath
$readEvents=@(Get-QaTypedDiagnostics $typedReport $repoRoot)
Assert-QaTest ($readEvents.Count -eq 1 -and $readEvents[0].status -ceq 'NOT_RUN' -and $readEvents[0].diagnosticCompleteness -ceq 'partial') 'Unsafe producer did not retain an explicit partial diagnostic'
$typedBundle=Publish-QaPublicBundle $typedReport $publicRaw $publicParent -Diagnostics $readEvents
$zip=[IO.Compression.ZipFile]::OpenRead($typedBundle.zipPath)
try{$entry=$zip.GetEntry('diagnostics.public.json');$reader=[IO.StreamReader]::new($entry.Open());try{$projectionText=$reader.ReadToEnd();Assert-QaTest (-not $projectionText.Contains('PRIVATE_SENTINEL') -and $projectionText.Contains('partial')) 'Unsafe typed source leaked or claimed complete'}finally{$reader.Dispose()}}finally{$zip.Dispose()}
$typedEnvelope.runId='synthetic-stale';$typedEnvelope.results=@($typedEvent|Select-Object * -ExcludeProperty rawRows)
$typedEnvelope|ConvertTo-Json -Depth 10|Set-Content -LiteralPath $typedPath
$readEvents=@(Get-QaTypedDiagnostics $typedReport $repoRoot)
Assert-QaTest ($readEvents[0].diagnosticCompleteness -ceq 'partial' -and $null -eq $readEvents[0].assertionId) 'Stale run typed diagnostics were accepted'

# UI observations are source-bound identifiers, not arbitrary literal values
# elsewhere on the assertion line. Both extraction and ZIP projection retain
# their identities while unknown/wrong-scenario observations remain private.
function New-LiteralUiObservation([string]$Stem,[string]$Scenario,[string]$Id,[string]$SourcePattern,[int]$Sequence) {
    $sourceLines=[IO.File]::ReadAllLines((Join-Path $repoRoot ('tests/Win7POS.Wpf.UiSmokeHarness/'+$Stem+'.cs')))
    $matching=@(for($i=0;$i -lt $sourceLines.Length;$i++){if($sourceLines[$i].Contains($SourcePattern)){$i+1}})
    Assert-QaTest ($matching.Count -eq 1) 'UI observation test source is ambiguous'
    return [pscustomobject]@{sequence=$Sequence;stepId='functional-completion';scenarioId=$Scenario;assertionId=('QA_'+$Stem+'_L'+$matching[0]);observationId=$Id;status='PASS';expected=$true;actual=$true;checkpoint='assertion';code='none'}
}
$literalReport=$typedReport|ConvertTo-Json -Depth 10|ConvertFrom-Json;$literalReport.runId='synthetic-literal-ui'
$literalEvents=@(
    (New-LiteralUiObservation 'HistoricalImportGuidedUxSmoke' 'HISTORY_IMPORT_GUIDED_UX' 'retry_requires_explicit_command' 'Check("retry_requires_explicit_command",' 0)
    (New-LiteralUiObservation 'HistoricalImportGuidedUxSmoke' 'HISTORY_WEB_IMPORT_UX' 'web_prepare_pending_is_terminal_without_commit' 'Check("web_prepare_pending_is_terminal_without_commit",' 1)
    (New-LiteralUiObservation 'OperatorDialogGeometrySmoke' 'OPERATOR_DIALOG_GEOMETRY' 'geometry_matrix_complete' 'observationId:"geometry_matrix_complete"' 2)
    (New-LiteralUiObservation 'OperatorDialogGeometrySmoke' 'OPERATOR_DIALOG_GEOMETRY' 'geometry_qualified_review_ready' ',"geometry_qualified_review_ready");' 3)
)
$literalEnvelope=[pscustomobject]@{schemaVersion='win7pos-qa-diagnostics-v1';runId=$literalReport.runId;sourceFingerprintSha256=('b'*64);harnessSha256=$harnessHash;diagnosticCompleteness='complete';results=$literalEvents}
$literalEnvelope|ConvertTo-Json -Depth 10|Set-Content -LiteralPath $typedPath
$literalProjected=@(Get-QaTypedDiagnostics $literalReport $repoRoot)
Assert-QaTest ($literalProjected.Count -eq 4 -and @($literalProjected|Where-Object diagnosticCompleteness -cne 'complete').Count -eq 0) 'Known UI observation details were discarded'
Assert-QaTest (($literalProjected.observationId -join '|') -ceq ($literalEvents.observationId -join '|')) 'UI observation identity changed during extraction'
$literalBundle=Publish-QaPublicBundle $literalReport $publicRaw $publicParent -Diagnostics $literalProjected
$zip=[IO.Compression.ZipFile]::OpenRead($literalBundle.zipPath)
try{$reader=[IO.StreamReader]::new($zip.GetEntry('diagnostics.public.json').Open());try{$literalReadback=$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()}}finally{$zip.Dispose()}
Assert-QaTest ($literalReadback.diagnosticCompleteness -ceq 'complete' -and ($literalReadback.results.observationId -join '|') -ceq ($literalEvents.observationId -join '|')) 'UI observation details did not survive public ZIP publication'
foreach($bad in @('unknown','wrong_line','wrong_scenario','working','queued')){
    $rejectedEvent=$literalEvents[0]|ConvertTo-Json -Depth 5|ConvertFrom-Json
    switch($bad){
        'unknown' {$rejectedEvent.observationId='private_sentinel'}
        'wrong_line' {$rejectedEvent.assertionId='QA_HistoricalImportGuidedUxSmoke_L1'}
        'wrong_scenario' {$rejectedEvent.scenarioId='IMPORT_completion'}
        'working' {$rejectedEvent=New-LiteralUiObservation 'HistoricalImportGuidedUxSmoke' 'HISTORY_IMPORT_GUIDED_UX' 'working' 'Check("transfer_failure_has_terminal_outcome",' 0}
        'queued' {$rejectedEvent=New-LiteralUiObservation 'HistoricalImportGuidedUxSmoke' 'HISTORY_WEB_IMPORT_UX' 'queued' 'Check("verified_web_sources_need_no_original_disk_read",' 0}
    }
    $literalEnvelope.results=@($rejectedEvent);$literalEnvelope|ConvertTo-Json -Depth 10|Set-Content -LiteralPath $typedPath
    $withheld=@(Get-QaTypedDiagnostics $literalReport $repoRoot)
    Assert-QaTest ($withheld.Count -eq 1 -and $withheld[0].diagnosticCompleteness -ceq 'partial' -and $withheld[0].status -ceq 'NOT_RUN') 'Unbound UI observation was accepted'
    $literalReport.runId='synthetic-literal-reject-'+$bad.Replace('_','-')
    Must-Fail {Publish-QaPublicBundle $literalReport $publicRaw $publicParent -Diagnostics @($rejectedEvent)} 'qa_public_diagnostic_privacy_rejected'
    $literalReport.runId='synthetic-literal-ui'
}

# Visual evidence is a closed registry of actual synthetic view regions. A
# missing capture remains NOT_RUN; no generated bitmap substitutes for a view.
$visualPath=Join-Path $publicRaw 'qa-visuals.json'
function New-SyntheticVisualEnvelope {
    [pscustomobject]@{schemaVersion='win7pos-qa-visuals-v1';registryVersion='synthetic-history-visuals-v1';runId=$historyExpected.runId;runNonce=$historyExpected.runNonce;sourceFingerprintSha256=$historyExpected.sourceFingerprintSha256;harnessSha256=$historyExpected.harnessSha256;fixtureSha256=$historyExpected.fixtureSha256;captures=@(Get-QaSyntheticVisualCatalog|ForEach-Object {[pscustomobject]@{id=$_.id;scenarioId=$_.scenarioId;capturePoint=$_.capturePoint;textRegistryId=$_.textRegistryId;status='NOT_RUN';code='unavailable';width=0;height=0;nativeFixture=$false}})}
}
$visuals=New-SyntheticVisualEnvelope;$visuals|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $visualPath
$safeVisuals=Read-QaSyntheticVisualEvidence $visualPath $historyExpected $typedRoot
Assert-QaTest (@($safeVisuals.files).Count -eq 0 -and @($safeVisuals.captures|Where-Object status -ceq 'NOT_RUN').Count -eq 3) 'Unavailable views produced synthetic PASS/image'
$visuals.runId='synthetic-stale';$visuals|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $visualPath
Must-Fail {Read-QaSyntheticVisualEvidence $visualPath $historyExpected $typedRoot} 'qa_public_visual_identity_invalid'
$visuals=New-SyntheticVisualEnvelope;$visuals.captures[0].capturePoint='PRIVATE_SENTINEL';$visuals|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $visualPath
Must-Fail {Read-QaSyntheticVisualEvidence $visualPath $historyExpected $typedRoot} 'qa_public_visual_registry_invalid'
$visuals=New-SyntheticVisualEnvelope;$visuals.captures[0]|Add-Member NoteProperty renderedText 'PRIVATE_SENTINEL';$visuals|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $visualPath
Must-Fail {Read-QaSyntheticVisualEvidence $visualPath $historyExpected $typedRoot} 'qa_public_visual_privacy_rejected'
$visuals=New-SyntheticVisualEnvelope;$visuals.captures[2].status='PASS';$visuals.captures[2].code='none'
foreach($binding in @{nativeFixture=$true;shopId='10000000-0000-4000-8000-000000003019';fixtureToken=('a'*32);textSha256=('f'*64);relativePath='qa-visual-import_summary.png';pngSha256=('a'*64);width=100;height=40}.GetEnumerator()){$visuals.captures[2]|Add-Member NoteProperty $binding.Key $binding.Value -Force}
$visuals|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $visualPath
Must-Fail {Read-QaSyntheticVisualEvidence $visualPath $historyExpected $typedRoot} 'qa_public_visual_provenance_invalid'
$visualReport=$publicReport|ConvertTo-Json -Depth 10|ConvertFrom-Json;$visualReport.runId=$historyExpected.runId
$visualBundle=Publish-QaPublicBundle $visualReport $publicRaw $publicParent -VisualEvidence $safeVisuals
$zip=[IO.Compression.ZipFile]::OpenRead($visualBundle.zipPath)
try{Assert-QaTest (@($zip.Entries|Where-Object FullName -like '*.png').Count -eq 0) 'NOT_RUN visual exported an image';$reader=[IO.StreamReader]::new($zip.GetEntry('visuals.public.json').Open());try{$text=$reader.ReadToEnd();Assert-QaTest ($text.Contains('partial') -and -not $text.Contains('PRIVATE_SENTINEL')) 'Visual projection claimed complete or disclosed unknown text'}finally{$reader.Dispose()}}finally{$zip.Dispose()}

# Execute the actual runner finalizer with report/public writers deliberately
# failing. The primary private error must survive, and stderr must remain DTO-only.
$outerTry=@($ast.EndBlock.Statements|Where-Object {$_ -is [Management.Automation.Language.TryStatementAst]})[-1]
$finalizer=[scriptblock]::Create(($outerTry.Finally.Statements|ForEach-Object {$_.Extent.Text}) -join "`n")
$savedReportWriter=${function:Write-QaReports}
function Write-QaReports {param($Report,[string]$Directory) throw 'PRIVATE_SENTINEL report write'}
function Publish-QaPublicBundle {param($Report,$RawDirectory,$PublicParent,$HistoricalEvidence,$Diagnostics) throw 'PRIVATE_SENTINEL projection write'}
$report=[ordered]@{runId='synthetic-finalizer';sourceSha=('a'*40);sourceState=$null;suite='Windows';status='FAIL';firstError=@{name='core';code='mock_exit';error='PRIVATE_SENTINEL';path='PRIVATE_SENTINEL'};results=@();reportError=$null}
$locked=$false;$mutex=$null;$runDirectory=$publicRaw;$runId=$report.runId;$Suite='Windows';$plan=@();$exitCode=0
$consoleWriter=[IO.StringWriter]::new();$previousError=[Console]::Error
try{[Console]::SetError($consoleWriter);. $finalizer}finally{[Console]::SetError($previousError);${function:Write-QaReports}=$savedReportWriter;Remove-Item Function:Publish-QaPublicBundle}
Assert-QaTest ($report.firstError.code -ceq 'mock_exit' -and $report.firstError.error -ceq 'PRIVATE_SENTINEL' -and $exitCode -eq 1) 'Report failure replaced the primary error or passed'
Assert-QaTest (-not $consoleWriter.ToString().Contains('PRIVATE_SENTINEL') -and $consoleWriter.ToString().Contains('qa_report_write_failed') -and $consoleWriter.ToString().Contains('qa_public_bundle_failed')) 'Actual finalizer disclosed raw firstError/path or lost secondary failure'
$finalizerEvents=@($consoleWriter.ToString() -split '\r?\n'|Where-Object {$_}|ForEach-Object {$_|ConvertFrom-Json})
Assert-QaTest (@($finalizerEvents|Where-Object {$_.rawStatus -ceq 'FAIL' -and $_.code -ceq 'none'}).Count -eq 0 -and @($finalizerEvents|Where-Object {$_.publicationStatus -ceq 'FAIL' -and $_.primaryStepId -ceq 'core' -and $_.code -ceq 'mock_exit'}).Count -eq 1) 'Actual finalizer replaced the primary diagnosis with a publication error'

# All native WinExe historical children use the retained waited helper with
# one residual body deadline. No native call operator/LASTEXITCODE path remains.
$historyWrapper=Join-Path $repoRoot 'scripts/run-historical-sales-import-smoke.ps1'
$historyAst=[Management.Automation.Language.Parser]::ParseFile($historyWrapper,[ref]$tokens,[ref]$parseErrors)
Assert-QaTest ($parseErrors.Count -eq 0) 'Historical wrapper syntax errors'
$historySource=[IO.File]::ReadAllText($historyWrapper)
Assert-QaTest ($historySource -notmatch '(?m)^\s*&\s*\$harness\b|\$LASTEXITCODE' -and $historySource.Contains('Invoke-HistoryWorker') -and $historySource.Contains('historical-sales-wrapper-error.private.txt')) 'Historical wrapper still races native GUI exit or drops the private cause'
$historyFunctions=@($historyAst.FindAll({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst]},$true))
foreach($name in @('ConvertTo-HistoryProcessArgument','Get-HistoryWorkerPhase','Get-HistoryRemainingMilliseconds','Invoke-HistoryWorker')){. ([scriptblock]::Create(@($historyFunctions|Where-Object Name -ceq $name)[0].Extent.Text))}
$clock=[Diagnostics.Stopwatch]::StartNew();$contract=@{bodySeconds=60};$harness='C:\synthetic harness.exe';$output=$publicRaw
$phaseSpent=@{'checkpoint-report'=0L}
$script:historyWaitedCalls=[Collections.Generic.List[object]]::new()
$script:historyWaitedDuration=0L
function Invoke-Win7PosWaitedProcess {param($FilePath,$ArgumentList,$TimeoutMilliseconds,$EvidenceDirectory)$script:historyWaitedCalls.Add(@{file=$FilePath;arguments=$ArgumentList;timeout=$TimeoutMilliseconds});if($script:historyWaitedDuration){$clock.ElapsedMilliseconds+=$script:historyWaitedDuration};[pscustomobject]@{Started=$true;ExitCode=79;TimedOut=$false;Cancelled=$false;LaunchError=$null;CleanupSucceeded=$true;OrphanRemaining=$false}}
function Write-HistoryCheckpoint {param($Receipt,$Path)}
try{
    $observed=Invoke-HistoryWorker -Arguments @('--data-dir','C:\synthetic data\','--nonce','synthetic') -Name crash-probe
    Assert-QaTest ($observed -eq 79 -and $script:historyWaitedCalls.Count -eq 1 -and $script:historyWaitedCalls[0].timeout -gt 0 -and $script:historyWaitedCalls[0].timeout -le 60000 -and $script:historyWaitedCalls[0].arguments[1] -ceq '"C:\synthetic data\\"') 'Historical worker did not await expected exit with quoted unchanged D and residual budget'
    $contract.bodySeconds=0
    Must-Fail {Invoke-HistoryWorker -Arguments @() -Name crash-probe} 'qa_history_worker_deadline_exhausted'
    Assert-QaTest ($script:historyWaitedCalls.Count -eq 1) 'Expired historical deadline launched a child'
    $clock=[pscustomobject]@{ElapsedMilliseconds=0L};$contract.bodySeconds=330;$phaseSpent=@{'checkpoint-report'=0L}
    foreach($phase in @(@{name='crash-probe';limit=90000},@{name='crash-verify';limit=90000},@{name='HIST_B_COMMIT_AUTHORITY';limit=25000},@{name='HIST_B_PROCESS_ADMISSION';limit=30000},@{name='process-preparation';limit=120000},@{name='process-probes';limit=120000},@{name='HIST_B_ENTRYPOINT_AUTHORITY';limit=120000},@{name='HIST_B_BACKUP_RESTORE';limit=45000},@{name='checkpoint-report';limit=20000})){
        Assert-QaTest ((Get-HistoryRemainingMilliseconds $phase.name) -eq $phase.limit) ('Historical phase budget changed: '+$phase.name)
    }
    $script:historyWaitedDuration=60000
    $null=Invoke-HistoryWorker -Arguments @() -Name crash-probe
    Assert-QaTest ((Get-HistoryRemainingMilliseconds 'crash-verify') -eq 30000) 'Crash verifier reset the shared ninety-second hot-journal budget'
    $script:historyWaitedDuration=20000
    $null=Invoke-HistoryWorker -Arguments @() -Name crash-verify
    Assert-QaTest ((Get-HistoryRemainingMilliseconds 'crash-probe') -eq 10000) 'Repeated crash phase reset its elapsed allowance'
    $beforeCalls=$script:historyWaitedCalls.Count;$phaseSpent['commit-authority']=25000
    Must-Fail {Invoke-HistoryWorker -Arguments @() -Name HIST_B_COMMIT_AUTHORITY} 'qa_history_worker_deadline_exhausted'
    Assert-QaTest ($script:historyWaitedCalls.Count -eq $beforeCalls) 'Expired commit phase launched a child'
    $phaseSpent['entrypoint']=100000;$phaseSpent['admission']=10000
    $script:historyWaitedDuration=10000;$null=Invoke-HistoryWorker -Arguments @() -Name HIST_B_PROCESS_ADMISSION
    Assert-QaTest ((Get-HistoryRemainingMilliseconds 'HIST_B_ENTRYPOINT_AUTHORITY') -eq 20000) 'Admission consumed or reset the separate entrypoint phase allowance'
    $phaseSpent['backup-restore']=0;$clock.ElapsedMilliseconds=325000;$script:historyWaitedDuration=6000
    Must-Fail {Invoke-HistoryWorker -Arguments @() -Name HIST_B_BACKUP_RESTORE} 'qa_history_worker_deadline_exhausted'
    Assert-QaTest ($script:historyWaitedCalls[$script:historyWaitedCalls.Count-1].timeout -eq 5000) 'Worker ignored the smaller shared 330-second residual deadline'
    $clock.ElapsedMilliseconds=0;$phaseSpent['hot-journal']=0;$script:historyWaitedDuration=90001
    Must-Fail {Invoke-HistoryWorker -Arguments @() -Name crash-probe} 'qa_history_worker_deadline_exhausted'
    Assert-QaTest ($script:historyWaitedCalls[$script:historyWaitedCalls.Count-1].timeout -eq 90000) 'Reserved exit79 was accepted past the hot-journal subbudget'
    $clock.ElapsedMilliseconds=0;$phaseSpent['checkpoint-report']=20000
    Must-Fail {Get-HistoryRemainingMilliseconds 'checkpoint-report'} 'qa_history_worker_deadline_exhausted'
    Must-Fail {Get-HistoryRemainingMilliseconds 'unregistered-phase'} 'qa_history_worker_phase_invalid'
}finally{Remove-Variable clock,contract,harness,output,phaseSpent -ErrorAction SilentlyContinue;Remove-Item Function:Invoke-Win7PosWaitedProcess;Remove-Item Function:Write-HistoryCheckpoint}
Write-Output ('WIN7POS_AUTONOMOUS_TESTS=PASS runtime=' + [bool]$Runtime)
