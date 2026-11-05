#requires -Version 7.4
[CmdletBinding()]
param([switch]$DefinitionsOnly)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$measurementRepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Import-Module (Join-Path $measurementRepoRoot 'scripts/qa/Win7PosHistoricalSalesValidation.psm1') -Force -DisableNameChecking

# Pure protocol fixtures: no SQLite, Dispatcher, process launch, filesystem
# mutation, timing delay or host qualification is performed by this test.
function New-HistoricalSalesMeasurementFixture {
    $phaseNames = @('analysis','plan','hashing','dedup','admission','backup','apply','commit','apply_return',
        'ownership_cold','query_cold','query_warm','query_warm','query_warm','analysis_cancel',
        'rollback_prepare','rollback_backup','rollback','maintenance_wait','snapshot_mutation','drain')
    $phases = @(
        for ($i = 0; $i -lt $phaseNames.Count; $i++) {
            [pscustomobject]@{ sequence=$i; id=$phaseNames[$i]; startTicks=1000L+$i*100; endTicks=1100L+$i*100; expectedSlots=2; expectedMemorySlots=1 }
        }
    )
    $samples = @(
        for ($i = 0; $i -lt $phases.Count*2; $i++) {
            $phaseIndex = [int][Math]::Floor($i/2.0)
            $due = 1000L+$i*50
            [pscustomobject]@{ sequence=$i; phase=$phaseNames[$phaseIndex]; phaseSequence=$phaseIndex; monotonicTicks=$due; dueTicks=$due;
                probeId=$i; postedTicks=$due; completedTicks=$due+1; pendingAgeMs=0.0; dropped=0; suspended=0 }
        }
    )
    $memory = @(
        for ($i = 0; $i -lt $phases.Count; $i++) {
            $due = 1000L+$i*100
            [pscustomobject]@{ sequence=$i; phase=$phaseNames[$i]; phaseSequence=$i; monotonicTicks=$due; dueTicks=$due; privateBytes=1000L; dropped=0; suspended=0 }
        }
    )
    $timings = [Collections.Generic.List[object]]::new()
    foreach ($phase in $phases) {
        if ($phase.id -cin @('analysis','plan','hashing','dedup','admission','backup','commit','apply_return','ownership_cold','query_cold','query_warm','analysis_cancel','rollback','rollback_backup')) {
            $timings.Add([pscustomobject]@{ id=$phase.id; startTicks=$phase.startTicks; endTicks=$phase.endTicks })
        }
    }
    $timings.Add([pscustomobject]@{ id='service'; startTicks=1400L; endTicks=1900L })
    $timings.Add([pscustomobject]@{ id='backup_transition'; startTicks=1600L; endTicks=1600L })
    $timings.Add([pscustomobject]@{ id='apply'; startTicks=1600L; endTicks=1800L })
    $timings.Add([pscustomobject]@{ id='mutation'; startTicks=1600L; endTicks=1700L })
    $timings.Add([pscustomobject]@{ id='fixture_setup'; startTicks=10L; endTicks=900L })
    $timings.Add([pscustomobject]@{ id='source_generation'; startTicks=100L; endTicks=200L })
    $timings.Add([pscustomobject]@{ id='native_seed'; startTicks=300L; endTicks=800L })
    $assertionIds = @('fixture_identity','exact_counters','phase_measurements','memory_budget','dispatcher_budget','cancellation_budget',
        'mixed_query_budget','ownership_cold_warm','snapshot_invalidation','maintenance_writer_exclusion','environment_identity')
    [pscustomobject]@{
        schemaVersion='win7pos-historical-sales-load-v2'; protocolVersion=2
        runId='Win7POS.QA.20000101T000000000Z.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'; runNonce=('a'*32); sourceSha=('a'*40)
        sourceFingerprintSha256=('b'*64); harnessSha256=('c'*64); wpfSha256=('d'*64); fixtureVersion='historical-sales-getnet-v1'; fixtureSha256=('e'*64)
        timePolicyVersion='synthetic-fixed-offset-v1'; startedAtUtc='2000-01-01T00:00:00.0000000Z'; completedAtUtc='2000-01-01T00:00:04.0000000Z'
        startedTicks=0L; completedTicks=4000L; durationMs=4000.0; completed=$true; status='PASS'; lastCheckpoint='completed'
        process=[pscustomobject]@{ pid=1; creationTimeUtc='2000-01-01T00:00:00.0000000Z'; architecture='x86'; clr='4.0.30319.42000'; os='10.0.0.0' }
        summary=[pscustomobject]@{ expected=1; executed=1; passed=1; failed=0; notRun=0; timedOut=0; checksTotal=11; checksPassed=11; checksFailed=0 }
        results=@([pscustomobject]@{ id='HIST_LOAD_50K'; rawStatus='PASS'; completed=$true; durationMs=4000.0;
            assertionResults=@($assertionIds | ForEach-Object { [pscustomobject]@{ id=$_; status='PASS'; expected=$true; actual=$true } }) })
        measurementCompleted=$true; environmentValid=$true; thresholdsPassed=$true
        measurement=[pscustomobject]@{ protocol='HISTORY-MEASUREMENT-v2'; startTicks=1000L; endTicks=3100L;
            dispatcherIntervalMs=50; memoryIntervalMs=100; dispatcherPriority='Background'; samplerKind='dedicated-thread'; targetProfile='reference-windows' }
        phases=$phases; timings=$timings.ToArray(); samples=$samples; memorySamples=$memory
        metrics=[pscustomobject]@{
            analysisMs=100.0; planMs=100.0; hashingMs=100.0; dedupMs=100.0; serviceMs=500.0; admissionMs=100.0
            backupMs=100.0; backupTransitionMs=0.0; applyMs=200.0; mutationMs=100.0; commitMs=100.0; applyReturnMs=100.0
            ownershipColdMs=100.0; queryColdMs=100.0; queryMs=100.0; analysisCancelMs=100.0; cancelMs=100.0; rollbackMs=100.0; rollbackBackupMs=100.0
            fixtureSetupMs=890.0; sourceGenerationMs=100.0; nativeSeedMs=500.0
            privateBytesBefore=1000L; privateBytesAfter=1000L; sampledPeakPrivateBytes=1000L; dispatcherP95Ms=1.0; dispatcherMaximumMs=1.0
            expectedSamples=42; observedSamples=42; expectedMemorySamples=21; observedMemorySamples=21; completedProbes=42; pendingProbes=0
            dropped=0; memoryDropped=0; overflow=0; suspended=0; stopwatchFrequency=1000L; sampleIntervalMs=50; memoryIntervalMs=100
        }
    }
}

if ($DefinitionsOnly) { return }

$script:measurementChecks = 0
function Assert-MeasurementTest([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:measurementChecks++
}
function Must-RejectMeasurement([string]$Name, [scriptblock]$Mutate, [switch]$Privacy, $ExpectedTarget=$null, [string]$Pattern='^qa_history_') {
    $fixture = New-HistoricalSalesMeasurementFixture
    $before = ConvertTo-Json $fixture -Depth 32 -Compress
    $null = & $Mutate $fixture
    Assert-MeasurementTest ((ConvertTo-Json $fixture -Depth 32 -Compress) -cne $before) "Negative fixture did not mutate: $Name"
    $failure = ''
    try {
        if ($Privacy) { $null = ValidateHistoricalEvidenceStructureAndPrivacy $fixture Load $fixture }
        else { $null = Assert-HistoricalSalesMeasurement $fixture $ExpectedTarget }
    } catch { $failure = $_.Exception.Message }
    Assert-MeasurementTest ($failure -cmatch $Pattern) "Unsafe measurement accepted or failed outside the protocol validator: $Name ($failure)"
}

$valid = New-HistoricalSalesMeasurementFixture
$null = Assert-HistoricalSalesMeasurement $valid
$null = Assert-HistoricalSalesQaPass $valid Load $valid
Assert-MeasurementTest $true 'Complete independent grids and service timings must pass'

# Two consecutive 50 ms observations may legitimately share the same pending
# probe. It is one real 51 ms latency, not two completions and not a dropped slot.
$sharedProbe = New-HistoricalSalesMeasurementFixture
foreach ($sample in $sharedProbe.samples) {
    $id=[int][Math]::Floor($sample.sequence/2.0); $posted=1000L+$id*100
    $sample.probeId=$id; $sample.postedTicks=$posted; $sample.completedTicks=$posted+51
    $sample.pendingAgeMs=[double]($sample.monotonicTicks-$posted)
}
$sharedProbe.metrics.completedProbes=21; $sharedProbe.metrics.dispatcherP95Ms=51.0; $sharedProbe.metrics.dispatcherMaximumMs=51.0
$null = Assert-HistoricalSalesMeasurement $sharedProbe
Assert-MeasurementTest $true 'A real pending probe must retain full latency without becoming a drop'

# With 21 real probes the nearest-rank 95th percentile is the 20th value.
# Exactly two 100 ms probes therefore put p95 on the inclusive 100 ms bound.
foreach ($sample in $sharedProbe.samples | Where-Object probeId -ge 19) { $sample.completedTicks=$sample.postedTicks+100 }
$sharedProbe.metrics.dispatcherP95Ms=100.0; $sharedProbe.metrics.dispatcherMaximumMs=100.0
$null = Assert-HistoricalSalesMeasurement $sharedProbe
Assert-MeasurementTest $true 'Nearest-rank p95 must use unique probes and retain the cold outliers'

$trustedWin7 = New-HistoricalSalesMeasurementFixture
$trustedWin7.measurement.targetProfile='win7-sp1'
$trustedWin7.measurement | Add-Member -NotePropertyName targetIdentitySha256 -NotePropertyValue ('f'*64)
$trustedWin7.process.os='6.1.7601.0'
$trustedTarget=[pscustomobject]@{ measurementTargetProfile='win7-sp1'; measurementTargetIdentitySha256=('f'*64) }
$null = Assert-HistoricalSalesMeasurement $trustedWin7 $trustedTarget
Assert-MeasurementTest $true 'A Win7 profile requires a matching independent target identity'

# The producer counters are deliberately made self-consistent with the surviving
# samples. Only reconstruction from the fixed start/end catches these prefixes.
Must-RejectMeasurement 'truncated beginning with self-consistent counters' {
    param($f)
    $f.samples = @($f.samples | Select-Object -Skip 1)
    for ($i=0; $i -lt $f.samples.Count; $i++) { $f.samples[$i].sequence=$i; $f.samples[$i].probeId=$i }
    $f.metrics.expectedSamples=$f.samples.Count; $f.metrics.observedSamples=$f.samples.Count; $f.metrics.completedProbes=$f.samples.Count
}
Must-RejectMeasurement 'truncated end with self-consistent counters' {
    param($f)
    $f.samples=@($f.samples | Select-Object -SkipLast 1)
    $f.metrics.expectedSamples=$f.samples.Count; $f.metrics.observedSamples=$f.samples.Count; $f.metrics.completedProbes=$f.samples.Count
}
Must-RejectMeasurement 'missing interior slot' {
    param($f)
    $f.samples=@($f.samples | Where-Object sequence -ne 11)
    $f.metrics.expectedSamples=$f.samples.Count; $f.metrics.observedSamples=$f.samples.Count; $f.metrics.completedProbes=$f.samples.Count
}
Must-RejectMeasurement 'omitted phase' { param($f) $f.phases=@($f.phases | Where-Object id -cne 'backup') }
Must-RejectMeasurement 'phase renamed to hide required work' { param($f) $f.phases[5].id='apply'; $f.samples[10].phase='apply'; $f.samples[11].phase='apply'; $f.memorySamples[5].phase='apply' }
Must-RejectMeasurement 'phase boundary gap' { param($f) $f.phases[6].startTicks++ }
Must-RejectMeasurement 'self-reported wrong phase expected count' { param($f) $f.phases[6].expectedSlots=1 }
Must-RejectMeasurement 'sample attributed to another phase' { param($f) $f.samples[12].phaseSequence=5; $f.samples[12].phase='backup' }
Must-RejectMeasurement 'shifted start and self-consistent producer counters' {
    param($f)
    $f.measurement.startTicks=1050L; $f.samples=@($f.samples | Select-Object -Skip 1)
    for ($i=0; $i -lt $f.samples.Count; $i++) { $f.samples[$i].sequence=$i; $f.samples[$i].probeId=$i }
    $f.metrics.expectedSamples=$f.samples.Count; $f.metrics.observedSamples=$f.samples.Count; $f.metrics.completedProbes=$f.samples.Count
}
Must-RejectMeasurement 'pending stall retains a lower bound' {
    param($f)
    foreach ($s in $f.samples | Where-Object sequence -ge 30) {
        $s.probeId=30; $s.postedTicks=2500L; $s.completedTicks=$null; $s.pendingAgeMs=[double]($s.monotonicTicks-2500L)
    }
    $f.metrics.completedProbes=30; $f.metrics.pendingProbes=1; $f.metrics.dispatcherMaximumMs=550.0
}
Must-RejectMeasurement 'pending completion field omitted by serializer' {
    param($f)
    $f.samples[-1].PSObject.Properties.Remove('completedTicks')
    $f.metrics.completedProbes=41; $f.metrics.pendingProbes=1
}
Must-RejectMeasurement 'grid gap' { param($f) $f.samples[3].dueTicks+=10 }
Must-RejectMeasurement 'explicit dispatcher drop' { param($f) $f.samples[3].dropped=1; $f.metrics.dropped=1 }
Must-RejectMeasurement 'explicit suspend' { param($f) $f.samples[3].suspended=1; $f.metrics.suspended=1 }
Must-RejectMeasurement 'explicit memory suspend' { param($f) $f.memorySamples[3].suspended=1; $f.metrics.suspended=1 }
Must-RejectMeasurement 'overflow' { param($f) $f.metrics.overflow=1 }
Must-RejectMeasurement 'unqualified environment' { param($f) $f.environmentValid=$false }
Must-RejectMeasurement 'insufficient unique completed probes despite 42 slots' {
    param($f)
    foreach ($s in $f.samples) {
        $id=[int][Math]::Floor($s.sequence/3.0); $posted=1000L+$id*150
        $s.probeId=$id; $s.postedTicks=$posted; $s.completedTicks=$posted+101; $s.pendingAgeMs=[double]($s.monotonicTicks-$posted)
    }
    $f.metrics.completedProbes=14; $f.metrics.dispatcherP95Ms=101.0; $f.metrics.dispatcherMaximumMs=101.0
}
Must-RejectMeasurement 'missing independent memory slot' {
    param($f)
    $f.memorySamples=@($f.memorySamples | Where-Object sequence -ne 10)
    $f.metrics.expectedMemorySamples=$f.memorySamples.Count; $f.metrics.observedMemorySamples=$f.memorySamples.Count
}
Must-RejectMeasurement 'truncated memory beginning' {
    param($f)
    $f.memorySamples=@($f.memorySamples | Select-Object -Skip 1)
    for ($i=0; $i -lt $f.memorySamples.Count; $i++) { $f.memorySamples[$i].sequence=$i }
    $f.metrics.expectedMemorySamples=$f.memorySamples.Count; $f.metrics.observedMemorySamples=$f.memorySamples.Count
}
Must-RejectMeasurement 'memory grid gap' { param($f) $f.memorySamples[3].dueTicks+=10 }
Must-RejectMeasurement 'explicit memory drop' { param($f) $f.memorySamples[3].dropped=1; $f.metrics.memoryDropped=1 }
Must-RejectMeasurement 'memory peak understated' { param($f) $f.memorySamples[3].privateBytes=1001L }
Must-RejectMeasurement 'dispatcher p95 understated' { param($f) $f.metrics.dispatcherP95Ms=0.0 }
Must-RejectMeasurement 'dispatcher maximum understated' { param($f) $f.metrics.dispatcherMaximumMs=0.0 }
Must-RejectMeasurement 'service cost concealed in backup' { param($f) $f.metrics.applyMs=100.0; $f.metrics.backupMs=200.0 }
Must-RejectMeasurement 'missing cold proof timing' { param($f) $f.timings=@($f.timings | Where-Object id -cne 'ownership_cold') }
Must-RejectMeasurement 'service timings fail reconciliation' { param($f) ($f.timings | Where-Object id -ceq 'admission').endTicks=1499L; $f.metrics.admissionMs=99.0 }
Must-RejectMeasurement 'out of measurement bounds' { param($f) $f.measurement.endTicks=3000L }
Must-RejectMeasurement 'completion beyond measurement closure' { param($f) $f.samples[-1].completedTicks=3101L }
Must-RejectMeasurement 'undeclared priority' { param($f) $f.measurement.dispatcherPriority='Normal' }
Must-RejectMeasurement 'not a dedicated sampler thread' { param($f) $f.measurement.samplerKind='Task.Run' }
Must-RejectMeasurement 'Win7 profile cannot select its own larger budget' {
    param($f)
    $f.measurement.targetProfile='win7-sp1'; $f.process.os='6.1.7601.0'
    $f.measurement | Add-Member -NotePropertyName targetIdentitySha256 -NotePropertyValue ('f'*64)
}
Must-RejectMeasurement 'Win7 target identity missing' {
    param($f) $f.measurement.targetProfile='win7-sp1'; $f.process.os='6.1.7601.0'
} -ExpectedTarget $trustedTarget
Must-RejectMeasurement 'Win7 target identity mismatched' {
    param($f)
    $f.measurement.targetProfile='win7-sp1'; $f.process.os='6.1.7601.0'
    $f.measurement | Add-Member -NotePropertyName targetIdentitySha256 -NotePropertyValue ('a'*64)
} -ExpectedTarget $trustedTarget
Must-RejectMeasurement 'OS 6.1 text alone must not widen the reference analysis budget' {
    param($f)
    # Extend the real analysis phase and both grids, rather than only lying in
    # the scalar metric. Every count and timing still reconciles at 40 seconds.
    $delta=39900L
    $f.process.os='6.1.7601.0'; $f.measurement.endTicks+=$delta; $f.completedTicks+=$delta; $f.durationMs+=$delta
    $f.phases[0].endTicks+=$delta; $f.phases[0].expectedSlots=800; $f.phases[0].expectedMemorySlots=400
    foreach ($phase in $f.phases | Select-Object -Skip 1) { $phase.startTicks+=$delta; $phase.endTicks+=$delta }
    foreach ($timing in $f.timings) {
        if ($timing.id -ceq 'analysis') { $timing.endTicks+=$delta }
        elseif ($timing.id -cnotin @('fixture_setup','source_generation','native_seed')) { $timing.startTicks+=$delta; $timing.endTicks+=$delta }
    }
    $f.samples=@(
        for ($i=0; $i -lt 840; $i++) {
            $due=1000L+$i*50; $phaseIndex=if($due -lt 41000){0}else{1+[int][Math]::Floor(($due-41000)/100.0)}
            [pscustomobject]@{ sequence=$i; phase=$f.phases[$phaseIndex].id; phaseSequence=$phaseIndex; monotonicTicks=$due; dueTicks=$due;
                probeId=$i; postedTicks=$due; completedTicks=$due+1; pendingAgeMs=0.0; dropped=0; suspended=0 }
        }
    )
    $f.memorySamples=@(
        for ($i=0; $i -lt 420; $i++) {
            $due=1000L+$i*100; $phaseIndex=if($due -lt 41000){0}else{1+[int][Math]::Floor(($due-41000)/100.0)}
            [pscustomobject]@{ sequence=$i; phase=$f.phases[$phaseIndex].id; phaseSequence=$phaseIndex; monotonicTicks=$due; dueTicks=$due; privateBytes=1000L; dropped=0; suspended=0 }
        }
    )
    $f.metrics.analysisMs=40000.0; $f.metrics.expectedSamples=840; $f.metrics.observedSamples=840; $f.metrics.completedProbes=840
    $f.metrics.expectedMemorySamples=420; $f.metrics.observedMemorySamples=420
} -Pattern '^qa_history_threshold_failed$'
Must-RejectMeasurement 'unknown cell text field rejected by privacy allowlist' { param($f) $f.samples[0] | Add-Member -NotePropertyName rawCell -NotePropertyValue 'SYNTHETIC_PRIVATE_CANARY' } -Privacy
Must-RejectMeasurement 'unknown memory field rejected by privacy allowlist' { param($f) $f.memorySamples[0] | Add-Member -NotePropertyName sourcePath -NotePropertyValue 'SYNTHETIC_PRIVATE_CANARY' } -Privacy
Must-RejectMeasurement 'unknown timing text rejected by privacy allowlist' { param($f) $f.timings[0] | Add-Member -NotePropertyName diagnostic -NotePropertyValue 'SYNTHETIC_PRIVATE_CANARY' } -Privacy

Write-Output ("Historical sales measurement pure checks: {0} PASS" -f $script:measurementChecks)
