#requires -Version 7.4
[CmdletBinding()]
param([string]$RunnerPath = (Join-Path $PSScriptRoot '../../scripts/check-required-gates.ps1'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($RunnerPath)
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Required gate runner syntax errors' }
$definition = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-RequiredGate'
}, $true))
if ($definition.Count -ne 1) { throw 'Required gate function not found' }
. ([scriptblock]::Create($definition[0].Extent.Text))

# Exercise only the result/output handling, without launching any real gate.
$pwsh = 'Invoke-SyntheticGate'
function Join-Path { param([string]$Path, [string]$ChildPath) $source }
function Invoke-SyntheticGate {
    $global:LASTEXITCODE = $script:fixtureExitCode
    foreach ($line in $script:fixtureOutput) { $line }
}
function Assert-GateOutput([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
$marker = 'synthetic exception detail ' + ('x' * 300) + ' END_OF_DETAIL'
foreach ($case in @(
    @{ name='nonzero'; exitCode=7; output=@($marker, '# Subtest: rejects missing table', 'ok 68 - rejects missing table'); expected='FAIL' },
    @{ name='reported-failure'; exitCode=0; output=@('FAIL synthetic assertion', $marker); expected='FAIL' },
    @{ name='success'; exitCode=0; output=@('# Subtest: rejects missing table', 'ok 68 - rejects missing table'); expected='PASS' }
)) {
    $results = [Collections.Generic.List[object]]::new()
    $script:fixtureExitCode = $case.exitCode
    $script:fixtureOutput = $case.output
    $transcript = @(Invoke-RequiredGate -Gate 'synthetic.ps1' -Label $case.name 6>&1 | ForEach-Object { $_.ToString() })
    Assert-GateOutput ($results.Count -eq 1 -and $results[0].Result -ceq $case.expected) ('Gate classification changed: ' + $case.name)
    if ($case.expected -eq 'FAIL') {
        Assert-GateOutput ($transcript -ccontains ('=== FAILED GATE: ' + $case.name + ' (exit ' + $case.exitCode + ') ===')) 'Failure transcript lost gate or exit code'
        foreach ($line in $case.output) {
            Assert-GateOutput ($transcript -ccontains $line) 'Failure transcript lost or truncated a child output line'
        }
        Assert-GateOutput ($transcript[-1] -ceq ('=== END FAILED GATE: ' + $case.name + ' ===')) 'Failure transcript is incomplete'
    } else {
        Assert-GateOutput ($transcript.Count -eq 0) 'Passing gate emitted a failure transcript'
    }
}
Write-Output 'REQUIRED_GATE_FAILURE_OUTPUT=PASS'
