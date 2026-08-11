$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$wpfRoot = Join-Path $repoRoot "src/Win7POS.Wpf"
$failures = New-Object System.Collections.Generic.List[string]

function Add-Failure([string]$message) {
    $failures.Add($message) | Out-Null
}

function Get-RelativePath([string]$path) {
    return $path.Substring($repoRoot.Length + 1)
}

function Test-AllowedXamlLiteral([string]$value) {
    $trimmed = if ($null -eq $value) { "" } else { $value.Trim() }
    if ($trimmed.Length -eq 0) { return $true }
    if ($trimmed.StartsWith("{", [System.StringComparison]::Ordinal)) { return $true }

    if ($trimmed -in @("Win7POS", "POS", "CLP", "VACUUM", "X")) { return $true }
    if ($trimmed -match '^[0-9]+$') { return $true }
    if ($trimmed -match '^\+\s*[0-9]+$') { return $true }
    if ($trimmed -notmatch '\p{L}') { return $true }

    return $false
}

$attributeNames = '(?:Text|Content|Header|ToolTip|Title|AutomationProperties\.Name|AutomationProperties\.HelpText)'
$xamlPattern = '(?<![\w.:])(?<attribute>' + $attributeNames + ')\s*=\s*"(?<value>[^"]*)"'

Get-ChildItem -LiteralPath $wpfRoot -Recurse -Filter "*.xaml" |
    Where-Object { $_.FullName -notmatch '\\(?:bin|obj)\\' } |
    ForEach-Object {
        $file = $_
        $lineNumber = 0
        [System.IO.File]::ReadAllLines($file.FullName, [System.Text.Encoding]::UTF8) | ForEach-Object {
            $lineNumber += 1
            foreach ($match in [regex]::Matches($_, $xamlPattern)) {
                $value = $match.Groups["value"].Value
                if (-not (Test-AllowedXamlLiteral $value)) {
                    Add-Failure ("{0}:{1} {2}='{3}'" -f
                        (Get-RelativePath $file.FullName),
                        $lineNumber,
                        $match.Groups["attribute"].Value,
                        $value)
                }
            }
        }
    }

# ViewModel Status/Summary/Title text is operator-facing. The legacy
# ProductDbImportViewModel has no shipping XAML or call-site and is intentionally
# excluded until that dormant implementation is either removed or made shipping.
$viewModelPattern = '(?<![\w.])(?:Status|StatusMessage|Summary|Title|ToolTip|Text)\s*=\s*"(?<value>[^"]*\p{L}[^"]*)"'
Get-ChildItem -LiteralPath $wpfRoot -Recurse -Filter "*ViewModel.cs" |
    Where-Object {
        $_.FullName -notmatch '\\(?:bin|obj)\\' -and
        $_.Name -ne "ProductDbImportViewModel.cs"
    } |
    ForEach-Object {
        $file = $_
        $lineNumber = 0
        [System.IO.File]::ReadAllLines($file.FullName, [System.Text.Encoding]::UTF8) | ForEach-Object {
            $lineNumber += 1
            foreach ($match in [regex]::Matches($_, $viewModelPattern)) {
                $value = $match.Groups["value"].Value
                if ($value -ne "Win7POS") {
                    Add-Failure ("{0}:{1} operator-facing literal='{2}'" -f
                        (Get-RelativePath $file.FullName),
                        $lineNumber,
                        $value)
                }
            }
        }
    }

$dialogShell = [System.IO.File]::ReadAllText((Join-Path $wpfRoot "Chrome/DialogShellWindow.cs"))
if ($dialogShell -match 'ToolTip\s*=\s*"\p{L}') {
    Add-Failure "DialogShellWindow close tooltip must use localization."
}

$uiErrorHandler = [System.IO.File]::ReadAllText((Join-Path $wpfRoot "Infrastructure/UiErrorHandler.cs"))
if ($uiErrorHandler -match 'Si è verificato|Controlla i log') {
    Add-Failure "UiErrorHandler fallback message must use localization."
}

if ($failures.Count -gt 0) {
    Write-Host "FAIL: operator-facing hardcoded strings found" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host " - $_" }
    exit 1
}

Write-Host "PASS: shipping XAML literals are localized or intentional symbols/brand tokens" -ForegroundColor Green
Write-Host "PASS: shipping ViewModel status/summary assignments use localization" -ForegroundColor Green
Write-Host "PASS: shared dialog/error fallbacks use localization" -ForegroundColor Green
exit 0
