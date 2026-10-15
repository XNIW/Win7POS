#requires -Version 7.4
[CmdletBinding()]
param(
    [ValidateSet('Local','Windows','Staging','Win7','Hardware')][string]$Suite = 'Local',
    [string]$OutputRoot = ([IO.Path]::GetTempPath()),
    [ValidatePattern('^$|^[a-z0-9][a-z0-9-]{0,63}$')][string]$Profile = '',
    [ValidatePattern('^$|^ASUSART_POST_PR68_[0-9]{8}T[0-9]{9}Z_[A-F0-9]{8}$')][string]$ReadinessRunId = '',
    [ValidatePattern('^$|^[0-9]+$')][string]$ReleasePackRunId = '',
    [string]$CancellationFile = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-QaWithin([string]$Path, [string]$Root) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\','/')
    $parent = [IO.Path]::GetFullPath($Root).TrimEnd('\','/')
    return $full.Equals($parent, [StringComparison]::OrdinalIgnoreCase) -or
        $full.StartsWith($parent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-QaPath([string]$Path, [string]$Root = '') {
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw 'qa_path_must_be_absolute' }
    $full = [IO.Path]::GetFullPath($Path)
    if ($Root -and -not (Test-QaWithin $full $Root)) { throw 'qa_path_outside_root' }
    $cursor = $full
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'qa_path_contains_link'
            }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    return $full
}

function Assert-QaOutputRoot([string]$OutputRoot, [string]$RepoRoot) {
    $root = Assert-QaPath $OutputRoot
    $forbidden = @($RepoRoot, 'C:\POSData', (Join-Path ([IO.Path]::GetTempPath()) 'Win7POS'))
    # Match every fallback in Core PosPaths.GetDataRoot, even when no store
    # process or WIN7POS_DATA_DIR override is currently present.
    foreach ($folder in @([Environment]::GetFolderPath('CommonApplicationData'),
        [Environment]::GetFolderPath('LocalApplicationData'), $env:APPDATA)) {
        if ($folder) { $forbidden += Join-Path $folder 'Win7POS' }
    }
    if ($env:WIN7POS_DATA_DIR) { $forbidden += $env:WIN7POS_DATA_DIR }
    foreach ($denied in $forbidden) {
        if (Test-QaWithin $root $denied) { throw 'qa_output_overlaps_checkout_or_store_data' }
    }
    return $root
}

function Test-QaProcessIdentity($Process, $Identity) {
    try {
        $Process.Refresh()
        return -not $Process.HasExited -and $Process.Id -eq $Identity.pid -and
            $Process.StartTime.ToUniversalTime().Ticks -eq $Identity.creationTicks -and
            [string]::Equals((Get-QaProcessImagePath $Process), $Identity.executable, [StringComparison]::OrdinalIgnoreCase)
    } catch { return $false }
}

function Get-QaProcessImagePath($Process) {
    if ($IsWindows -and $Process -is [Diagnostics.Process]) {
        Initialize-QaJobApi
        return [Win7PosQaJob]::ImagePath($Process.Handle)
    }
    $module = $Process.MainModule
    if ($null -ne $module) { return $module.FileName }
    return $null
}

function Get-QaProcessIdentity($Process, [string]$ExpectedExecutable) {
    # Windows uses the process-handle image path, independent of loader readiness.
    # The portable fallback permits a bounded wait while the launch gate is shut.
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $Process.Refresh()
        if ($Process.HasExited) { throw 'qa_process_exited_before_identity' }
        $imagePath = Get-QaProcessImagePath $Process
        if ($imagePath) {
            if (-not [string]::Equals($imagePath, $ExpectedExecutable, [StringComparison]::OrdinalIgnoreCase)) { throw 'qa_process_image_mismatch' }
            return @{ pid=$Process.Id; creationTicks=$Process.StartTime.ToUniversalTime().Ticks; executable=$imagePath }
        }
        [Threading.Thread]::Sleep(20)
    } while ($clock.ElapsedMilliseconds -lt 2000)
    throw 'qa_process_identity_unavailable'
}

function Get-QaProcessOutcome([bool]$Exited, [Nullable[int]]$ExitCode, [bool]$TimedOut, [bool]$Cancelled) {
    if ($TimedOut) { return @{ status='TIMEOUT'; code='process_timeout' } }
    if ($Cancelled) { return @{ status='NOT_RUN'; code='cancelled' } }
    if (-not $Exited -or $null -eq $ExitCode) { return @{ status='FAIL'; code='process_launch_failed' } }
    return @{ status=$(if ($ExitCode -eq 0) { 'PASS' } else { 'FAIL' }); code='process_exit' }
}

function Test-QaStagingProfile($State) {
    return $null -ne $State -and $State.Exists -and $State.Valid -and
        $State.BaseUrlHost -ceq 'merchandise-control-admin-web-staging.merchandise-control-admin-web.workers.dev' -and
        $State.ProfileBindingSha256 -cmatch '^[0-9a-f]{64}$'
}

function Test-QaUiAvailable([bool]$Windows, [bool]$Interactive) { return $Windows -and $Interactive }

function Assert-QaNotCancelled([string]$CancellationFile) {
    if ($CancellationFile -and (Test-Path -LiteralPath $CancellationFile)) {
        throw [OperationCanceledException]::new('qa_cancelled')
    }
}

function Get-QaErrorCode($Record) {
    $message = $Record.Exception.Message
    if ($message -cmatch '^qa_[a-z_]+$') { return $message }
    return $Record.Exception.GetType().Name
}

function Get-QaDigest([string]$Text) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant()
}

function Invoke-QaGit([string]$RepoRoot, [string[]]$Arguments) {
    $lines = @(& git -C $RepoRoot -c core.quotepath=false @Arguments)
    if ($LASTEXITCODE -ne 0) { throw 'qa_source_identity_missing' }
    return $lines
}

function Get-QaSourceState([string]$RepoRoot) {
    $head = @(Invoke-QaGit $RepoRoot @('rev-parse','HEAD'))
    if ($head.Count -ne 1 -or $head[0] -cnotmatch '^[0-9a-f]{40}$') { throw 'qa_source_identity_missing' }
    # Hash changes without writing their content or values into evidence.
    $diff = @(Invoke-QaGit $RepoRoot @('diff','HEAD','--binary','--no-ext-diff','--no-textconv','--no-color'))
    $status = @(Invoke-QaGit $RepoRoot @('status','--porcelain=v1','--untracked-files=all'))
    $untracked = @(Invoke-QaGit $RepoRoot @('ls-files','--others','--exclude-standard','--','src','scripts','tests','.github','global.json','Win7POS.slnx','Directory.Build.props','Directory.Build.targets'))
    $sourceManifest = @()
    foreach ($relative in ($untracked | Sort-Object -CaseSensitive)) {
        $full = Assert-QaPath (Join-Path $RepoRoot $relative) $RepoRoot
        if ([IO.Path]::GetExtension($full) -in @('.cs','.xaml','.csproj','.props','.targets','.ps1','.psm1','.json','.yml','.yaml','.mjs')) {
            $sourceManifest += $relative + [char]0 + (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    $diffDigest = Get-QaDigest ($diff -join "`n")
    $untrackedDigest = Get-QaDigest ($sourceManifest -join "`n")
    return [pscustomobject]@{ sourceSha=$head[0]; dirty=($status.Count -gt 0); trackedDiffSha256=$diffDigest;
        untrackedSourceCount=$sourceManifest.Count; untrackedSourceSha256=$untrackedDigest;
        fingerprintSha256=(Get-QaDigest ($head[0] + [char]0 + $diffDigest + [char]0 + $untrackedDigest)) }
}

function Initialize-QaJobApi {
    if ('Win7PosQaJob' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Win7PosQaJob {
    [StructLayout(LayoutKind.Sequential)] struct Basic { public long a,b; public uint flags; public UIntPtr min,max; public uint active; public UIntPtr affinity; public uint priority,scheduling; }
    [StructLayout(LayoutKind.Sequential)] struct Io { public ulong a,b,c,d,e,f; }
    [StructLayout(LayoutKind.Sequential)] struct Extended { public Basic basic; public Io io; public UIntPtr process,job,peakProcess,peakJob; }
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr CreateJobObject(IntPtr a, string name);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetInformationJobObject(IntPtr h,int kind,ref Extended value,uint size);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, uint flags, System.Text.StringBuilder image, ref uint size);
    public static string ImagePath(IntPtr process) {
        uint size=32768; var image=new System.Text.StringBuilder((int)size);
        if(!QueryFullProcessImageName(process,0,image,ref size)) throw new System.ComponentModel.Win32Exception();
        return image.ToString();
    }
    public static IntPtr Create() {
        var h=CreateJobObject(IntPtr.Zero,null);
        if(h==IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        var info=new Extended(); info.basic.flags=0x2000;
        if(!SetInformationJobObject(h,9,ref info,(uint)Marshal.SizeOf(info))) { CloseHandle(h); throw new System.ComponentModel.Win32Exception(); }
        return h;
    }
}
'@
}

function Invoke-QaProcess {
    param([string]$Executable, [string[]]$Arguments, [string]$Directory,
        [string]$WorkingDirectory, [int]$TimeoutSeconds, [string]$CancellationFile = '', [bool]$Staging = $false)
    $result = [ordered]@{ status='FAIL'; code='process_launch_failed'; exitCode=$null; identity=$null; elapsedMs=0; errorType=$null }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = $null; $started = $false; $job = [IntPtr]::Zero; $outFile = $null; $errFile = $null; $outTask = $null; $errTask = $null
    try {
        Assert-QaNotCancelled $CancellationFile
        $command = Get-Command $Executable -CommandType Application -ErrorAction Stop | Select-Object -First 1
        $shell = (Get-Process -Id $PID).Path
        $descriptor = Join-Path $Directory 'command.json'
        @{ executable=$command.Source; arguments=@($Arguments) } | ConvertTo-Json -Depth 4 |
            Set-Content -LiteralPath $descriptor -Encoding utf8
        $gate = Join-Path $Directory 'launch.gate'
        $bootstrap = Join-Path $Directory 'launch.ps1'
        @'
param($Descriptor, $Gate)
$ErrorActionPreference='Stop'
$clock=[Diagnostics.Stopwatch]::StartNew()
while (-not (Test-Path -LiteralPath $Gate)) {
    if ($clock.Elapsed.TotalSeconds -ge 15) { exit 125 }
    Start-Sleep -Milliseconds 50
}
try {
    $command=Get-Content -LiteralPath $Descriptor -Raw | ConvertFrom-Json
    $commandArgs=@($command.arguments)
    & $command.executable @commandArgs
    exit $LASTEXITCODE
} catch { [Console]::Error.WriteLine($_.Exception.GetType().Name); exit 126 }
'@ | Set-Content -LiteralPath $bootstrap -Encoding utf8
        $start = [Diagnostics.ProcessStartInfo]::new($shell)
        $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WorkingDirectory = $WorkingDirectory
        $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
        foreach ($arg in @('-NoProfile','-File',$bootstrap,$descriptor,$gate)) { $start.ArgumentList.Add($arg) }
        # Never mutate the caller's environment. Every canonical temp fixture is below this run.
        foreach ($key in @($start.Environment.Keys)) {
            if ($key -like 'WIN7POS_*') { [void]$start.Environment.Remove($key) }
        }
        $temporary = Join-Path $Directory 'temp'; $data = Join-Path $Directory 'data'
        New-Item -ItemType Directory -Path $temporary,$data | Out-Null
        foreach ($key in @('TEMP','TMP','TMPDIR')) { $start.Environment[$key] = $temporary }
        $start.Environment['WIN7POS_DATA_DIR'] = $data
        if (-not $Staging) { $start.Environment['WIN7POS_ADMIN_WEB_BASE_URL'] = 'http://127.0.0.1:9' }
        if ($IsWindows) { Initialize-QaJobApi; $job = [Win7PosQaJob]::Create() }
        $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
        Assert-QaNotCancelled $CancellationFile
        if (-not $process.Start()) { throw 'qa_process_missing' }; $started = $true
        # Assign by the exact process handle before any image-query failure can
        # leave the gated child outside the owned Job.
        $assigned = -not $IsWindows -or [Win7PosQaJob]::AssignProcessToJobObject($job, $process.Handle)
        $result.identity = Get-QaProcessIdentity $process $shell
        if (-not $assigned) { throw 'qa_job_assignment_failed' }
        $outFile = [IO.File]::Create((Join-Path $Directory 'stdout.log'))
        $errFile = [IO.File]::Create((Join-Path $Directory 'stderr.log'))
        $outTask = $process.StandardOutput.BaseStream.CopyToAsync($outFile)
        $errTask = $process.StandardError.BaseStream.CopyToAsync($errFile)
        # No tested command can start until its supervisor owns the Windows Job.
        Assert-QaNotCancelled $CancellationFile
        [IO.File]::WriteAllText($gate, 'owned')
        $result.code = 'process_exit'
        while (-not $process.WaitForExit(100)) {
            if ($CancellationFile -and (Test-Path -LiteralPath $CancellationFile)) { $result.code='cancelled'; $result.status='NOT_RUN'; break }
            if ($watch.Elapsed.TotalSeconds -ge $TimeoutSeconds) { $result.code='process_timeout'; $result.status='TIMEOUT'; break }
        }
        if ($process.HasExited -and $result.code -eq 'process_exit') {
            $result.exitCode = $process.ExitCode
            $outcome = Get-QaProcessOutcome $true $process.ExitCode $false $false
            $result.status = $outcome.status
        }
    } catch [OperationCanceledException] { $result.status='NOT_RUN'; $result.code='cancelled' }
    catch {
        $result.code = if ($_.Exception.Message -cmatch '^qa_[a-z_]+$') { $_.Exception.Message } else { 'process_launch_or_supervision_failed' }
        $result.errorType=$_.Exception.GetType().Name
    }
    finally {
        if ($job -ne [IntPtr]::Zero) { [void][Win7PosQaJob]::CloseHandle($job) }
        if ($started) {
            if ($null -ne $result.identity -and (Test-QaProcessIdentity $process $result.identity)) {
                try { $process.Kill($true) } catch { if ($result.status -eq 'PASS') { $result.status='FAIL'; $result.code='owned_process_cleanup_failed' } }
            }
            if (-not $process.HasExited -and -not $process.WaitForExit(5000) -and $result.status -eq 'PASS') { $result.status='FAIL'; $result.code='owned_process_remaining' }
            foreach ($task in @($outTask,$errTask)) {
                if ($null -ne $task) { try { if (-not $task.Wait(5000)) { throw 'stream_timeout' } } catch { if ($result.status -eq 'PASS') { $result.status='FAIL'; $result.code='process_log_flush_failed' } } }
            }
        }
        if ($null -ne $process) { $process.Dispose() }
        if ($null -ne $outFile) { $outFile.Dispose() }; if ($null -ne $errFile) { $errFile.Dispose() }
        $result.elapsedMs = $watch.ElapsedMilliseconds
    }
    return [pscustomobject]$result
}

function New-QaStep($Name, $Executable, [string[]]$Arguments, [string[]]$Depends = @(), [int]$Timeout = 600, $Evidence = '') {
    [pscustomobject]@{ name=$Name; executable=$Executable; arguments=$Arguments; depends=$Depends; timeout=$Timeout; evidence=$Evidence }
}

function New-QaPlan([string]$Suite, [string]$RunDirectory, [string]$Shell) {
    $core = 'tests/Win7POS.Core.Tests/Win7POS.Core.Tests.csproj'; $cli = 'src/Win7POS.Cli/Win7POS.Cli.csproj'
    if ($Suite -eq 'Windows') {
        New-QaStep 'restore' 'dotnet' @('restore','Win7POS.slnx','--locked-mode')
        New-QaStep 'gates' $Shell @('-NoProfile','-File','scripts/check-required-gates.ps1')
        New-QaStep 'build' 'dotnet' @('build','Win7POS.slnx','-c','Release','--no-restore') @('restore')
    } else {
        New-QaStep 'restore' 'dotnet' @('restore',$core,'--locked-mode')
        New-QaStep 'restore-cli' 'dotnet' @('restore',$cli,'--locked-mode')
        New-QaStep 'gates' $Shell @('-NoProfile','-File','scripts/check-owned-catalog-qa.ps1')
        New-QaStep 'build' 'dotnet' @('build',$core,'-c','Release','--no-restore') @('restore')
        New-QaStep 'build-cli' 'dotnet' @('build',$cli,'-c','Release','--no-restore') @('restore-cli')
    }
    # Original main CI measured 35m56s for Core/Data; retain a bounded hour.
    New-QaStep 'core' 'dotnet' @('test',$core,'-c','Release','--no-build','--no-restore','--logger','trx;LogFileName=core.trx','--results-directory',(Join-Path $RunDirectory 'core')) @('build') 3600 'trx'
    $cliDependency = if ($Suite -eq 'Windows') { 'build' } else { 'build-cli' }
    New-QaStep 'selftest' 'dotnet' @('run','--project',$cli,'-c','Release','--no-build','--no-restore','--','--selftest','--keepdb') @($cliDependency)
    if ($Suite -ne 'Windows') { return }
    New-QaStep 'build-wpf' 'dotnet' @('build','src/Win7POS.Wpf/Win7POS.Wpf.csproj','-c','Release','-p:Platform=x86','-p:PlatformTarget=x86','--no-restore') @('restore')
    New-QaStep 'build-harness' 'dotnet' @('build','tests/Win7POS.Wpf.UiSmokeHarness/Win7POS.Wpf.UiSmokeHarness.csproj','-c','Release','-p:Platform=x86','-p:PlatformTarget=x86','--no-restore') @('build-wpf')
    $smokeRunners = @(
        @{ name='product-image-profile'; path='scripts/run-product-image-profile-smoke.ps1' }
        @{ name='functional-completion'; path='scripts/run-functional-completion-smoke.ps1' }
        @{ name='authorization-lease'; path='scripts/run-authorization-lease-smoke.ps1' }
        @{ name='bounded-logging'; path='scripts/run-bounded-logging-smoke.ps1' }
        @{ name='product-paging-dispatcher'; path='scripts/run-product-paging-dispatcher-smoke.ps1' }
    )
    foreach ($smokeRunner in $smokeRunners) {
        $smoke = $smokeRunner.name
        $arguments = @('-NoProfile','-File',$smokeRunner.path,'-Configuration','Release','-Platform','x86')
        $evidence = ''
        if ($smoke -eq 'functional-completion') { $arguments += @('-OutputDirectory',(Join-Path $RunDirectory 'functional')); $evidence = 'functional' }
        New-QaStep $smoke $Shell $arguments @('build-harness') 1800 $evidence
    }
}

function Assert-QaEvidence($Step, [string]$RunDirectory) {
    if ($Step.evidence -eq 'trx') {
        [xml]$trx = Get-Content -LiteralPath (Join-Path $RunDirectory 'core/core.trx') -Raw
        $counters = $trx.SelectSingleNode("//*[local-name()='Counters']")
        if ($null -eq $counters -or [int]$counters.total -le 0 -or [int]$counters.executed -le 0) { throw 'zero_tests' }
        if ([int]$counters.failed -ne 0 -or [int]$counters.passed -ne [int]$counters.total) { throw 'incomplete_core_tests' }
    }
    if ($Step.evidence -eq 'functional') {
        $manifest = Get-Content -LiteralPath (Join-Path $RunDirectory 'functional/functional-results.json') -Raw | ConvertFrom-Json
        $results = @($manifest.results)
        if ($manifest.schemaVersion -ne 1 -or $results.Count -ne 20 -or @($results.name | Select-Object -Unique).Count -ne 20) { throw 'incomplete_functional_tests' }
        $expected = @('FUNCTIONAL_VISUAL','IMPORT_completion','IMPORT_RECOVERY_completion','PRICE_HISTORY_completion','REFUND_completion','REVERSAL_authorization','F06_reversed_preview_delete_close','F01_export_gate','F07_settings_rollback','HARDWARE_settings_scanner','CUSTOMERDISPLAY_polish','CUSTOMERDISPLAY_completion','STOCK_fractional_input','F02_F03_workflow_stable_identity_batch_dispatcher','F05_same_clock_recovery_restart_active_cart','F08_discount_preview_lifetime','INTEGRATED_login_sale_retry_receipt_restart','LOAD_sync_backup_x86','P109_product_editor_late_render_close','PERF_visual_lifetime_and_public_commands')
        foreach ($name in $expected) { if (@($results | Where-Object { $_.name -ceq $name }).Count -ne 1) { throw 'functional_scenario_missing' } }
        if (@($results | Where-Object { $_.status -cne 'PASS' -or $null -eq $_.exitCode -or $_.exitCode -ne 0 }).Count) { throw 'functional_tests_failed' }
    }
}

function Invoke-QaPlan($Plan, [string]$RunDirectory, [string]$RepoRoot, [string]$CancellationFile = '', [Collections.IDictionary]$Report = $null) {
    $rows = [Collections.Generic.List[object]]::new(); $states = @{}
    if (@($Plan).Count -eq 0) { throw 'zero_tests' }
    foreach ($step in $Plan) {
        $row = [ordered]@{ name=$step.name; status='NOT_RUN'; code='dependency_failed'; exitCode=$null; identity=$null; elapsedMs=0; errorType=$null; evidenceDirectory=$null; tests=@() }
        try {
            if ($CancellationFile -and (Test-Path -LiteralPath $CancellationFile)) { $row.code='cancelled' }
            elseif (@($step.depends | Where-Object { -not $states.ContainsKey($_) -or $states[$_] -ne 'PASS' }).Count -eq 0) {
                $directory = Assert-QaPath (Join-Path $RunDirectory $step.name) $RunDirectory
                $row.evidenceDirectory = $directory
                if (Test-Path -LiteralPath $directory) {
                    if (-not (Test-Path -LiteralPath $directory -PathType Container)) { throw 'qa_step_directory_invalid' }
                } else { New-Item -ItemType Directory -Path $directory | Out-Null }
                $processResult = Invoke-QaProcess -Executable $step.executable -Arguments $step.arguments -Directory $directory -WorkingDirectory $RepoRoot -TimeoutSeconds $step.timeout -CancellationFile $CancellationFile
                foreach ($key in @('status','code','exitCode','identity','elapsedMs')) { $row[$key] = $processResult.$key }
                if ($processResult.PSObject.Properties.Name -contains 'errorType') { $row.errorType=$processResult.errorType }
                # Preserve partial scenario results even when the canonical runner exits nonzero.
                if ($step.evidence -eq 'functional') {
                    try {
                        $partial = Get-Content -LiteralPath (Join-Path $RunDirectory 'functional/functional-results.json') -Raw | ConvertFrom-Json
                        $row.tests = @($partial.results)
                    } catch { }
                }
                if ($row.status -eq 'PASS') {
                    try { Assert-QaEvidence $step $RunDirectory } catch { $row.status='FAIL'; $row.code='missing_or_invalid_test_evidence' }
                }
            }
        } catch {
            $row.status='FAIL'; $row.code='qa_step_preparation_or_supervision_failed'; $row.errorType=$_.Exception.GetType().Name
        }
        $states[$step.name] = $row.status; $rows.Add([pscustomobject]$row)
        if ($null -ne $Report) {
            $Report.results = $rows.ToArray()
            $Report.status = 'FAIL' # A checkpoint never certifies a partial suite.
            if ($null -eq $Report.firstError -and $row.status -cne 'PASS') { $Report.firstError=[pscustomobject]$row }
            try { Write-QaReports $Report $RunDirectory }
            catch {
                $Report['reportError'] = @{ name='report'; status='FAIL'; code='qa_report_write_failed'; errorType=$_.Exception.GetType().Name }
                if ($null -eq $Report.firstError) { $Report.firstError=$Report['reportError'] }
            }
        }
    }
    return $rows.ToArray()
}

function Write-QaReports($Report, [string]$Directory) {
    # Do not replace firstError when report export fails; caller returns its failure separately.
    $summary = @("WIN7POS_AUTONOMOUS_QA $($Report.status)","runId=$($Report.runId)","sourceSha=$($Report.sourceSha)","suite=$($Report.suite)")
    $summary += @($Report.results | ForEach-Object { "$($_.name): $($_.status) [$($_.code)]" })
    $summary | Set-Content -LiteralPath (Join-Path $Directory 'qa-summary.txt') -Encoding utf8
    $Report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $Directory 'qa-results.json') -Encoding utf8
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runId = 'Win7POS.QA.' + [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '.' + [Guid]::NewGuid().ToString('N')
$report = [ordered]@{ schemaVersion=1; runId=$runId; suite=$Suite; sourceSha=$null; sourceState=$null; sourceStateAfter=$null; host=[Environment]::MachineName; os=[Environment]::OSVersion.VersionString; powershell=$PSVersionTable.PSVersion.ToString(); profile=$Profile; packageSha256=$null; binaries=@(); status='FAIL'; firstError=$null; reportError=$null; results=@() }
$mutex = $null; $locked = $false; $runDirectory = $null; $exitCode = 1
try {
    $root = Assert-QaOutputRoot $OutputRoot $repoRoot
    $runDirectory = Assert-QaPath (Join-Path $root $runId) $root
    if (Test-Path -LiteralPath $runDirectory) { throw 'qa_run_identity_reused' }
    New-Item -ItemType Directory -Path $runDirectory | Out-Null
    # Verify both output files before restore/build or any live action.
    Write-QaReports $report $runDirectory
    $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($repoRoot.ToUpperInvariant())))
    $mutexPrefix = if ($IsWindows) { 'Global\' } else { '' }
    $mutex = [Threading.Mutex]::new($false, ($mutexPrefix + 'Win7POS.QA.Build.' + $digest))
    try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
    if (-not $locked) { throw 'qa_checkout_build_locked' }
    $report.sourceState = Get-QaSourceState $repoRoot
    $report.sourceSha = $report.sourceState.sourceSha
    if ($Suite -in @('Win7','Hardware')) {
        $report.results = @([pscustomobject]@{ name=$Suite; status='BLOCKED_EXTERNAL'; code='identified_real_host_package_device_and_scope_required' })
    } elseif ($Suite -in @('Windows','Staging') -and -not (Test-QaUiAvailable $IsWindows ([Environment]::UserInteractive))) {
        $report.results = @([pscustomobject]@{ name=$Suite; status='BLOCKED_EXTERNAL'; code='interactive_windows_ui_unavailable' })
    } elseif ($Suite -eq 'Staging') {
        # The existing runner alone validates current readiness, exact main/release and remote scope.
        $ready = $false
        if ($Profile -and $ReadinessRunId -and $ReleasePackRunId) {
            Import-Module (Join-Path $PSScriptRoot 'Win7PosQaCredentialVault.psm1') -Force
            $state = Test-Win7PosQaCredentialProfile -Profile $Profile
            $ready = Test-QaStagingProfile $state
        }
        if (-not $ready) {
            $report.results = @([pscustomobject]@{ name='staging'; status='BLOCKED_EXTERNAL'; code='verified_vault_profile_fresh_readiness_and_release_required' })
        } else {
            $directory = Join-Path $runDirectory 'staging'; New-Item -ItemType Directory -Path $directory | Out-Null
            $result = Invoke-QaProcess -Executable (Get-Process -Id $PID).Path -Arguments @('-NoProfile','-File','scripts/qa/Invoke-Win7PosStagingAcceptance.ps1','-Profile',$Profile,'-ReadinessRunId',$ReadinessRunId,'-ReleasePackRunId',$ReleasePackRunId) -Directory $directory -WorkingDirectory $repoRoot -TimeoutSeconds 3600 -CancellationFile $CancellationFile -Staging $true
            if ($result.status -eq 'PASS') {
                try {
                    $receiptPath = "C:\Dev\_codex-evidence\win7pos-final-post-pr68-$ReadinessRunId\staging-acceptance-runner-result.json"
                    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
                    if ($receipt.runId -cne $ReadinessRunId -or $receipt.passed -ne $true -or $receipt.logicalRuns -ne 1 -or $receipt.orphanRemaining -ne $false) { throw 'staging_result_invalid' }
                } catch { $result.status='FAIL'; $result.code='missing_or_invalid_staging_evidence' }
            }
            $report.results = @([pscustomobject]@{ name='staging'; status=$result.status; code=$result.code; exitCode=$result.exitCode; identity=$result.identity })
        }
    } else {
        $plan = @(New-QaPlan $Suite $runDirectory (Get-Process -Id $PID).Path)
        $report.results = @(Invoke-QaPlan $plan $runDirectory $repoRoot $CancellationFile $report)
        $firstFailure = @($report.results | Where-Object { $_.status -cne 'PASS' } | Select-Object -First 1)
        if ($null -eq $report.firstError -and $firstFailure.Count) { $report.firstError=$firstFailure[0] }
        if ($Suite -eq 'Windows') {
            foreach ($built in @(@{ step='build-wpf'; path='src/Win7POS.Wpf/bin/x86/Release/net48/Win7POS.Wpf.exe' }, @{ step='build-harness'; path='tests/Win7POS.Wpf.UiSmokeHarness/bin/x86/Release/net48/Win7POS.Wpf.UiSmokeHarness.exe' })) {
                if (@($report.results | Where-Object { $_.name -eq $built.step -and $_.status -eq 'PASS' }).Count -ne 1) { continue }
                $path = Join-Path $repoRoot $built.path
                if (Test-Path -LiteralPath $path -PathType Leaf) { $report.binaries += @{ path=$built.path; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() } }
                else { $report.results += [pscustomobject]@{ name=$built.step + '-binary'; status='FAIL'; code='qa_expected_binary_missing' } }
            }
        }
    }
    $firstFailure = @($report.results | Where-Object { $_.status -cne 'PASS' } | Select-Object -First 1)
    if ($null -eq $report.firstError -and $firstFailure.Count) { $report.firstError=$firstFailure[0] }
    $report.sourceStateAfter = Get-QaSourceState $repoRoot
    if ($report.sourceStateAfter.fingerprintSha256 -cne $report.sourceState.fingerprintSha256) {
        $report.results += [pscustomobject]@{ name='source-identity'; status='FAIL'; code='qa_sources_changed_during_run' }
    }
    $failures = @($report.results | Where-Object { $_.status -cne 'PASS' })
    if ($report.results.Count -gt 0 -and $failures.Count -eq 0 -and $null -eq $report.reportError) { $report.status='PASS'; $exitCode=0 }
    elseif ($failures.Count -and $null -eq $report.firstError) { $report.firstError = $failures[0] }
} catch {
    if ($null -eq $report.firstError) { $report.firstError = @{ test='preflight'; status='FAIL'; code=(Get-QaErrorCode $_) } }
    [Console]::Error.WriteLine('WIN7POS_QA_FAILED: ' + $report.firstError.code)
} finally {
    try { if ($locked) { $mutex.ReleaseMutex() }; if ($null -ne $mutex) { $mutex.Dispose() } }
    catch {
        $exitCode=1; $report.status='FAIL'
        if ($null -eq $report.firstError) { $report.firstError=@{ test='cleanup'; status='FAIL'; code='qa_checkout_lock_release_failed' } }
    }
    if ($runDirectory) {
        try { Write-QaReports $report $runDirectory }
        catch {
            $exitCode=1; $report.status='FAIL'
            if ($null -eq $report.firstError) { $report.firstError=@{ test='report'; status='FAIL'; code='qa_report_write_failed' } }
            [Console]::Error.WriteLine('WIN7POS_QA_REPORT_WRITE_FAILED; firstError=' + ($report.firstError | ConvertTo-Json -Depth 10 -Compress))
        }
        Write-Output ('WIN7POS_QA_EVIDENCE=' + $runDirectory)
    }
}
exit $exitCode
