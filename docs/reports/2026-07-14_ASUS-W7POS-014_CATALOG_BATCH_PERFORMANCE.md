# ASUS-W7POS-014 — Catalog SQLite batch apply

## Task ledger

| Task | State | Commit | Evidence | Notes |
|---|---|---|---|---|
| ASUS-W7POS-014.1 | DONE | `3ed7a9a` | One connection and transaction per catalog page; prepared product/meta commands; cached references | `PosCatalogPullService.cs` intentionally not edited |
| ASUS-W7POS-014.2 | DONE | `31a6f36` | 98/98 Core tests; local before/after benchmark; synthetic 19,762-row run | Staging credentials/catalog were not available in this lane |

## Implementation

`RemoteCatalogBatchRepository.ApplyAsync(RemoteCatalogBatch, CancellationToken)` applies, in order, categories, suppliers, products/meta, pending prices, tombstones, prices, and a final pending-price replay on one SQLite connection and one transaction. Product/meta statements are explicitly prepared and reused. Active category and supplier references are loaded once per page, removing product-level reference lookup N+1 queries.

Cancellation is checked before the page transaction. After the transaction starts, the page either commits or rolls back; cancellation is not reported after a durable commit. Rollback is best-effort so a rollback exception cannot hide the original apply error.

The pending-stock cache records both barcode and existing `remote_product_id` before product identity changes. This preserves local stock with unresolved sale outbox entries even when the server changes a product barcode.

## Controlled local benchmark

Environment: Windows `10.0.26200`, .NET SDK `10.0.301`, isolated fresh SQLite database per iteration. The timed region contains 40 categories, 40 suppliers, 2,000 products, and 2,000 prices. Input construction and database initialization are outside the timed region. Each result finished with exactly 2,000 active remote products, 2,000 price rows, zero pending prices, and a 1,286,144-byte DB.

Command:

```powershell
$env:WIN7POS_RUN_CATALOG_BENCHMARK='1'
Remove-Item Env:WIN7POS_CATALOG_BENCHMARK_MODE -ErrorAction SilentlyContinue
Remove-Item Env:WIN7POS_CATALOG_BENCHMARK_ROWS -ErrorAction SilentlyContinue
Remove-Item Env:WIN7POS_CATALOG_BENCHMARK_ITERATIONS -ErrorAction SilentlyContinue
C:\Dev\dotnet10\dotnet.exe test tests\Win7POS.Core.Tests\Win7POS.Core.Tests.csproj `
  -c Release --no-build --no-restore `
  --filter FullyQualifiedName~CatalogBatchPerformanceTests `
  --logger "trx;LogFileName=catalog-performance-post-review.trx" `
  --results-directory artifacts\catalog-performance
```

| Mode | Iteration | Elapsed ms | Product rows/s | CPU ms | Working set bytes | DB bytes |
|---|---:|---:|---:|---:|---:|---:|
| legacy per-row | 1 | 12,784.599 | 156.44 | 4,546.875 | 92,000,256 | 1,286,144 |
| legacy per-row | 2 | 14,313.914 | 139.72 | 4,718.750 | 90,161,152 | 1,286,144 |
| legacy per-row | 3 | 12,604.613 | 158.67 | 4,562.500 | 88,813,568 | 1,286,144 |
| page batch | 1 | 88.416 | 22,620.39 | 93.750 | 89,870,336 | 1,286,144 |
| page batch | 2 | 81.484 | 24,544.67 | 78.125 | 91,541,504 | 1,286,144 |
| page batch | 3 | 81.523 | 24,532.83 | 78.125 | 91,512,832 | 1,286,144 |

Median wall time improved from 12,784.599 ms to 81.523 ms (`156.82x`). Median throughput improved from 156.44 to 24,532.83 product rows/s (`156.82x`). Median CPU time improved from 4,562.500 ms to 78.125 ms (`58.4x`). Median working set increased by about 1.50%; final database size and row counts were identical.

The original pre-change product-only probe, before the batch API existed, measured 2,000 products at 6,758.078 / 9,870.071 / 8,257.987 ms. The controlled comparison above supersedes that probe because it uses identical product, price, and reference inputs for both modes.

## Synthetic 19,762-row batch

This is a deterministic local scale test, not a staging full refresh and not proof of staging exactness.

Command additions before the same test command:

```powershell
$env:WIN7POS_CATALOG_BENCHMARK_MODE='batch'
$env:WIN7POS_CATALOG_BENCHMARK_ROWS='19762'
$env:WIN7POS_CATALOG_BENCHMARK_ITERATIONS='3'
```

| Iteration | Products | Prices | Pending | Elapsed ms | Product rows/s | CPU ms | Working set bytes | DB bytes |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 19,762 | 19,762 | 0 | 1,200.809 | 16,457.24 | 1,703.125 | 102,232,064 | 10,125,312 |
| 2 | 19,762 | 19,762 | 0 | 727.502 | 27,164.18 | 984.375 | 119,115,776 | 10,125,312 |
| 3 | 19,762 | 19,762 | 0 | 733.240 | 26,951.62 | 859.375 | 122,159,104 | 10,125,312 |

## Regression and build evidence

```text
C:\Dev\dotnet10\dotnet.exe test tests\Win7POS.Core.Tests\Win7POS.Core.Tests.csproj -c Release --no-restore
PASS: 98/98

C:\Dev\dotnet10\dotnet.exe build src\Win7POS.Wpf\Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86 --no-restore
PASS: net48 / x86, 0 warnings, 0 errors

git diff --check
PASS
```

Dedicated regression coverage proves:

- full rollback when the second product fails mid-page;
- clean retry after rollback and idempotent retry of an already committed page;
- remote identity canonicalization without duplicate active remote rows;
- unresolved-outbox stock preservation across a remote barcode change;
- pending remote-price queue and replay in the page transaction;
- soft tombstones for products, categories, and suppliers;
- cancellation before the page boundary writes nothing.

## Required orchestrator patch

`src/Win7POS.Wpf/Pos/Online/PosCatalogPullService.cs` is orchestrator-owned and was not modified. On base `00a3fe0`, change the call at line 315 to:

```csharp
var applyStats = await ApplyCatalogAsync(result.Value, cancellationToken)
    .ConfigureAwait(false);
```

Replace the method at lines 555–708 with the following exact adapter:

```csharp
private async Task<CatalogApplyStats> ApplyCatalogAsync(
    PosCatalogPullResponse response,
    CancellationToken cancellationToken)
{
    var catalog = response.Catalog;
    var products = catalog.Products ?? Array.Empty<PosCatalogProductResponse>();
    var priceRows = catalog.Prices ?? Array.Empty<PosCatalogPriceResponse>();
    var categories = BuildCategoryMap(catalog.Categories);
    var suppliers = BuildSupplierMap(catalog.Suppliers);
    var batch = new RemoteCatalogBatch
    {
        Categories = (catalog.Categories ?? Array.Empty<PosCatalogCategoryResponse>())
            .Where(item => item != null)
            .Select(item => new RemoteCatalogCategoryWrite
            {
                RemoteCategoryId = Normalize(item.CategoryId),
                Name = Normalize(item.Name),
                RemoteUpdatedAt = Normalize(item.UpdatedAt)
            })
            .ToArray(),
        Suppliers = (catalog.Suppliers ?? Array.Empty<PosCatalogSupplierResponse>())
            .Where(item => item != null)
            .Select(item => new RemoteCatalogSupplierWrite
            {
                RemoteSupplierId = Normalize(item.SupplierId),
                Name = Normalize(item.Name),
                RemoteUpdatedAt = Normalize(item.UpdatedAt)
            })
            .ToArray(),
        Products = products
            .Where(item => item != null)
            .Select(item =>
            {
                var barcode = Normalize(item.Barcode);
                return new RemoteCatalogProductWrite
                {
                    ArticleCode = Normalize(item.ItemNumber),
                    Barcode = barcode,
                    CategoryName = NameFor(categories, item.CategoryId),
                    Name = FirstNonEmpty(item.ProductName, item.SecondProductName, barcode),
                    PurchasePrice = ToInt(item.PurchasePrice),
                    RemoteProductId = Normalize(item.ProductId),
                    SecondName = Normalize(item.SecondProductName),
                    StockQuantity = ToInt(item.StockQuantity),
                    SupplierName = NameFor(suppliers, item.SupplierId),
                    UnitPrice = ToLong(item.RetailPrice)
                };
            })
            .Where(item => item.Barcode.Length > 0)
            .ToArray(),
        ProductTombstones = (catalog.Tombstones?.Products ?? Array.Empty<PosCatalogProductTombstoneResponse>())
            .Where(item => item != null)
            .Select(item => new RemoteCatalogProductTombstoneWrite
            {
                RemoteProductId = Normalize(item.ProductId),
                RemoteDeletedAt = Normalize(item.DeletedAt)
            })
            .ToArray(),
        CategoryTombstones = (catalog.Tombstones?.Categories ?? Array.Empty<PosCatalogCategoryTombstoneResponse>())
            .Where(item => item != null)
            .Select(item => new RemoteCatalogCategoryTombstoneWrite
            {
                RemoteCategoryId = Normalize(item.CategoryId),
                RemoteDeletedAt = Normalize(item.DeletedAt),
                RemoteUpdatedAt = Normalize(item.UpdatedAt)
            })
            .ToArray(),
        SupplierTombstones = (catalog.Tombstones?.Suppliers ?? Array.Empty<PosCatalogSupplierTombstoneResponse>())
            .Where(item => item != null)
            .Select(item => new RemoteCatalogSupplierTombstoneWrite
            {
                RemoteSupplierId = Normalize(item.SupplierId),
                RemoteDeletedAt = Normalize(item.DeletedAt),
                RemoteUpdatedAt = Normalize(item.UpdatedAt)
            })
            .ToArray(),
        Prices = priceRows
            .Where(item => item != null)
            .Select(item => new RemoteCatalogPriceWrite
            {
                RemoteProductId = Normalize(item.ProductId),
                RemotePriceId = Normalize(item.PriceId),
                Type = Normalize(item.Type),
                Price = ToInt(item.Price),
                EffectiveAt = Normalize(item.EffectiveAt),
                Source = Normalize(item.Source)
            })
            .ToArray()
    };

    var applied = await new RemoteCatalogBatchRepository(_factory)
        .ApplyAsync(batch, cancellationToken)
        .ConfigureAwait(false);
    var tombstonesReceived =
        (catalog.Tombstones?.Products?.Length ?? 0) +
        (catalog.Tombstones?.Categories?.Length ?? 0) +
        (catalog.Tombstones?.Suppliers?.Length ?? 0);

    if (tombstonesReceived > 0)
    {
        _logger.LogInfo(
            "Catalog tombstones received: count=" + tombstonesReceived.ToString() +
            ", appliedProducts=" + applied.ProductTombstonesApplied.ToString() +
            ", appliedCategories=" + applied.CategoryTombstonesApplied.ToString() +
            ", appliedSuppliers=" + applied.SupplierTombstonesApplied.ToString() +
            "; local purge disabled; tombstones are stored as inactive rows.");
    }

    return new CatalogApplyStats
    {
        CategoryRowsReceived = catalog.Categories?.Length ?? 0,
        PendingPriceRowsApplied = applied.PendingPricesApplied,
        PriceRowsApplied = applied.PricesApplied,
        PriceRowsQueued = applied.PricesQueued,
        PriceRowsReceived = priceRows.Length,
        SupplierRowsReceived = catalog.Suppliers?.Length ?? 0,
        TombstonesApplied = applied.TombstonesApplied,
        TombstonesReceived = tombstonesReceived,
        UpdatedProducts = applied.ProductsApplied
    };
}
```

After applying the adapter, rerun the WPF build and the service-level catalog pull tests. A real staging full refresh still requires the staging service/credentials and must not be inferred from the local synthetic 19,762-row result.
