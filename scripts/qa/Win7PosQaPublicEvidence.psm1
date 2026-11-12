Set-StrictMode -Version Latest

function Get-QaPublicCatalog {
    $functional=@('FUNCTIONAL_VISUAL','OPERATOR_DIALOG_GEOMETRY','AUTHENTICATED_SHELL_UX','IMPORT_completion','IMPORT_RECOVERY_completion','PRICE_HISTORY_completion','REFUND_completion','REVERSAL_authorization','F06_reversed_preview_delete_close','F01_export_gate','F07_settings_rollback','HARDWARE_settings_scanner','CUSTOMERDISPLAY_polish','CUSTOMERDISPLAY_completion','STOCK_fractional_input','F02_F03_workflow_stable_identity_batch_dispatcher','F05_same_clock_recovery_restart_active_cart','F08_discount_preview_lifetime','INTEGRATED_login_sale_retry_receipt_restart','LOAD_sync_backup_x86','P109_product_editor_late_render_close','PERF_visual_lifetime_and_public_commands')
    $steps=@('preflight','restore','restore-cli','gates','build','build-cli','core','selftest','build-wpf','build-harness','product-image-profile','functional-completion','authorization-lease','bounded-logging','product-paging-dispatcher','historical-sales-import-crash','historical-sales-import-smoke','historical-sales-import-load','historical-sales-import-cloudsync','source-identity','report','cleanup','public-evidence','unknown')
    return @{ functional=$functional; steps=$steps; statuses=@('PASS','FAIL','TIMEOUT','NOT_RUN','BLOCKED_EXTERNAL'); codes=@('process_exit','process_timeout','cancelled','dependency_failed','mock_exit','qa_step_failed','qa_unclassified_failure','qa_report_write_failed','qa_public_bundle_failed','qa_public_collision','qa_sources_changed_during_run','qa_history_receipt_privacy_rejected','qa_history_receipt_missing','missing_or_invalid_test_evidence','qa_smoke_evidence_export_failed','qa_previous_process_cleanup_failed','qa_step_preparation_or_supervision_failed','owned_process_cleanup_failed','qa_source_identity_missing','qa_checkout_build_locked','qa_public_projection_incomplete','qa_safe_console_failed','none','assertion_failed','not_run','timed_out','unavailable') }
}

function ConvertTo-QaPublicCode($Code) {
    if ([string]$Code -cin (Get-QaPublicCatalog).codes) { return [string]$Code }
    return 'qa_unclassified_failure'
}

function Write-QaPublicConsoleEvent {
    param([string]$RunId='', [string]$Suite='', [string]$StepId='unknown', [string]$Status='FAIL', [string]$Code='qa_step_failed', [string]$SecondaryCode='none',[string]$PrimaryStepId='unknown',[string]$PublicationStatus='NOT_RUN',[string]$PublicationCode='none')
    try {
        $catalog=Get-QaPublicCatalog
        $safeRun=if ($RunId -cmatch '^Win7POS\.QA\.\d{8}T\d{9}Z\.[a-f0-9]{32}$' -or $RunId -cmatch '^synthetic-[a-z0-9-]{1,48}$') { $RunId } else { 'unavailable' }
        $safeStep=if ($StepId -cin $catalog.steps) { $StepId } else { 'unknown' }
        $safeSuite=if ($Suite -cin @('Local','Windows','Staging','Win7','Hardware')) { $Suite } else { 'unknown' }
        $safeStatus=if ($Status -cin $catalog.statuses) { $Status } else { 'FAIL' }
        $safeCode=ConvertTo-QaPublicCode $Code
        if($safeStatus -cne 'PASS' -and $safeCode -ceq 'none'){$safeCode=switch($safeStatus){TIMEOUT{'process_timeout'} NOT_RUN{'not_run'} BLOCKED_EXTERNAL{'dependency_failed'} default{'qa_step_failed'}}}
        $safePublication=if($PublicationStatus -cin @('PASS','FAIL','NOT_RUN')){$PublicationStatus}else{'NOT_RUN'}
        $safePublicationCode=ConvertTo-QaPublicCode $PublicationCode
        if($safePublication -ceq 'FAIL' -and $safePublicationCode -ceq 'none'){$safePublicationCode='qa_public_bundle_failed'}
        $dto=[ordered]@{ schemaVersion='win7pos-qa-console-v1';runId=$safeRun;suite=$safeSuite;stepId=$safeStep;rawStatus=$safeStatus;code=$safeCode;secondaryCode=(ConvertTo-QaPublicCode $SecondaryCode);primaryStepId=$(if($PrimaryStepId -cin $catalog.steps){$PrimaryStepId}else{'unknown'});publicationStatus=$safePublication;publicationCode=$safePublicationCode }
        [Console]::Error.WriteLine(($dto | ConvertTo-Json -Compress))
    } catch { [Console]::Error.WriteLine('WIN7POS_QA_PUBLIC_EVENT_FAILED code=qa_safe_console_failed') }
}

function Assert-QaPublicPath([string]$Path, [string]$Root='') {
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw 'qa_public_absolute_path_required' }
    $full=[IO.Path]::GetFullPath($Path)
    if ($Root) {
        $parent=[IO.Path]::GetFullPath($Root).TrimEnd('\','/')
        if (-not $full.StartsWith($parent+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'qa_public_path_outside_root' }
    }
    for ($cursor=$full; $cursor; $cursor=[IO.Path]::GetDirectoryName($cursor)) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'qa_public_link_rejected' }
    }
    return $full
}

function Get-QaPublicSha256([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

function Test-QaPublicField($Value,[string]$Name) {
    if($null -eq $Value){return $false}
    if($Value -is [Collections.IDictionary]){return $Value.Contains($Name)}
    return $Value.PSObject.Properties.Name -contains $Name
}

function ConvertTo-QaPublicScalar($Value) {
    if ($null -eq $Value -or $Value -is [bool]) { return $Value }
    if ($Value -is [int] -or $Value -is [long] -or $Value -is [double] -or $Value -is [decimal]) {
        if ([double]::IsNaN([double]$Value) -or [double]::IsInfinity([double]$Value)) { throw 'qa_public_invalid_numeric' }
        return $Value
    }
    if ($Value -is [string] -and $Value -cin @('PASS','FAIL','NOT_RUN','TIMEOUT','absent','present','denied','allowed','unchanged','unavailable','x86','DELETE','FULL','true','false','clock_rollback','last_server_time_invalid','local_receipt_time_invalid','offline_attestation_invalid','offline_attestation_required','offline_lease_expired','session_expiry_invalid','session_window_invalid','trusted_session_missing','trusted_time_continuity_lost','sync_generation_inactive')) { return $Value }
    throw 'qa_public_scalar_withheld'
}

function Test-QaPublicAssertionId([string]$Id,[string]$RepoRoot) {
    if($Id -cmatch '^GATE_([A-Za-z0-9_-]+)_L([0-9]+)$'){
        $stem=$Matches[1];$line=[int]$Matches[2]
        foreach($folder in @('scripts','tests/qa')){foreach($file in Get-ChildItem -LiteralPath (Join-Path $RepoRoot $folder) -File -Recurse | Where-Object {$_.BaseName -ceq $stem -and $_.Extension -cin @('.ps1','.js','.mjs')}){$null=Assert-QaPublicPath $file.FullName $RepoRoot;if($line -gt 0 -and $line -le [IO.File]::ReadAllLines($file.FullName).Length){return $true}}}
        return $false
    }
    if($Id -cmatch '^TRX_(Win7POS\.Core\.Tests(?:\.[A-Za-z_][A-Za-z0-9_]*)+)\.([A-Za-z_][A-Za-z0-9_]*)$'){
        $qualified=$Matches[1];$method=$Matches[2];$split=$qualified.LastIndexOf('.');$namespace=$qualified.Substring(0,$split);$class=$qualified.Substring($split+1)
        foreach($file in Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'tests/Win7POS.Core.Tests') -Filter '*.cs' -File -Recurse){
            $text=[IO.File]::ReadAllText($file.FullName)
            if($text -match ('\bnamespace\s+'+[regex]::Escape($namespace)+'\b') -and $text -match ('\bclass\s+'+[regex]::Escape($class)+'\b') -and $text -match ('\b'+[regex]::Escape($method)+'\s*\(')){return $true}
        }
        return $false
    }
    if($Id -cnotmatch '^QA_([A-Za-z0-9_]+)_L([0-9]+)$'){return $false}
    $file=Join-Path $RepoRoot ('tests/Win7POS.Wpf.UiSmokeHarness/'+$Matches[1]+'.cs');$line=[int]$Matches[2]
    if(-not(Test-Path -LiteralPath $file -PathType Leaf)){return $false}
    $null=Assert-QaPublicPath $file $RepoRoot
    return $line -gt 0 -and $line -le [IO.File]::ReadAllLines($file).Length
}

function Get-QaTypedDiagnostics {
    param([Parameter(Mandatory)]$Report,[Parameter(Mandatory)][string]$RepoRoot)
    $events=[Collections.Generic.List[object]]::new()
    $harness=Join-Path $RepoRoot 'tests/Win7POS.Wpf.UiSmokeHarness/bin/x86/Release/net48/Win7POS.Wpf.UiSmokeHarness.exe'
    $harnessHash=if(Test-Path -LiteralPath $harness -PathType Leaf){Get-QaPublicSha256 $harness}else{$null}
    $fingerprint=if((Test-QaPublicField $Report 'sourceState') -and $null -ne $Report.sourceState){$Report.sourceState.fingerprintSha256}else{$null}
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($row in @($Report.results)){
        if($row.name -cnotin @('product-image-profile','functional-completion','authorization-lease','bounded-logging','product-paging-dispatcher')){continue}
        $inputRoot=if(Test-QaPublicField $row 'workDirectory'){$row.workDirectory}else{$null}
        if($row.name -ceq 'functional-completion' -and (Test-QaPublicField $row 'tests')){
            # The canonical child TEMP is separate from its copied evidence.
            # Follow only a recognized functional case's bounded export root;
            # never scan the common TEMP base or arbitrary profile JSON.
            $case=@($row.tests|Where-Object {(Test-QaPublicField $_ 'name') -and $_.name -cin (Get-QaPublicCatalog).functional -and
                (Test-QaPublicField $_ 'evidenceDirectory') -and $_.evidenceDirectory}|Select-Object -First 1)
            if($case.Count){$inputRoot=[IO.Path]::GetDirectoryName((Assert-QaPublicPath $case[0].evidenceDirectory))}
        }
        if(-not $inputRoot -or -not(Test-Path -LiteralPath $inputRoot -PathType Container)){
            $events.Add([pscustomobject]@{stepId=$row.name;scenarioId=$null;assertionId=$null;status='NOT_RUN';expected=$true;actual=$null;checkpoint='unavailable';code='unavailable';sequence=0;diagnosticCompleteness='partial'});continue
        }
        $root=Assert-QaPublicPath $inputRoot;$queue=[Collections.Generic.Queue[object]]::new();$queue.Enqueue(@{path=$root;depth=0});$count=0;$found=$false
        while($queue.Count){
            $current=$queue.Dequeue()
            foreach($path in [IO.Directory]::EnumerateFileSystemEntries($current.path)){
                if(++$count -gt 4096){throw 'qa_public_diagnostic_scan_limit'}
                $full=Assert-QaPublicPath $path $root;$item=Get-Item -LiteralPath $full -Force
                if($item.PSIsContainer){if($current.depth -ge 8){throw 'qa_public_diagnostic_scan_limit'};$queue.Enqueue(@{path=$full;depth=$current.depth+1});continue}
                if($item.Name -cne 'qa-diagnostics.json' -or -not $seen.Add($item.FullName)){continue}
                $found=$true
                try{
                    $sidecar=Read-HistoricalSalesQaJson $item.FullName
                    if(@($sidecar.PSObject.Properties.Name|Where-Object {$_ -cnotin @('schemaVersion','runId','sourceFingerprintSha256','harnessSha256','diagnosticCompleteness','results')}).Count -or $sidecar.schemaVersion -cne 'win7pos-qa-diagnostics-v1' -or $sidecar.runId -cne $Report.runId -or $sidecar.sourceFingerprintSha256 -cne $fingerprint -or $sidecar.harnessSha256 -cne $harnessHash -or $sidecar.diagnosticCompleteness -cnotin @('complete','partial') -or @($sidecar.results).Count -gt 2048){throw 'qa_public_diagnostic_identity_invalid'}
                    $sequence=0;$fileEvents=[Collections.Generic.List[object]]::new()
                    foreach($event in @($sidecar.results)){
                        if(@($event.PSObject.Properties.Name|Where-Object {$_ -cnotin @('sequence','stepId','scenarioId','assertionId','observationId','status','expected','actual','checkpoint','code')}).Count){throw 'qa_public_diagnostic_privacy_rejected'}
                        if($event.sequence -ne $sequence++ -or $event.stepId -cne $row.name){throw 'qa_public_diagnostic_identity_invalid'}
                        if($event.status -cnotin (Get-QaPublicCatalog).statuses -or $event.checkpoint -cnotin @('assertion','starting','completed','unavailable') -or $event.code -cnotin (Get-QaPublicCatalog).codes){throw 'qa_public_diagnostic_privacy_rejected'}
                        if((Test-QaPublicField $event 'scenarioId') -and $null -ne $event.scenarioId -and $event.scenarioId -cnotin (Get-QaPublicCatalog).functional){throw 'qa_public_diagnostic_privacy_rejected'}
                        if((Test-QaPublicField $event 'observationId') -and $event.observationId -cnotin @('scenario_success','product_image_profile_dpapi_isolated','shared_unchanged','plaintext_secrets','offline_authority_cloned','supervisor_profile_injected','cleanup_exact','staging_diagnostic_redacted','net48_request_serialization','net48_json_stringify_response','net48_storage_error_mapping','net48_code128_roundtrip','worker_alive_after_shutdown','producer_p95_us','producer_max_us')){throw 'qa_public_diagnostic_privacy_rejected'}
                        $null=ConvertTo-QaPublicScalar $event.expected;$null=ConvertTo-QaPublicScalar $event.actual
                        $sourceKnown=Test-QaPublicAssertionId $event.assertionId $RepoRoot
                        $dto=[ordered]@{stepId=$event.stepId;scenarioId=$(if(Test-QaPublicField $event 'scenarioId'){$event.scenarioId}else{$null});assertionId=$(if($sourceKnown){$event.assertionId}else{$null});status=$event.status;expected=$event.expected;actual=$event.actual;checkpoint=$event.checkpoint;code=$event.code;sequence=$event.sequence;diagnosticCompleteness=$(if($sourceKnown){$sidecar.diagnosticCompleteness}else{'partial'})}
                        if(Test-QaPublicField $event 'observationId'){$dto.observationId=$event.observationId}
                        $fileEvents.Add([pscustomobject]$dto)
                    }
                    $events.AddRange($fileEvents.ToArray())
                }catch{
                    # Unsafe/unrecognized payload stays private; the public record
                    # declares the diagnostic gap without copying its error text.
                    $events.Add([pscustomobject]@{stepId=$row.name;scenarioId=$null;assertionId=$null;status='NOT_RUN';expected=$true;actual=$null;checkpoint='unavailable';code='unavailable';sequence=0;diagnosticCompleteness='partial'})
                }
            }
        }
        if(-not $found){$events.Add([pscustomobject]@{stepId=$row.name;scenarioId=$null;assertionId=$null;status='NOT_RUN';expected=$true;actual=$null;checkpoint='unavailable';code='unavailable';sequence=0;diagnosticCompleteness='partial'})}
    }
    return $events.ToArray()
}

function Get-QaSyntheticVisualCatalog {
    $summary='Rows 1; sales 1; cancellations 0; rejected 0. Signed gross 1000 CLP; known net 1000 CLP (1/1 movements). Coverage 2000-01-02 08:00:00 → 2000-01-02 08:00:00.'
    $emptyHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]::new(0))).ToLowerInvariant()
    $summaryHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($summary))).ToLowerInvariant()
    return @(
        @{id='hourly_mixed';scenarioId='HIST07_REPORTS_EXPORT';capturePoint='hourly_mixed_realized';textRegistryId='no_text';textSha256=$emptyHash},
        @{id='hourly_negative';scenarioId='HIST07_REPORTS_EXPORT';capturePoint='hourly_negative_realized';textRegistryId='no_text';textSha256=$emptyHash},
        @{id='import_summary';scenarioId='HIST08_UI_LIFECYCLE';capturePoint='import_summary_analyzed';textRegistryId='one_row_1000_v1';textSha256=$summaryHash}
    )
}

function Read-QaSyntheticVisualEvidence {
    param([Parameter(Mandatory)][string]$SidecarPath,[Parameter(Mandatory)]$Expected,[Parameter(Mandatory)][string]$OwnedDataRoot)
    $registry=@(Get-QaSyntheticVisualCatalog);$files=[Collections.Generic.List[object]]::new();$captures=[Collections.Generic.List[object]]::new()
    $sidecar=Read-HistoricalSalesQaJson (Assert-QaPublicPath $SidecarPath)
    if(@($sidecar.PSObject.Properties.Name|Where-Object {$_ -cnotin @('schemaVersion','registryVersion','runId','runNonce','sourceFingerprintSha256','harnessSha256','fixtureSha256','captures')}).Count -or $sidecar.schemaVersion -cne 'win7pos-qa-visuals-v1' -or $sidecar.registryVersion -cne 'synthetic-history-visuals-v1' -or @($sidecar.captures).Count -ne $registry.Count){throw 'qa_public_visual_schema_invalid'}
    foreach($key in @('runId','runNonce','sourceFingerprintSha256','harnessSha256','fixtureSha256')){if($sidecar.$key -cne $Expected.$key){throw 'qa_public_visual_identity_invalid'}}
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($capture in @($sidecar.captures)){
        if(@($capture.PSObject.Properties.Name|Where-Object {$_ -cnotin @('id','scenarioId','capturePoint','textRegistryId','status','code','relativePath','pngSha256','textSha256','width','height','nativeFixture','fixtureRoot','fixtureToken','shopId')}).Count -or -not $seen.Add([string]$capture.id)){throw 'qa_public_visual_privacy_rejected'}
        $known=@($registry|Where-Object id -ceq $capture.id)
        if($known.Count -ne 1 -or $capture.scenarioId -cne $known[0].scenarioId -or $capture.capturePoint -cne $known[0].capturePoint -or $capture.textRegistryId -cne $known[0].textRegistryId -or $capture.status -cnotin @('PASS','NOT_RUN') -or $capture.code -cnotin @('none','unavailable')){throw 'qa_public_visual_registry_invalid'}
        $safe=[ordered]@{id=$capture.id;scenarioId=$capture.scenarioId;capturePoint=$capture.capturePoint;textRegistryId=$capture.textRegistryId;status=$capture.status;code=$capture.code;diagnosticCompleteness=$(if($capture.status -ceq 'PASS'){'complete'}else{'partial'})}
        if($capture.status -ceq 'PASS'){
            if($capture.code -cne 'none' -or $capture.nativeFixture -ne $true -or $capture.shopId -cne 'qa-auth-shop' -or $capture.fixtureToken -cnotmatch '^[a-f0-9]{32}$' -or $capture.textSha256 -cne $known[0].textSha256 -or $capture.relativePath -cne ('qa-visual-'+$capture.id+'.png') -or $capture.pngSha256 -cnotmatch '^[a-f0-9]{64}$' -or $capture.width -lt 8 -or $capture.width -gt 2048 -or $capture.height -lt 8 -or $capture.height -gt 1024){throw 'qa_public_visual_provenance_invalid'}
            $fixtureRoot=Assert-QaPublicPath $capture.fixtureRoot $OwnedDataRoot
            $marker=Assert-QaPublicPath (Join-Path $fixtureRoot 'pos.db.synthetic-owner') $OwnedDataRoot
            if(-not(Test-Path -LiteralPath (Join-Path $fixtureRoot 'pos.db') -PathType Leaf) -or (Get-Item -LiteralPath $marker).Length -ne 32 -or [IO.File]::ReadAllText($marker) -cne $capture.fixtureToken){throw 'qa_public_visual_fixture_invalid'}
            $image=Assert-QaPublicPath (Join-Path ([IO.Path]::GetDirectoryName($SidecarPath)) $capture.relativePath) ([IO.Path]::GetDirectoryName($SidecarPath))
            if((Get-Item -LiteralPath $image).Length -gt 2MB -or (Get-QaPublicSha256 $image) -cne $capture.pngSha256){throw 'qa_public_visual_digest_invalid'}
            $bytes=[IO.File]::ReadAllBytes($image)
            if($bytes.Length -lt 45 -or [Convert]::ToHexString($bytes,0,8) -cne '89504E470D0A1A0A'){throw 'qa_public_visual_png_invalid'}
            $offset=8;$ended=$false;$first=$true;$chunks=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            while($offset -lt $bytes.Length){
                if($offset+12 -gt $bytes.Length){throw 'qa_public_visual_png_invalid'}
                $length=[uint32]$bytes[$offset]*16777216+[uint32]$bytes[$offset+1]*65536+[uint32]$bytes[$offset+2]*256+[uint32]$bytes[$offset+3]
                if($length -gt 2MB -or [long]$offset+12+$length -gt $bytes.Length){throw 'qa_public_visual_png_invalid'}
                $kind=[Text.Encoding]::ASCII.GetString($bytes,$offset+4,4)
                if($kind -cnotin @('IHDR','IDAT','IEND','pHYs','sRGB','gAMA') -or ($first -and ($kind -cne 'IHDR' -or $length -ne 13))){throw 'qa_public_visual_png_metadata_rejected'}
                if($kind -cne 'IDAT' -and -not $chunks.Add($kind)){throw 'qa_public_visual_png_metadata_rejected'}
                if(($kind -ceq 'IHDR' -and $length -ne 13) -or ($kind -ceq 'pHYs' -and $length -ne 9) -or ($kind -ceq 'sRGB' -and $length -ne 1) -or ($kind -ceq 'gAMA' -and $length -ne 4)){throw 'qa_public_visual_png_metadata_rejected'}
                $first=$false;$offset+=12+[int]$length
                if($kind -ceq 'IEND'){if($length -ne 0 -or $offset -ne $bytes.Length){throw 'qa_public_visual_png_invalid'};$ended=$true;break}
            }
            if(-not $ended){throw 'qa_public_visual_png_invalid'}
            Add-Type -AssemblyName System.Drawing
            $bitmap=[Drawing.Bitmap]::new($image)
            try{
                if($bitmap.Width -ne $capture.width -or $bitmap.Height -ne $capture.height){throw 'qa_public_visual_dimensions_invalid'}
                $initial=$bitmap.GetPixel(0,0).ToArgb();$varied=$false
                for($y=0;$y -lt $bitmap.Height -and -not $varied;$y+=[Math]::Max(1,[int]($bitmap.Height/64))){for($x=0;$x -lt $bitmap.Width;$x+=[Math]::Max(1,[int]($bitmap.Width/64))){if($bitmap.GetPixel($x,$y).ToArgb() -ne $initial){$varied=$true;break}}}
                if(-not $varied){throw 'qa_public_visual_blank_rejected'}
            }finally{$bitmap.Dispose()}
            $safe.width=$capture.width;$safe.height=$capture.height;$safe.pngSha256=$capture.pngSha256;$safe.textSha256=$capture.textSha256;$safe.fileName=$capture.relativePath
            $files.Add(@{name=$capture.relativePath;path=$image;sha256=$capture.pngSha256})
        }elseif($capture.code -cne 'unavailable' -or $capture.nativeFixture -ne $false -or $capture.width -ne 0 -or $capture.height -ne 0 -or @($capture.PSObject.Properties.Name|Where-Object {$_ -cin @('relativePath','pngSha256','textSha256','fixtureRoot','fixtureToken','shopId')}).Count){throw 'qa_public_visual_partial_invalid'}
        $captures.Add($safe)
    }
    return @{runId=$Expected.runId;sourceFingerprintSha256=$Expected.sourceFingerprintSha256;captures=$captures.ToArray();files=$files.ToArray()}
}

function ConvertTo-QaPublicReport($Report) {
    $catalog=Get-QaPublicCatalog
    if ($Report.runId -cnotmatch '^Win7POS\.QA\.\d{8}T\d{9}Z\.[a-f0-9]{32}$' -and $Report.runId -cnotmatch '^synthetic-[a-z0-9-]{1,48}$') { throw 'qa_public_run_invalid' }
    if ($Report.sourceSha -and $Report.sourceSha -cnotmatch '^[a-f0-9]{40}$') { throw 'qa_public_source_invalid' }
    if ($Report.suite -cnotin @('Local','Windows','Staging','Win7','Hardware')) {throw 'qa_public_suite_invalid'}
    $rows=@()
    foreach ($row in @($Report.results)) {
        $name=if ($row.name -cin $catalog.steps) { $row.name } else { 'unknown' }
        $status=if ($row.status -cin $catalog.statuses) { $row.status } else { 'FAIL' }
        $tests=@()
        if ($row.PSObject.Properties.Name -contains 'tests') {
            foreach ($test in @($row.tests)) {
                if ($null -eq $test) { continue }
                $id=if ($test.PSObject.Properties.Name -contains 'name') { $test.name } elseif ($test.PSObject.Properties.Name -contains 'id') { $test.id } else { 'unknown' }
                $known=$id -cin $catalog.functional
                if (Get-Command Get-HistoricalSalesQaContract -ErrorAction SilentlyContinue) { foreach ($mode in @('Smoke','Load','CrashProbe','CloudSync')) { if ($id -cin @((Get-HistoricalSalesQaContract $mode).scenarios.Keys)) { $known=$true } } }
                if (-not $known) { $tests+=@{id='unknown';status='NOT_RUN';diagnosticCompleteness='partial'};continue }
                $testStatus=if ($test.PSObject.Properties.Name -contains 'rawStatus') {$test.rawStatus} elseif ($test.PSObject.Properties.Name -contains 'status') {$test.status} else {'NOT_RUN'}
                if ($testStatus -cnotin $catalog.statuses) { $testStatus='FAIL' }
                $tests+=@{id=$id;status=$testStatus}
            }
        }
        $elapsed=0
        if ($row.PSObject.Properties.Name -contains 'elapsedMs' -and $null -ne $row.elapsedMs) { $elapsed=ConvertTo-QaPublicScalar $row.elapsedMs }
        $exitCode=$null
        if ($row.PSObject.Properties.Name -contains 'exitCode' -and $null -ne $row.exitCode) { $exitCode=ConvertTo-QaPublicScalar $row.exitCode }
        $rows+=@{id=$name;status=$status;code=(ConvertTo-QaPublicCode $row.code);exitCode=$exitCode;elapsedMs=$elapsed;tests=$tests}
    }
    $fingerprint=$null
    if ((Test-QaPublicField $Report 'sourceState') -and $null -ne $Report.sourceState) {
        $fingerprint=$Report.sourceState.fingerprintSha256
        if ($fingerprint -cnotmatch '^[a-f0-9]{64}$') { throw 'qa_public_source_invalid' }
    }
    $safeStatus=if ($Report.status -cin $catalog.statuses) {$Report.status} else {'FAIL'}
    $first=$null
    if ($null -ne $Report.firstError) {
        $firstName=if ($Report.firstError.PSObject.Properties.Name -contains 'name') {$Report.firstError.name} elseif ($Report.firstError -is [Collections.IDictionary] -and $Report.firstError.Contains('name')) {$Report.firstError['name']} else {'unknown'}
        $first=@{id=$(if ($firstName -cin $catalog.steps) {$firstName} else {'unknown'});code=(ConvertTo-QaPublicCode $Report.firstError.code)}
    }
    return [ordered]@{schemaVersion='win7pos-qa-public-v1';runId=$Report.runId;sourceSha=$Report.sourceSha;sourceFingerprintSha256=$fingerprint;suite=$Report.suite;status=$safeStatus;firstError=$first;results=$rows}
}

function Test-QaPublicBundle {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Directory,[Parameter(Mandatory)][string]$ExpectedRunId)
    $directoryPath=Assert-QaPublicPath $Directory
    $manifestPath=Assert-QaPublicPath (Join-Path $directoryPath 'upload-manifest.json') $directoryPath
    $zipPath=Assert-QaPublicPath (Join-Path $directoryPath 'qa-public.zip') $directoryPath
    $manifest=Read-HistoricalSalesQaJson $manifestPath
    if (@($manifest.PSObject.Properties.Name | Where-Object {$_ -cnotin @('schemaVersion','runId','sourceSha','sourceFingerprintSha256','zipBytes','zipSha256','entries')}).Count -or $manifest.schemaVersion -cne 'win7pos-qa-upload-v1' -or $manifest.runId -cne $ExpectedRunId -or $manifest.zipBytes -ne (Get-Item -LiteralPath $zipPath).Length -or $manifest.zipSha256 -cne (Get-QaPublicSha256 $zipPath)) { throw 'qa_public_bundle_invalid' }
    $allowed=@('qa-results.public.json','qa-summary.public.txt','core-tests.public.json','historical-sales-smoke.public.json','historical-sales-load.public.json','historical-sales-foundation.public.json','historical-cloud-sync.public.json','diagnostics.public.json','visuals.public.json','qa-visual-hourly_mixed.png','qa-visual-hourly_negative.png','qa-visual-import_summary.png')
    foreach($binding in @($manifest.entries)){
        if(@($binding.PSObject.Properties.Name|Where-Object {$_ -cnotin @('name','bytes','sha256')}).Count -or $binding.name -cnotin $allowed -or $binding.sha256 -cnotmatch '^[a-f0-9]{64}$' -or $binding.bytes -lt 0 -or $binding.bytes -gt 2MB){throw 'qa_public_manifest_entry_invalid'}
    }
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $archive=[IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        if ($archive.Entries.Count -ne @($manifest.entries).Count -or $archive.Entries.Count -gt 32) { throw 'qa_public_zip_entries_invalid' }
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName -cnotin $allowed -or -not $seen.Add($entry.FullName) -or $entry.Length -gt 2MB) { throw 'qa_public_zip_entries_invalid' }
            $expected=@($manifest.entries | Where-Object name -ceq $entry.FullName)
            if ($expected.Count -ne 1 -or $expected[0].bytes -ne $entry.Length) { throw 'qa_public_zip_entries_invalid' }
            $stream=$entry.Open();try { $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() } finally {$stream.Dispose()}
            if ($hash -cne $expected[0].sha256) { throw 'qa_public_zip_entry_digest_invalid' }
            if($entry.FullName.EndsWith('.json',[StringComparison]::Ordinal)){
                $reader=[IO.StreamReader]::new($entry.Open());try{$payload=$reader.ReadToEnd()|ConvertFrom-Json -Depth 32 -ErrorAction Stop}finally{$reader.Dispose()}
                if(-not(Test-QaPublicField $payload 'runId') -or $payload.runId -cne $ExpectedRunId){throw 'qa_public_zip_run_invalid'}
                if((Test-QaPublicField $payload 'sourceFingerprintSha256') -and $payload.sourceFingerprintSha256 -cne $manifest.sourceFingerprintSha256){throw 'qa_public_zip_source_invalid'}
            }
        }
    } finally { $archive.Dispose() }
    return $manifest
}

function Publish-QaPublicBundle {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Report,[Parameter(Mandatory)][string]$RawDirectory,[Parameter(Mandatory)][string]$PublicParent,[object[]]$HistoricalEvidence=@(),[object[]]$Diagnostics=@(),$VisualEvidence=$null)
    $public=ConvertTo-QaPublicReport $Report
    $parent=Assert-QaPublicPath $PublicParent
    $raw=Assert-QaPublicPath $RawDirectory
    if ($parent.StartsWith($raw.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or $raw.StartsWith($parent.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or $parent -ceq $raw) {throw 'qa_public_raw_overlap'}
    $final=Assert-QaPublicPath (Join-Path $parent $Report.runId) $parent
    if (Test-Path -LiteralPath $final) { throw 'qa_public_collision' }
    $stage=Assert-QaPublicPath (Join-Path $parent ('.'+$Report.runId+'.'+[Guid]::NewGuid().ToString('N')+'.building')) $parent
    $null=New-Item -ItemType Directory -Path $stage
    $entries=[Collections.Generic.List[object]]::new()
    function Add-PublicEntry([string]$Name,$Value,[switch]$Text) {
        $path=Assert-QaPublicPath (Join-Path $stage $Name) $stage
        $body=if ($Text) {[string]$Value} else {$Value | ConvertTo-Json -Depth 20}
        [IO.File]::WriteAllText($path,$body,[Text.UTF8Encoding]::new($false))
        $file=Get-Item -LiteralPath $path
        if ($file.Length -gt 2MB) {throw 'qa_public_entry_limit'}
        $entries.Add(@{name=$Name;bytes=$file.Length;sha256=(Get-QaPublicSha256 $path)})
    }
    Add-PublicEntry 'qa-results.public.json' $public
    Add-PublicEntry 'qa-summary.public.txt' ("WIN7POS_AUTONOMOUS_QA {0}`nrunId={1}`nsourceSha={2}" -f $public.status,$public.runId,$public.sourceSha) -Text
    $safeDiagnostics=@()
    foreach ($event in $Diagnostics) {
        # Diagnostic producers must emit typed observations. Free text is never copied.
        if (@($event.PSObject.Properties.Name | Where-Object {$_ -cnotin @('stepId','scenarioId','assertionId','observationId','status','expected','actual','checkpoint','code','durationMs','sequence','diagnosticCompleteness')}).Count) {throw 'qa_public_diagnostic_privacy_rejected'}
        $step=if ($event.stepId -cin (Get-QaPublicCatalog).steps) {$event.stepId} else {'unknown'}
        $scenario=if((Test-QaPublicField $event 'scenarioId') -and $null -ne $event.scenarioId){if($event.scenarioId -cnotin (Get-QaPublicCatalog).functional){throw 'qa_public_diagnostic_privacy_rejected'};$event.scenarioId}else{$null}
        $assertion=if((Test-QaPublicField $event 'assertionId') -and $event.assertionId){if(-not(Test-QaPublicAssertionId $event.assertionId ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))))){throw 'qa_public_diagnostic_privacy_rejected'};$event.assertionId}else{$null}
        $status=if((Test-QaPublicField $event 'status') -and $event.status -cin (Get-QaPublicCatalog).statuses){$event.status}else{'NOT_RUN'}
        $checkpoint=if((Test-QaPublicField $event 'checkpoint') -and $event.checkpoint -cin @('assertion','starting','completed','unavailable')){$event.checkpoint}else{'unavailable'}
        $diagnosis=@{stepId=$step;scenarioId=$scenario;assertionId=$assertion;status=$status;checkpoint=$checkpoint;code=(ConvertTo-QaPublicCode $event.code);expected=(ConvertTo-QaPublicScalar $event.expected);actual=(ConvertTo-QaPublicScalar $event.actual);diagnosticCompleteness=$(if((Test-QaPublicField $event 'diagnosticCompleteness') -and $event.diagnosticCompleteness -ceq 'complete'){'complete'}else{'partial'})}
        if(Test-QaPublicField $event 'sequence'){$diagnosis.sequence=ConvertTo-QaPublicScalar $event.sequence}
        if(Test-QaPublicField $event 'durationMs'){$diagnosis.durationMs=ConvertTo-QaPublicScalar $event.durationMs}
        if(Test-QaPublicField $event 'observationId'){
            if($event.observationId -cnotin @('scenario_success','product_image_profile_dpapi_isolated','shared_unchanged','plaintext_secrets','offline_authority_cloned','supervisor_profile_injected','cleanup_exact','staging_diagnostic_redacted','net48_request_serialization','net48_json_stringify_response','net48_storage_error_mapping','net48_code128_roundtrip','worker_alive_after_shutdown','producer_p95_us','producer_max_us')){throw 'qa_public_diagnostic_privacy_rejected'}
            $diagnosis.observationId=$event.observationId
        }
        $safeDiagnostics+=$diagnosis
    }
    Add-PublicEntry 'diagnostics.public.json' @{schemaVersion='win7pos-qa-diagnostics-public-v1';runId=$Report.runId;results=$safeDiagnostics;diagnosticCompleteness=$(if ($safeDiagnostics.Count -and @($safeDiagnostics|Where-Object diagnosticCompleteness -cne 'complete').Count -eq 0) {'complete'} else {'partial'})}
    $safeVisuals=@(Get-QaSyntheticVisualCatalog|ForEach-Object {@{id=$_.id;scenarioId=$_.scenarioId;capturePoint=$_.capturePoint;textRegistryId=$_.textRegistryId;status='NOT_RUN';code='unavailable';diagnosticCompleteness='partial'}})
    if($null -ne $VisualEvidence){
        if($VisualEvidence.runId -cne $Report.runId -or $VisualEvidence.sourceFingerprintSha256 -cne $public.sourceFingerprintSha256){throw 'qa_public_visual_identity_invalid'}
        $projected=[Collections.Generic.List[object]]::new();$seenVisuals=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach($candidate in @($VisualEvidence.captures)){
            $candidate=[pscustomobject]$candidate;$known=@(Get-QaSyntheticVisualCatalog|Where-Object id -ceq $candidate.id)
            if(@($candidate.PSObject.Properties.Name|Where-Object {$_ -cnotin @('id','scenarioId','capturePoint','textRegistryId','status','code','diagnosticCompleteness','width','height','pngSha256','textSha256','fileName')}).Count -or $known.Count -ne 1 -or -not $seenVisuals.Add($candidate.id) -or $candidate.scenarioId -cne $known[0].scenarioId -or $candidate.capturePoint -cne $known[0].capturePoint -or $candidate.textRegistryId -cne $known[0].textRegistryId -or $candidate.status -cnotin @('PASS','NOT_RUN')){throw 'qa_public_visual_privacy_rejected'}
            $safe=@{id=$known[0].id;scenarioId=$known[0].scenarioId;capturePoint=$known[0].capturePoint;textRegistryId=$known[0].textRegistryId;status=$candidate.status;code=$(if($candidate.status -ceq 'PASS'){'none'}else{'unavailable'});diagnosticCompleteness=$(if($candidate.status -ceq 'PASS'){'complete'}else{'partial'})}
            if($candidate.status -ceq 'PASS'){
                if($candidate.textSha256 -cne $known[0].textSha256 -or $candidate.pngSha256 -cnotmatch '^[a-f0-9]{64}$' -or $candidate.fileName -cne ('qa-visual-'+$candidate.id+'.png') -or $candidate.width -lt 8 -or $candidate.width -gt 2048 -or $candidate.height -lt 8 -or $candidate.height -gt 1024){throw 'qa_public_visual_provenance_invalid'}
                foreach($key in @('width','height','pngSha256','textSha256','fileName')){$safe[$key]=$candidate.$key}
            }
            $projected.Add($safe)
        }
        if($projected.Count -ne 3 -or @($VisualEvidence.files).Count -ne @($projected|Where-Object status -ceq 'PASS').Count){throw 'qa_public_visual_registry_invalid'}
        $safeVisuals=$projected.ToArray();$seenImages=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach($visual in @($VisualEvidence.files)){
        if($visual.name -cnotin @('qa-visual-hourly_mixed.png','qa-visual-hourly_negative.png','qa-visual-import_summary.png') -or $visual.sha256 -cnotmatch '^[a-f0-9]{64}$' -or $visual.sha256 -cne (Get-QaPublicSha256 $visual.path)){throw 'qa_public_visual_digest_invalid'}
        if(-not $seenImages.Add($visual.name) -or @($safeVisuals|Where-Object {$_.status -ceq 'PASS' -and $_.fileName -ceq $visual.name -and $_.pngSha256 -ceq $visual.sha256}).Count -ne 1){throw 'qa_public_visual_registry_invalid'}
        $null=Assert-QaPublicPath $visual.path $raw
        $target=Assert-QaPublicPath (Join-Path $stage $visual.name) $stage;[IO.File]::Copy($visual.path,$target,$false)
        if((Get-QaPublicSha256 $target) -cne $visual.sha256){throw 'qa_public_visual_digest_invalid'}
        $entries.Add(@{name=$visual.name;bytes=(Get-Item -LiteralPath $target).Length;sha256=(Get-QaPublicSha256 $target)})
    }}
    Add-PublicEntry 'visuals.public.json' @{schemaVersion='win7pos-qa-visuals-public-v1';registryVersion='synthetic-history-visuals-v1';runId=$Report.runId;captures=$safeVisuals;diagnosticCompleteness=$(if(@($safeVisuals|Where-Object status -cne 'PASS').Count){'partial'}else{'complete'})}
    foreach ($item in $HistoricalEvidence) {
        $safe=ValidateHistoricalEvidenceStructureAndPrivacy $item.receipt $item.mode $item.expected
        $name=switch ($item.mode) {Smoke {'historical-sales-smoke.public.json'} Load {'historical-sales-load.public.json'} CrashProbe {'historical-sales-foundation.public.json'} CloudSync {'historical-cloud-sync.public.json'}}
        Add-PublicEntry $name $safe
    }
    $zip=Join-Path $stage 'qa-public.zip'
    $archive=[IO.Compression.ZipFile]::Open($zip,[IO.Compression.ZipArchiveMode]::Create)
    try {foreach ($entry in $entries) {$null=[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,(Join-Path $stage $entry.name),$entry.name,[IO.Compression.CompressionLevel]::Optimal)}} finally {$archive.Dispose()}
    $manifest=@{schemaVersion='win7pos-qa-upload-v1';runId=$Report.runId;sourceSha=$public.sourceSha;sourceFingerprintSha256=$public.sourceFingerprintSha256;zipBytes=(Get-Item -LiteralPath $zip).Length;zipSha256=(Get-QaPublicSha256 $zip);entries=$entries.ToArray()}
    [IO.File]::WriteAllText((Join-Path $stage 'upload-manifest.json'),($manifest|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    $null=Test-QaPublicBundle $stage $Report.runId
    # Publish the validated pair as one directory operation. No overwrite/cleanup.
    [IO.Directory]::Move($stage,$final)
    $null=Test-QaPublicBundle $final $Report.runId
    return @{runId=$Report.runId;directory=$final;zipPath=(Join-Path $final 'qa-public.zip');manifestPath=(Join-Path $final 'upload-manifest.json');verified=$true}
}

function Get-QaNet48PayloadRequiredFiles {
    # Runtime files shared by the app and the separate QA host, plus the
    # independently hashed synthetic corpus. Presence is distinct from runtime
    # qualification; a digest-consistent but incomplete archive is never ready.
    return @(
        'Win7POS.Wpf.exe','Win7POS.Wpf.exe.config','Win7POS.Wpf.UiSmokeHarness.exe','Win7POS.Wpf.UiSmokeHarness.exe.config',
        'Win7POS.Core.dll','Win7POS.Data.dll','ClosedXML.dll','ClosedXML.Parser.dll','Dapper.dll',
        'DocumentFormat.OpenXml.dll','DocumentFormat.OpenXml.Framework.dll','ExcelDataReader.dll','ExcelDataReader.DataSet.dll',
        'ExcelNumberFormat.dll','Microsoft.Bcl.AsyncInterfaces.dll','Microsoft.Bcl.HashCode.dll','Microsoft.Data.Sqlite.dll',
        'RBush.dll','SixLabors.Fonts.dll','System.Buffers.dll','System.Drawing.Common.dll','System.IO.Packaging.dll',
        'System.Memory.dll','System.Numerics.Vectors.dll','System.Runtime.CompilerServices.Unsafe.dll',
        'System.Text.Encoding.CodePages.dll','System.Threading.Tasks.Extensions.dll','System.ValueTuple.dll',
        'e_sqlite3.dll','SQLitePCLRaw.batteries_v2.dll','SQLitePCLRaw.core.dll','SQLitePCLRaw.provider.e_sqlite3.dll',
        'zxing.dll','zxing.presentation.dll','ZXing.Windows.Compatibility.dll','Assets/sii_qrcode.png',
        'fixtures/historical-sales-getnet-v1/manifest.json','fixtures/historical-sales-getnet-v1/synthetic-legacy.xlsx',
        'fixtures/historical-sales-getnet-v1/synthetic-modern.xlsx','fixtures/historical-sales-getnet-v1/corpus-v1.json'
    )
}

function Test-QaNet48PayloadBundle {
    param([Parameter(Mandatory)][string]$Directory,[Parameter(Mandatory)][string]$SourceSha)
    $root=Assert-QaPublicPath $Directory
    $manifest=Read-HistoricalSalesQaJson (Join-Path $root 'payload-manifest.json')
    $zip=Join-Path $root 'Win7POS.QA-net48-x86.zip'
    if ($manifest.schemaVersion -cne 'win7pos-net48-qa-payload-v1' -or $manifest.sourceSha -cne $SourceSha -or $manifest.architecture -cne 'x86' -or $manifest.targetFramework -cne 'net48' -or $manifest.zipSha256 -cne (Get-QaPublicSha256 $zip) -or $manifest.zipBytes -ne (Get-Item -LiteralPath $zip).Length) {throw 'qa_payload_manifest_invalid'}
    $archive=[IO.Compression.ZipFile]::OpenRead($zip)
    try {
        if ($archive.Entries.Count -ne @($manifest.files).Count -or $archive.Entries.Count -gt 512) {throw 'qa_payload_manifest_invalid'}
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName -cmatch '(^/|\\|(^|/)\.\.?(/|$))' -or -not $seen.Add($entry.FullName)) {throw 'qa_payload_entry_invalid'}
            $binding=@($manifest.files|Where-Object path -ceq $entry.FullName)
            if ($binding.Count -ne 1 -or $entry.Length -ne $binding[0].bytes -or $entry.Length -gt 128MB) {throw 'qa_payload_entry_invalid'}
            $stream=$entry.Open();try{$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()}finally{$stream.Dispose()}
            if ($hash -cne $binding[0].sha256) {throw 'qa_payload_entry_invalid'}
        }
        foreach ($required in (Get-QaNet48PayloadRequiredFiles)) {
            $requiredEntry=$archive.GetEntry($required)
            if ($null -eq $requiredEntry -or $requiredEntry.Length -eq 0) {throw 'qa_payload_required_file_missing'}
        }
        foreach ($configName in @('Win7POS.Wpf.exe.config','Win7POS.Wpf.UiSmokeHarness.exe.config')) {
            $settings=[Xml.XmlReaderSettings]::new();$settings.DtdProcessing=[Xml.DtdProcessing]::Prohibit;$settings.XmlResolver=$null
            $stream=$archive.GetEntry($configName).Open();$reader=$null
            try {
                $reader=[Xml.XmlReader]::Create($stream,$settings)
                $xml=[Xml.XmlDocument]::new();$xml.XmlResolver=$null;$xml.Load($reader)
                $ns=[Xml.XmlNamespaceManager]::new($xml.NameTable);$ns.AddNamespace('a','urn:schemas-microsoft-com:asm.v1')
                $nodes=@($xml.SelectNodes('/configuration/runtime/a:assemblyBinding/a:dependentAssembly[a:assemblyIdentity/@name="System.ValueTuple"]',$ns))
                if ($nodes.Count -ne 1) {throw 'qa_payload_valuetuple_redirect_invalid'}
                $identity=$nodes[0].SelectSingleNode('a:assemblyIdentity',$ns)
                $redirect=$nodes[0].SelectSingleNode('a:bindingRedirect',$ns)
                if ($identity.GetAttribute('publicKeyToken') -cne 'cc7b13ffcd2ddd51' -or $identity.GetAttribute('culture') -cne 'neutral' -or $null -eq $redirect -or $redirect.GetAttribute('oldVersion') -cne '0.0.0.0-4.0.5.0' -or $redirect.GetAttribute('newVersion') -cne '4.0.5.0') {throw 'qa_payload_valuetuple_redirect_invalid'}
            } finally {if ($null -ne $reader) {$reader.Dispose()};$stream.Dispose()}
        }
        $fixtureEntry=$archive.GetEntry('fixtures/historical-sales-getnet-v1/manifest.json')
        $reader=[IO.StreamReader]::new($fixtureEntry.Open());try{$fixture=$reader.ReadToEnd()|ConvertFrom-Json -ErrorAction Stop}finally{$reader.Dispose()}
        if($fixture.schemaVersion -cne 'win7pos-historical-sales-fixtures-v1' -or $fixture.fixtureVersion -cne 'historical-sales-getnet-v1' -or @($fixture.files).Count -ne 3){throw 'qa_payload_fixture_manifest_invalid'}
        foreach($binding in $fixture.files){
            if($binding.name -cnotin @('corpus-v1.json','synthetic-legacy.xlsx','synthetic-modern.xlsx')){throw 'qa_payload_fixture_manifest_invalid'}
            $payloadFile=@($manifest.files|Where-Object path -ceq ('fixtures/historical-sales-getnet-v1/'+$binding.name))
            if($payloadFile.Count -ne 1 -or $payloadFile[0].sha256 -cne $binding.sha256){throw 'qa_payload_fixture_digest_invalid'}
        }
    } finally {$archive.Dispose()}
    return $manifest
}

function New-QaNet48PayloadBundle {
    param([Parameter(Mandatory)][string]$HarnessDirectory,[Parameter(Mandatory)][string]$OutputDirectory,[Parameter(Mandatory)][string]$SourceSha)
    if ($SourceSha -cnotmatch '^[0-9a-f]{40}$') {throw 'qa_payload_source_invalid'}
    $source=Assert-QaPublicPath $HarnessDirectory
    $target=Assert-QaPublicPath $OutputDirectory
    if (Test-Path -LiteralPath $target) {throw 'qa_payload_identity_reused'}
    foreach ($name in @('Win7POS.Wpf.exe','Win7POS.Wpf.UiSmokeHarness.exe')) {
        $path=Assert-QaPublicPath (Join-Path $source $name) $source
        $bytes=[IO.File]::ReadAllBytes($path)
        if ($bytes.Length -lt 256 -or [BitConverter]::ToUInt16($bytes,0) -ne 0x5a4d) {throw 'qa_payload_x86_required'}
        $pe=[BitConverter]::ToInt32($bytes,0x3c)
        if ($pe -lt 0 -or $pe+6 -gt $bytes.Length -or [BitConverter]::ToInt32($bytes,$pe) -ne 0x4550 -or [BitConverter]::ToUInt16($bytes,$pe+4) -ne 0x14c -or -not [Text.Encoding]::UTF8.GetString($bytes).Contains('.NETFramework,Version=v4.8')) {throw 'qa_payload_net48_x86_required'}
    }
    foreach ($required in (Get-QaNet48PayloadRequiredFiles)) {
        $path=Assert-QaPublicPath (Join-Path $source $required) $source
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {throw 'qa_payload_required_file_missing'}
    }
    $null=New-Item -ItemType Directory -Path $target
    $bindings=[Collections.Generic.List[object]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File) {
        $null=Assert-QaPublicPath $file.FullName $source
        $relative=[IO.Path]::GetRelativePath($source,$file.FullName).Replace('\','/')
        $extension=$file.Extension.ToLowerInvariant()
        $include=$extension -cin @('.dll','.config') -or $relative -cin @('Win7POS.Wpf.exe','Win7POS.Wpf.UiSmokeHarness.exe','Assets/sii_qrcode.png','fixtures/historical-sales-getnet-v1/manifest.json','fixtures/historical-sales-getnet-v1/corpus-v1.json','fixtures/historical-sales-getnet-v1/synthetic-legacy.xlsx','fixtures/historical-sales-getnet-v1/synthetic-modern.xlsx')
        if (-not $include) {continue}
        if ($bindings.Count -ge 512 -or $file.Length -gt 128MB) {throw 'qa_payload_limit'}
        $bindings.Add(@{path=$relative;bytes=$file.Length;sha256=(Get-QaPublicSha256 $file.FullName)})
    }
    $zip=Join-Path $target 'Win7POS.QA-net48-x86.zip'
    $archive=[IO.Compression.ZipFile]::Open($zip,[IO.Compression.ZipArchiveMode]::Create)
    try {foreach ($binding in $bindings) {$null=[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,(Join-Path $source $binding.path),$binding.path,[IO.Compression.CompressionLevel]::Optimal)}}finally{$archive.Dispose()}
    $manifest=@{schemaVersion='win7pos-net48-qa-payload-v1';sourceSha=$SourceSha;architecture='x86';targetFramework='net48';zipBytes=(Get-Item -LiteralPath $zip).Length;zipSha256=(Get-QaPublicSha256 $zip);files=$bindings.ToArray()}
    [IO.File]::WriteAllText((Join-Path $target 'payload-manifest.json'),($manifest|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    $null=Test-QaNet48PayloadBundle $target $SourceSha
    return $manifest
}

Export-ModuleMember -Function Get-QaPublicCatalog,ConvertTo-QaPublicCode,Write-QaPublicConsoleEvent,Assert-QaPublicPath,ConvertTo-QaPublicReport,ConvertTo-QaPublicScalar,Get-QaTypedDiagnostics,Get-QaSyntheticVisualCatalog,Read-QaSyntheticVisualEvidence,Publish-QaPublicBundle,Test-QaPublicBundle,New-QaNet48PayloadBundle,Test-QaNet48PayloadBundle
