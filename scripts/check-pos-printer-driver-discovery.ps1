$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$fail = $false

function Fail([string]$message) {
    Write-Host "FAIL: $message" -ForegroundColor Red
    $script:fail = $true
}

function Pass([string]$message) {
    Write-Host "PASS: $message" -ForegroundColor Green
}

function Read-Required([string]$relativePath) {
    $path = Join-Path $repoRoot $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Fail "$relativePath missing"
        return ""
    }

    return [System.IO.File]::ReadAllText($path)
}

function Require-Pattern([string]$label, [string]$text, [string]$pattern) {
    if ($text -match $pattern) { Pass $label } else { Fail $label }
}

function Forbid-Pattern([string]$label, [string]$text, [string]$pattern) {
    if ($text -match $pattern) { Fail $label } else { Pass $label }
}

function Get-MethodBody([string]$text, [string]$signaturePattern) {
    $signature = [regex]::Match($text, $signaturePattern)
    if (-not $signature.Success) {
        return ""
    }

    $bodyStart = $text.IndexOf("{", $signature.Index + $signature.Length, [StringComparison]::Ordinal)
    if ($bodyStart -lt 0) {
        return ""
    }

    $depth = 0
    for ($i = $bodyStart; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq "{") {
            $depth++
        }
        elseif ($text[$i] -eq "}") {
            $depth--
            if ($depth -eq 0) {
                return $text.Substring($signature.Index, ($i - $signature.Index) + 1)
            }
        }
    }

    return ""
}

$model = Read-Required "src/Win7POS.Wpf/Printing/InstalledPrinterInfo.cs"
$discovery = Read-Required "src/Win7POS.Wpf/Printing/WindowsPrinterDiscovery.cs"
$inventory = Read-Required "src/Win7POS.Wpf/Printing/WindowsSpoolerPrinterInventory.cs"
$workflow = Read-Required "src/Win7POS.Wpf/Pos/PosWorkflowService.cs"
$viewModel = Read-Required "src/Win7POS.Wpf/Pos/Dialogs/PrinterSettingsViewModel.cs"
$dialog = Read-Required "src/Win7POS.Wpf/Pos/Dialogs/PrinterSettingsDialog.xaml.cs"
$dialogXaml = Read-Required "src/Win7POS.Wpf/Pos/Dialogs/PrinterSettingsDialog.xaml"
$posViewModel = Read-Required "src/Win7POS.Wpf/Pos/PosViewModel.cs"
$uiSmoke = Read-Required "tests/Win7POS.Wpf.UiSmokeHarness/Program.cs"
$wpfProject = Read-Required "src/Win7POS.Wpf/Win7POS.Wpf.csproj"

if ($fail) {
    exit 1
}

$requiredProperties = @(
    @{ Name = "Name"; Type = "string" },
    @{ Name = "DriverName"; Type = "string" },
    @{ Name = "PortName"; Type = "string" },
    @{ Name = "IsDefault"; Type = "bool" },
    @{ Name = "IsAvailable"; Type = "bool" },
    @{ Name = "IsOffline"; Type = "bool" },
    @{ Name = "IsPaused"; Type = "bool" },
    @{ Name = "IsVirtual"; Type = "bool" },
    @{ Name = "StatusText"; Type = "string" },
    @{ Name = "Notes"; Type = "string" }
)

$missingProperties = New-Object System.Collections.Generic.List[string]
foreach ($property in $requiredProperties) {
    $pattern = 'public\s+' + $property.Type + '\s+' + $property.Name + '\s*\{\s*get\s*;\s*set\s*;\s*\}'
    if ($model -notmatch $pattern) {
        $missingProperties.Add($property.Name)
    }
}
if ($missingProperties.Count -eq 0) {
    Pass "installed-printer model exposes queue, driver, port and state fields"
}
else {
    Fail "installed-printer model missing fields: $($missingProperties -join ', ')"
}

$nativeContract = @(
    "EnumPrintersW",
    "OpenPrinterW",
    "GetPrinterW",
    "GetDefaultPrinterW",
    "ClosePrinter",
    "PrinterInfo4",
    "PrinterInfo2",
    "PrinterEnumLocal",
    "PrinterEnumConnections",
    "PrinterStatusOffline",
    "PrinterStatusPaused",
    "CharSet.Unicode",
    "SetLastError = true"
)
$missingNative = @($nativeContract | Where-Object {
    $inventory.IndexOf($_, [StringComparison]::Ordinal) -lt 0
})
if ($missingNative.Count -eq 0 -and $inventory -match 'DllImport\s*\(\s*"winspool\.drv"') {
    Pass "Win7 spooler inventory uses Unicode winspool level 4/2 APIs"
}
else {
    Fail "Win7 spooler contract incomplete: $($missingNative -join ', ')"
}

Require-Pattern "native inventory reads real driver and port fields" $inventory 'struct\s+(PrinterInfo2|PRINTER_INFO_2)[\s\S]{0,1800}(p?DriverName)[\s\S]*(p?PortName)|struct\s+(PrinterInfo2|PRINTER_INFO_2)[\s\S]{0,1800}(p?PortName)[\s\S]*(p?DriverName)'
Require-Pattern "offline state is derived from a native spooler bit" $discovery 'isOffline\s*=[\s\S]{0,360}(PrinterStatusOffline|PRINTER_STATUS_OFFLINE)'
Require-Pattern "paused state is derived from a native spooler bit" $discovery 'isPaused\s*=[\s\S]{0,260}(PrinterStatusPaused|PRINTER_STATUS_PAUSED)'
Require-Pattern "native buffers are released in finally" $inventory 'finally[\s\S]{0,500}FreeHGlobal'
Require-Pattern "native printer handles are closed in finally" $inventory 'finally[\s\S]{0,300}(ClosePrinter|SafeHandle)'
Require-Pattern "WORK_OFFLINE attribute uses the Win7 spooler value" $inventory '(PrinterAttributeWorkOffline|PRINTER_ATTRIBUTE_WORK_OFFLINE)\s*=\s*0x0*400\s*;'
Require-Pattern "FAX attribute uses the Win7 spooler value" $inventory '(PrinterAttributeFax|PRINTER_ATTRIBUTE_FAX)\s*=\s*0x0*4000\s*;'
Require-Pattern "SERVER_OFFLINE status uses the Win7 spooler value" $inventory '(PrinterStatusServerOffline|PRINTER_STATUS_SERVER_OFFLINE)\s*=\s*0x0*2000000\s*;'
$hasNativeDevModeField = $inventory -match 'struct\s+(PrinterInfo2|PRINTER_INFO_2)[\s\S]{0,1800}(DevMode|pDevMode)'
$hasDevModeProjection = $inventory -match 'HasDevMode\s*=\s*[^;\r\n]*(DevMode|pDevMode)\s*!=\s*IntPtr\.Zero'
$hasDevModeRecord = $inventory -match 'public\s+bool\s+HasDevMode\s*\{\s*get\s*;\s*set\s*;\s*\}'
if ($hasNativeDevModeField -and $hasDevModeProjection -and $hasDevModeRecord) {
    Pass "PRINTER_INFO_2 devmode is projected as HasDevMode"
}
else {
    Fail "PRINTER_INFO_2 devmode must be projected as HasDevMode"
}

Require-Pattern "managed discovery merges Win7 spooler inventory" $discovery 'WindowsSpoolerPrinterInventory'
Require-Pattern "managed installed-printer enumeration remains as fallback" $discovery 'PrinterSettings\.InstalledPrinters'
Require-Pattern "actual driver mapping reaches InstalledPrinterInfo" $discovery 'CreateFromSpooler[\s\S]{0,2400}DriverName\s*=\s*printer\.DriverName'
Require-Pattern "actual port mapping reaches InstalledPrinterInfo" $discovery 'CreateFromSpooler[\s\S]{0,2400}PortName\s*=\s*printer\.PortName'
Require-Pattern "offline and paused mapping reaches InstalledPrinterInfo" $discovery 'IsOffline\s*=\s*[^,;\r\n]*IsOffline[\s\S]*IsPaused\s*=\s*[^,;\r\n]*IsPaused|IsPaused\s*=\s*[^,;\r\n]*IsPaused[\s\S]*IsOffline\s*=\s*[^,;\r\n]*IsOffline'
Require-Pattern "managed fallback labels missing native metadata explicitly" $discovery 'CreateFromManagedPrinter[\s\S]{0,1600}DriverName\s*=\s*string\.Empty[\s\S]*PortName\s*=\s*string\.Empty[\s\S]*metadata unavailable'
Require-Pattern "virtual classification considers driver and port metadata" $discovery 'IsLikelyVirtualPrinter\s*\(\s*printer\.Name\s*,\s*printer\.DriverName\s*,\s*printer\.PortName\s*(,|\))'

$spoolerMappingBody = Get-MethodBody $discovery 'private\s+static\s+InstalledPrinterInfo\s+CreateFromSpooler\s*\('
if ([string]::IsNullOrWhiteSpace($spoolerMappingBody)) {
    Fail "CreateFromSpooler body missing"
}
else {
    Require-Pattern "WORK_OFFLINE contributes to offline state" $spoolerMappingBody '(PrinterAttributeWorkOffline|PRINTER_ATTRIBUTE_WORK_OFFLINE)'
    Require-Pattern "SERVER_OFFLINE contributes to offline state" $spoolerMappingBody '(PrinterStatusServerOffline|PRINTER_STATUS_SERVER_OFFLINE)'
    Require-Pattern "FAX attribute contributes to virtual classification" ($spoolerMappingBody + "`n" + $discovery) '(PrinterAttributeFax|PRINTER_ATTRIBUTE_FAX)'
    Require-Pattern "availability requires native details" $spoolerMappingBody 'hasUsableMetadata\s*=\s*printer\.DetailsAvailable|isAvailable\s*=\s*printer\.DetailsAvailable'
    Require-Pattern "availability requires driver metadata" $spoolerMappingBody '!string\.IsNullOrWhiteSpace\s*\(\s*printer\.DriverName\s*\)'
    Require-Pattern "availability requires port metadata" $spoolerMappingBody '!string\.IsNullOrWhiteSpace\s*\(\s*printer\.PortName\s*\)'
    Require-Pattern "availability requires a queue devmode" $spoolerMappingBody 'printer\.HasDevMode'
    Require-Pattern "availability excludes offline queues" $spoolerMappingBody 'isAvailable[\s\S]{0,500}!isOffline'

    $pausedExcludedDirectly = $spoolerMappingBody -match 'isAvailable[\s\S]{0,500}!isPaused'
    $pausedExcludedByMask = $discovery -match 'BlockingStatusMask[\s\S]{0,500}(PrinterStatusPaused|PRINTER_STATUS_PAUSED)' -and
                            $spoolerMappingBody -match 'isAvailable[\s\S]{0,500}BlockingStatusMask\)\s*==\s*0'
    if ($pausedExcludedDirectly -or $pausedExcludedByMask) {
        Pass "availability excludes paused queues"
    }
    else {
        Fail "availability must exclude paused queues"
    }
}

$bestEffortText = $inventory + "`n" + $discovery
Require-Pattern "native discovery is best-effort per failed spooler query" $bestEffortText 'Try(Get|Read|Enumerate|Open)[A-Za-z]*Printer|catch\s*(\([^\)]*\))?\s*\{'
Require-Pattern "default-printer lookup retains a managed fallback" $discovery 'GetDefaultPrinterName\([\s\S]*WindowsSpoolerPrinterInventory[\s\S]*PrintDocument'
Require-Pattern "printer discovery executes outside the UI thread" $workflow 'Task\.Run<[^>]*InstalledPrinterInfo[^>]*>|Task\.Run\s*\('
Require-Pattern "printer discovery has a bounded timeout" $workflow 'Task\.WhenAny\s*\([\s\S]{0,300}Task\.Delay\s*\(\s*PrinterDiscoveryTimeoutMilliseconds\s*\)'
Require-Pattern "printer discovery returns cached inventory on failure or timeout" $workflow '_lastPrinterDiscovery[\s\S]*Printer discovery failed[\s\S]*_lastPrinterDiscovery[\s\S]*Printer discovery timed out[\s\S]*_lastPrinterDiscovery'
Forbid-Pattern "operational printer resolution never bypasses bounded discovery" $workflow 'WindowsPrinterDiscovery\.(FindPrinter|GetDefaultPrinterName)\s*\('

$operationalDiscoveryMethods = @(
    @{ Label = "test receipt"; Signature = 'public\s+async\s+Task\s+TestReceiptPrinterAsync\s*\(' },
    @{ Label = "receipt print"; Signature = 'private\s+async\s+Task<PosPrintResult>\s+PrintReceiptTextNoLockAsync\s*\(' },
    @{ Label = "cash drawer open"; Signature = 'public\s+async\s+Task\s+OpenCashDrawerAsync\s*\(' },
    @{ Label = "cash drawer test"; Signature = 'public\s+async\s+Task\s+TestCashDrawerAsync\s*\(' }
)
foreach ($method in $operationalDiscoveryMethods) {
    $methodBody = Get-MethodBody $workflow $method.Signature
    if (-not [string]::IsNullOrWhiteSpace($methodBody) -and
        $methodBody -match 'await\s+GetInstalledPrintersAsync\s*\(\s*\)') {
        Pass "$($method.Label) resolves from bounded/cached inventory"
    }
    else {
        Fail "$($method.Label) must resolve from bounded/cached inventory"
    }
}

$openDrawerBody = Get-MethodBody $workflow 'public\s+async\s+Task\s+OpenCashDrawerAsync\s*\('
$openDrawerDiscoveryIndex = $openDrawerBody.IndexOf("GetInstalledPrintersAsync", [StringComparison]::Ordinal)
$openDrawerGateIndex = $openDrawerBody.IndexOf("_gate.WaitAsync", [StringComparison]::Ordinal)
if ($openDrawerDiscoveryIndex -ge 0 -and
    $openDrawerGateIndex -gt $openDrawerDiscoveryIndex) {
    Pass "cash drawer discovery completes before taking the POS operation gate"
}
else {
    Fail "cash drawer discovery must complete before taking the POS operation gate"
}

$timeoutMatch = [regex]::Match($workflow, 'PrinterDiscoveryTimeoutMilliseconds\s*=\s*(\d+)')
if (-not $timeoutMatch.Success) {
    Fail "printer discovery timeout constant missing"
}
else {
    $timeoutMilliseconds = [int]$timeoutMatch.Groups[1].Value
    if ($timeoutMilliseconds -ge 500 -and $timeoutMilliseconds -le 10000) {
        Pass "printer discovery timeout is bounded ($timeoutMilliseconds ms)"
    }
    else {
        Fail "printer discovery timeout must be between 500 and 10000 ms"
    }
}

$forbiddenPlatformApis = '(?i)System\.Management|ManagementObject(Searcher)?|Win32_Printer|System\.Printing|LocalPrintServer|PrintQueue|Windows\.(Devices|Graphics\.Printing|UI|Foundation|ApplicationModel)|PrintManager'
Forbid-Pattern "printer discovery has no WMI, System.Printing, or WinRT dependency" ($inventory + "`n" + $discovery + "`n" + $workflow) $forbiddenPlatformApis
Forbid-Pattern "WPF project has no WMI, System.Printing, or Windows SDK reference" $wpfProject '(?i)System\.Management|System\.Printing|Microsoft\.Windows\.SDK\.Contracts'

$allowedWpfPackages = @(
    "PDFsharp-gdi",
    "ZXing.Net.Bindings.Windows.Compatibility"
)
$packageMatches = [regex]::Matches($wpfProject, '<PackageReference\s+Include="([^"]+)"')
$unexpectedPackages = New-Object System.Collections.Generic.List[string]
foreach ($match in $packageMatches) {
    $packageName = $match.Groups[1].Value
    if ($allowedWpfPackages -notcontains $packageName) {
        $unexpectedPackages.Add($packageName)
    }
}
if ($unexpectedPackages.Count -eq 0) {
    Pass "printer discovery adds no WPF NuGet dependency"
}
else {
    Fail "unexpected WPF PackageReference(s): $($unexpectedPackages -join ', ')"
}

Require-Pattern "printer diagnostics UI displays actual driver name" $dialogXaml '\{Binding\s+DriverName\}'
Require-Pattern "printer diagnostics UI displays actual port name" $dialogXaml '\{Binding\s+PortName\}'
Require-Pattern "printer diagnostics UI displays computed availability state" $dialogXaml '\{Binding\s+StatusText\}'
Require-Pattern "summary exposes default, availability, offline, paused and virtual state" $model 'public\s+string\s+Summary[\s\S]*IsDefault[\s\S]*IsVirtual[\s\S]*IsPaused[\s\S]*IsOffline[\s\S]*IsAvailable'

Require-Pattern "printer settings view model implements IDisposable" $viewModel 'class\s+PrinterSettingsViewModel\s*:\s*[^\{\r\n]*IDisposable'
Require-Pattern "printer settings view model has idempotent disposal" $viewModel '_disposed[\s\S]*public\s+void\s+Dispose\s*\(\s*\)[\s\S]*_disposed'
Require-Pattern "language subscription is paired with unsubscribe" $viewModel 'LanguageChanged\s*\+=\s*OnLanguageChanged[\s\S]*LanguageChanged\s*-=\s*OnLanguageChanged'

$dialogLifecycleMarkers = @(
    "Closed += OnDialogClosed",
    "Closed -= OnDialogClosed",
    "RequestClose += OnRequestClose",
    "RequestClose -= OnRequestClose",
    "OnRequestClose(bool ok)",
    "OnDialogClosed(object sender, EventArgs e)",
    "ViewModel.Dispose()"
)
$missingDialogLifecycle = @($dialogLifecycleMarkers | Where-Object {
    $dialog.IndexOf($_, [StringComparison]::Ordinal) -lt 0
})
if ($missingDialogLifecycle.Count -eq 0 -and $dialog -match 'DataContext\s*=\s*null') {
    Pass "dialog uses named handlers and detaches/disposes on Closed"
}
else {
    Fail "dialog lifecycle cleanup incomplete: $($missingDialogLifecycle -join ', ')"
}

Require-Pattern "printer settings caller detaches refresh handler" $posViewModel 'RefreshPrintersRequested\s*-=\s*refreshPrintersHandler'
Require-Pattern "printer settings caller detaches print handler" $posViewModel 'TestPrintRequested\s*-=\s*testPrintHandler'
Require-Pattern "printer settings caller detaches drawer handler" $posViewModel 'TestCashDrawerRequested\s*-=\s*testCashDrawerHandler'
Require-Pattern "printer settings print callback returns Task" ($viewModel + "`n" + $posViewModel) 'event\s+Func<Task>\s+TestPrintRequested[\s\S]*Func<Task>\s+testPrintHandler'
Require-Pattern "printer settings drawer callback returns Task" ($viewModel + "`n" + $posViewModel) 'event\s+Func<string,\s*string,\s*Task>\s+TestCashDrawerRequested[\s\S]*Func<string,\s*string,\s*Task>\s+testCashDrawerHandler'
Forbid-Pattern "printer settings test callbacks avoid async-void Action handlers" $viewModel 'event\s+Action[^;\r\n]*Test(Print|CashDrawer)Requested'
Require-Pattern "printer settings tests use shared single-flight state" $viewModel 'CanTestPrint\s*=>[\s\S]{0,240}!IsTestOperationInProgress[\s\S]*CanTestCashDrawer\s*=>[\s\S]{0,240}!IsTestOperationInProgress[\s\S]*RunTestOperationAsync'
Require-Pattern "UI smoke awaits and verifies print/drawer single-flight" $uiSmoke 'printSingleFlight\s*=\s*printCalls\s*==\s*1[\s\S]*await\s+vm\.ActiveTestOperation[\s\S]*drawerSingleFlight\s*=\s*drawerCalls\s*==\s*1[\s\S]*await\s+vm\.ActiveTestOperation'

$printerLifecycleCycles = [regex]::Matches($uiSmoke, 'OpenThenCloseAsync\(CreatePrinterSettingsDialog\(\)').Count
if ($printerLifecycleCycles -ge 2 -and
    $uiSmoke -match 'private\s+static\s+PrinterSettingsDialog\s+CreatePrinterSettingsDialog\s*\(' -and
    $uiSmoke -match 'printerSettingsCycles=20') {
    Pass "UI smoke matrix covers repeated printer settings open/close and displacement"
}
else {
    Fail "UI smoke matrix must cover PrinterSettingsDialog lifecycle and report printerSettingsCycles=20"
}

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
