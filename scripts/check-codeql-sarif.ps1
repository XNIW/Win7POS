[CmdletBinding()]
param(
    [string]$SarifDirectory = "",
    [string]$SummaryPath = ""
)

$ErrorActionPreference = "Stop"

function Get-SarifFindingCount {
    param([Parameter(Mandatory = $true)]$Document)

    if ($Document.version -ne "2.1.0" -or
        $null -eq $Document.PSObject.Properties["runs"] -or
        @($Document.runs).Count -eq 0) {
        throw "CodeQL output is not a valid SARIF 2.1.0 document."
    }

    $findings = New-Object System.Collections.Generic.List[object]
    foreach ($run in @($Document.runs)) {
        $driver = $run.tool.driver
        $driverVersion = if (-not [string]::IsNullOrWhiteSpace([string]$driver.semanticVersion)) {
            [string]$driver.semanticVersion
        }
        else {
            [string]$driver.version
        }
        if ($null -eq $driver -or
            [string]$driver.name -cne "CodeQL" -or
            [string]$driver.organization -cne "GitHub" -or
            [string]::IsNullOrWhiteSpace($driverVersion)) {
            throw "SARIF run is not attributed to the expected GitHub CodeQL toolchain."
        }
        $rulesProperty = $driver.PSObject.Properties["rules"]
        if ($null -eq $rulesProperty -or
            $rulesProperty.Value -isnot [System.Array]) {
            throw "CodeQL SARIF driver must contain an explicit rules array."
        }

        $ruleCount = @($rulesProperty.Value).Count
        $hasCSharpQueryPack = $false
        $extensionsProperty = $run.tool.PSObject.Properties["extensions"]
        if ($null -ne $extensionsProperty) {
            if ($extensionsProperty.Value -isnot [System.Array]) {
                throw "CodeQL SARIF tool extensions must be an array when present."
            }
            foreach ($extension in @($extensionsProperty.Value)) {
                if ($null -eq $extension) { continue }
                $extensionRules = $extension.PSObject.Properties["rules"]
                if ($null -ne $extensionRules) {
                    if ($extensionRules.Value -isnot [System.Array]) {
                        throw "CodeQL SARIF extension rules must be arrays."
                    }
                    $ruleCount += @($extensionRules.Value).Count
                }
                $extensionVersion = if (-not [string]::IsNullOrWhiteSpace([string]$extension.semanticVersion)) {
                    [string]$extension.semanticVersion
                }
                else {
                    [string]$extension.version
                }
                if ([string]$extension.name -ceq "codeql/csharp-queries" -and
                    -not [string]::IsNullOrWhiteSpace($extensionVersion) -and
                    $null -ne $extensionRules -and
                    @($extensionRules.Value).Count -gt 0) {
                    $hasCSharpQueryPack = $true
                }
            }
        }
        if ($ruleCount -eq 0 -or
            (@($rulesProperty.Value).Count -eq 0 -and -not $hasCSharpQueryPack)) {
            throw "CodeQL SARIF must contain a non-empty driver or official C# query-pack rule inventory."
        }

        $resultsProperty = $run.PSObject.Properties["results"]
        if ($null -eq $resultsProperty -or $resultsProperty.Value -isnot [System.Array]) {
            throw "CodeQL SARIF run must contain an explicit results array."
        }

        $invocationsProperty = $run.PSObject.Properties["invocations"]
        if ($null -ne $invocationsProperty) {
            if ($invocationsProperty.Value -isnot [System.Array]) {
                throw "CodeQL SARIF invocations must be an array when present."
            }
            foreach ($invocation in @($invocationsProperty.Value)) {
                $executionSuccessfulProperty = if ($null -ne $invocation) {
                    $invocation.PSObject.Properties["executionSuccessful"]
                }
                else { $null }
                if ($null -eq $executionSuccessfulProperty -or
                    $executionSuccessfulProperty.Value -isnot [bool] -or
                    $executionSuccessfulProperty.Value -ne $true) {
                    throw "CodeQL SARIF records an unsuccessful or incomplete invocation."
                }
                $errorNotifications = @(
                    @($invocation.toolExecutionNotifications) +
                    @($invocation.toolConfigurationNotifications) |
                        Where-Object { [string]$_.level -ceq "error" }
                )
                if ($errorNotifications.Count -gt 0) {
                    throw "CodeQL SARIF invocation contains an error notification."
                }
            }
        }

        foreach ($result in @($run.results)) {
            if ($null -eq $result) { continue }
            $findings.Add($result) | Out-Null
        }
    }
    return $findings
}

$emptyVector = [pscustomobject]@{
    version = "2.1.0"
    runs = @([pscustomobject]@{
        tool = [pscustomobject]@{ driver = [pscustomobject]@{
            name = "CodeQL"
            organization = "GitHub"
            semanticVersion = "2.0.0"
            rules = @()
        }; extensions = @([pscustomobject]@{
            name = "codeql/csharp-queries"
            semanticVersion = "1.0.0"
            rules = @([pscustomobject]@{ id = "cs/win7pos-negative-vector" })
        }) }
        results = @()
    })
}
$findingVector = [pscustomobject]@{
    version = "2.1.0"
    runs = @([pscustomobject]@{
        tool = [pscustomobject]@{ driver = [pscustomobject]@{
            name = "CodeQL"
            organization = "GitHub"
            semanticVersion = "2.0.0"
            rules = @()
        }; extensions = @([pscustomobject]@{
            name = "codeql/csharp-queries"
            semanticVersion = "1.0.0"
            rules = @([pscustomobject]@{ id = "cs/win7pos-negative-vector" })
        }) }
        results = @([pscustomobject]@{
            ruleId = "win7pos/negative-vector"
            level = "error"
            message = [pscustomobject]@{ text = "Synthetic finding" }
        })
    })
}
if ((Get-SarifFindingCount -Document $emptyVector).Count -ne 0 -or
    (Get-SarifFindingCount -Document $findingVector).Count -ne 1) {
    throw "CodeQL SARIF gate self-test failed."
}

function Assert-RejectedSarifVector {
    param(
        [Parameter(Mandatory = $true)]$Document,
        [Parameter(Mandatory = $true)][string]$Label
    )

    try {
        Get-SarifFindingCount -Document $Document | Out-Null
    }
    catch {
        return
    }
    throw "CodeQL SARIF gate self-test accepted $Label."
}

$unrelatedToolVector = [pscustomobject]@{
    version = "2.1.0"
    runs = @([pscustomobject]@{
        tool = [pscustomobject]@{ driver = [pscustomobject]@{
            name = "Unrelated analyzer"
            organization = "Example"
            semanticVersion = "1.0.0"
            rules = @([pscustomobject]@{ id = "example/rule" })
        } }
        results = @()
    })
}
$failedInvocationVector = [pscustomobject]@{
    version = "2.1.0"
    runs = @([pscustomobject]@{
        tool = $emptyVector.runs[0].tool
        invocations = @([pscustomobject]@{ executionSuccessful = $false })
        results = @()
    })
}
$errorNotificationVector = [pscustomobject]@{
    version = "2.1.0"
    runs = @([pscustomobject]@{
        tool = $emptyVector.runs[0].tool
        invocations = @([pscustomobject]@{
            executionSuccessful = $true
            toolExecutionNotifications = @([pscustomobject]@{
                level = "error"
                message = [pscustomobject]@{ text = "Synthetic analysis error" }
            })
        })
        results = @()
    })
}
$missingRulesVector = [pscustomobject]@{
    version = "2.1.0"
    runs = @([pscustomobject]@{
        tool = [pscustomobject]@{ driver = [pscustomobject]@{
            name = "CodeQL"
            organization = "GitHub"
            semanticVersion = "2.0.0"
        } }
        results = @()
    })
}
$emptyRulesVector = $missingRulesVector | ConvertTo-Json -Depth 10 | ConvertFrom-Json
$emptyRulesVector.runs[0].tool.driver | Add-Member -NotePropertyName rules -NotePropertyValue @()
$unrelatedExtensionVector = $emptyRulesVector | ConvertTo-Json -Depth 10 | ConvertFrom-Json
$unrelatedExtensionVector.runs[0].tool | Add-Member -NotePropertyName extensions -NotePropertyValue @([pscustomobject]@{
    name = "example/csharp-queries"
    semanticVersion = "1.0.0"
    rules = @([pscustomobject]@{ id = "example/rule" })
})
Assert-RejectedSarifVector -Document ([pscustomobject]@{ version = "2.1.0"; runs = @() }) -Label "an empty run set"
Assert-RejectedSarifVector -Document $unrelatedToolVector -Label "an unrelated analyzer"
Assert-RejectedSarifVector -Document $failedInvocationVector -Label "an unsuccessful invocation"
Assert-RejectedSarifVector -Document $errorNotificationVector -Label "an invocation error notification"
Assert-RejectedSarifVector -Document $missingRulesVector -Label "a missing CodeQL rules inventory"
Assert-RejectedSarifVector -Document $emptyRulesVector -Label "an empty CodeQL rules inventory"
Assert-RejectedSarifVector -Document $unrelatedExtensionVector -Label "an unrelated extension posing as a C# query pack"

if ([string]::IsNullOrWhiteSpace($SarifDirectory)) {
    throw "-SarifDirectory is required."
}
if (-not (Test-Path -LiteralPath $SarifDirectory -PathType Container)) {
    throw "CodeQL SARIF directory is missing: $SarifDirectory"
}
$resolvedDirectory = (Resolve-Path -LiteralPath $SarifDirectory).Path
$sarifFiles = @(Get-ChildItem -LiteralPath $resolvedDirectory -Recurse -File -Filter "*.sarif" |
    Sort-Object FullName)
if ($sarifFiles.Count -eq 0) {
    throw "CodeQL produced no SARIF files in $resolvedDirectory."
}

$allFindings = New-Object System.Collections.Generic.List[object]
foreach ($sarifFile in $sarifFiles) {
    try {
        $document = [System.IO.File]::ReadAllText($sarifFile.FullName) | ConvertFrom-Json -Depth 100
        foreach ($finding in @(Get-SarifFindingCount -Document $document)) {
            $allFindings.Add([pscustomobject]@{
                File = $sarifFile.Name
                RuleId = ($finding.ruleId -as [string])
                Level = ($finding.level -as [string])
                Message = ($finding.message.text -as [string])
            }) | Out-Null
        }
    }
    catch {
        throw "CodeQL SARIF parse failed for '$($sarifFile.Name)': $($_.Exception.Message)"
    }
}

$summary = [pscustomobject][ordered]@{
    Schema = "win7pos-codeql-summary-v1"
    SarifFiles = $sarifFiles.Count
    Findings = $allFindings.Count
    Status = if ($allFindings.Count -eq 0) { "PASS" } else { "FAIL" }
}
if (-not [string]::IsNullOrWhiteSpace($SummaryPath)) {
    $summaryParent = Split-Path -Parent $SummaryPath
    if (-not [string]::IsNullOrWhiteSpace($summaryParent)) {
        New-Item -ItemType Directory -Force -Path $summaryParent | Out-Null
    }
    [System.IO.File]::WriteAllText(
        $SummaryPath,
        (($summary | ConvertTo-Json -Depth 5) + [Environment]::NewLine),
        (New-Object System.Text.UTF8Encoding($false)))
}

if ($allFindings.Count -gt 0) {
    $first = $allFindings[0]
    $message = if ([string]::IsNullOrWhiteSpace($first.Message)) { "No message" } else { $first.Message }
    if ($message.Length -gt 240) { $message = $message.Substring(0, 240) }
    throw "CodeQL found $($allFindings.Count) result(s). First: $($first.RuleId) [$($first.Level)] $message"
}

Write-Host "PASS: CodeQL SARIF contains zero findings across $($sarifFiles.Count) file(s)."
