#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Smoke','Load','CrashProbe')][string]$Mode,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$ContextFile,
    [string]$HarnessDirectory=''
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'qa/Win7PosHistoricalSalesValidation.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $PSScriptRoot 'qa/Win7PosQaPublicEvidence.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $PSScriptRoot 'qa/Win7PosAcceptanceProcessRunner.psm1') -Force -DisableNameChecking
$repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$contract=Get-HistoricalSalesQaContract $Mode
$context=$null
$clock=[Diagnostics.Stopwatch]::StartNew()
$phaseSpent=@{'checkpoint-report'=0L}
$exitCode=1

function Write-HistoryCheckpoint($Receipt,[string]$Path) {
    $null=Get-HistoryRemainingMilliseconds 'checkpoint-report'
    $started=[long]$clock.ElapsedMilliseconds
    try {
        $temporary=$Path+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
        $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($Receipt|ConvertTo-Json -Depth 20))
        $stream=[IO.FileStream]::new($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try {$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)} finally {$stream.Dispose()}
        [IO.File]::Move($temporary,$Path,$true)
    } finally {$phaseSpent['checkpoint-report']+=[long]$clock.ElapsedMilliseconds-$started}
    $null=Get-HistoryRemainingMilliseconds 'checkpoint-report' -AllowCompleted
}

function ConvertTo-HistoryProcessArgument([string]$Argument) {
    # Start-Process joins ArgumentList into a Windows command line. Escape each
    # original argument once, including trailing slashes before the closing quote.
    return '"'+[regex]::Replace([regex]::Replace($Argument,'(\\*)"','$1$1\"'),'(\\+)$','$1$1')+'"'
}

function Get-HistoryWorkerPhase([string]$Name) {
    switch -CaseSensitive ($Name) {
        'crash-probe' {return @{id='hot-journal';limit=90000}}
        'crash-verify' {return @{id='hot-journal';limit=90000}}
        'HIST_B_COMMIT_AUTHORITY' {return @{id='commit-authority';limit=25000}}
        'HIST_B_PROCESS_ADMISSION' {return @{id='admission';limit=30000}}
        'process-preparation' {return @{id='entrypoint';limit=120000}}
        'process-probes' {return @{id='entrypoint';limit=120000}}
        'HIST_B_ENTRYPOINT_AUTHORITY' {return @{id='entrypoint';limit=120000}}
        'HIST_B_BACKUP_RESTORE' {return @{id='backup-restore';limit=45000}}
        'smoke' {return @{id='smoke';limit=60000}}
        'load' {return @{id='load';limit=180000}}
        'checkpoint-report' {return @{id='checkpoint-report';limit=20000}}
        default {throw 'qa_history_worker_phase_invalid'}
    }
}

function Get-HistoryRemainingMilliseconds([string]$Name,[long]$InFlightStarted=-1,[long]$InFlightReportBase=0,[switch]$AllowCompleted) {
    $phase=Get-HistoryWorkerPhase $Name
    $elapsed=[long]$clock.ElapsedMilliseconds
    if(-not $phaseSpent.ContainsKey($phase.id)){$phaseSpent[$phase.id]=0L}
    $bodyRemaining=[long]$contract.bodySeconds*1000-$elapsed
    $inFlight=if($InFlightStarted -ge 0){[Math]::Max(0,$elapsed-$InFlightStarted-([long]$phaseSpent['checkpoint-report']-$InFlightReportBase))}else{0L}
    $phaseRemaining=[long]$phase.limit-[long]$phaseSpent[$phase.id]-$inFlight
    $remaining=[int][Math]::Min($bodyRemaining,$phaseRemaining)
    if($AllowCompleted){if($remaining -lt 0){throw 'qa_history_worker_deadline_exhausted'};return $remaining}
    if($remaining -lt 100){throw 'qa_history_worker_deadline_exhausted'}
    return $remaining
}

function Invoke-HistoryWorker([string[]]$Arguments,[string]$Name) {
    $remaining=Get-HistoryRemainingMilliseconds $Name
    $phase=Get-HistoryWorkerPhase $Name;$started=[long]$clock.ElapsedMilliseconds
    try {$child=Invoke-Win7PosWaitedProcess -FilePath $harness -ArgumentList @($Arguments|ForEach-Object {ConvertTo-HistoryProcessArgument $_}) -TimeoutMilliseconds $remaining -EvidenceDirectory $output}
    finally {$phaseSpent[$phase.id]+=[long]$clock.ElapsedMilliseconds-$started}
    $child|Add-Member NoteProperty phaseId $phase.id -Force
    $child|Add-Member NoteProperty timeoutMilliseconds $remaining -Force
    $child|Add-Member NoteProperty phaseConsumedMilliseconds $phaseSpent[$phase.id] -Force
    Write-HistoryCheckpoint $child (Join-Path $output ($Name+'.process.private.json'))
    if($child.TimedOut){throw 'qa_history_worker_timeout'}
    if($child.Cancelled){throw 'qa_history_worker_cancelled'}
    if($child.LaunchError -or -not $child.Started -or -not $child.CleanupSucceeded -or $child.OrphanRemaining -or $null -eq $child.ExitCode){throw 'qa_history_worker_supervision_failed'}
    $null=Get-HistoryRemainingMilliseconds $Name -AllowCompleted
    return [int]$child.ExitCode
}

function Invoke-HistoryProcessProbes([string]$Data,$Context,[string]$Harness,[string]$Wpf,[string]$RepoRoot) {
    $null=Get-HistoryRemainingMilliseconds 'process-probes'
    $probesStarted=[long]$clock.ElapsedMilliseconds;$reportBefore=[long]$phaseSpent['checkpoint-report']
    try {
    $prepared=Read-HistoricalSalesQaJson (Join-Path $Data 'process-probes-prepared.json')
    if($prepared.schemaVersion -cne 'win7pos-process-probes-prepared-v1' -or $prepared.runId -cne $Context.runId -or $prepared.runNonce -cne $Context.runNonce){throw 'qa_history_process_preparation_invalid'}
    foreach($db in @($prepared.databaseA,$prepared.databaseB)){ $null=Assert-QaPublicPath $db $Data }
    $directory=Join-Path $Data 'probe-diagnostics';$null=New-Item -ItemType Directory -Path $directory
    $contextA=Join-Path $directory 'context-a.json';$contextB=Join-Path $directory 'context-b.json'
    foreach($binding in @(@{path=$contextA;token=$prepared.fixtureTokenA},@{path=$contextB;token=$prepared.fixtureTokenB})){
        Write-HistoryCheckpoint @{schemaVersion='win7pos-authority-probe-context-v1';runId=$Context.runId;runNonce=$Context.runNonce;rootDirectory=$Data;fixtureToken=$binding.token} $binding.path
    }
    $ready=Join-Path $directory 'holder-ready.json';$release=Join-Path $directory 'holder-release.flag';$holderReceipt=Join-Path $directory 'holder-receipt.json'
    $dotnet=(Get-Command dotnet -CommandType Application -ErrorAction Stop|Select-Object -First 1).Source
    $cli=Join-Path $RepoRoot 'src/Win7POS.Cli/bin/Release/net10.0/Win7POS.Cli.dll'
    if(-not(Test-Path -LiteralPath $cli -PathType Leaf)){throw 'qa_history_cli_missing'}
    $state=[pscustomobject]@{ready=$false;cases=[Collections.Generic.List[object]]::new()}
    $callback={param($holderProcess,$remaining)
        $watch=[Diagnostics.Stopwatch]::StartNew()
        while(-not(Test-Path -LiteralPath $ready -PathType Leaf)){
            if($holderProcess.HasExited -or $watch.ElapsedMilliseconds -gt [Math]::Min(30000,$remaining)){throw 'qa_history_holder_not_ready'}
            Start-Sleep -Milliseconds 20
        }
        $witness=Read-HistoricalSalesQaJson $ready
        if($witness.runNonce -cne $Context.runNonce -or $witness.runId -cne $Context.runId -or $witness.stage -cne 'held' -or $witness.pid -ne $holderProcess.Id -or $witness.entryPoint -cne 'harness' -or [DateTimeOffset]::Parse($witness.creationTimeUtc).UtcTicks -ne $holderProcess.StartTime.ToUniversalTime().Ticks -or $witness.writeStarted -ne $false){throw 'qa_history_holder_identity_invalid'}
        $state.ready=$true
        try{
            foreach($case in @(
                @{id='wpf-normal';entry='wpf-normal';exe=$Wpf;prefix=@();db=$prepared.databaseA;context=$contextA;expected=78},
                @{id='wpf-safe';entry='wpf-safe';exe=$Wpf;prefix=@('--safe-start');db=$prepared.databaseA;context=$contextA;expected=78},
                @{id='cli';entry='cli';exe=$dotnet;prefix=@(('"'+$cli+'"'));db=$prepared.databaseA;context=$contextA;expected=78},
                @{id='harness';entry='harness';exe=$Harness;prefix=@();db=$prepared.databaseA;context=$contextA;expected=78},
                @{id='distinct';entry='harness';exe=$Harness;prefix=@();db=$prepared.databaseB;context=$contextB;expected=0}
            )){
                $overlapped=-not $holderProcess.HasExited
                $caseReceipt=Join-Path $directory ($case.id+'.json')
                $args=@($case.prefix)+@('--qa-authority-probe','--qa-probe-mode','Attempt','--qa-context',('"'+$case.context+'"'),'--qa-ready',('"'+(Join-Path $directory ($case.id+'-ready.json'))+'"'),'--qa-release',('"'+$release+'"'),'--qa-receipt',('"'+$caseReceipt+'"'),'--data-dir',('"'+[IO.Path]::GetDirectoryName($case.db)+'"'),'--db',('"'+$case.db+'"'))
                $residual=$remaining-[int]$watch.ElapsedMilliseconds
                if($residual -lt 100){throw 'qa_history_worker_deadline_exhausted'}
                $budget=[Math]::Min(20000,$residual)
                $result=Invoke-Win7PosWaitedProcess -FilePath $case.exe -ArgumentList $args -TimeoutMilliseconds $budget -EvidenceDirectory $directory -EnvironmentVariables @{WIN7POS_DATA_DIR=[IO.Path]::GetDirectoryName($case.db);WIN7POS_ADMIN_WEB_BASE_URL='http://127.0.0.1:9'}
                $proof=Read-HistoricalSalesQaJson $caseReceipt
                $sameIdentity=if($case.id -eq 'distinct'){$proof.databaseIdentityHash -cne $witness.databaseIdentityHash}else{$proof.databaseIdentityHash -ceq $witness.databaseIdentityHash}
                if($result.ExitCode -ne $case.expected -or $result.TimedOut -or -not $result.CleanupSucceeded -or $result.OrphanRemaining -or $proof.runId -cne $Context.runId -or $proof.runNonce -cne $Context.runNonce -or $proof.pid -ne $result.ProcessId -or $proof.entryPoint -cne $case.entry -or [DateTimeOffset]::Parse($proof.creationTimeUtc).UtcTicks -ne $result.Identity.creationTicks -or $proof.writeStarted -ne $false -or (-not $overlapped) -or -not $sameIdentity) {throw 'qa_history_contender_failed'}
                $state.cases.Add(@{id=$case.id;exitCode=$result.ExitCode;stage=$proof.stage;writeStarted=$proof.writeStarted;overlapped=$overlapped})
            }
        }finally{[IO.File]::WriteAllText($release,$Context.runNonce,[Text.UTF8Encoding]::new($false))}
    }.GetNewClosure()
    $holderArgs=@('--qa-authority-probe','--qa-probe-mode','Hold','--qa-context',('"'+$contextA+'"'),'--qa-ready',('"'+$ready+'"'),'--qa-release',('"'+$release+'"'),'--qa-receipt',('"'+$holderReceipt+'"'),'--data-dir',('"'+[IO.Path]::GetDirectoryName($prepared.databaseA)+'"'))
    $holderBudget=Get-HistoryRemainingMilliseconds 'process-probes' -InFlightStarted $probesStarted -InFlightReportBase $reportBefore
    $holder=Invoke-Win7PosWaitedProcess -FilePath $Harness -ArgumentList $holderArgs -TimeoutMilliseconds $holderBudget -EvidenceDirectory $directory -OnStarted $callback -EnvironmentVariables @{WIN7POS_DATA_DIR=[IO.Path]::GetDirectoryName($prepared.databaseA);WIN7POS_ADMIN_WEB_BASE_URL='http://127.0.0.1:9'}
    $holder|Add-Member NoteProperty phaseId 'entrypoint' -Force
    $holder|Add-Member NoteProperty timeoutMilliseconds $holderBudget -Force
    Write-HistoryCheckpoint $holder (Join-Path $directory 'holder.process.private.json')
    $proof=@{schemaVersion='win7pos-process-probes-proof-v1';runId=$Context.runId;runNonce=$Context.runNonce;holderReady=$state.ready;holderExitCode=$holder.ExitCode;cleanupSucceeded=($holder.CleanupSucceeded -and -not $holder.OrphanRemaining -and -not $holder.TimedOut);cases=$state.cases.ToArray()}
    Write-HistoryCheckpoint $proof (Join-Path $Data 'process-probes-proof.json')
    if($holder.ExitCode -ne 0 -or -not $proof.cleanupSucceeded -or $state.cases.Count -ne 5 -or (Get-FileHash -LiteralPath $prepared.databaseA -Algorithm SHA256).Hash.ToLowerInvariant() -cne $prepared.databaseAHash){throw 'qa_history_process_probes_incomplete'}
    } finally {$phaseSpent['entrypoint']+=[Math]::Max(0,[long]$clock.ElapsedMilliseconds-$probesStarted-([long]$phaseSpent['checkpoint-report']-$reportBefore))}
    $null=Get-HistoryRemainingMilliseconds 'process-probes' -AllowCompleted
}

try {
    $output=Assert-QaPublicPath $OutputDirectory
    if (-not (Test-Path -LiteralPath $output -PathType Container)) {throw 'qa_history_output_missing'}
    $context=Read-HistoricalSalesQaJson (Assert-QaPublicPath $ContextFile)
    $contextKeys=@('schemaVersion','runId','runNonce','sourceSha','sourceFingerprintSha256','harnessSha256','wpfSha256','fixtureVersion','fixtureSha256','timePolicyVersion')
    if (@($context.PSObject.Properties.Name|Where-Object {$_ -cnotin $contextKeys}).Count -or $context.schemaVersion -cne 'win7pos-historical-sales-context-v1' -or $context.runId -cnotmatch '^Win7POS\.QA\.\d{8}T\d{9}Z\.[a-f0-9]{32}$' -or $context.runNonce -cnotmatch '^[a-f0-9]{32}$' -or $context.sourceSha -cnotmatch '^[a-f0-9]{40}$' -or $context.fixtureVersion -cne 'historical-sales-getnet-v1' -or $context.timePolicyVersion -cne 'synthetic-fixed-offset-v1') {throw 'qa_history_parent_context_invalid'}
    foreach ($key in @('sourceFingerprintSha256','harnessSha256','wpfSha256','fixtureSha256')) {if ($context.$key -cnotmatch '^[a-f0-9]{64}$') {throw 'qa_history_parent_context_invalid'}}
    if (-not $env:WIN7POS_DATA_DIR -or -not $env:WIN7POS_QA_TEMP_BASE -or $env:WIN7POS_ADMIN_WEB_BASE_URL -cne 'http://127.0.0.1:9') {throw 'qa_history_owned_environment_required'}
    $data=Assert-QaPublicPath $env:WIN7POS_DATA_DIR $env:TEMP
    if (-not (Test-Path -LiteralPath $data -PathType Container)) {throw 'qa_history_owned_environment_required'}
    if (-not $HarnessDirectory) {$HarnessDirectory=Join-Path $repoRoot 'tests/Win7POS.Wpf.UiSmokeHarness/bin/x86/Release/net48'}
    $harness=Assert-QaPublicPath (Join-Path $HarnessDirectory 'Win7POS.Wpf.UiSmokeHarness.exe')
    $wpf=Assert-QaPublicPath (Join-Path $HarnessDirectory 'Win7POS.Wpf.exe')
    $manifest=Assert-QaPublicPath (Join-Path $HarnessDirectory 'fixtures/historical-sales-getnet-v1/manifest.json')
    foreach ($binding in @(@{path=$harness;hash=$context.harnessSha256},@{path=$wpf;hash=$context.wpfSha256},@{path=$manifest;hash=$context.fixtureSha256})) {
        if ((Get-FileHash -LiteralPath $binding.path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $binding.hash) {throw 'qa_history_binary_or_fixture_changed'}
    }
    $base=@('--data-dir',$data,'--diagnostics-dir',$output,'--run-id',$context.runId,'--run-nonce',$context.runNonce,'--source-fingerprint',$context.sourceFingerprintSha256,'--fixture-version',$context.fixtureVersion,'--context-file',$ContextFile)
    $receiptPath=Join-Path $output $contract.file
    if (Test-Path -LiteralPath $receiptPath) {throw 'qa_history_receipt_identity_reused'}
    if ($Mode -ne 'CrashProbe') {
        $flag=if ($Mode -eq 'Smoke') {'--historical-sales-import-smoke'} else {'--historical-sales-import-load'}
        $rawExit=Invoke-HistoryWorker -Arguments ($base+@($flag,'--receipt',$receiptPath)) -Name ($Mode.ToLowerInvariant())
        if ($rawExit -ne 0) {throw 'qa_history_harness_failed'}
        $receipt=Read-HistoricalSalesQaJson $receiptPath
        Assert-HistoricalSalesQaPass $receipt $Mode $context
    } else {
        $results=[Collections.Generic.List[object]]::new()
        $started=[DateTimeOffset]::UtcNow.ToString('O')
        $receipt=[ordered]@{schemaVersion=$contract.schemaVersion;protocolVersion=$contract.protocolVersion;runId=$context.runId;runNonce=$context.runNonce;sourceSha=$context.sourceSha;sourceFingerprintSha256=$context.sourceFingerprintSha256;harnessSha256=$context.harnessSha256;wpfSha256=$context.wpfSha256;fixtureVersion=$context.fixtureVersion;fixtureSha256=$context.fixtureSha256;timePolicyVersion=$context.timePolicyVersion;startedAtUtc=$started;completedAtUtc=$null;durationMs=0;completed=$false;status='NOT_RUN';summary=@{expected=5;executed=0;passed=0;failed=0;notRun=0;timedOut=0};results=@();lastCheckpoint='starting'}
        Write-HistoryCheckpoint $receipt $receiptPath
        $checkpoint=Join-Path $output 'historical-sales-crash-checkpoint.json'
        $verified=Join-Path $output 'historical-sales-crash-verified.json'
        $workerExit=Invoke-HistoryWorker -Arguments ($base+@('--historical-sales-import-crash-probe','--receipt',$checkpoint)) -Name crash-probe
        # Expected process termination never becomes a PASS by itself.
        $receipt.workerRawExitCode=$workerExit;$receipt.workerRawStatus='FAIL'
        Write-HistoryCheckpoint $receipt $receiptPath
        $proof=Read-HistoricalSalesQaJson $checkpoint
        if ($workerExit -ne 79 -or $proof.schemaVersion -cne 'win7pos-historical-sales-crash-checkpoint-v1' -or $proof.runId -cne $context.runId -or $proof.runNonce -cne $context.runNonce -or $proof.sourceFingerprintSha256 -cne $context.sourceFingerprintSha256 -or $proof.harnessSha256 -cne $context.harnessSha256 -or $proof.fixtureSha256 -cne $context.fixtureSha256 -or $proof.phase -cne 'precommit_after_spill' -or $proof.prepared -ne $true -or $proof.journalReady -ne $true -or $proof.spillObserved -ne $true) {throw 'qa_history_crash_cause_unproven'}
        $receipt.verifierExitCode=Invoke-HistoryWorker -Arguments ($base+@('--historical-sales-import-crash-verify','--worker-exit-code','79','--receipt',$verified)) -Name crash-verify
        if ($receipt.verifierExitCode -ne 0) {throw 'qa_history_crash_verification_failed'}
        $case=Read-HistoricalSalesQaJson $verified
        $null=ValidateHistoricalEvidenceStructureAndPrivacy $case 'CrashProbe' $context
        if (@($case.results).Count -ne 1 -or $case.results[0].id -cne 'HIST_CRASH_HOT_JOURNAL') {throw 'qa_history_crash_verification_failed'}
        $results.Add($case.results[0])
        $receipt.process=$case.process
        foreach ($id in @('HIST_B_COMMIT_AUTHORITY','HIST_B_PROCESS_ADMISSION','HIST_B_ENTRYPOINT_AUTHORITY','HIST_B_BACKUP_RESTORE')) {
            $receipt.results=$results.ToArray();$receipt.summary.executed=$results.Count;$receipt.summary.passed=@($results|Where-Object rawStatus -ceq 'PASS').Count;$receipt.durationMs=$clock.ElapsedMilliseconds;$receipt.lastCheckpoint=$id
            Write-HistoryCheckpoint $receipt $receiptPath
            $casePath=Join-Path $output ($id+'.json')
            if($id -eq 'HIST_B_PROCESS_ADMISSION'){
                $preparationExit=Invoke-HistoryWorker -Arguments ($base+@('--historical-sales-import-foundation','--foundation-case',$id,'--prepare-process-probes','--receipt',(Join-Path $output 'process-probes-preparation.json'))) -Name process-preparation
                if($preparationExit -ne 0){throw 'qa_history_process_preparation_failed'}
                Invoke-HistoryProcessProbes $data $context $harness $wpf $repoRoot
            }
            $caseExit=Invoke-HistoryWorker -Arguments ($base+@('--historical-sales-import-foundation','--foundation-case',$id,'--receipt',$casePath)) -Name $id
            if (Test-Path -LiteralPath $casePath -PathType Leaf) {
                $case=Read-HistoricalSalesQaJson $casePath
                $null=ValidateHistoricalEvidenceStructureAndPrivacy $case 'CrashProbe' $context
                if (@($case.results).Count -ne 1 -or $case.results[0].id -cne $id) {throw 'qa_history_foundation_case_invalid'}
                $results.Add($case.results[0])
            }
            $receipt.results=$results.ToArray();$receipt.summary.executed=$results.Count;$receipt.summary.passed=@($results|Where-Object rawStatus -ceq 'PASS').Count;$receipt.summary.failed=@($results|Where-Object rawStatus -ceq 'FAIL').Count;$receipt.durationMs=$clock.ElapsedMilliseconds
            Write-HistoryCheckpoint $receipt $receiptPath
            if ($caseExit -ne 0) {throw 'qa_history_foundation_case_failed'}
        }
        $receipt.results=$results.ToArray();$receipt.summary.executed=$results.Count;$receipt.summary.passed=@($results|Where-Object rawStatus -ceq 'PASS').Count;$receipt.summary.failed=@($results|Where-Object rawStatus -cne 'PASS').Count
        $receipt.durationMs=$clock.ElapsedMilliseconds;$receipt.completed=$true;$receipt.completedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');$receipt.status=if ($receipt.summary.failed -eq 0) {'PASS'} else {'FAIL'};$receipt.lastCheckpoint='completed'
        Write-HistoryCheckpoint $receipt $receiptPath
        Assert-HistoricalSalesQaPass (Read-HistoricalSalesQaJson $receiptPath) 'CrashProbe' $context
    }
    $exitCode=0
} catch {
    $primaryException=$_
    try{
        if(Get-Variable output -ErrorAction SilentlyContinue){[IO.File]::AppendAllText((Join-Path $output 'historical-sales-wrapper-error.private.txt'),$primaryException.ToString()+[Environment]::NewLine+$primaryException.ScriptStackTrace+[Environment]::NewLine+$primaryException.Exception.ToString()+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))}
    }catch{}
    if (Get-Variable receipt -ErrorAction SilentlyContinue) {
        try {$receipt.status='FAIL';$receipt.durationMs=$clock.ElapsedMilliseconds;Write-HistoryCheckpoint $receipt $receiptPath} catch {}
    }
    if ($null -ne $context) {Write-QaPublicConsoleEvent -RunId $context.runId -Suite Windows -StepId ('historical-sales-import-'+$(if ($Mode -eq 'CrashProbe') {'crash'} else {$Mode.ToLowerInvariant()})) -Code qa_step_failed}
    else {Write-QaPublicConsoleEvent -Suite Windows -StepId preflight -Code qa_step_failed}
}
exit $exitCode
