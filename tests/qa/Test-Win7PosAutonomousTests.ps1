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
New-Item -ItemType Directory -Path $root | Out-Null
$unicode = Join-Path $root 'spazi e qualità'
Assert-QaTest ((Assert-QaPath $unicode $root) -ceq $unicode) 'Unicode/spaces path changed'
Must-Fail { Assert-QaPath (Join-Path $root '../outside') $root } 'qa_path_outside_root'
Assert-QaTest (-not (Test-QaWithin ($root + '-other') $root)) 'Sibling prefix accepted as owned data'
Must-Fail { Assert-QaPath 'relative-path' } 'qa_path_must_be_absolute'

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
    foreach ($key in @('WIN7POS_ADMIN_WEB_BASE_URL','WIN7POS_DATA_DIR','TEMP','TMP','TMPDIR')) { $environmentBefore[$key] = [Environment]::GetEnvironmentVariable($key) }
    foreach ($case in @(@{ name='success'; command='exit 0'; expected='PASS'; timeout=15 }, @{ name='nonzero'; command='exit 7'; expected='FAIL'; timeout=15 }, @{ name='timeout'; command='Start-Sleep -Seconds 30'; expected='TIMEOUT'; timeout=2 })) {
        $directory = Join-Path $unicode $case.name; New-Item -ItemType Directory -Path $directory | Out-Null
        $result = Invoke-QaProcess -Executable $shell -Arguments @('-NoProfile','-Command',$case.command) -Directory $directory -WorkingDirectory $root -TimeoutSeconds $case.timeout
        Assert-QaTest ($result.status -ceq $case.expected) "Synthetic runtime case $($case.name): $($result.status) / $($result.code) / $($result.errorType)"
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
    $remaining = Get-Process -Id $result.identity.pid -ErrorAction SilentlyContinue
    if ($null -ne $remaining) {
        try { Assert-QaTest (-not (Test-QaProcessIdentity $remaining $result.identity)) 'Cancelled owned process remained' }
        finally { $remaining.Dispose() }
    }
    foreach ($key in $environmentBefore.Keys) { Assert-QaTest ([Environment]::GetEnvironmentVariable($key) -ceq $environmentBefore[$key]) "Caller environment changed: $key" }
}

$script:calls = [Collections.Generic.List[string]]::new()
function Invoke-QaProcess {
    param($Executable,$Arguments,$Directory,$WorkingDirectory,$TimeoutSeconds,$CancellationFile,$Staging)
    $script:calls.Add($Executable)
    [pscustomobject]@{ status=$(if ($Executable -eq 'functional-completion') { 'FAIL' } else { 'PASS' }); code='mock_exit'; exitCode=$(if ($Executable -eq 'functional-completion') { 7 } else { 0 }); identity=$null; elapsedMs=1 }
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
$plan = @((New-QaStep 'build-harness' 'build-harness' @()))
foreach ($step in $smokes) { $step.executable=$step.name; $step.evidence=''; $plan += $step }
$rows = @(Invoke-QaPlan $plan $root $root)
Assert-QaTest (@($rows | Where-Object status -eq 'FAIL').Count -eq 1) 'Expected single functional failure'
Assert-QaTest ($script:calls.Count -eq 6 -and $rows[-1].status -ceq 'PASS') 'Functional FAIL blocked independent smoke'
Must-Fail { Invoke-QaPlan @() $root $root } 'zero_tests'
$dependentPlan = @((New-QaStep 'functional-completion' 'functional-completion' @()), (New-QaStep 'dependent' 'must-not-launch' @() @('functional-completion')), (New-QaStep 'independent' 'independent' @()))
$dependentRows = @(Invoke-QaPlan $dependentPlan $root $root)
Assert-QaTest ($dependentRows[1].status -ceq 'NOT_RUN' -and $dependentRows[2].status -ceq 'PASS') 'Dependencies or independent collection broken'
$stepBlocked = 'blocked-step-directory'
[IO.File]::WriteAllText((Join-Path $root $stepBlocked),'file prevents directory creation')
$checkpointReport = @{ status='FAIL'; runId='checkpoint'; sourceSha=('a' * 40); suite='Local'; results=@(); firstError=$null; reportError=$null }
$checkpointPlan = @((New-QaStep 'functional-completion' 'functional-completion' @()), (New-QaStep $stepBlocked 'must-not-launch' @()), (New-QaStep 'after-blocked-directory' 'independent' @()))
$checkpointRows = @(Invoke-QaPlan $checkpointPlan $root $root '' $checkpointReport)
Assert-QaTest ($checkpointRows.Count -eq 3 -and $checkpointRows[1].status -ceq 'FAIL' -and $checkpointRows[2].status -ceq 'PASS') 'Step directory failure lost completed rows or blocked independent collection'
$checkpointDisk = Get-Content -LiteralPath (Join-Path $root 'qa-results.json') -Raw | ConvertFrom-Json
Assert-QaTest ($checkpointDisk.status -ceq 'FAIL' -and @($checkpointDisk.results).Count -eq 3 -and $checkpointDisk.firstError.name -ceq 'functional-completion' -and $checkpointDisk.firstError.exitCode -eq 7) 'Progress checkpoint lost original failure'
$cancel = Join-Path $root 'cancel'; [IO.File]::WriteAllText($cancel,'stop')
$cancelled = @(Invoke-QaPlan @((New-QaStep 'cancelled' 'must-not-launch' @())) $root $root $cancel)
Assert-QaTest ($cancelled[0].status -ceq 'NOT_RUN' -and $cancelled[0].code -ceq 'cancelled') 'Cancellation launched work'

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
foreach ($name in @('Read-FunctionalChildImage','Get-FunctionalChildImage','Stop-FunctionalChild')) {
    $definition = @($functionalAst.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true))
    Assert-QaTest ($definition.Count -eq 1) "Missing functional identity helper: $name"
    . ([scriptblock]::Create($definition[0].Extent.Text))
}
if ($Runtime -and $IsWindows) {
    $own = [Diagnostics.Process]::GetCurrentProcess()
    try { Assert-QaTest ((Read-FunctionalChildImage $own) -ceq $own.MainModule.FileName) 'Native process image differs from owned synthetic host' }
    finally { $own.Dispose() }
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
