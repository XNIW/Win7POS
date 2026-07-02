# Supplier Excel Import Contract

Android is the canonical source for supplier Excel import behavior. Public import keys are: `barcode`, `productName`, `itemNumber`, `purchasePrice`, `retailPrice`, `quantity`, `supplier`, `category`, `secondProductName`, `totalPrice`, `rowNumber`, `discount`, `discountedPrice`, `oldPurchasePrice`, `oldRetailPrice`, `realQuantity`, `complete`.

Forbidden names such as `stockQuantity`, `supplierName`, `categoryName`, `articleCode`, `unitPrice`, `name`, `name2`, `cost`, `prevPurchase`, and `prevRetail` are allowed only as storage/model boundary details or legacy aliases normalized before preview.

UX workflow: supplier import is a three-step wizard: file selection, column analysis/mapping, then editable price preview/apply. The preview must allow editing `purchasePrice`, `retailPrice`, `quantity`, `supplier`, and `category` before apply. The bulk helper can calculate `retailPrice` from `purchasePrice` using a markup percent, rounding to 10/50/100 CLP, and an apply-only-empty option. `purchasePrice` must never silently auto-fill `retailPrice`; new products without `retailPrice` are blocked until the user fills the value or uses the helper.

Detection policy: normalize headers by trim/lowercase/diacritic removal/space and underscore removal/non-alphanumeric removal; first data row is `numericCount >= 3 && textCount >= 1`; previous row is header when present, otherwise generated columns; each column keeps `headerSource` as `alias`, `pattern`, `generated`, or `unknown`; required `barcode`, `productName`, and `purchasePrice` are generated when missing.

Pattern policy: barcode 8/12/13 digits; itemNumber length 4..12 with letters or digits; quantity and purchasePrice positive numeric in at least 70%; totalPrice matches quantity * purchasePrice within 10% in at least 70%; productName text length at least 3 in at least 50%; headerless files also infer retailPrice, secondProductName, supplier, discount, discountedPrice, and rowNumber.

Rows: filter summary rows with the exact Android token set `合计`, `总计`, `小计`, `汇总`, `合計`, `總計`, `小計`, `總結`, `总额`, `subtotal`, `total`, `totale`, `tot.`, `sommario`, `resumen`, `sum`; parse `1.234,56`, `1,234.56`, `1234,56`, and `1234`; duplicate barcodes keep the last occurrence, warn with row numbers, and do not sum quantity.

Boundary mapping:
- Android: `quantity -> stockQuantity` and other canonical keys map to Room `Product` fields only inside analyzer/repository apply.
- iOS: `quantity -> stockQuantity` and other canonical keys map to `ProductDraft`/SwiftData fields only inside `ProductImportCore`.
- Admin: canonical preview/import rows map to API/database schema only inside server apply/merge functions.
- Win7POS: `itemNumber -> ImportRow.ArticleCode / product_meta.article_code`; `productName -> ImportRow.Name / products.name`; `secondProductName -> ImportRow.Name2 / product_meta.name2`; `retailPrice -> ImportRow.UnitPrice / products.unitPrice`; `purchasePrice -> ImportRow.Cost / product_meta.purchase_price`; `quantity -> ImportRow.Stock / product_meta.stock_qty`; `supplier -> SupplierName` only inside the adapter; `category -> CategoryName` only inside the adapter.

Excel readers must support real `.xlsx`, real `.xls`, and simple HTML table exports saved as `.xls` when the platform reader supports those formats. Win7POS handles HTML `.xls` with an internal table parser so no new Win7-incompatible UI/runtime dependency is introduced.

Evidence: `dotnet run --project src/Win7POS.Cli/Win7POS.Cli.csproj -c Release -- --supplier-excel-selftest` loads `tests/fixtures/supplier-import/android-canonical-sample.json` and checks canonical keys, metadata/alias fixture cases, `headerSource`, no-header pattern detection, summary filtering, parseNumber parity, duplicate last-wins, missing-retail handling, and HTML `.xls`. `--supplier-excel-ui-selftest` plus `scripts/check-supplier-excel-wizard.ps1` statically prove Products/DB entry points, modal three-step wizard, mapping override/disable, editable price grid, bulk helper, backup/transaction/rollback, IMPORT price history, and catalog refresh wiring.
