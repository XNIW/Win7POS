$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$xamlPath = Join-Path $repoRoot "src/Win7POS.Wpf/Products/ProductEditDialog.xaml"
$viewModelPath = Join-Path $repoRoot "src/Win7POS.Wpf/Products/ProductEditViewModel.cs"
$repositoryPath = Join-Path $repoRoot "src/Win7POS.Data/Repositories/ProductRepository.cs"
$contentPolicyPath = Join-Path $repoRoot "src/Win7POS.Core/Receipt/ReceiptContentPolicy.cs"

$fail = $false

function Fail([string]$message) {
    Write-Host "FAIL: $message" -ForegroundColor Red
    $script:fail = $true
}

function Pass([string]$message) {
    Write-Host "PASS: $message" -ForegroundColor Green
}

function Read-Text([string]$path) {
    if (-not (Test-Path $path)) {
        Fail "Missing file: $path"
        return ""
    }

    return Get-Content -Raw -Path $path
}

$xaml = Read-Text $xamlPath
$viewModel = Read-Text $viewModelPath
$repository = Read-Text $repositoryPath
$contentPolicy = Read-Text $contentPolicyPath

Write-Host "Checking ProductEdit free-text supplier/category support..."

if ($xaml -match 'ItemsSource="\{Binding Suppliers\}"[\s\S]*?IsEditable="True"[\s\S]*?Text="\{Binding SupplierText, UpdateSourceTrigger=PropertyChanged\}"') {
    Pass "Supplier ComboBox is editable and binds SupplierText"
} else {
    Fail "Supplier ComboBox must be editable and bind Text to SupplierText"
}

if ($xaml -match 'ItemsSource="\{Binding Categories\}"[\s\S]*?IsEditable="True"[\s\S]*?Text="\{Binding CategoryText, UpdateSourceTrigger=PropertyChanged\}"') {
    Pass "Category ComboBox is editable and binds CategoryText"
} else {
    Fail "Category ComboBox must be editable and bind Text to CategoryText"
}

if ($xaml -match 'Text="\{Binding Barcode, UpdateSourceTrigger=PropertyChanged\}"[\s\S]{0,180}MaxLength="256"' -and
    $xaml -match 'Text="\{Binding ProductName, UpdateSourceTrigger=PropertyChanged\}"[\s\S]{0,180}MaxLength="512"') {
    Pass "Barcode and product name inputs use shared receipt-safe limits"
} else {
    Fail "Barcode and product name TextBoxes must be bounded to 256/512 characters"
}

if ($viewModel -match 'SalesReceiptContentPolicy\.IsValidBarcode\(Barcode\)' -and
    $viewModel -match 'SalesReceiptContentPolicy\.IsValidProductName\(ProductName\)' -and
    $contentPolicy -match 'MaxSaleLineBarcodeCharacters\s*=\s*256' -and
    $contentPolicy -match 'MaxSaleLineNameCharacters\s*=\s*512') {
    Pass "ViewModel validates barcode and name against shared content policy"
} else {
    Fail "ViewModel must reject unsafe barcode/name values without truncation"
}

if ($repository -match 'UpsertAsync[\s\S]{0,350}EnsureValidProductIdentity' -and
    $repository -match 'UpsertProductAndMetaInTransactionAsync[\s\S]{0,500}EnsureValidProductIdentity' -and
    $repository -match 'UpdateProductAndMetaWithPriceHistoryAsync[\s\S]{0,500}EnsureValidProductIdentity') {
    Pass "Product repository enforces receipt-safe identity at write sinks"
} else {
    Fail "Product repository write sinks must enforce receipt-safe identity"
}

foreach ($snippet in @(
    "public string SupplierText",
    "public string CategoryText",
    "BuildSupplierSelection",
    "BuildCategorySelection"
)) {
    if ($viewModel.Contains($snippet)) {
        Pass "ViewModel contains $snippet"
    } else {
        Fail "ViewModel must contain $snippet"
    }
}

foreach ($snippet in @(
    "ResolveSupplierReferenceAsync",
    "ResolveCategoryReferenceAsync",
    "FindSupplierByNormalizedNameAsync",
    "FindCategoryByNormalizedNameAsync"
)) {
    if ($repository.Contains($snippet)) {
        Pass "ProductRepository contains $snippet"
    } else {
        Fail "ProductRepository must contain $snippet"
    }
}

if ($repository -match 'BeginTransaction\(\)[\s\S]*ResolveSupplierReferenceAsync[\s\S]*ResolveCategoryReferenceAsync[\s\S]*INSERT OR REPLACE INTO product_meta[\s\S]*tx\.Commit\(\)') {
    Pass "Create path resolves supplier/category inside product transaction"
} else {
    Fail "Create path must resolve supplier/category inside the product transaction"
}

if ($repository -match 'UpdateProductAndMetaWithPriceHistoryAsync[\s\S]*BeginTransaction\(\)[\s\S]*ResolveSupplierReferenceAsync[\s\S]*ResolveCategoryReferenceAsync[\s\S]*INSERT OR REPLACE INTO product_meta[\s\S]*tx\.Commit\(\)') {
    Pass "Edit path resolves supplier/category inside product transaction"
} else {
    Fail "Edit path must resolve supplier/category inside the product transaction"
}

if ($repository -match 'UpdateProductAndMetaWithPriceHistoryAsync[\s\S]*BeginTransaction\(\)[\s\S]*INSERT INTO product_price_history[\s\S]*tx\.Commit\(\)') {
    Pass "Edit path writes price history inside product transaction"
} else {
    Fail "Edit path must write price history inside the product transaction"
}

if ($repository -match 'StringComparer\.OrdinalIgnoreCase|LOWER\(') {
    Pass "Case-insensitive duplicate guard is present"
} else {
    Fail "Case-insensitive duplicate guard is required"
}

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
