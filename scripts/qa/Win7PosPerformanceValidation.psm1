Set-StrictMode -Version Latest

function Test-Win7PosPerformance {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][object]$Budget,
        [Parameter(Mandatory)][double]$RequiredSeconds,
        [Parameter(Mandatory)][bool]$ProcessCompleted,
        [ValidateSet(20000,100000)][int]$ExpectedProducts = 100000
    )
    $measurement = [Collections.Generic.List[string]]::new()
    $environment = [Collections.Generic.List[string]]::new()
    $stability = [Collections.Generic.List[string]]::new()
    $metrics = [ordered]@{}
    function Number($row, [string]$name) {
        $property = $row.PSObject.Properties[$name]
        $value = 0.0
        if ($null -eq $property -or -not [double]::TryParse([string]$property.Value,
                [Globalization.NumberStyles]::Float, [cultureinfo]::InvariantCulture, [ref]$value) -or
                [double]::IsNaN($value) -or [double]::IsInfinity($value) -or $value -lt 0) {
            throw "invalid_numeric_field:$name"
        }
        return $value
    }
    function P95([double[]]$values) {
        if ($values.Count -eq 0) { throw 'missing_warm_samples' }
        $ordered = @($values | Sort-Object)
        return $ordered[[Math]::Ceiling(.95 * $ordered.Count) - 1]
    }
    if (-not $ProcessCompleted) { $measurement.Add('process_not_completed_normally') }
    try {
        if ([double]::IsNaN($RequiredSeconds) -or [double]::IsInfinity($RequiredSeconds) -or $RequiredSeconds -le 0 -or
            $Budget.schemaVersion -cne 'win7pos-performance-budget-v1') { throw 'invalid_qualification_contract' }
        foreach ($field in @('warmupCycles','serviceP95Ms','sendP95Ms','uiP95Ms','uiMaximumMs','probeMaximumMs',
            'pendingMaximum','oldestMaximumMs','privateMaximumBytes','managedMaximumBytes',
            'managedWindowGrowthBytes','memoryWindowCycles','cacheMaximumBytes','realizedGridMaximum','visualMaximumWaitMs')) {
            $Budget.$field = Number $Budget $field
        }
        foreach ($field in @('warmupCycles','memoryWindowCycles','pendingMaximum','realizedGridMaximum')) {
            if ($Budget.$field -ne [Math]::Floor($Budget.$field)) { throw 'invalid_integral_budget' }
        }
        if ($Budget.serviceP95Ms -gt 50 -or $Budget.sendP95Ms -gt 16 -or $Budget.uiP95Ms -gt 250 -or $Budget.uiMaximumMs -gt 1000 -or
            $Budget.memoryWindowCycles -lt 2 -or $Budget.warmupCycles -lt 1 -or $Budget.probeMaximumMs -ge 250 -or
            $Budget.visualMaximumWaitMs -gt 250) { throw 'budget_exceeds_fixed_latency_contract' }
        $scans = @(Import-Csv -LiteralPath (Join-Path $Directory 'qualification-scans.csv') -ErrorAction Stop)
        $idle = @(Import-Csv -LiteralPath (Join-Path $Directory 'qualification-idle.csv') -ErrorAction Stop)
        $receipt = Get-Content -LiteralPath (Join-Path $Directory 'qualification-measurement.json') -Raw -ErrorAction Stop | ConvertFrom-Json
        if ($receipt.schemaVersion -cne 'win7pos-performance-measurement-v1' -or $receipt.measurementCompleted -isnot [bool] -or $receipt.measurementCompleted -ne $true -or
            $receipt.cycles -ne $idle.Count -or $receipt.products -ne $ExpectedProducts -or $receipt.cartSize -ne 500 -or $receipt.protocolVersion -notin @(3,4)) { throw 'invalid_measurement_receipt' }
        # Preserve v3 historical verdicts; v4 additionally proves fixture readiness.
        if ($receipt.protocolVersion -eq 4) {
            $setup = Get-Content -LiteralPath (Join-Path $Directory 'fixture-setup.json') -Raw -ErrorAction Stop | ConvertFrom-Json
            if ($setup.schemaVersion -cne 'win7pos-fixture-setup-v1' -or $setup.completed -isnot [bool] -or $setup.completed -ne $true -or
                $setup.legacyDiagnostic -isnot [bool] -or $setup.legacyDiagnostic -ne $false -or $setup.cartSize -ne 500 -or
                (Number $setup 'realizedRows') -lt 1 -or (Number $setup 'elapsedMs') -ge 10000) { throw 'invalid_fixture_setup' }
        }
        if ($receipt.environmentValid -isnot [bool] -or $receipt.environmentValid -ne $true) { $environment.Add('unqualified_desktop:receipt') }
        $serviceSamples = @(Import-Csv -LiteralPath (Join-Path $Directory 'cart-performance.csv') -ErrorAction Stop)
        if ($serviceSamples.Count -ne 155) { throw 'incomplete_isolated_service_benchmark' }
        $serviceKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($row in $serviceSamples) {
            foreach ($field in @('products','cart','sample','ms','dispatcher_probe_ms','product_commands','commands_on_dispatcher')) { $null = Number $row $field }
            if ([double]$row.products -ne $receipt.products) { throw 'benchmark_product_count_mismatch' }
            if ([double]$row.cart -ne [int]$row.cart -or [int]$row.cart -notin @(1,10,50,100,500) -or [double]$row.sample -ne [int]$row.sample -or [int]$row.sample -gt 30 -or
                -not $serviceKeys.Add("$([int]$row.cart)/$([int]$row.sample)")) { throw 'invalid_isolated_service_sample_identity' }
            if ([double]$row.product_commands -gt 2 -or [double]$row.commands_on_dispatcher -ne 0) { $stability.Add('product_query_batch_or_dispatcher_violation') }
        }
        $metrics.isolatedServiceP95Ms = P95 @($serviceSamples | Where-Object { [int]$_.cart -eq 500 -and [int]$_.sample -gt 0 } | ForEach-Object { [double]$_.ms })
        $metrics.sendProbeP95Ms = P95 @($serviceSamples | Where-Object { [int]$_.sample -gt 0 } | ForEach-Object { [double]$_.dispatcher_probe_ms })
        if ($metrics.isolatedServiceP95Ms -gt $Budget.serviceP95Ms) { $stability.Add('isolated_service_p95') }
        if ($metrics.sendProbeP95Ms -gt $Budget.sendP95Ms) { $stability.Add('send_probe_p95') }
        if ($idle.Count -lt $Budget.warmupCycles + 2 * $Budget.memoryWindowCycles) { throw 'insufficient_equivalent_idle_cycles' }
        $previousTime = -1.0
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        for ($index = 0; $index -lt $idle.Count; $index++) {
            $row = $idle[$index]
            if ((Number $row 'cycle') -ne $index) { throw 'missing_or_duplicate_cycle' }
            $time = Number $row 'elapsed_s'
            if ($time -le $previousTime) { throw 'non_monotonic_elapsed_time' }
            $previousTime = $time
            foreach ($probe in @('input_ms','render_ms','databind_ms','background_ms')) {
                $property = $row.PSObject.Properties[$probe]
                # The harness writes Infinity only when its bounded probe did
                # not complete. Preserve that specific cause before rejecting
                # the non-finite measurement below.
                if ($null -ne $property -and $property.Value -ceq 'Infinity') { $measurement.Add("priority_probe_timeout:${probe}:cycle=$index") }
            }
            foreach ($field in @('private_bytes','managed_bytes','pending','oldest_ms','focus_pending','focus_oldest_ms',
                'scroll_pending','scroll_oldest_ms','closed_rooted_windows','cache_bytes','input_ms','render_ms','databind_ms','background_ms','observer_dropped',
                'focus_peak','scroll_peak','focus_maximum_wait_ms','scroll_maximum_wait_ms','metadata_entries')) {
                $null = Number $row $field
            }
            if ((Number $row 'environment_valid') -ne 1) { $environment.Add("unqualified_desktop:cycle=$index") }
            if ((Number $row 'suspended') -ne 0) { $environment.Add("suspend_or_clock_discontinuity:cycle=$index") }
            if ((Number $row 'observer_dropped') -ne 0) { $measurement.Add("observer_overflow:cycle=$index") }
            if ($index -lt $Budget.warmupCycles) { continue }
            if ([double]$row.pending -gt $Budget.pendingMaximum -or [double]$row.oldest_ms -gt $Budget.oldestMaximumMs) { $stability.Add("dispatcher_backlog:cycle=$index") }
            if ([double]$row.focus_pending -ne 0 -or [double]$row.scroll_pending -ne 0 -or
                [double]$row.focus_peak -gt 1 -or [double]$row.scroll_peak -gt 1 -or
                [double]$row.focus_maximum_wait_ms -gt $Budget.visualMaximumWaitMs -or [double]$row.scroll_maximum_wait_ms -gt $Budget.visualMaximumWaitMs) { $stability.Add("application_visual_backlog:cycle=$index") }
            foreach ($field in @('input_ms','render_ms','databind_ms','background_ms')) {
                if ([double]$row.$field -gt $Budget.probeMaximumMs) { $stability.Add("priority_probe_failed:${field}:cycle=$index") }
            }
            if ([double]$row.closed_rooted_windows -ne 0) { $stability.Add("closed_window_rooted:cycle=$index") }
            if ([double]$row.private_bytes -gt $Budget.privateMaximumBytes -or [double]$row.managed_bytes -gt $Budget.managedMaximumBytes) { $stability.Add("idle_memory_ceiling:cycle=$index") }
            if ([double]$row.cache_bytes -gt $Budget.cacheMaximumBytes) { $stability.Add("cache_ceiling:cycle=$index") }
            if ([double]$row.metadata_entries -gt 512) { $stability.Add("metadata_cache_ceiling:cycle=$index") }
        }
        if ($previousTime -lt $RequiredSeconds) { $measurement.Add('incomplete_duration') }
        $metrics.elapsedSeconds = $previousTime
        foreach ($row in $scans) {
            foreach ($field in @('cycle','ordinal','service_ms','ui_return_ms','apply_ms','layout_ms','bitmap_ms','command_overhead_ms','realized_grid','product_commands','commands_on_dispatcher','collection_changes','focus_scroll_wait_ms')) { $null = Number $row $field }
            if ([double]$row.product_commands -gt 2 -or [double]$row.commands_on_dispatcher -ne 0) { $stability.Add('public_product_query_batch_or_dispatcher_violation') }
            if ([double]$row.collection_changes -ne 0) { $stability.Add('public_scan_rebuilt_collection') }
            $cycle = [int]$row.cycle
            $ordinal = [int]$row.ordinal
            if ($cycle -ne [double]$row.cycle -or $cycle -ge $idle.Count -or $ordinal -ne [double]$row.ordinal -or
                $ordinal -lt 1 -or $ordinal -gt 10 -or $row.mode -cnotin @('Rows','Grid') -or
                -not $seen.Add("$cycle/$($row.mode)/$ordinal")) { throw 'invalid_or_duplicate_scan_identity' }
            if ($cycle -ge $Budget.warmupCycles -and [double]$row.focus_scroll_wait_ms -gt $Budget.visualMaximumWaitMs) {
                $stability.Add("public_visual_wait:cycle=$cycle,mode=$($row.mode),ordinal=$ordinal")
            }
        }
        if ($scans.Count -ne $idle.Count * 20) { throw 'missing_scan_samples' }
        foreach ($mode in @('Rows','Grid')) {
            $warm = @($scans | Where-Object { [int]$_.cycle -ge $Budget.warmupCycles -and $_.mode -ceq $mode })
            if ($warm.Count -lt 30) { throw 'fewer_than_30_warm_samples' }
            $service = @($warm | ForEach-Object { [double]$_.service_ms })
            $ui = @($warm | ForEach-Object { [double]$_.service_ms + [double]$_.ui_return_ms + [double]$_.apply_ms + [double]$_.layout_ms + [double]$_.command_overhead_ms })
            $metrics["${mode}ServiceP95Ms"] = P95 $service
            $metrics["${mode}UiP95Ms"] = P95 $ui
            $metrics["${mode}UiMaximumMs"] = ($ui | Measure-Object -Maximum).Maximum
            $metrics["${mode}BitmapP95Ms"] = P95 @($warm | ForEach-Object { [double]$_.bitmap_ms })
            if ($metrics["${mode}ServiceP95Ms"] -gt $Budget.serviceP95Ms) { $stability.Add("service_p95:$mode") }
            if ($metrics["${mode}UiP95Ms"] -gt $Budget.uiP95Ms) { $stability.Add("ui_p95:$mode") }
            if ($metrics["${mode}UiMaximumMs"] -gt $Budget.uiMaximumMs) { $stability.Add("ui_stall:$mode") }
            if (@($warm | Where-Object { [double]$_.realized_grid -gt $Budget.realizedGridMaximum }).Count) { $stability.Add("unbounded_grid_realization:$mode") }
        }
        $memory = @($idle | Select-Object -Skip $Budget.warmupCycles | ForEach-Object { [double]$_.managed_bytes })
        $window = [int]$Budget.memoryWindowCycles
        $reference = ($memory | Select-Object -First $window | Measure-Object -Average).Average
        $maximumWindow = $reference
        for ($start = 1; $start -le $memory.Count - $window; $start++) {
            $mean = ($memory[$start..($start + $window - 1)] | Measure-Object -Average).Average
            $maximumWindow = [Math]::Max($maximumWindow, $mean)
        }
        $metrics.maximumManagedWindowGrowthBytes = $maximumWindow - $reference
        if ($maximumWindow - $reference -gt $Budget.managedWindowGrowthBytes) { $stability.Add('persistent_or_intermediate_memory_growth') }
    }
    catch { $measurement.Add($_.Exception.Message) }
    $completed = $measurement.Count -eq 0
    $valid = $completed -and $environment.Count -eq 0
    return [pscustomobject][ordered]@{
        MEASUREMENT_COMPLETED = $completed
        ENVIRONMENT_VALID = $valid
        STABILITY_PASS = $valid -and $stability.Count -eq 0
        measurementReasons = @($measurement.ToArray())
        environmentReasons = @($environment.ToArray())
        stabilityReasons = @($stability.ToArray())
        metrics = [pscustomobject]$metrics
    }
}

Export-ModuleMember -Function Test-Win7PosPerformance
