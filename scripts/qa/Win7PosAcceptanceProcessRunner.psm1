Set-StrictMode -Version Latest

function Enter-Win7PosAcceptanceLock {
    [CmdletBinding()]
    param(
        [string]$Name = 'Local\Win7POS.StagingAcceptance.v2'
    )

    $mutex = [System.Threading.Mutex]::new($false, $Name)
    $acquired = $false
    try {
        $acquired = $mutex.WaitOne(0)
    }
    catch [System.Threading.AbandonedMutexException] {
        $acquired = $true
    }

    [pscustomobject]@{
        Acquired = $acquired
        Mutex = $mutex
        Name = $Name
    }
}

function Exit-Win7PosAcceptanceLock {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Lease
    )

    if ($Lease.Acquired) {
        try {
            $Lease.Mutex.ReleaseMutex()
        }
        catch [System.ApplicationException] {
        }
    }
    $Lease.Mutex.Dispose()
}

function Test-Win7PosAcceptanceProcessActive {
    [CmdletBinding()]
    param()

    $names = @(
        'Win7POS.Wpf',
        'Win7POS.Wpf.UiSmokeHarness'
    )
    foreach ($name in $names) {
        if (Get-Process -Name $name -ErrorAction SilentlyContinue) {
            return $true
        }
    }
    return $false
}

function Get-Win7PosAcceptanceResultCode {
    [CmdletBinding()]
    param(
        [AllowNull()][AllowEmptyString()][string]$Code
    )

    $normalized = if ($null -eq $Code) {
        ''
    }
    else {
        $Code.Trim().ToLowerInvariant()
    }
    if ($normalized -match '^[a-z0-9][a-z0-9_.-]{0,119}$') {
        return $normalized
    }

    return 'acceptance_result_invalid_code'
}

function Get-Win7PosAcceptanceLogicalRunCount {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$EvidenceDirectory,
        [Parameter(Mandatory = $true)][string]$RunId
    )

    $fullEvidenceDirectory = [System.IO.Path]::GetFullPath(
        $EvidenceDirectory)
    $consumedMarkerPath = Join-Path $fullEvidenceDirectory (
        'run-consumed-redacted.json')

    # The evidence directory is unique per invocation. Treat marker presence
    # conservatively as a consumed run even if its bounded JSON was damaged
    # after the atomic rename; a corrupt marker must never restore run budget.
    $temporaryConsumedMarkers = @(
        Get-ChildItem -LiteralPath $fullEvidenceDirectory `
            -Filter 'run-consumed-redacted.json.*.tmp' `
            -File `
            -ErrorAction SilentlyContinue
    )
    if ((Test-Path -LiteralPath $consumedMarkerPath -PathType Leaf) -or
        $temporaryConsumedMarkers.Count -gt 0) {
        return 1
    }

    $acceptanceReportPath = Join-Path $fullEvidenceDirectory (
        'staging-acceptance-result.json')
    if (-not (Test-Path -LiteralPath $acceptanceReportPath -PathType Leaf)) {
        return 0
    }

    try {
        $boundedReport = Get-Content -Raw -LiteralPath (
            $acceptanceReportPath) | ConvertFrom-Json -ErrorAction Stop
        if ($boundedReport.requestReachedServer -eq $true -and
            [string]::Equals(
                [string]$boundedReport.runId,
                $RunId,
                [System.StringComparison]::Ordinal)) {
            return 1
        }
    }
    catch {
    }

    return 0
}

function Get-Win7PosAcceptanceProcessIdentity {
    param([Parameter(Mandatory)]$Process,[Parameter(Mandatory)][string]$ExpectedPath,[ValidateRange(1,2000)][int]$TimeoutMilliseconds=2000)
    $clock=[Diagnostics.Stopwatch]::StartNew()
    $expected=[IO.Path]::GetFullPath($ExpectedPath);$expectedHash=(Get-FileHash -LiteralPath $expected -Algorithm SHA256).Hash.ToLowerInvariant()
    $pinnedPid=$Process.Id;$pinnedCreation=$Process.StartTime.ToUniversalTime().Ticks;$sawMismatch=$false
    do {
        $Process.Refresh()
        if ($Process.HasExited) {throw 'qa_owned_process_exited_before_identity'}
        if($Process.Id -ne $pinnedPid -or $Process.StartTime.ToUniversalTime().Ticks -ne $pinnedCreation){throw 'qa_owned_process_identity_changed'}
        $module=$Process.MainModule
        if ($null -ne $module -and $module.FileName) {
            if([string]::Equals([IO.Path]::GetFullPath($module.FileName),$expected,[StringComparison]::OrdinalIgnoreCase)){
                $observedHash=(Get-FileHash -LiteralPath $module.FileName -Algorithm SHA256).Hash.ToLowerInvariant()
                if($observedHash -cne $expectedHash){throw 'qa_owned_process_binary_changed'}
                return @{pid=$pinnedPid;creationTicks=$pinnedCreation;executable=$expected;exeSha256=$observedHash}
            }
            # WOW64 can briefly expose its loader image through the retained
            # process handle. Readiness still requires the exact expected image.
            $sawMismatch=$true
        }
        $remaining=$TimeoutMilliseconds-[int]$clock.ElapsedMilliseconds
        if($remaining -gt 0){[Threading.Thread]::Sleep([Math]::Min(10,$remaining))}
    } while ($clock.ElapsedMilliseconds -lt $TimeoutMilliseconds)
    if($sawMismatch){throw 'qa_owned_process_image_mismatch'}
    throw 'qa_owned_process_identity_missing'
}

function Test-Win7PosAcceptanceProcessIdentity {
    param($Process,$Identity)
    try {
        $Process.Refresh()
        return -not $Process.HasExited -and $Process.Id -eq $Identity.pid -and $Process.StartTime.ToUniversalTime().Ticks -eq $Identity.creationTicks -and [string]::Equals($Process.MainModule.FileName,$Identity.executable,[StringComparison]::OrdinalIgnoreCase)
    } catch {return $false}
}

function Invoke-Win7PosWaitedProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList,
        [Parameter(Mandatory = $true)]
        [ValidateRange(100, 3600000)]
        [int]$TimeoutMilliseconds,
        [Parameter(Mandatory = $true)][string]$EvidenceDirectory,
        [scriptblock]$OnStarted,
        [string]$CancellationFile = '',
        [hashtable]$EnvironmentVariables = @{}
    )

    $result = [ordered]@{
        EvidenceDirectory = [System.IO.Path]::GetFullPath($EvidenceDirectory)
        ExitCode = $null
        OrphanRemaining = $false
        ProcessId = $null
        Started = $false
        TimedOut = $false
        LaunchError = $null
        Identity = $null
        CleanupSucceeded = $true
        Cancelled = $false
    }

    $process = $null
    $clock=[Diagnostics.Stopwatch]::StartNew()
    try {
        if ($CancellationFile -and (Test-Path -LiteralPath $CancellationFile)) {throw [OperationCanceledException]::new('qa_cancelled')}
        $launch=@{FilePath=([IO.Path]::GetFullPath($FilePath));ArgumentList=$ArgumentList;PassThru=$true;WindowStyle='Hidden'}
        if($EnvironmentVariables.Count){
            foreach($key in $EnvironmentVariables.Keys){if($key -cnotin @('WIN7POS_DATA_DIR','WIN7POS_ADMIN_WEB_BASE_URL')){throw 'qa_owned_child_environment_invalid'}}
            # Start-Process -Environment is available on the required QA host
            # PowerShell 7.4; no caller environment is changed.
            $launch.Environment=$EnvironmentVariables
        }
        $process=Start-Process @launch
        $result.Started = $true
        $result.ProcessId = $process.Id
        $null=$process.Handle
        $identityBudget=[int][Math]::Min(2000,$TimeoutMilliseconds-$clock.ElapsedMilliseconds)
        if($identityBudget -lt 1){$result.TimedOut=$true;throw 'qa_owned_process_identity_deadline'}
        $result.Identity=Get-Win7PosAcceptanceProcessIdentity $process $FilePath -TimeoutMilliseconds $identityBudget
        # Callback shares this absolute deadline and the existing outer Job.
        # It can wait for nonce-bound readiness and invoke a contender through
        # this same helper, without creating a new supervisor or work root.
        if ($null -ne $OnStarted) {$null=& $OnStarted $process ([Math]::Max(0,$TimeoutMilliseconds-[int]$clock.ElapsedMilliseconds))}
        if ($clock.ElapsedMilliseconds -ge $TimeoutMilliseconds) {$result.TimedOut=$true}
        while (-not $result.TimedOut -and -not $process.WaitForExit(50)) {
            if ($CancellationFile -and (Test-Path -LiteralPath $CancellationFile)) {$result.Cancelled=$true;break}
            if ($clock.ElapsedMilliseconds -ge $TimeoutMilliseconds) {$result.TimedOut=$true;break}
        }
        if ($process.HasExited -and -not $result.TimedOut -and -not $result.Cancelled) {$result.ExitCode=$process.ExitCode}
    }
    catch [OperationCanceledException] {$result.Cancelled=$true;$result.LaunchError='qa_cancelled'}
    catch {
        $result.LaunchError = $_.Exception.GetType().Name
        try {[IO.File]::AppendAllText((Join-Path $result.EvidenceDirectory 'qa-process-error.private.txt'),$_.Exception.ToString()+[Environment]::NewLine+$_.ScriptStackTrace+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))} catch {}
    }
    finally {
        if ($null -ne $process) {
            try {
                if (-not $process.HasExited) {
                    if ($null -ne $result.Identity -and (Test-Win7PosAcceptanceProcessIdentity $process $result.Identity)) {$process.Kill()}
                    # If identity was not yet available, the retained start
                    # handle still identifies exactly our just-launched child.
                    elseif ($result.Started -and $null -eq $result.Identity) {$process.Kill()}
                    else {$result.CleanupSucceeded=$false}
                    if (-not $process.WaitForExit(5000)) {$result.OrphanRemaining=$true;$result.CleanupSucceeded=$false}
                }
                $result.OrphanRemaining=$result.OrphanRemaining -or -not $process.HasExited
            } catch {$result.CleanupSucceeded=$false;$result.OrphanRemaining=$true}
            finally {$process.Dispose()}
        }
    }

    [pscustomobject]$result
}

Export-ModuleMember -Function `
    Enter-Win7PosAcceptanceLock, `
    Exit-Win7PosAcceptanceLock, `
    Get-Win7PosAcceptanceLogicalRunCount, `
    Get-Win7PosAcceptanceResultCode, `
    Test-Win7PosAcceptanceProcessActive, `
    Get-Win7PosAcceptanceProcessIdentity, `
    Test-Win7PosAcceptanceProcessIdentity, `
    Invoke-Win7PosWaitedProcess
