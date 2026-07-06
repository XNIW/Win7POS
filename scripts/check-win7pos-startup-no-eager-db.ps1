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

function Read-Text([string]$relativePath) {
    [System.IO.File]::ReadAllText((Join-Path $repoRoot $relativePath))
}

$required = @(
    "src/Win7POS.Wpf/App.xaml",
    "src/Win7POS.Wpf/App.xaml.cs",
    "src/Win7POS.Wpf/Infrastructure/DialogOwnerHelper.cs",
    "src/Win7POS.Wpf/MainWindow.xaml",
    "src/Win7POS.Wpf/MainWindow.xaml.cs",
    "src/Win7POS.Wpf/Products/ProductsView.xaml.cs",
    "src/Win7POS.Wpf/Products/ProductsViewModel.cs",
    "src/Win7POS.Wpf/Products/ProductsWorkflowService.cs"
)

foreach ($path in $required) {
    if (-not (Test-Path (Join-Path $repoRoot $path))) {
        Fail "$path missing"
    }
}

if ($fail) {
    exit 1
}

$appXaml = Read-Text "src/Win7POS.Wpf/App.xaml"
$app = Read-Text "src/Win7POS.Wpf/App.xaml.cs"
$dialogOwnerHelper = Read-Text "src/Win7POS.Wpf/Infrastructure/DialogOwnerHelper.cs"
$mainXaml = Read-Text "src/Win7POS.Wpf/MainWindow.xaml"
$mainWindow = Read-Text "src/Win7POS.Wpf/MainWindow.xaml.cs"
$productsView = Read-Text "src/Win7POS.Wpf/Products/ProductsView.xaml.cs"
$productsViewModel = Read-Text "src/Win7POS.Wpf/Products/ProductsViewModel.cs"
$productsWorkflow = Read-Text "src/Win7POS.Wpf/Products/ProductsWorkflowService.cs"

if ($appXaml -match "StartupUri") {
    Fail "App.xaml must not use StartupUri"
} else {
    Pass "App.xaml has no StartupUri"
}

if ($app -notmatch "new MainWindow\(\)" -or $app -notmatch "mainWindow\.Show\(\)") {
    Fail "App.OnStartup must explicitly create and show MainWindow"
} else {
    Pass "App.OnStartup explicitly creates and shows MainWindow"
}

if ($app -notmatch "App\.OnStartup startup failed" -or $app -notmatch "Shutdown\(-1\)") {
    Fail "App.OnStartup must trace constructor/startup failures and exit cleanly"
} else {
    Pass "App.OnStartup traces startup failures and exits cleanly"
}

$mainWindowOwnerGuard =
    $dialogOwnerHelper -match "return\s+mainWindow\s*!=\s*null\s*&&\s*mainWindow\.IsVisible\s*\?\s*mainWindow\s*:\s*null" -or
    $dialogOwnerHelper -match "return\s+IsSafeOwner\(mainWindow\)\s*\?\s*mainWindow\s*:\s*null"

if ($dialogOwnerHelper -notmatch "IsVisible" -or -not $mainWindowOwnerGuard) {
    Fail "DialogOwnerHelper must not return an invisible startup owner"
} else {
    Pass "DialogOwnerHelper skips invisible startup owners"
}

if ($mainXaml -notmatch 'x:Name="ProductsViewControl"') {
    Fail "MainWindow.xaml must name ProductsView as ProductsViewControl for lazy DataContext"
} else {
    Pass "ProductsViewControl is named for lazy loading"
}

if ($productsView -match "DataContext\s*=\s*new\s+ProductsViewModel") {
    Fail "ProductsView constructor eagerly creates ProductsViewModel"
} else {
    Pass "ProductsView constructor does not create ProductsViewModel"
}

if ($productsView -match "DbInitializer\.EnsureCreated") {
    Fail "ProductsView must not call DbInitializer"
} else {
    Pass "ProductsView does not call DbInitializer"
}

if ($productsViewModel -match "new\s+ProductsWorkflowService\s*\(\s*\)") {
    Fail "ProductsViewModel eagerly creates ProductsWorkflowService in a field initializer"
} else {
    Pass "ProductsViewModel does not eagerly create ProductsWorkflowService in a field initializer"
}

if ($productsViewModel -notmatch "LoadAsync") {
    Fail "ProductsViewModel must expose LoadAsync for explicit lazy loading"
} else {
    Pass "ProductsViewModel exposes explicit LoadAsync"
}

if ($productsWorkflow -match "public\s+ProductsWorkflowService\s*\(\s*\)[\s\S]*?DbInitializer\.EnsureCreated") {
    Fail "ProductsWorkflowService constructor still runs DbInitializer"
} else {
    Pass "ProductsWorkflowService constructor does not run DbInitializer"
}

if ($mainWindow -notmatch "EnsureProductsViewModel" -or
    $mainWindow -notmatch "new\s+ProductsViewModel" -or
    $mainWindow -notmatch "ProductsViewControl\.DataContext" -or
    $mainWindow -notmatch "LoadAsync") {
    Fail "MainWindow must create and load ProductsViewModel lazily from Products menu"
} else {
    Pass "MainWindow lazily creates and loads ProductsViewModel from Products menu"
}

if ($mainWindow -notmatch "PermissionCodes\.CatalogView[\s\S]*EnsureProductsViewModel") {
    Fail "Products lazy creation must happen after CatalogView permission check"
} else {
    Pass "Products lazy creation follows CatalogView permission check"
}

if ($mainWindow -notmatch "ProductsViewControl\.DataContext\s*=\s*null") {
    Fail "Products DataContext must be cleared when operator lacks CatalogView"
} else {
    Pass "Products DataContext clear path present"
}

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
