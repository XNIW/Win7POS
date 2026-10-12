[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$source = Join-Path $repoRoot 'scripts/qa/Collect-Win7PosOwnedResources.ps1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Collector syntax errors' }
# Load only identity functions. Never execute the collector, Add-Type, CIM,
# GetProcessById, SQL, native processes or its top-level Windows SID lookup.
$names = @('Path-Absolute','Same-Path','Parse-Time','Cim-Time','Process-Key',
    'Assert-RootIdentity','Record-UnregisteredDeparture','Record-UnregisteredMissingImageDeparture')
foreach ($name in $names) {
    $definitions = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true))
    if ($definitions.Count -ne 1) { throw "Identity function missing or duplicated: $name" }
    . ([scriptblock]::Create($definitions[0].Extent.Text))
}
$script:records = [Collections.Generic.List[object]]::new()
$script:fresh = @(); $script:freshError = $null; $script:ownerCalls = 0
$script:ownerSid = 'test-owner-identity'; $script:unregisteredDepartedCandidates = @{}
$script:reportedOwnerSid = $script:ownerSid
$script:lastMissingImageCandidate = $null
$watch = [Diagnostics.Stopwatch]::StartNew()
function Write-Record($Record) { $script:records.Add($Record) }
function Get-CimInstance { param($ClassName, $Filter, $ErrorAction)
    if ($ClassName -ne 'Win32_Process' -or $Filter -ne 'ProcessId = 42' -or $ErrorAction -ne 'Stop') { throw 'Unexpected mock CIM query' }
    if ($script:freshError) { throw $script:freshError }
    return $script:fresh
}
function Invoke-CimMethod { param($InputObject, $MethodName)
    if ($MethodName -ne 'GetOwnerSid') { throw 'Unexpected owner query' }
    $script:ownerCalls++
    return [pscustomobject]@{ Sid = $script:reportedOwnerSid }
}
function Assert-True([bool]$Condition, [string]$Label) { if (!$Condition) { throw $Label } }
function Must-Fail([scriptblock]$Action, [string]$Pattern) {
    $failure = $null
    try { & $Action } catch { $failure = $_.Exception.Message }
    if (!$failure -or $failure -notmatch $Pattern) { throw "Expected failure $Pattern; got $failure" }
}
function Reset-Departure {
    $script:records.Clear(); $script:unregisteredDepartedCandidates = @{}
    $script:fresh = @(); $script:freshError = $null; $script:ownerCalls = 0
}
function Assert-Unknown($Record, [string]$Operation) {
    Assert-True ($Record.status -ceq 'unregistered_departed_candidate' -and $Record.departureOperation -ceq $Operation) 'Typed departure operation'
    foreach ($key in @('identityFullyVerified','registrationCompleted','acceptedForDescendantLineage','allLifetimeMetricsOrPidsProvenCaptured')) {
        Assert-True ($Record[$key] -is [bool] -and !$Record[$key]) "Departure incorrectly admitted: $key"
    }
    foreach ($key in @('privateBytes','workingSetBytes','privateWorkingSetBytes','handleCount','cpuTotalMs',
        'osLifetimePeakWorkingSetBytes','osLifetimePeakPrivateCommitBytes')) { Assert-True ($null -eq $Record[$key]) "Invented metric: $key" }
    Assert-True ($Record.metricStatus -ceq 'UNKNOWN_NOT_OBSERVED' -and $Record.absentFromFreshCimPidQuery) 'UNKNOWN requires actual successful fresh absence'
}

$path = Join-Path ([IO.Path]::GetTempPath()) 'Owned QA\postgres.exe'
Assert-True (Same-Path $path ($path.ToUpperInvariant() + '\')) 'Normalized exact executable comparison'
Assert-True (!(Same-Path $path ($path + '.other'))) 'Prefix match must not admit another executable'
Must-Fail { Same-Path 'relative.exe' $path } 'Absolute path'
$created = [DateTimeOffset]::Parse('2026-01-01T00:00:01Z').UtcDateTime
$parentCreated = $created.AddSeconds(-1)
$root = [pscustomobject]@{ ProcessId = 42; ParentProcessId = 10; Name = 'postgres.exe'; CreationDate = $created; ExecutablePath = $path }
$expected = [pscustomobject]@{ rootPid = 42; executablePath = $path.ToUpperInvariant(); startedAtUtc = $created.ToString('o') }
$null = Assert-RootIdentity $root $expected 'postgres'
Assert-True ($script:ownerCalls -eq 1) 'Exact root must query owner SID'
$script:reportedOwnerSid = 'other-owner'; Must-Fail { Assert-RootIdentity $root $expected 'postgres' } 'another Windows SID'
$script:reportedOwnerSid = $script:ownerSid
$expected.rootPid = 43; Must-Fail { Assert-RootIdentity $root $expected 'postgres' } 'PID mismatch'; $expected.rootPid = 42
$expected.startedAtUtc = $created.AddSeconds(1).ToString('o'); Must-Fail { Assert-RootIdentity $root $expected 'postgres' } 'creation time mismatch'
$expected.startedAtUtc = $created.ToString('o'); $expected.executablePath = $path + '.other'
Must-Fail { Assert-RootIdentity $root $expected 'postgres' } 'executable mismatch'

$candidate = [pscustomobject]@{ ProcessId = 42; ParentProcessId = 10; Name = 'postgres.exe'; CreationDate = $created; ExecutablePath = $null }
Reset-Departure
Record-UnregisteredMissingImageDeparture $candidate $parentCreated
Record-UnregisteredMissingImageDeparture $candidate $parentCreated
Assert-True ($script:records.Count -eq 1 -and $script:unregisteredDepartedCandidates.Count -eq 1) 'Departure must be deduplicated by PID plus creation'
Assert-Unknown $script:records[0] 'ExecutablePathMissing.FreshPidAbsent'
Assert-True ($null -eq $script:records[0].ownerSid -and !$script:records[0].ownerSidObserved -and !$script:records[0].initialImageObserved -and $script:ownerCalls -eq 0) 'Missing image must never infer or query SID'
Assert-True ($script:records[0].missingImageSnapshot.freshQuerySucceeded -and $script:records[0].missingImageSnapshot.freshCount -eq 0) 'Fresh absence witness'
foreach ($case in @('live','reused','parent','duplicate','queryError','predates')) {
    Reset-Departure
    $live = [pscustomobject]@{ ProcessId = 42; ParentProcessId = 10; Name = 'postgres.exe'; CreationDate = $created; ExecutablePath = $path }
    switch ($case) {
        live { $script:fresh = @($live); $pattern = 'Live descendant image' }
        reused { $live.CreationDate = $created.AddSeconds(2); $script:fresh = @($live); $pattern = 'PID was reused' }
        parent { $live.ParentProcessId = 99; $script:fresh = @($live); $pattern = 'parent identity changed' }
        duplicate { $script:fresh = @($live,$live); $pattern = 'duplicate PID' }
        queryError { $script:freshError = 'synthetic access denied'; $pattern = 'synthetic access denied' }
        predates { $pattern = 'predates admitted parent' }
    }
    $accepted = if ($case -eq 'predates') { $created.AddSeconds(1) } else { $parentCreated }
    Must-Fail { Record-UnregisteredMissingImageDeparture $candidate $accepted } $pattern
    Assert-True ($script:records.Count -eq 0 -and $script:unregisteredDepartedCandidates.Count -eq 0) "False absence admitted: $case"
}
Reset-Departure
$failure = try { throw 'synthetic typed owner NotFound already classified by Register-Process' } catch { $_ }
Record-UnregisteredDeparture $root 'postgres' 'Admitted snapshot' $failure 'GetOwnerSid.NativeErrorCode.NotFound' $null
Assert-Unknown $script:records[0] 'GetOwnerSid.NativeErrorCode.NotFound'
$register = @($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Register-Process'}, $true))[0].Extent.Text
Assert-True ($register.Contains('catch [Microsoft.Management.Infrastructure.CimException]') -and
    $register.Contains('NativeErrorCode -ne [Microsoft.Management.Infrastructure.NativeErrorCode]::NotFound') -and
    $register.Contains('-not $AllowDepartedDescendant')) 'Only typed CIM NotFound can enter owner-departure tolerance'
'PASS collector identity/departure synthetic tests; no native/CIM/process sampling executed'
