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

Win7POS operator workflow:
1. Open Products/Catalog or Database/Maintenance and choose `Import Excel fornitore`.
2. Choose the supplier `.xls`/`.xlsx` file.
3. Review Step 1 sheet/header/row counts and warning/error summary.
4. In Step 2 set any wrong column to the Android canonical key, or disable columns that should not import.
5. Click analyze/continue so Step 3 rebuilds from the corrected mapping.
6. In Step 3 edit `retailPrice`, `purchasePrice`, `quantity`, `supplier`, or `category`; for new products with empty `retailPrice`, use the bulk helper or type the values.
7. Apply only after the button is enabled; the summary shows backup path, inserted, updated, skipped, warning count, and error count.

Operational state policy:
- `ready_to_apply`: Step 3 preview has required values; the user reviews and applies.
- `ready_after_mapping_override`: Step 2 needs a canonical mapping correction, then the same file can continue.
- `ready_after_price_edit`: Step 3 needs retail price entry or the bulk helper, then the same file can continue.
- `business_blocked_missing_barcode`: the workbook row must be corrected or removed before apply.
- `unsupported_or_corrupt_with_clear_message`: the app shows a recoverable file-format message.

Admin web import is intended for normal/smaller workbooks. Win7POS offline import is the recommended path for large or complex supplier files and for store-side offline operation; Admin must show a clear message routing over-limit files to Win7POS or split workbooks.

Evidence: `dotnet run --project src/Win7POS.Cli/Win7POS.Cli.csproj -c Release -- --supplier-excel-selftest` loads `tests/fixtures/supplier-import/android-canonical-sample.json` and checks canonical keys, metadata/alias fixture cases, `headerSource`, no-header pattern detection, summary filtering, parseNumber parity, duplicate last-wins, missing-retail handling, and HTML `.xls`. `--supplier-excel-ui-selftest` plus `scripts/check-supplier-excel-wizard.ps1` statically prove Products/DB entry points, modal three-step wizard, mapping override/disable, editable price grid, bulk helper, backup/transaction/rollback, IMPORT price history, and catalog refresh wiring. `--supplier-excel-apply-selftest` creates a temporary `WIN7POS_DATA_DIR`, initializes SQLite, analyzes a workbook, applies through the same supplier workflow, verifies backup, transaction commit, forced rollback, `IMPORT` price history, DB integrity, and no Program Files data write. `--supplier-excel-drive-completion-report <folder>` turns real-file parser outcomes into the operational action matrix used by the shop.

Windows/ASUS validation checklist:
1. `git pull`.
2. `dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86`.
3. Run `--supplier-excel-selftest`, `--supplier-excel-ui-selftest`, `--supplier-excel-apply-selftest`, and the two PowerShell checks.
4. Start Win7POS, open supplier import from Products and Database/Maintenance.
5. Open one `.xls` and one `.xlsx`, verify Step 1/2/3/Apply, backup path, and DB summary.
