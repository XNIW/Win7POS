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

function Invoke-Win7PosWaitedProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList,
        [Parameter(Mandatory = $true)]
        [ValidateRange(100, 3600000)]
        [int]$TimeoutMilliseconds,
        [Parameter(Mandatory = $true)][string]$EvidenceDirectory
    )

    $result = [ordered]@{
        EvidenceDirectory = [System.IO.Path]::GetFullPath($EvidenceDirectory)
        ExitCode = $null
        OrphanRemaining = $false
        ProcessId = $null
        Started = $false
        TimedOut = $false
        LaunchError = $null
    }

    $process = $null
    try {
        $process = Start-Process `
            -FilePath ([System.IO.Path]::GetFullPath($FilePath)) `
            -ArgumentList $ArgumentList `
            -PassThru `
            -WindowStyle Hidden
        $result.Started = $true
        $result.ProcessId = $process.Id

        if (-not $process.WaitForExit($TimeoutMilliseconds)) {
            $result.TimedOut = $true
            try {
                $process.Kill()
            }
            catch [System.InvalidOperationException] {
            }
            if (-not $process.WaitForExit(30000)) {
                $result.OrphanRemaining = $true
            }
        }
        else {
            # The second wait flushes redirected/native completion state for WinExe.
            $process.WaitForExit()
            $result.ExitCode = $process.ExitCode
        }
    }
    catch {
        $result.LaunchError = $_.Exception.GetType().Name
    }
    finally {
        if ($null -ne $process) {
            $process.Dispose()
        }
    }

    if ($null -ne $result.ProcessId) {
        $result.OrphanRemaining = $result.OrphanRemaining -or
            ($null -ne (Get-Process -Id $result.ProcessId -ErrorAction SilentlyContinue))
    }

    [pscustomobject]$result
}

Export-ModuleMember -Function `
    Enter-Win7PosAcceptanceLock, `
    Exit-Win7PosAcceptanceLock, `
    Get-Win7PosAcceptanceResultCode, `
    Test-Win7PosAcceptanceProcessActive, `
    Invoke-Win7PosWaitedProcess
