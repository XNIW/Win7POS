$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$xamlPath = Join-Path $repoRoot "src/Win7POS.Wpf/Products/ProductEditDialog.xaml"
$viewModelPath = Join-Path $repoRoot "src/Win7POS.Wpf/Products/ProductEditViewModel.cs"
$repositoryPath = Join-Path $repoRoot "src/Win7POS.Data/Repositories/ProductRepository.cs"

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
