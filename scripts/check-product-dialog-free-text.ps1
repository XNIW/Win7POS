$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$xamlPath = Join-Path $repoRoot "src/Win7POS.Wpf/Products/ProductEditDialog.xaml"
$viewModelPath = Join-Path $repoRoot "src/Win7POS.Wpf/Products/ProductEditViewModel.cs"
$repositoryPath = Join-Path $repoRoot "src/Win7POS.Data/Repositories/ProductRepository.cs"
$localProductWriterPath = Join-Path $repoRoot "src/Win7POS.Data/Repositories/LocalProductWriter.cs"
$productMetaResolverPath = Join-Path $repoRoot "src/Win7POS.Data/Repositories/ProductMetaResolver.cs"
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
$localProductWriter = Read-Text $localProductWriterPath
$productMetaResolver = Read-Text $productMetaResolverPath
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

if ($repository -match '_localProductWriter\.UpsertAsync' -and
    $repository -match '_localProductWriter\.UpsertProductAndMetaInTransactionAsync' -and
    $repository -match '_localProductWriter\.UpdateProductAndMetaWithPriceHistoryAsync' -and
    $localProductWriter -match 'UpsertAsync[\s\S]{0,350}EnsureValidProductIdentity' -and
    $localProductWriter -match 'UpsertProductAndMetaInTransactionAsync[\s\S]{0,1200}EnsureValidProductIdentity' -and
    $localProductWriter -match 'UpdateProductAndMetaWithPriceHistoryAsync[\s\S]{0,1200}EnsureValidProductIdentity') {
    Pass "Product facade and local writer enforce receipt-safe identity at write sinks"
} else {
    Fail "Product facade/local writer write sinks must enforce receipt-safe identity"
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
    if ($productMetaResolver.Contains($snippet)) {
        Pass "ProductMetaResolver contains $snippet"
    } else {
        Fail "ProductMetaResolver must contain $snippet"
    }
}

if ($localProductWriter -match 'UpsertProductAndMetaInTransactionAsync[\s\S]{0,800}BeginTransaction\(\)' -and
    $localProductWriter -match 'UpsertProductAndMetaInTransactionCoreAsync[\s\S]{0,5000}ProductMetaResolver\.ResolveSupplierReferenceAsync[\s\S]{0,1000}ProductMetaResolver\.ResolveCategoryReferenceAsync[\s\S]{0,2000}INSERT OR REPLACE INTO product_meta' -and
    $localProductWriter -match 'tx\.Commit\(\)') {
    Pass "Create path resolves supplier/category inside product transaction"
} else {
    Fail "Create path must resolve supplier/category inside the product transaction"
}

if ($localProductWriter -match 'UpdateProductAndMetaWithPriceHistoryAsync[\s\S]*BeginTransaction\(\)[\s\S]*ProductMetaResolver\.ResolveSupplierReferenceAsync[\s\S]*ProductMetaResolver\.ResolveCategoryReferenceAsync[\s\S]*INSERT OR REPLACE INTO product_meta[\s\S]*tx\.Commit\(\)') {
    Pass "Edit path resolves supplier/category inside product transaction"
} else {
    Fail "Edit path must resolve supplier/category inside the product transaction"
}

if ($localProductWriter -match 'UpdateProductAndMetaWithPriceHistoryAsync[\s\S]*BeginTransaction\(\)[\s\S]*INSERT INTO product_price_history[\s\S]*tx\.Commit\(\)') {
    Pass "Edit path writes price history inside product transaction"
} else {
    Fail "Edit path must write price history inside the product transaction"
}

if ($productMetaResolver -match 'StringComparer\.OrdinalIgnoreCase|LOWER\(') {
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
