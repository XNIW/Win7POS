$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'qa/Win7PosPerformanceValidation.psm1') -Force
$directory = Join-Path ([IO.Path]::GetTempPath()) ('Win7PosValidator-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $directory
# Synthetic test limits are not Asus qualification budgets.
$budget = [pscustomobject]@{ schemaVersion='win7pos-performance-budget-v1'; warmupCycles=2;
    serviceP95Ms=50; sendP95Ms=16; uiP95Ms=250; uiMaximumMs=1000; probeMaximumMs=50;
    pendingMaximum=5; oldestMaximumMs=100; privateMaximumBytes=2000; managedMaximumBytes=1000;
    managedWindowGrowthBytes=100; memoryWindowCycles=3; cacheMaximumBytes=100; realizedGridMaximum=24; visualMaximumWaitMs=100 }
function Fixture {
    $script:idle = @(0..13 | ForEach-Object { [pscustomobject]@{cycle=$_; elapsed_s=($_+1)*30;
        private_bytes=500; managed_bytes=300+($_%3)*10; pending=1; oldest_ms=2;
        focus_pending=0; focus_oldest_ms=0; scroll_pending=0; scroll_oldest_ms=0;
        closed_rooted_windows=0; cache_bytes=50; input_ms=2; render_ms=2; databind_ms=2; background_ms=2;
        observer_dropped=0; environment_valid=1; suspended=0; focus_peak=1;scroll_peak=1;focus_maximum_wait_ms=5;scroll_maximum_wait_ms=5;metadata_entries=500} })
    $script:scans = @(foreach($cycle in 0..13) { foreach($mode in @('Rows','Grid')) { foreach($ordinal in 1..10) {
        [pscustomobject]@{cycle=$cycle;mode=$mode;ordinal=$ordinal;service_ms=8;ui_return_ms=3;apply_ms=2;layout_ms=8;bitmap_ms=20;command_overhead_ms=1;realized_grid=12;product_commands=2;commands_on_dispatcher=0;collection_changes=0;focus_scroll_wait_ms=1}
    } } })
}
function Check([string]$Name, [bool]$Expected, [bool]$Completed=$true, [string]$Reason='', [int]$ReceiptProducts=100000, [int]$ReceiptCart=500, [switch]$DuplicateIsolated, [double]$Seconds=400) {
    @{schemaVersion='win7pos-performance-measurement-v1';measurementCompleted=$true;environmentValid=$true;products=$ReceiptProducts;cartSize=$ReceiptCart;protocolVersion=3;cycles=$idle.Count} |
        ConvertTo-Json | Set-Content (Join-Path $directory 'qualification-measurement.json')
    $isolated = @(foreach($size in @(1,10,50,100,500)) { foreach($sample in 0..30) {
        [pscustomobject]@{products=100000;cart=$size;sample=$sample;ms=8;dispatcher_probe_ms=$script:sendLatency;product_commands=2;commands_on_dispatcher=0}
    } })
    if ($DuplicateIsolated) { $isolated[2].sample='01' }
    $isolated | Export-Csv (Join-Path $directory 'cart-performance.csv') -NoTypeInformation
    $idle | Export-Csv (Join-Path $directory 'qualification-idle.csv') -NoTypeInformation
    $scans | Export-Csv (Join-Path $directory 'qualification-scans.csv') -NoTypeInformation
    $result = Test-Win7PosPerformance -Directory $directory -Budget $budget -RequiredSeconds $Seconds -ProcessCompleted $Completed
    if ($result.STABILITY_PASS -ne $Expected) { throw "$Name unexpected result: $($result | ConvertTo-Json -Depth 5 -Compress)" }
    $reasons = @($result.measurementReasons) + @($result.environmentReasons) + @($result.stabilityReasons)
    if ($Reason -and -not ($reasons -like "*$Reason*")) { throw "$Name missing expected reason $Reason" }
    Write-Output "PASS validator $Name"
}
$script:sendLatency=.2
Fixture; Check 'stable' $true
Fixture; Check 'wrong product fixture' $false -Reason 'invalid_measurement_receipt' -ReceiptProducts 20000
Fixture; Check 'wrong cart fixture' $false -Reason 'invalid_measurement_receipt' -ReceiptCart 50
Fixture; Check 'equivalent isolated sample identities' $false -Reason 'invalid_isolated_service_sample_identity' -DuplicateIsolated
Fixture; Check 'invalid required duration' $false -Reason 'invalid_qualification_contract' -Seconds ([double]::NaN)
Fixture; $script:sendLatency=17; Check 'existing Send budget' $false -Reason 'send_probe_p95'; $script:sendLatency=.2
Fixture; $budget.serviceP95Ms='1e3'; Check 'exponential widened budget' $false -Reason 'budget_exceeds'; $budget.serviceP95Ms=50
Fixture; $budget.memoryWindowCycles=2.5; Check 'fractional window rejected' $false -Reason 'invalid_integral'; $budget.memoryWindowCycles=3
Fixture; foreach($sample in $scans | Where-Object { $_.mode -eq 'Rows' -and $_.ordinal -eq 3 }) { $sample.ui_return_ms=1650 }; Check 'periodic third scan stall' $false -Reason 'ui_stall'
Fixture; foreach($sample in $scans) { $sample.command_overhead_ms=400 }; Check 'public command overhead included' $false -Reason 'ui_p95'
Fixture; foreach($row in $idle) { $row.managed_bytes=300+40*$row.cycle }; Check 'persistent growth' $false -Reason 'memory_growth'
Fixture; foreach($index in 6..11) { $idle[$index].managed_bytes=700 }; $idle[12].managed_bytes=400; $idle[13].managed_bytes=350; Check 'partial recovery' $false -Reason 'memory_growth'
Fixture; $idle[-1].elapsed_s=399; Check 'incomplete duration' $false -Reason 'incomplete_duration'
Fixture; $scans=$scans[1..($scans.Count-1)]; Check 'missing sample' $false -Reason 'missing_scan'
Fixture; $scans[3].service_ms='NaN'; Check 'invalid sample' $false -Reason 'invalid_numeric'
Fixture; $scans[3].ordinal=3; Check 'duplicate sample' $false -Reason 'duplicate_scan'
Fixture; Check 'terminated process' $false -Completed $false -Reason 'process_not_completed'
Fixture; $idle[5].environment_valid=0; Check 'unsuitable desktop' $false -Reason 'unqualified_desktop'
Fixture; $idle[5].suspended=1; Check 'suspend' $false -Reason 'suspend'
Fixture; $idle[5].background_ms=251; Check 'priority timeout' $false -Reason 'priority_probe'
Fixture; $idle[5].background_ms='Infinity'; Check 'explicit probe timeout' $false -Reason 'priority_probe_timeout'
Fixture; $idle[5].focus_maximum_wait_ms=101; Check 'visual delay before idle' $false -Reason 'application_visual_backlog'
Fixture; $scans[62].focus_scroll_wait_ms=101; $scans[62].command_overhead_ms=101; Check 'per-scan visual wait despite acceptable UI percentiles' $false -Reason 'public_visual_wait'
Fixture; $scans[62].focus_scroll_wait_ms='Infinity'; Check 'non-finite scan visual wait' $false -Reason 'invalid_numeric_field:focus_scroll_wait_ms'
Fixture; $scans[62].PSObject.Properties.Remove('focus_scroll_wait_ms'); Check 'missing scan visual wait' $false -Reason 'invalid_numeric_field:focus_scroll_wait_ms'
Fixture; $budget.visualMaximumWaitMs=251; Check 'widened visual wait budget' $false -Reason 'budget_exceeds'; $budget.visualMaximumWaitMs=100
Fixture; $idle[5].scroll_peak=2; Check 'duplicate visual operations' $false -Reason 'application_visual_backlog'
Fixture; $scans[5].product_commands=3; Check 'public query budget' $false -Reason 'query_batch'
Fixture; $idle[5].closed_rooted_windows=1; Check 'closed window retained' $false -Reason 'closed_window'
Fixture; $idle[5].observer_dropped=1; Check 'observer overflow' $false -Reason 'observer_overflow'
Fixture; $scans[5].PSObject.Properties.Remove('service_ms'); Check 'missing column' $false -Reason 'invalid_numeric'
$historical = Join-Path $PSScriptRoot '../docs/reports/evidence/2026-09-25-post-pr109/patched-package-soak-100000'
$result = Test-Win7PosPerformance -Directory $historical -Budget $budget -RequiredSeconds 3600 -ProcessCompleted $true
if ($result.STABILITY_PASS -or $result.MEASUREMENT_COMPLETED) { throw 'Historical qualification evidence must not pass' }
Write-Output 'PASS validator historical soak rejected; evidence preserved'
# These contract checks never launch a process, even if a preflight guard regresses.
$runnerHarness = Join-Path $directory 'runner-harness'
$null = New-Item -ItemType Directory -Path $runnerHarness
[IO.File]::WriteAllText((Join-Path $runnerHarness 'Win7POS.Wpf.UiSmokeHarness.exe'), 'non-executable preflight fixture')
$runnerBudget = Join-Path $directory 'runner-budget.json'
$budget | ConvertTo-Json | Set-Content -LiteralPath $runnerBudget
$runnerBinding = Join-Path $directory 'runner-binding.json'
@{clientCommit=('a' * 40);payload=@();harnessSha256=('0' * 64)} | ConvertTo-Json | Set-Content -LiteralPath $runnerBinding
function Check-RunnerRejection([string]$Name, [hashtable]$Arguments, [string]$Expected) {
    function Start-Process { throw 'test_forbids_process_launch' }
    $output = Join-Path $directory $Name
    $caught = $false
    try { & (Join-Path $PSScriptRoot 'run-cart-performance.ps1') -OutputDirectory $output -HarnessDirectory $runnerHarness @Arguments }
    catch { $caught=$true; if ($_.Exception.Message -notlike "*$Expected*") { throw } }
    if (-not $caught -or (Test-Path -LiteralPath $output)) { throw "$Name did not reject before execution setup" }
    Write-Output "PASS runner $Name"
}
Check-RunnerRejection 'short-and-soak' @{Mode='Diagnostic';DiagnosticScanCount=5;SoakMinutes=12} 'mutually exclusive'
Check-RunnerRejection 'short-qualification' @{Mode='Qualification';DiagnosticScanCount=5} 'diagnostic only'
Check-RunnerRejection 'input-qualification' @{Mode='Qualification';DiagnosticScanCount=5;DiagnosticInputDispatch=$true} 'requires short Diagnostic'
Check-RunnerRejection 'input-without-short' @{Mode='Diagnostic';DiagnosticInputDispatch=$true} 'requires short Diagnostic'
Check-RunnerRejection 'integrated-sha-mismatch' @{Mode='Qualification';Stage='Integrated';SoakMinutes=12;BudgetPath=$runnerBudget;PayloadBindingPath=$runnerBinding;ExpectedCommit=('b' * 40)} 'verified payload binding'
$oldTrace = $env:WIN7POS_QA_PERF_TRACE
try {
    $env:WIN7POS_QA_PERF_TRACE='1'
    Check-RunnerRejection 'trace-qualification' @{Mode='Qualification'} 'diagnostic only'
}
finally { $env:WIN7POS_QA_PERF_TRACE=$oldTrace }
# Execute the workflow's actual aggregation with synthetic receipts. A tolerated
# measurement step failure must never become a successful qualification job.
$workflow = Get-Content (Join-Path $PSScriptRoot '../.github/workflows/ci.yml') -Raw
$aggregateMatch = [regex]::Match($workflow, '(?s)- name: Preserve qualification failures in the final job outcome.*?        run: \|\r?\n(?<body>.*?)(?=      - name:)')
if (-not $aggregateMatch.Success -or $workflow -notmatch "steps\.integrated\.outcome == 'success' && inputs\.cart_qualification == 'integrated-12-and-soak-60'") { throw 'Hosted qualification failure gating missing.' }
$aggregate = [scriptblock]::Create([regex]::Replace($aggregateMatch.Groups['body'].Value, '(?m)^          ', ''))
$savedEnvironment = @{}
foreach ($name in @('QA_ROOT','INTEGRATED_OUTCOME','SOAK_OUTCOME','SELECTION')) { $savedEnvironment[$name]=[Environment]::GetEnvironmentVariable($name) }
try {
    foreach ($case in @(
        @{name='12 minutes only';selection='integrated-12';integrated='success';soak='skipped';receipt=$true;stable=$true;expected=$true},
        @{name='12 plus 60 minutes';selection='integrated-12-and-soak-60';integrated='success';soak='success';receipt=$true;stable=$true;expected=$true},
        @{name='failed integrated blocks soak';selection='integrated-12-and-soak-60';integrated='failure';soak='skipped';receipt=$true;stable=$false;expected=$false},
        @{name='failed soak stays failed';selection='integrated-12-and-soak-60';integrated='success';soak='failure';receipt=$true;stable=$true;expected=$false},
        @{name='requested soak cannot be skipped';selection='integrated-12-and-soak-60';integrated='success';soak='skipped';receipt=$true;stable=$true;expected=$false},
        @{name='missing receipt stays failed';selection='integrated-12';integrated='success';soak='skipped';receipt=$false;stable=$true;expected=$false},
        @{name='nonpositive validator stays failed';selection='integrated-12';integrated='success';soak='skipped';receipt=$true;stable=$false;expected=$false}
    )) {
        $env:QA_ROOT=Join-Path $directory ('hosted-' + [guid]::NewGuid().ToString('N'))
        $null=New-Item -ItemType Directory -Path $env:QA_ROOT
        $env:INTEGRATED_OUTCOME=$case.integrated; $env:SOAK_OUTCOME=$case.soak; $env:SELECTION=$case.selection
        if ($case.receipt) {
            foreach ($lane in @('integrated-12','premerge-soak-60')) {
                $null=New-Item -ItemType Directory -Path (Join-Path $env:QA_ROOT $lane)
                @{MEASUREMENT_COMPLETED=$true;ENVIRONMENT_VALID=$true;STABILITY_PASS=$case.stable} | ConvertTo-Json | Set-Content (Join-Path $env:QA_ROOT "$lane/performance-result.json")
            }
        }
        $passed=$true
        try { & $aggregate } catch { if ($_.Exception.Message -notlike 'Hosted qualification did not pass*') { throw }; $passed=$false }
        if ($passed -ne $case.expected -or -not (Test-Path (Join-Path $env:QA_ROOT 'hosted-attestation.json'))) { throw "Hosted outcome regression: $($case.name)" }
        Write-Output "PASS hosted aggregation $($case.name)"
    }
} finally { foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name,$savedEnvironment[$name]) } }
# Preserve tiny vectors in OS temp for failure inspection.
