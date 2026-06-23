$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$srcRoot = Join-Path $repoRoot "src"
$fail = $false

function Fail([string]$message) {
    Write-Host "FAIL: $message" -ForegroundColor Red
    $script:fail = $true
}

function Pass([string]$message) {
    Write-Host "PASS: $message" -ForegroundColor Green
}

function Read-Text([string]$relativePath) {
    [System.IO.File]::ReadAllText((Join-Path $repoRoot $relativePath))
}

$paymentXaml = Read-Text "src/Win7POS.Wpf/Pos/PaymentView.xaml"
$paymentVm = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/PaymentViewModel.cs"
$workflow = Read-Text "src/Win7POS.Wpf/Pos/PosWorkflowService.cs"
$registerVm = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/SalesRegisterViewModel.cs"
$registerXaml = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/SalesRegisterDialog.xaml"
$dailyDialog = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/DailyReportDialog.xaml"
$dailyView = Read-Text "src/Win7POS.Wpf/Pos/DailyReportView.xaml"
$combined = Get-ChildItem -Path $srcRoot -Recurse -File -Include *.cs,*.xaml |
    Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" } |
    ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) } |
    Out-String

if ($paymentXaml -notmatch "Boleta/PDF locale") { Fail "payment view must label local boleta/PDF" } else { Pass "local boleta/PDF label present" }
if ($paymentXaml -match "Fiscale \(SII Web\)|Area fiscale temporaneamente disattivata") { Fail "payment view still has misleading SII Web/disabled copy" } else { Pass "misleading SII Web copy absent" }
if ($paymentXaml -notmatch "Stampa automatica boleta solo con contanti") { Fail "auto-print cash policy copy missing" } else { Pass "auto-print cash policy copy present" }
if ($paymentVm -notmatch "Resto \(solo contanti\)") { Fail "cash-only change copy missing" } else { Pass "cash-only change copy present" }
if ($paymentVm -notmatch "La carta non può superare il saldo da pagare") { Fail "card over-balance validation message missing" } else { Pass "card over-balance validation message present" }
if ($workflow -notmatch "CardAmountMinor > Math\.Max\(0, totalMinor - CashAmountMinor\)") { Fail "workflow must reject card over residual balance" } else { Pass "workflow rejects card over residual balance" }
if ($paymentVm -match "REF\. VENDEDOR|Dirección: Santiago|Giro: VENTA PRENDAS") { Fail "hardcoded fiscal literals remain in boleta preview" } else { Pass "hardcoded fiscal literals absent" }
if ($paymentVm -notmatch "BusinessGiro" -or $paymentVm -notmatch "LegalRepresentativeRut") { Fail "boleta preview must use official shop fiscal fields" } else { Pass "boleta preview uses official shop fiscal fields" }
if ($registerVm -notmatch "Documento: Boleta PDF stampata" -or $registerVm -notmatch "Non stampata \(contanti\)" -or $registerVm -notmatch "Non stampata \(policy carta\)") { Fail "register detail document status copy missing" } else { Pass "register document status copy present" }
if ($workflow -notmatch "document_status" -or $workflow -notmatch "pdf_printed") { Fail "CSV export must include document status" } else { Pass "CSV document status present" }
if (($registerXaml + $dailyDialog + $dailyView) -match "Aggiorna vista completa") { Fail "fake/ambiguous complete-view lock copy remains" } else { Pass "ambiguous complete-view lock copy absent" }

$blockedCopy = "(?i)\bfatturato\b|\butile\b|ricavi dichiarati|fiscale ok|SII accettato"
if ($combined -match $blockedCopy) { Fail "ambiguous revenue/fiscal copy detected" } else { Pass "ambiguous revenue/fiscal copy absent" }

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
