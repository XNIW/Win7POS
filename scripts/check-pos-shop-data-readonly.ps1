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

$required = @(
    "src/Win7POS.Data/Repositories/ShopOfficialSnapshotRepository.cs",
    "src/Win7POS.Wpf/Pos/Dialogs/ShopSettingsDialog.xaml",
    "src/Win7POS.Wpf/Pos/Dialogs/ShopSettingsViewModel.cs",
    "src/Win7POS.Wpf/Pos/Online/PosOnlineBootstrapService.cs",
    "src/Win7POS.Wpf/Pos/Online/PosCatalogPullService.cs",
    "src/Win7POS.Wpf/Pos/Online/PosSalesSyncService.cs",
    "src/Win7POS.Wpf/Pos/PosWorkflowService.cs"
)

foreach ($path in $required) {
    if (-not (Test-Path (Join-Path $repoRoot $path))) {
        Fail "$path missing"
    }
}

if ($fail) { exit 1 }

$repo = Read-Text "src/Win7POS.Data/Repositories/ShopOfficialSnapshotRepository.cs"
$dialog = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/ShopSettingsDialog.xaml"
$vm = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/ShopSettingsViewModel.cs"
$bootstrap = Read-Text "src/Win7POS.Wpf/Pos/Online/PosOnlineBootstrapService.cs"
$catalog = Read-Text "src/Win7POS.Wpf/Pos/Online/PosCatalogPullService.cs"
$sales = Read-Text "src/Win7POS.Wpf/Pos/Online/PosSalesSyncService.cs"
$workflow = Read-Text "src/Win7POS.Wpf/Pos/PosWorkflowService.cs"
$client = (Read-Text "src/Win7POS.Data/Online/PosAdminWebClient.cs") + "`n" + (Read-Text "src/Win7POS.Core/Online/PosOnlineTransportContracts.cs")
$localization = Read-Text "src/Win7POS.Wpf/Localization/PosLocalization.cs"
$secondaryLocalization = Read-Text "src/Win7POS.Wpf/Localization/PosTranslations.Secondary.cs"
$dialogCopy = $dialog + $localization + $secondaryLocalization
$combined = Get-ChildItem -Path $srcRoot -Recurse -File -Include *.cs,*.xaml |
    Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" } |
    ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) } |
    Out-String

if ($repo -notmatch "pos\.official_shop\.") { Fail "official shop snapshot must use dedicated pos.official_shop keys" } else { Pass "official shop snapshot keys present" }
if ($repo -notmatch "Source[\s\S]*supabase_admin_server") { Fail "official shop snapshot source missing" } else { Pass "official shop snapshot source present" }
if ($repo -notmatch "SyncedAtUtc") { Fail "official shop snapshot sync timestamp missing" } else { Pass "official shop snapshot sync timestamp present" }
if ($client -notmatch 'DataMember\(Name = "companyRut"') { Fail "POS shop response companyRut missing" } else { Pass "POS shop response companyRut present" }
if ($client -notmatch 'DataMember\(Name = "businessAddress"') { Fail "POS shop response businessAddress missing" } else { Pass "POS shop response businessAddress present" }
if ($client -notmatch 'DataMember\(Name = "businessGiro"') { Fail "POS shop response businessGiro missing" } else { Pass "POS shop response businessGiro present" }
if ($bootstrap -notmatch "PosOnlineShopSnapshot\.SaveAsync") { Fail "first-login does not cache official shop snapshot" } else { Pass "first-login caches official shop snapshot" }
if ($catalog -notmatch "PosOnlineShopSnapshot\.SaveAsync") { Fail "catalog pull does not refresh official shop snapshot" } else { Pass "catalog pull refreshes official shop snapshot" }
if ($sales -notmatch "PosOnlineShopSnapshot\.SaveAsync") { Fail "sales sync does not refresh official shop snapshot from ack" } else { Pass "sales sync refreshes official shop snapshot" }
if ($workflow -notmatch "officialSnapshot\.HasOfficialData[\s\S]*officialSnapshot\.ToReceiptShopInfo") { Fail "receipt/shop reader must prefer official snapshot" } else { Pass "receipt/shop reader prefers official snapshot" }
if ($workflow -match "public\s+async\s+Task\s+SaveShopInfoAsync") { Fail "public local SaveShopInfoAsync write path must not exist" } else { Pass "public local shop save path absent" }
if ($vm -notmatch "public bool IsReadOnly => true") { Fail "shop settings view model must stay read-only" } else { Pass "shop settings view model read-only" }
if ($vm -match "SaveCommand|SaveAsync|SaveShopInfoAsync") { Fail "shop settings view model must not contain save command/path" } else { Pass "shop settings has no save command" }
if ($dialogCopy -notmatch "Dati negozio ufficiali") { Fail "shop dialog copy must be official/read-only" } else { Pass "shop dialog official copy present" }
if ($dialogCopy -notmatch "Master Console") { Fail "shop dialog must point edits to Master Console" } else { Pass "shop dialog Master Console boundary present" }
if ($dialogCopy -notmatch "cache anche offline") { Fail "shop dialog must explain offline cache" } else { Pass "shop dialog offline cache copy present" }
if ($dialog -match 'Content="(Salva|Guardar|Save|Modifica|Editar|Edit|Apply)"') { Fail "shop dialog must not expose save/edit/apply button" } else { Pass "shop dialog has no save/edit/apply button" }
$shopTextBoxes = [regex]::Matches($dialog, "<TextBox[\s\S]*?(?:/>|</TextBox>)")
$readonlyValueBorders = [regex]::Matches($dialog, 'ReadOnlyInfoValueBorderStyle')
if ($shopTextBoxes.Count -gt 0) {
    if ($shopTextBoxes | Where-Object { $_.Value -notmatch 'IsReadOnly="\{Binding IsReadOnly\}"' }) {
        Fail "all shop dialog text boxes must bind IsReadOnly"
    }
    else {
        Pass "all shop dialog text boxes bind IsReadOnly"
    }
}
elseif ($readonlyValueBorders.Count -ge 6 -and $dialog -match 'ReadOnlyInfoValueTextStyle') {
    Pass "shop dialog uses readonly value surfaces"
}
else {
    Fail "shop dialog readonly value surfaces not found"
}

$shopMutationPattern = '(?i)/api/pos/shop|/api/shop/[^\r\n]{0,120}(update|settings)|SaveShopInfoAsync|ShopSettings[^\r\n]*(Save|Edit|Apply)|method:\s*"(PUT|PATCH|DELETE)"[^\r\n]*(shop|settings)'
if ($combined -match $shopMutationPattern) { Fail "Win7POS must not introduce shop mutation endpoint/client" } else { Pass "no Win7POS shop mutation endpoint/client" }

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
