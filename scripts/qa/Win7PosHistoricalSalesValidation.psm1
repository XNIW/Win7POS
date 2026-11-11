Set-StrictMode -Version Latest

function Get-HistoricalSalesQaContract {
    param([ValidateSet('Smoke','Load','CrashProbe','CloudSync')][string]$Mode)
    $smoke = [ordered]@{
        HIST01_PROFILES_PREVIEW=@('profiles_detected','header_mapping','exact_values','text_ids','policy_visible','invalid_input_blocked')
        HIST02_APPLY_REOPEN=@('durable_receipt','manifest_totals','reopened_records','operational_invariants')
        HIST03_IDENTITY_DEDUP=@('unchanged_zero_new','renamed_reordered_overlap_zero','distinct_ids_kept','conflict_blocked')
        HIST04_STALE_AUTH_SCOPE=@('permission_revoked','operator_changed','shop_changed','epoch_changed','precommit_revocation')
        HIST05_ATOMIC_CANCEL_RECOVERY=@('cancel_rollback','precommit_fault_rollback','disk_fault_safe','lost_response_reconciled')
        HIST06_BATCH_CANCEL_REIMPORT=@('owner_only_undo','duplicate_batch_not_owner','three_cycles','audit_retained')
        HIST07_REPORTS_EXPORT=@('signed_totals','hourly_parity','operator_scope','tenant_scope','detail_routing','csv_contract','native_ticket_average','snapshot_invalidation','export_publication_authorization','export_publication_outcome','coverage_state_no_disclosure','hourly_signed_geometry')
        HIST08_UI_LIFECYCLE=@('keyboard_focus','dialog_standard','nonblocking_dispatcher','stale_result_ignored','reopened_receipt','retry_empty_ui','retry_expiry_ui','retry_revocation_ui','retry_generation_ui','retry_cancel_ui','retry_normal_late_authority_ui','retry_normal_finalization_fault_ui','retry_normal_ready_ui')
    }
    $foundation = [ordered]@{
        HIST_CRASH_HOT_JOURNAL=@('same_existing_database','prepared_sentinel','reached_after_spill','expected_abrupt_exit','journal_recovery_completed','atomic_data_and_revision','operational_invariants','owned_process_cleanup')
        HIST_B_COMMIT_AUTHORITY=@('apply_revoke_before_insert','apply_revoke_precommit','undo_revoke_precommit','expiry_before_commit','operator_permission_changed','linearized_commit_once')
        HIST_B_PROCESS_ADMISSION=@('real_holder_contender','sqlite_busy_bounded','workflow_cancel_no_orphan','auth_cancel_no_orphan','maintenance_cancel_no_orphan','lock_order_no_deadlock','commands_cart_restored')
        HIST_B_ENTRYPOINT_AUTHORITY=@('wpf_normal_rejected_before_init','wpf_safe_rejected_before_init','cli_rejected_before_seed','harness_rejected_before_init','distinct_databases_admitted','case_shortpath_alias_collide','reparse_hardlink_rejected','restart_reacquires')
        HIST_B_BACKUP_RESTORE=@('backup_validate_publish','aux_scope_reject','candidate_swap_rebind','rollback_restore','epoch_stales_plan','mapping_fk_preserved','reimport_zero','all_handles_released')
    }
    switch ($Mode) {
        CloudSync { return @{ mode=$Mode; schemaVersion='win7pos-historical-cloud-sync-v1'; protocolVersion=1; file='historical-cloud-sync-results.json'; scenarios=([ordered]@{ HIST_C_OPEN_REPLICA=@('open_view_convergence') }); bodySeconds=60 } }
        Smoke { return @{ mode=$Mode; schemaVersion='win7pos-historical-sales-qa-v3'; protocolVersion=3; file='historical-sales-results.json'; scenarios=$smoke; bodySeconds=60 } }
        Load { return @{ mode=$Mode; schemaVersion='win7pos-historical-sales-load-v2'; protocolVersion=2; file='historical-sales-load-results.json'; scenarios=([ordered]@{ HIST_LOAD_50K=@('fixture_identity','exact_counters','phase_measurements','memory_budget','dispatcher_budget','cancellation_budget','mixed_query_budget','ownership_cold_warm','snapshot_invalidation','maintenance_writer_exclusion','environment_identity') }); bodySeconds=180 } }
        CrashProbe { return @{ mode=$Mode; schemaVersion='win7pos-historical-sales-foundation-v3'; protocolVersion=3; file='historical-sales-crash-results.json'; scenarios=$foundation; bodySeconds=330 } }
    }
}

function Read-HistoricalSalesQaJson {
    param([Parameter(Mandatory)][string]$Path)
    $file = Get-Item -LiteralPath $Path -ErrorAction Stop
    if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $file.Length -eq 0 -or $file.Length -gt 2MB) { throw 'qa_history_receipt_size_or_link' }
    $json = [IO.File]::ReadAllText($file.FullName)
    $document = [Text.Json.JsonDocument]::Parse($json)
    try {
        function Visit-HistoryJson($Element, [int]$Depth) {
            if ($Depth -gt 16) { throw 'qa_history_receipt_depth' }
            if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
                $keys = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                $object=[pscustomobject]@{}
                foreach ($property in $Element.EnumerateObject()) {
                    if (-not $keys.Add($property.Name)) { throw 'qa_history_duplicate_json_key' }
                    $value=Visit-HistoryJson $property.Value ($Depth + 1)
                    $object.PSObject.Properties.Add([Management.Automation.PSNoteProperty]::new($property.Name,$value))
                }
                return $object
            } elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
                if ($Element.GetArrayLength() -gt 20000) { throw 'qa_history_receipt_array_limit' }
                $array=[Collections.Generic.List[object]]::new()
                foreach ($item in $Element.EnumerateArray()) { $array.Add((Visit-HistoryJson $item ($Depth + 1))) }
                # Unary comma preserves empty/single/nested arrays in the
                # PowerShell pipeline rather than unrolling their members.
                return ,$array.ToArray()
            } elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::String) {
                return $Element.GetString()
            } elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Number) {
                $integer=0L
                if($Element.TryGetInt64([ref]$integer)){return $integer}
                if($Element.GetRawText() -cmatch '^-?[0-9]+$'){throw 'qa_history_integer_out_of_range'}
                $number=0.0
                if(-not $Element.TryGetDouble([ref]$number) -or [double]::IsNaN($number) -or [double]::IsInfinity($number)){throw 'qa_history_invalid_numeric'}
                return $number
            } elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::True) {
                return $true
            } elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::False) {
                return $false
            } elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Null) {
                return $null
            }
            throw 'qa_history_json_kind_invalid'
        }
        # One strict reader on every supported QA host (.NET 8 / PowerShell
        # 7.4 and later). GetString never coerces ISO timestamps to DateTime.
        return Visit-HistoryJson $document.RootElement 0
    } finally { $document.Dispose() }
}

function Assert-HistoryKeys($Object, [string[]]$Allowed, [string[]]$Required = @()) {
    if ($null -eq $Object -or $Object -isnot [pscustomobject]) { throw 'qa_history_object_required' }
    foreach ($name in $Object.PSObject.Properties.Name) { if ($name -cnotin $Allowed) { throw 'qa_history_receipt_privacy_rejected' } }
    foreach ($name in $Required) { if ($name -cnotin $Object.PSObject.Properties.Name) { throw 'qa_history_required_field_missing' } }
}

function Assert-HistoryNumber($Value, [switch]$Integral, [switch]$Signed) {
    if ($Value -isnot [byte] -and $Value -isnot [int] -and $Value -isnot [long] -and $Value -isnot [double] -and $Value -isnot [decimal]) { throw 'qa_history_numeric_required' }
    $number = [double]$Value
    if ([double]::IsNaN($number) -or [double]::IsInfinity($number) -or (-not $Signed -and $number -lt 0) -or ($Integral -and $Value -ne [Math]::Truncate([decimal]$Value))) { throw 'qa_history_invalid_numeric' }
}

function Get-HistoryMeasurementPhases {
    return @('analysis','plan','hashing','dedup','admission','backup','apply','commit','apply_return','ownership_cold','query_cold','query_warm','query_warm','query_warm','analysis_cancel','rollback_prepare','rollback_backup','rollback','maintenance_wait','snapshot_mutation','drain')
}

function Assert-HistoryMeasurementStructure($Receipt) {
    $metricNames=@('analysisMs','planMs','hashingMs','dedupMs','serviceMs','admissionMs','applyMs','backupMs','backupTransitionMs','mutationMs','commitMs','applyReturnMs','rollbackMs','rollbackBackupMs','queryMs','queryColdMs','ownershipColdMs','analysisCancelMs','fixtureSetupMs','sourceGenerationMs','nativeSeedMs','privateBytesBefore','privateBytesAfter','sampledPeakPrivateBytes','dispatcherP95Ms','dispatcherMaximumMs','cancelMs','expectedSamples','observedSamples','expectedMemorySamples','observedMemorySamples','completedProbes','pendingProbes','dropped','memoryDropped','overflow','suspended','stopwatchFrequency','sampleIntervalMs','memoryIntervalMs')
    $metricNames+=@('samplerGapCount','samplerGapFirstSequence','samplerGapFirstWaitMs','samplerGapFirstRequestedMs','samplerGapFirstMemoryMs','samplerGapFirstObservationMs','samplerGapFirstWaitSignaled','samplerGapFirstGc0','samplerGapFirstGc1','samplerGapFirstGc2')
    if ($Receipt.PSObject.Properties.Name -contains 'metrics') {
        Assert-HistoryKeys $Receipt.metrics $metricNames
        foreach($field in $Receipt.metrics.PSObject.Properties.Name){ Assert-HistoryNumber $Receipt.metrics.$field -Integral:($field -notlike '*Ms') }
    }
    if ($Receipt.PSObject.Properties.Name -contains 'measurement') {
        Assert-HistoryKeys $Receipt.measurement @('protocol','startTicks','endTicks','dispatcherIntervalMs','memoryIntervalMs','dispatcherPriority','samplerKind','targetProfile','targetIdentitySha256') @('protocol','startTicks','endTicks','dispatcherIntervalMs','memoryIntervalMs','dispatcherPriority','samplerKind','targetProfile')
        if($Receipt.measurement.protocol -cne 'HISTORY-MEASUREMENT-v2' -or $Receipt.measurement.dispatcherPriority -cne 'Background' -or $Receipt.measurement.samplerKind -cne 'dedicated-thread'){throw 'qa_history_sample_contract_invalid'}
        if($Receipt.measurement.targetProfile -cnotin @('reference-windows','win7-sp1')){throw 'qa_history_target_profile_invalid'}
        if($Receipt.measurement.PSObject.Properties.Name -contains 'targetIdentitySha256' -and $null -ne $Receipt.measurement.targetIdentitySha256 -and [string]$Receipt.measurement.targetIdentitySha256 -cnotmatch '^[a-f0-9]{64}$'){throw 'qa_history_target_profile_invalid'}
        foreach($field in @('startTicks','endTicks','dispatcherIntervalMs','memoryIntervalMs')){Assert-HistoryNumber $Receipt.measurement.$field -Integral}
    }
    $phaseNames=Get-HistoryMeasurementPhases
    if ($Receipt.PSObject.Properties.Name -contains 'phases') {
        if(@($Receipt.phases).Count -gt 64){throw 'qa_history_phase_limit'}
        foreach($phase in $Receipt.phases){
            Assert-HistoryKeys $phase @('sequence','id','startTicks','endTicks','expectedSlots','expectedMemorySlots') @('sequence','id','startTicks','endTicks','expectedSlots','expectedMemorySlots')
            if($phase.id -cnotin $phaseNames){throw 'qa_history_sample_phase_invalid'}
            foreach($field in @('sequence','startTicks','endTicks','expectedSlots','expectedMemorySlots')){Assert-HistoryNumber $phase.$field -Integral}
        }
    }
    if ($Receipt.PSObject.Properties.Name -contains 'timings') {
        if(@($Receipt.timings).Count -gt 64){throw 'qa_history_timing_limit'}
        foreach($timing in $Receipt.timings){
            Assert-HistoryKeys $timing @('id','startTicks','endTicks') @('id','startTicks','endTicks')
            if($timing.id -cnotin @('analysis','plan','hashing','dedup','service','admission','backup','backup_transition','apply','mutation','commit','apply_return','ownership_cold','query_cold','query_warm','analysis_cancel','rollback','rollback_backup','fixture_setup','source_generation','native_seed')){throw 'qa_history_timing_invalid'}
            Assert-HistoryNumber $timing.startTicks -Integral;Assert-HistoryNumber $timing.endTicks -Integral
        }
    }
    foreach($list in @('samples','memorySamples')){
        if($Receipt.PSObject.Properties.Name -notcontains $list){continue}
        if(@($Receipt.$list).Count -gt $(if($list -eq 'samples'){4096}else{2048})){throw 'qa_history_sample_limit'}
        $previousSequence=-1L;$previousTicks=-1L
        foreach($sample in $Receipt.$list){
            $fields=@('sequence','phase','phaseSequence','monotonicTicks','dueTicks','dropped','suspended')
            if($list -eq 'samples'){$fields+=@('probeId','postedTicks','completedTicks','pendingAgeMs')}else{$fields+=@('privateBytes')}
            Assert-HistoryKeys $sample $fields @($fields|Where-Object{$_ -ne 'completedTicks'})
            if($sample.phase -cnotin $phaseNames){throw 'qa_history_sample_phase_invalid'}
            foreach($field in @($sample.PSObject.Properties.Name|Where-Object{$_ -ne 'phase'})){
                if($null -ne $sample.$field){Assert-HistoryNumber $sample.$field -Integral:($field -ne 'pendingAgeMs')}
            }
            if($sample.sequence -le $previousSequence -or $sample.monotonicTicks -le $previousTicks){throw 'qa_history_sample_sequence_invalid'}
            $previousSequence=$sample.sequence;$previousTicks=$sample.monotonicTicks
        }
    }
}

# Pure verifier: every expected slot is derived from the closed monotonic window,
# independently of producer counts. A pending probe can span many real slots.
function Assert-HistoricalSalesMeasurement {
    param([Parameter(Mandatory)]$Receipt, $Expected=$null)
    Assert-HistoryMeasurementStructure $Receipt
    foreach($field in @('measurement','phases','timings','samples','memorySamples','metrics','startedTicks','completedTicks','measurementCompleted','environmentValid','thresholdsPassed')){
        if($Receipt.PSObject.Properties.Name -notcontains $field){throw 'qa_history_measurement_incomplete'}
    }
    foreach($flag in @('measurementCompleted','environmentValid','thresholdsPassed')){if($Receipt.$flag -isnot [bool] -or $Receipt.$flag -ne $true){throw 'qa_history_measurement_incomplete'}}
    Assert-HistoryNumber $Receipt.startedTicks -Integral;Assert-HistoryNumber $Receipt.completedTicks -Integral
    $m=$Receipt.metrics;$window=$Receipt.measurement
    $requiredMetrics=@('analysisMs','planMs','hashingMs','dedupMs','serviceMs','admissionMs','backupMs','backupTransitionMs','applyMs','mutationMs','commitMs','applyReturnMs','ownershipColdMs','queryColdMs','queryMs','analysisCancelMs','cancelMs','rollbackMs','rollbackBackupMs','fixtureSetupMs','sourceGenerationMs','nativeSeedMs','privateBytesBefore','privateBytesAfter','sampledPeakPrivateBytes','dispatcherP95Ms','dispatcherMaximumMs','expectedSamples','observedSamples','expectedMemorySamples','observedMemorySamples','completedProbes','pendingProbes','dropped','memoryDropped','overflow','suspended','stopwatchFrequency','sampleIntervalMs','memoryIntervalMs')
    foreach($name in $requiredMetrics){if($m.PSObject.Properties.Name -notcontains $name){throw 'qa_history_metric_missing'}}
    $frequency=[double]$m.stopwatchFrequency
    if($frequency -le 0 -or $m.sampleIntervalMs -ne 50 -or $m.memoryIntervalMs -ne 100 -or $window.dispatcherIntervalMs -ne 50 -or $window.memoryIntervalMs -ne 100){throw 'qa_history_sample_contract_invalid'}
    $start=[long]$window.startTicks;$end=[long]$window.endTicks
    if($start -lt $Receipt.startedTicks -or $end -le $start -or $end -gt $Receipt.completedTicks){throw 'qa_history_measurement_bounds_invalid'}
    if($Receipt.PSObject.Properties.Name -notcontains 'durationMs' -or ($Receipt.completedTicks-$Receipt.startedTicks)*1000.0/$frequency -gt $Receipt.durationMs+1){throw 'qa_history_measurement_bounds_invalid'}
    $slots={param([long]$tick,[int]$interval) [long][Math]::Ceiling(($tick-$start)*1000.0/($frequency*$interval))}
    $expectedDispatcherSlots=&$slots $end 50;$expectedMemory=&$slots $end 100
    if($expectedDispatcherSlots -ne @($Receipt.samples).Count -or $expectedMemory -ne @($Receipt.memorySamples).Count -or
        $m.expectedSamples -ne $expectedDispatcherSlots -or $m.observedSamples -ne $expectedDispatcherSlots -or $m.expectedMemorySamples -ne $expectedMemory -or $m.observedMemorySamples -ne $expectedMemory -or
        $m.dropped -ne 0 -or $m.memoryDropped -ne 0 -or $m.overflow -ne 0 -or $m.suspended -ne 0){throw 'qa_history_measurement_incomplete'}
    $phaseNames=Get-HistoryMeasurementPhases;$phases=@($Receipt.phases)
    if($phases.Count -ne $phaseNames.Count){throw 'qa_history_phase_missing'}
    $phaseEnd=$start
    for($i=0;$i -lt $phases.Count;$i++){
        $phase=$phases[$i]
        $count=(&$slots $phase.endTicks 50)-(&$slots $phase.startTicks 50)
        $memoryCount=(&$slots $phase.endTicks 100)-(&$slots $phase.startTicks 100)
        if($phase.sequence -ne $i -or $phase.id -cne $phaseNames[$i] -or $phase.startTicks -ne $phaseEnd -or $phase.endTicks -lt $phase.startTicks -or $phase.endTicks -gt $end -or $phase.expectedSlots -ne $count -or $phase.expectedMemorySlots -ne $memoryCount){throw 'qa_history_phase_bounds_invalid'}
        if($phase.id -cin @('analysis','apply','ownership_cold') -and ($count -eq 0 -or $memoryCount -eq 0)){throw 'qa_history_phase_unobserved'}
        $phaseEnd=$phase.endTicks
    }
    if($phaseEnd -ne $end){throw 'qa_history_phase_bounds_invalid'}
    $probes=[Collections.Generic.Dictionary[long,object]]::new();$latencies=[Collections.Generic.List[double]]::new();$maximum=0.0;$peak=0L
    foreach($list in @('samples','memorySamples')){
        $interval=if($list -eq 'samples'){50}else{100};$sequence=0L
        foreach($sample in $Receipt.$list){
            $due=$start+[long][Math]::Floor($sequence*$frequency*$interval/1000.0)
            $nextDue=$start+[long][Math]::Floor(($sequence+1)*$frequency*$interval/1000.0)
            if($sample.sequence -ne $sequence -or $sample.dueTicks -ne $due -or $sample.monotonicTicks -lt $due -or $sample.monotonicTicks -ge $nextDue -or $sample.monotonicTicks -ge $end -or $sample.dropped -ne 0 -or $sample.suspended -ne 0){throw 'qa_history_sample_gap'}
            if($sample.phaseSequence -ge $phases.Count){throw 'qa_history_sample_phase_invalid'}
            $phase=$phases[[int]$sample.phaseSequence]
            if($sample.phase -cne $phase.id -or $due -lt $phase.startTicks -or $due -ge $phase.endTicks){throw 'qa_history_sample_phase_invalid'}
            if($list -eq 'memorySamples'){$peak=[Math]::Max($peak,[long]$sample.privateBytes);$sequence++;continue}
            if($sample.PSObject.Properties.Name -notcontains 'completedTicks' -or $null -eq $sample.completedTicks){throw 'qa_history_pending_sample'}
            if($sample.postedTicks -gt $sample.monotonicTicks -or [Math]::Abs($sample.pendingAgeMs-($sample.monotonicTicks-$sample.postedTicks)*1000.0/$frequency) -gt .01){throw 'qa_history_probe_age_invalid'}
            $id=[long]$sample.probeId
            if(-not $probes.ContainsKey($id)){
                if($id -ne $probes.Count -or $sample.postedTicks -ne $sample.monotonicTicks){throw 'qa_history_probe_sequence_invalid'}
                if($id -gt 0){$prior=$probes[$id-1];if($null -eq $prior.completedTicks -or $sample.postedTicks -lt $prior.completedTicks){throw 'qa_history_overlapping_probe'}}
                $probes.Add($id,$sample)
            }else{
                $first=$probes[$id]
                if($id -ne $probes.Count-1 -or $sample.postedTicks -ne $first.postedTicks -or $sample.completedTicks -ne $first.completedTicks){throw 'qa_history_probe_sequence_invalid'}
            }
            $maximum=[Math]::Max($maximum,[double]$sample.pendingAgeMs)
            if($null -ne $sample.completedTicks -and ($sample.completedTicks -lt $sample.monotonicTicks -or $sample.completedTicks -gt $end)){throw 'qa_history_probe_bounds_invalid'}
            $sequence++
        }
    }
    $pending=0
    foreach($probe in $probes.Values){
        if($null -eq $probe.completedTicks){$pending++;$maximum=[Math]::Max($maximum,($end-$probe.postedTicks)*1000.0/$frequency)}
        else{$latency=($probe.completedTicks-$probe.postedTicks)*1000.0/$frequency;$latencies.Add($latency);$maximum=[Math]::Max($maximum,$latency)}
    }
    if($pending -ne 0 -or $m.pendingProbes -ne $pending){throw 'qa_history_pending_sample'}
    if($latencies.Count -lt 20 -or @($probes.Values|Where-Object phase -cne 'drain').Count -lt 20 -or $m.completedProbes -ne $latencies.Count){throw 'qa_history_insufficient_probes'}
    $ordered=@($latencies|Sort-Object);$p95=$ordered[[Math]::Ceiling(.95*$ordered.Count)-1]
    if([Math]::Abs($m.dispatcherP95Ms-$p95) -gt .01 -or [Math]::Abs($m.dispatcherMaximumMs-$maximum) -gt .01 -or $m.sampledPeakPrivateBytes -ne $peak){throw 'qa_history_sample_metric_mismatch'}

    $timings=@{};$timingNames=@('analysis','plan','hashing','dedup','service','admission','backup','backup_transition','apply','mutation','commit','apply_return','ownership_cold','query_cold','query_warm','analysis_cancel','rollback','rollback_backup','fixture_setup','source_generation','native_seed')
    foreach($timing in $Receipt.timings){
        $outside=$timing.id -cin @('fixture_setup','source_generation','native_seed')
        $lower=if($outside){$Receipt.startedTicks}else{$start};$upper=if($outside){$start}else{$end}
        if($timing.startTicks -lt $lower -or $timing.endTicks -lt $timing.startTicks -or $timing.endTicks -gt $upper){throw 'qa_history_timing_bounds_invalid'}
        if(-not $timings.ContainsKey($timing.id)){$timings[$timing.id]=[Collections.Generic.List[object]]::new()};$timings[$timing.id].Add($timing)
    }
    foreach($name in $timingNames){if(-not $timings.ContainsKey($name) -or $timings[$name].Count -ne $(if($name -eq 'query_warm'){3}else{1})){throw 'qa_history_timing_missing'}}
    $metricMap=@{analysis='analysisMs';plan='planMs';hashing='hashingMs';dedup='dedupMs';service='serviceMs';admission='admissionMs';backup='backupMs';backup_transition='backupTransitionMs';apply='applyMs';mutation='mutationMs';commit='commitMs';apply_return='applyReturnMs';ownership_cold='ownershipColdMs';query_cold='queryColdMs';analysis_cancel='analysisCancelMs';rollback='rollbackMs';rollback_backup='rollbackBackupMs';fixture_setup='fixtureSetupMs';source_generation='sourceGenerationMs';native_seed='nativeSeedMs'}
    foreach($name in $metricMap.Keys){$t=$timings[$name][0];if([Math]::Abs($m.($metricMap[$name])-($t.endTicks-$t.startTicks)*1000.0/$frequency) -gt .01){throw 'qa_history_timing_metric_mismatch'}}
    $warmMaximum=($timings.query_warm|ForEach-Object{($_.endTicks-$_.startTicks)*1000.0/$frequency}|Measure-Object -Maximum).Maximum
    if([Math]::Abs($m.queryMs-$warmMaximum) -gt .01 -or [Math]::Abs($m.cancelMs-$m.rollbackMs) -gt .01){throw 'qa_history_timing_metric_mismatch'}
    $service=$timings.service[0];$cursor=$service.startTicks
    foreach($name in @('admission','backup','backup_transition','apply','apply_return')){$t=$timings[$name][0];if($t.startTicks -ne $cursor){throw 'qa_history_service_cost_mismatch'};$cursor=$t.endTicks}
    if($cursor -ne $service.endTicks -or $timings.mutation[0].startTicks -lt $timings.apply[0].startTicks -or $timings.mutation[0].endTicks -ne $timings.commit[0].startTicks -or $timings.commit[0].endTicks -ne $timings.apply[0].endTicks){throw 'qa_history_service_cost_mismatch'}
    $setup=$timings.fixture_setup[0]
    foreach($name in @('source_generation','native_seed')){if($timings[$name][0].startTicks -lt $setup.startTicks -or $timings[$name][0].endTicks -gt $setup.endTicks){throw 'qa_history_timing_bounds_invalid'}}
    $win7=$false
    if($window.targetProfile -ceq 'win7-sp1'){
        if($null -eq $Expected -or $Expected.PSObject.Properties.Name -notcontains 'measurementTargetProfile' -or $Expected.measurementTargetProfile -cne 'win7-sp1' -or
            $Expected.PSObject.Properties.Name -notcontains 'measurementTargetIdentitySha256' -or [string]$Expected.measurementTargetIdentitySha256 -cnotmatch '^[a-f0-9]{64}$' -or
            $window.PSObject.Properties.Name -notcontains 'targetIdentitySha256' -or $window.targetIdentitySha256 -cne $Expected.measurementTargetIdentitySha256 -or
            $Receipt.PSObject.Properties.Name -notcontains 'process' -or $Receipt.process.os -cnotmatch '^6\.1(?:\.\d+){0,2}$'){throw 'qa_history_target_profile_unqualified'}
        $win7=$true
    }elseif($null -ne $Expected -and $Expected.PSObject.Properties.Name -contains 'measurementTargetProfile' -and $null -ne $Expected.measurementTargetProfile -and $Expected.measurementTargetProfile -cne 'reference-windows'){throw 'qa_history_target_profile_unqualified'}
    if($m.analysisMs -gt $(if($win7){60000}else{30000}) -or $m.applyMs -gt $(if($win7){30000}else{15000}) -or $m.queryMs -gt $(if($win7){1000}else{500}) -or $m.ownershipColdMs -gt $(if($win7){60000}else{30000}) -or
        $m.backupMs -gt 60000 -or $m.rollbackBackupMs -gt 60000 -or $m.rollbackMs -gt 10000 -or $m.cancelMs -gt 1000 -or $m.analysisCancelMs -gt 1000 -or $peak -gt 700MB -or $peak-$m.privateBytesBefore -gt 150MB -or $p95 -gt 100 -or $maximum -gt 500){throw 'qa_history_threshold_failed'}
}

function ValidateHistoricalEvidenceStructureAndPrivacy {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Receipt, [Parameter(Mandatory)][ValidateSet('Smoke','Load','CrashProbe','CloudSync')][string]$Mode,
        [Parameter(Mandatory)]$Expected)
    $contract = Get-HistoricalSalesQaContract $Mode
    $envelope=@('schemaVersion','protocolVersion','runId','runNonce','sourceSha','sourceFingerprintSha256','harnessSha256','wpfSha256','fixtureVersion','fixtureSha256','timePolicyVersion','startedAtUtc','completedAtUtc','startedTicks','completedTicks','durationMs','completed','status','process','summary','results','lastCheckpoint','measurementCompleted','environmentValid','thresholdsPassed','metrics','samples','memorySamples','phases','timings','measurement','workerRawExitCode','workerRawStatus','verifierExitCode')
    Assert-HistoryKeys $Receipt $envelope @('schemaVersion','protocolVersion','runId','runNonce','sourceSha','sourceFingerprintSha256','harnessSha256','wpfSha256','fixtureVersion','fixtureSha256','completed','status','summary','results')
    if ($Receipt.schemaVersion -cne $contract.schemaVersion -or $Receipt.protocolVersion -ne $contract.protocolVersion) { throw 'qa_history_schema_mismatch' }
    foreach ($field in @('runId','runNonce','sourceSha','sourceFingerprintSha256','harnessSha256','wpfSha256','fixtureVersion','fixtureSha256')) {
        if ($null -eq $Expected.$field -or $Receipt.$field -cne $Expected.$field) { throw 'qa_history_identity_mismatch' }
    }
    if ($Receipt.completed -isnot [bool] -or $Receipt.status -cnotin @('PASS','FAIL','TIMEOUT','NOT_RUN','BLOCKED_EXTERNAL')) { throw 'qa_history_status_invalid' }
    $known = @($contract.scenarios.Keys) + @($contract.scenarios.Values | ForEach-Object { $_ })
    if ($Receipt.PSObject.Properties.Name -contains 'lastCheckpoint' -and $null -ne $Receipt.lastCheckpoint -and $Receipt.lastCheckpoint -cnotin (@('prepared','precommit_after_spill','verifying','completed','starting')+$known)) { throw 'qa_history_checkpoint_invalid' }
    foreach ($field in @('startedAtUtc','completedAtUtc')) {
        if ($Receipt.PSObject.Properties.Name -contains $field -and $null -ne $Receipt.$field) {
            $date=[DateTimeOffset]::MinValue
            if ([string]$Receipt.$field -cnotmatch '^\d{4}-\d{2}-\d{2}T[0-9:.]+(?:Z|\+00:00)$' -or -not [DateTimeOffset]::TryParse($Receipt.$field,[ref]$date)) { throw 'qa_history_time_invalid' }
        }
    }
    if ($Receipt.PSObject.Properties.Name -contains 'durationMs') { Assert-HistoryNumber $Receipt.durationMs }
    foreach($field in @('startedTicks','completedTicks')){if($Receipt.PSObject.Properties.Name -contains $field -and $null -ne $Receipt.$field){Assert-HistoryNumber $Receipt.$field -Integral}}
    if ($Receipt.PSObject.Properties.Name -contains 'timePolicyVersion' -and $Receipt.timePolicyVersion -cnotin @('synthetic-fixed-offset-v1','getnet-time-policy-v1')) { throw 'qa_history_policy_invalid' }
    if ($Receipt.PSObject.Properties.Name -contains 'process') {
        Assert-HistoryKeys $Receipt.process @('pid','creationTimeUtc','architecture','clr','os') @('pid','architecture')
        Assert-HistoryNumber $Receipt.process.pid -Integral
        if ($Receipt.process.architecture -cne 'x86') { throw 'qa_history_architecture_invalid' }
        foreach ($field in @('clr','os')) { if ($Receipt.process.PSObject.Properties.Name -contains $field -and [string]$Receipt.process.$field -cnotmatch '^\d+(?:\.\d+){1,3}$') { throw 'qa_history_process_text_invalid' } }
        if ($Receipt.process.PSObject.Properties.Name -contains 'creationTimeUtc' -and [string]$Receipt.process.creationTimeUtc -cnotmatch '^\d{4}-\d{2}-\d{2}T[0-9:.]+(?:Z|\+00:00)$') { throw 'qa_history_process_time_invalid' }
    }
    Assert-HistoryKeys $Receipt.summary @('expected','executed','passed','failed','notRun','timedOut','checksTotal','checksPassed','checksFailed') @('expected','executed','passed','failed','notRun','timedOut')
    foreach ($field in $Receipt.summary.PSObject.Properties.Name) { Assert-HistoryNumber $Receipt.summary.$field -Integral }
    if ($Receipt.summary.expected -ne $contract.scenarios.Count -or $Receipt.summary.executed -ne @($Receipt.results).Count) { throw 'qa_history_summary_mismatch' }
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($result in @($Receipt.results)) {
        Assert-HistoryKeys $result @('id','status','rawStatus','completed','durationMs','assertionResults','counters','lastCheckpoint') @('id','completed','assertionResults')
        if (-not $contract.scenarios.Contains($result.id) -or -not $seen.Add($result.id)) { throw 'qa_history_scenario_invalid' }
        if ($result.completed -isnot [bool]) { throw 'qa_history_status_invalid' }
        if ($result.PSObject.Properties.Name -contains 'lastCheckpoint' -and $result.lastCheckpoint -cnotin $known) {throw 'qa_history_checkpoint_invalid'}
        foreach ($field in @('status','rawStatus')) { if ($result.PSObject.Properties.Name -contains $field -and $result.$field -cnotin @('PASS','FAIL','TIMEOUT','NOT_RUN','BLOCKED_EXTERNAL')) { throw 'qa_history_status_invalid' } }
        if ($result.PSObject.Properties.Name -contains 'durationMs') { Assert-HistoryNumber $result.durationMs }
        $assertions=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($assertion in @($result.assertionResults)) {
            Assert-HistoryKeys $assertion @('id','status','completed','expected','actual','code','durationMs') @('id','status')
            if ($assertion.id -cnotin $contract.scenarios[$result.id] -or -not $assertions.Add($assertion.id)) { throw 'qa_history_assertion_invalid' }
            if ($assertion.status -cnotin @('PASS','FAIL','TIMEOUT','NOT_RUN')) { throw 'qa_history_status_invalid' }
            foreach ($field in @('expected','actual')) {
                if ($assertion.PSObject.Properties.Name -contains $field -and $null -ne $assertion.$field) {
                    if ($assertion.$field -is [string]) { if ($assertion.$field -cnotin @('PASS','FAIL','absent','present','denied','allowed','unchanged','unavailable','x86','DELETE','FULL')) { throw 'qa_history_assertion_text_invalid' } }
                    elseif ($assertion.$field -isnot [bool]) { Assert-HistoryNumber $assertion.$field -Signed }
                }
            }
            if ($assertion.PSObject.Properties.Name -contains 'code' -and $assertion.code -cnotin @('none','assertion_failed','not_run','timed_out','unavailable')) { throw 'qa_history_assertion_code_invalid' }
        }
        if ($result.PSObject.Properties.Name -contains 'counters') {
            Assert-HistoryKeys $result.counters @('rows','sales','cancellations','rejected','accepted','new','duplicates','conflicts','balanceClp','nativeRows','historicalRows','revisionBefore','revisionAfter')
            foreach ($field in $result.counters.PSObject.Properties.Name) { Assert-HistoryNumber $result.counters.$field -Integral -Signed:($field -eq 'balanceClp') }
        }
    }
    foreach ($pair in @(@{field='passed';status='PASS'},@{field='failed';status='FAIL'},@{field='notRun';status='NOT_RUN'},@{field='timedOut';status='TIMEOUT'})) {
        $count=@($Receipt.results|Where-Object { $(if($_.PSObject.Properties.Name -contains 'rawStatus'){$_.rawStatus}else{$_.status}) -ceq $pair.status }).Count
        if ($Receipt.summary.($pair.field) -ne $count) {throw 'qa_history_summary_mismatch'}
    }
    foreach($field in @('checksTotal','checksPassed','checksFailed')) {
        if($Receipt.summary.PSObject.Properties.Name -contains $field){
            $all=@($Receipt.results|ForEach-Object {$_.assertionResults})
            $observed=switch($field){checksTotal{$all.Count} checksPassed{@($all|Where-Object status -ceq 'PASS').Count} checksFailed{@($all|Where-Object status -ceq 'FAIL').Count}}
            if($Receipt.summary.$field -ne $observed){throw 'qa_history_summary_mismatch'}
        }
    }
    foreach ($field in @('measurementCompleted','environmentValid','thresholdsPassed')) { if ($Receipt.PSObject.Properties.Name -contains $field -and $Receipt.$field -isnot [bool]) { throw 'qa_history_measurement_flag_invalid' } }
    foreach ($field in @('workerRawExitCode','verifierExitCode')) {if ($Receipt.PSObject.Properties.Name -contains $field -and $null -ne $Receipt.$field) {Assert-HistoryNumber $Receipt.$field -Integral -Signed}}
    if ($Receipt.PSObject.Properties.Name -contains 'workerRawStatus' -and $Receipt.workerRawStatus -cne 'FAIL') {throw 'qa_history_worker_status_invalid'}
    if($Mode -eq 'Load'){Assert-HistoryMeasurementStructure $Receipt}
    elseif(@(@('measurement','phases','timings','samples','memorySamples','metrics')|Where-Object{$Receipt.PSObject.Properties.Name -contains $_}).Count -ne 0){throw 'qa_history_receipt_privacy_rejected'}
    return $Receipt
}

function Assert-HistoricalSalesQaPass {
    param([Parameter(Mandatory)]$Receipt, [Parameter(Mandatory)][ValidateSet('Smoke','Load','CrashProbe','CloudSync')][string]$Mode, [Parameter(Mandatory)]$Expected)
    $null = ValidateHistoricalEvidenceStructureAndPrivacy $Receipt $Mode $Expected
    $contract=Get-HistoricalSalesQaContract $Mode
    if ($Receipt.PSObject.Properties.Name -notcontains 'process') {throw 'qa_history_process_missing'}
    if (-not $Receipt.completed -or $Receipt.status -cne 'PASS' -or @($Receipt.results).Count -ne $contract.scenarios.Count -or $Receipt.summary.passed -ne $contract.scenarios.Count -or $Receipt.summary.failed -ne 0 -or $Receipt.summary.notRun -ne 0 -or $Receipt.summary.timedOut -ne 0) { throw 'qa_history_incomplete_tests' }
    foreach ($result in $Receipt.results) {
        $status=if ($result.PSObject.Properties.Name -contains 'rawStatus') { $result.rawStatus } else { $result.status }
        if (-not $result.completed -or $status -cne 'PASS' -or @($result.assertionResults).Count -ne $contract.scenarios[$result.id].Count -or @($result.assertionResults | Where-Object status -cne 'PASS').Count) { throw 'qa_history_incomplete_assertions' }
    }
    if ($Receipt.durationMs -gt $contract.bodySeconds*1000) { throw 'qa_history_body_budget_exceeded' }
    if ($Mode -eq 'CrashProbe' -and ($Receipt.workerRawExitCode -ne 79 -or $Receipt.workerRawStatus -cne 'FAIL' -or $Receipt.verifierExitCode -ne 0)) {throw 'qa_history_crash_cause_unproven'}
    if ($Mode -eq 'Load') { Assert-HistoricalSalesMeasurement $Receipt $Expected }
}

Export-ModuleMember -Function Get-HistoricalSalesQaContract,Read-HistoricalSalesQaJson,ValidateHistoricalEvidenceStructureAndPrivacy,Assert-HistoricalSalesQaPass,Assert-HistoricalSalesMeasurement
