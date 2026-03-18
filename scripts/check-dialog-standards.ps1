$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$base = Join-Path $repoRoot "src/Win7POS.Wpf"
$fail = $false
$generatedPathPattern = '[\\/](obj|bin)[\\/]'

function Write-Section([string]$label) {
    Write-Host "`n=== $label ===" -ForegroundColor Cyan
}

function Fail([string]$message) {
    Write-Host "FAIL: $message" -ForegroundColor Red
    $script:fail = $true
}

function Pass([string]$message = "OK") {
    Write-Host $message -ForegroundColor Green
}

function Get-ProjectFiles([string]$filter) {
    Get-ChildItem -Path $base -Recurse -File -Filter $filter |
        Where-Object { $_.FullName -notmatch $generatedPathPattern }
}

function Check-Absent([string]$label, $matches) {
    Write-Section $label
    if ($matches) {
        $matches | ForEach-Object {
            Write-Host ("  {0}:{1}: {2}" -f $_.Path, $_.LineNumber, $_.Line.Trim())
        }
        $script:fail = $true
    }
    else {
        Pass
    }
}

function Check-Present([string]$label, $matches) {
    Write-Section $label
    if ($matches) {
        Pass
    }
    else {
        Fail "atteso presente, non trovato"
    }
}

$allCs = Get-ProjectFiles "*.cs"
$allXaml = Get-ProjectFiles "*.xaml"
$dialogXaml = $allXaml | Where-Object {
    Select-String -Path $_.FullName -Pattern '^\s*<chrome:DialogShellWindow\b' -Quiet
}

$windowSizingHelperPath = Join-Path $base "Infrastructure/WindowSizingHelper.cs"
$dialogShellWindowPath = Join-Path $base "Chrome/DialogShellWindow.cs"
$monitorHelperPath = Join-Path $base "Infrastructure/MonitorHelper.cs"

$positionAssignmentPattern = '\b(Left|Top)\s*=|SetCurrentValue\s*\(\s*Window\.(Left|Top)Property\b'

Check-Absent "1a. WindowSizingHelper no Left/Top" (
    Select-String -Path $windowSizingHelperPath -Pattern $positionAssignmentPattern -CaseSensitive
)

$singleDialogPositionHits = $allCs |
    Where-Object { $_.Name -notmatch 'DialogShellWindow\.cs|MonitorHelper\.cs' } |
    Select-String -Pattern $positionAssignmentPattern -CaseSensitive |
    Where-Object {
        $_.Line -notmatch '//|ActualWidth|ActualHeight|work\.|windowPos\.|RECT|rcWork|rcMonitor'
    }
Check-Absent "1b. Dialog singoli no Left/Top manuali" $singleDialogPositionHits

Check-Absent "2. No CenterToOwner residui" (
    $allCs | Select-String -Pattern 'CenterToOwner|CenterWindow|PositionWindow'
)

Check-Absent "3. No Loaded hook in DialogShellWindow" (
    Select-String -Path $dialogShellWindowPath -Pattern 'Loaded \+='
)

Check-Absent "4. No Loaded hook in WindowSizingHelper" (
    Select-String -Path $windowSizingHelperPath -Pattern 'Loaded \+='
)

Check-Absent "5. No Dispatcher BeginInvoke overlay recenter" (
    Select-String -Path $dialogShellWindowPath -Pattern 'BeginInvoke'
)

Write-Section "6. CenterOwner coverage"
$missingCenterOwner = @()
foreach ($file in $dialogXaml) {
    if (-not (Select-String -Path $file.FullName -Pattern 'WindowStartupLocation="CenterOwner"' -Quiet)) {
        $missingCenterOwner += $file.FullName
    }
}
if ($missingCenterOwner.Count -gt 0) {
    $missingCenterOwner | ForEach-Object { Fail "missing CenterOwner in $_" }
}
else {
    Pass ("OK: {0}/{0}" -f $dialogXaml.Count)
}

$ownerFiles = @(
    (Join-Path $base "Pos/PosViewModel.cs"),
    (Join-Path $base "Products/ProductsViewModel.cs"),
    (Join-Path $base "Pos/Dialogs/UserManagementViewModel.cs"),
    (Join-Path $base "Infrastructure/Security/OverrideAuthService.cs"),
    (Join-Path $base "App.xaml.cs"),
    (Join-Path $base "Products/ProductEditDialog.xaml.cs")
)
Check-Absent "7. No MainWindow fallback residui nei file target" (
    $ownerFiles | ForEach-Object {
        Select-String -Path $_ -Pattern 'Application\.Current\?\.MainWindow|System\.Windows\.Application\.Current\?\.MainWindow'
    }
)

Write-Section "8. Product owner fallback"
$productFallbackChecks = @(
    @{ Path = (Join-Path $base "Products/ProductPriceHistoryDialog.xaml.cs"); Pattern = 'Owner\s*=\s*owner\s*\?\?\s*DialogOwnerHelper\.GetSafeOwner\(\)' },
    @{ Path = (Join-Path $base "Products/DeleteProductConfirmDialog.xaml.cs"); Pattern = 'Owner\s*=\s*owner\s*\?\?\s*DialogOwnerHelper\.GetSafeOwner\(\)' },
    @{ Path = (Join-Path $base "Products/ExportDataDialog.xaml.cs"); Pattern = 'Owner\s*=\s*owner\s*\?\?\s*DialogOwnerHelper\.GetSafeOwner\(\)' }
)
foreach ($check in $productFallbackChecks) {
    if (Select-String -Path $check.Path -Pattern $check.Pattern -Quiet) {
        Pass ("OK: {0}" -f $check.Path)
    }
    else {
        Fail ("missing owner fallback in {0}" -f $check.Path)
    }
}

Write-Section "9. Shared dialog helper owners"
$sharedHelperChecks = @(
    @{ Path = (Join-Path $base "Import/ApplyConfirmDialog.xaml.cs"); Pattern = 'Owner\s*=\s*DialogOwnerHelper\.GetSafeOwner\(owner\)' },
    @{ Path = (Join-Path $base "Import/ModernMessageDialog.xaml.cs"); Pattern = 'Owner\s*=\s*DialogOwnerHelper\.GetSafeOwner\(owner\)' },
    @{ Path = (Join-Path $base "Import/ImportDataDialog.xaml.cs"); Pattern = 'Owner\s*=\s*DialogOwnerHelper\.GetSafeOwner\(owner\)' }
)
foreach ($check in $sharedHelperChecks) {
    if (Select-String -Path $check.Path -Pattern $check.Pattern -Quiet) {
        Pass ("OK: {0}" -f $check.Path)
    }
    else {
        Fail ("missing shared helper owner normalization in {0}" -f $check.Path)
    }
}

Write-Section "10. UserManagement nested owner"
if (-not (Select-String -Path (Join-Path $base "Pos/Dialogs/UserManagementViewModel.cs") -Pattern 'internal\s+Window\s+OwnerWindow\s*\{\s*get;\s*set;\s*\}' -Quiet)) {
    Fail "OwnerWindow property missing in UserManagementViewModel"
}
elseif (-not (Select-String -Path (Join-Path $base "Pos/Dialogs/UserManagementDialog.xaml.cs") -Pattern 'vm\.OwnerWindow\s*=\s*this' -Quiet)) {
    Fail "vm.OwnerWindow = this missing in UserManagementDialog"
}
else {
    Pass
}

Check-Present "11. Clamp hook in DialogShellWindow" (
    Select-String -Path $dialogShellWindowPath -Pattern 'AddWorkAreaClampHook'
)

Check-Absent "12. Clamp hook NOT in WindowSizingHelper" (
    Select-String -Path $windowSizingHelperPath -Pattern 'AddWorkAreaClampHook'
)

Write-Section "13. Base-type consistency"
$mismatches = @()
foreach ($file in $dialogXaml) {
    $codeBehind = [System.IO.Path]::ChangeExtension($file.FullName, ".xaml.cs")
    if (-not (Test-Path $codeBehind)) {
        $mismatches += "$($file.FullName) -> missing code-behind"
        continue
    }

    if (-not (Select-String -Path $codeBehind -Pattern ':\s*DialogShellWindow' -Quiet)) {
        $mismatches += "$($file.FullName) -> code-behind does not inherit DialogShellWindow"
    }
}
if ($mismatches.Count -gt 0) {
    $mismatches | ForEach-Object { Fail $_ }
}
else {
    Pass
}

Check-Present "14. MonitorHelper idempotency guard" (
    Select-String -Path $monitorHelperPath -Pattern 'ConditionalWeakTable'
)

Check-Absent "15. ProductEdit no Loaded hook" (
    Select-String -Path (Join-Path $base "Products/ProductEditDialog.xaml") -Pattern 'Loaded="Window_Loaded"'
)

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
