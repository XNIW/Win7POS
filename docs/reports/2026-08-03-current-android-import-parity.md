# Current Android supplier import parity — 2026-08-03

## Sources and classification

- Win7POS baseline: `8170c5030444fa459aabfe08874f3322cdc2fba3` (PR #85 merged).
- Android canonical main: `4b2b4a93dd5d4db7d1cfb83e897aa5cbac40366e`.
- No commit newer than either expected SHA existed when the comparison began.
- `A` = `CATALOG_IMPORT_CORE`; `B` = `ANDROID_RECEIVING_WORKFLOW_ONLY`; `C` = `PLATFORM_SPECIFIC_SAFETY`.
- Status totals: 47 rows — 31 `ALIGNED`, 3 `ANDROID_AHEAD`, 8 `WIN7POS_AHEAD`, 5 `PLATFORM_SPECIFIC`.

The Android repository was read-only. The selected Win7POS port is limited to exact compound-header fragments, two-row header detection/merge, ambiguity-aware core scoring, and bounded structural diagnostics. No Android receiving UI, payment/history/export feature, database migration, or Win7POS apply-loop change is included.

## Behavioral matrix

| # | Feature | Class | Android current behavior and exact source | Win7POS behavior after this change and exact source | Status | Intentional scope decision | Test coverage |
|---:|---|:---:|---|---|---|---|---|
| 1 | Canonical public keys | A | `ExcelUtils.KNOWN_EXCEL_HEADER_ALIASES`; `ImportAnalyzer.DEFERRED_RELATION_ROW_KEYS` | `AndroidImportKeys.AllKeys` | ALIGNED | Preserve public interop keys. | Old canonical fixture; golden corpus |
| 2 | Forbidden/storage aliases | A | `ImportAnalyzer.analyzeStreamingDeferredRelations` maps canonical keys into Room `Product` fields. | `SupplierImportAnalyzer.BuildEditableRow` and `ToFinalCanonicalRow` isolate `ArticleCode`, `UnitPrice`, `Cost`, and `Stock`. | ALIGNED | Storage names never become preview keys. | Supplier selftest; apply selftest |
| 3 | Header normalization | A | `normalizeExcelHeader` removes diacritics, spaces, `_`, and non-letter/digit characters. | `SupplierImportAnalyzer.NormalizeHeader` | ALIGNED | Exact current Android normalization retained. | Compound golden cases |
| 4 | Unicode/diacritic normalization | A | `normalizeExcelHeader` uses NFD plus `\p{M}` removal. | `NormalizeHeader` uses FormD plus combining-mark removal. | ALIGNED | Platform-equivalent Unicode operation. | Chinese/Spanish; locale corpus |
| 5 | CJK/Latin script splitting | A | `splitHeaderFragmentByScript` and `isCjkHeaderChar`. | `SplitHeaderFragmentByScript` and `IsCjkHeaderChar`. | ALIGNED | BMP CJK ranges cover supported supplier headers on netstandard2.0. | Chinese/Spanish and Chinese/English cases |
| 6 | Slash/parenthesis/newline fragments | A | `normalizedHeaderFragments` splits the bounded separator set and whitespace. | `NormalizedHeaderFragments` uses the same separator set. | ALIGNED | Whole header remains a candidate too. | Compound and two-row cases |
| 7 | Exact alias matching | A | `headerMatchesAlias` compares normalized fragments exactly. | `HeaderMatchesAlias` compares normalized fragments exactly. | ALIGNED | Unsafe substring matching is forbidden. | `CompoundHeaders_UseExactFragmentsWithoutUnsafeSubstringMatching` |
| 8 | Metadata rows | A | `detectHeader` finds repeated data profiles and excludes preceding metadata. | `DetectHeader`; `SupplierImportAnalysis.SkippedMetadataRows`. | ALIGNED | Metadata is not copied into data rows. | Metadata plus two-row golden case |
| 9 | One-row headers | A | `LEGACY_HEADER_ALIAS_FAST_PATH = 3`. | `LegacyHeaderAliasFastPath = 3`. | ALIGNED | Existing clean-file fast path stays first. | Old reader corpus; compound cases |
| 10 | Two-row headers | A | `MAX_HEADER_LOOKBACK_ROWS = 2`; `mergeHeaderRows`. | `MaxHeaderLookbackRows = 2`; `MergeHeaderRows`. | ALIGNED | Merge only non-data-like rows when evidence improves. | `TwoRowHeaders_MergeAtMostTwoNonDataRowsAndPreserveSourceRows` |
| 11 | Headerless files | A | `detectHeader` returns `generated-no-header`; patterns map columns. | `DetectHeader` plus generated columns and pattern inference. | ALIGNED | Legacy Win7 mappings remain stable where structurally clear. | Headerless clear/ambiguous corpus; PR #85 test |
| 12 | Row profiling | A | `buildRowProfiles`; data-like means 4 nonblank, 2 numeric, 1 text. | `BuildRowProfiles`; `RowProfile.LooksDataLike`. | ALIGNED | Cancellation remains checked every 64 source rows. | Golden corpus; cancellation tests |
| 13 | Barcode scoring | A | `scorePatternCandidates("barcode")` weights numeric, 8–14 digit, length, and non-alphanumeric evidence. | `ScorePatternCandidates(Barcode)` with the same weights. | ALIGNED | No compatibility tie-break may resolve barcode ambiguity. | Almost-equal barcode case |
| 14 | Item-number scoring | A | `scorePatternCandidates("itemNumber")`. | Same weights plus `TryAssignLegacyItemNumber` only for an unambiguous strict alphanumeric shape. | WIN7POS_AHEAD | Retains the PR #85 clear AB12/CD34 fixture without weakening ambiguity rules. | PR #85 headerless test; clear corpus |
| 15 | Product-name scoring | A | `scorePatternCandidates("productName")`. | Same weights plus `catalog-adjacency-tiebreak` when a strong candidate immediately follows the selected barcode. | WIN7POS_AHEAD | Additional structural evidence preserves a legacy clear catalog layout. | PR #85 headerless test; diagnostic trace test |
| 16 | Quantity scoring | A | Weighted integer/small/rank score with sequential-row penalty. | Same score plus `catalog-adjacency-tiebreak` when a qualifying candidate follows product name. | WIN7POS_AHEAD | Keeps the established 12-column layout; sequential row candidates remain penalized. | Quantity-versus-row-number case |
| 17 | Purchase-price scoring | A | `scorePatternCandidates("purchasePrice")`. | `ScorePatternCandidates(PurchasePrice)`. | ALIGNED | No price adjacency tie-break. | Ambiguous numeric case |
| 18 | Total-price multiplication match | A | `detectPurchaseTotalPair`; 10% tolerance and 70% match. | `DetectPurchaseTotalPair`; bounded to 40 samples for x86 safety. | ALIGNED | Bounded sample is the platform safety adaptation. | Purchase-versus-total case |
| 19 | Row-number exclusion | A | `rowNumberLikeRatio`; `shouldSkipHeaderAlias` rejects sequential `REF.CAJAS`. | `RowNumberLikeRatio`; `ShouldSkipHeaderAlias`. | ALIGNED | Sequential evidence is diagnostic, not a data dump. | Quantity-versus-row-number case |
| 20 | Ambiguity margin | A | Score `>= 0.45`; winner must beat runner-up by `> 0.08`. | `MinimumPatternScore`; `AmbiguityMargin`; `ShouldAssignCandidate`. | ALIGNED | Low-confidence columns stay available in Step 2. | Two ambiguous golden cases |
| 21 | Confidence levels | A | `confidenceFor` returns low/medium/high. | `ConfidenceFor`; alias is high, generated/unknown low. | ALIGNED | Confidence remains structural metadata. | All golden snapshots |
| 22 | Candidate trace | A | `ExcelAnalysisTrace` / `ExcelFieldDecisionTrace`, top three candidates. | `SupplierImportDetectionTrace` / `SupplierImportFieldDecisionTrace`; WPF `LogDetectionTrace`. | ALIGNED | At most 17 fields × 3 candidates × 4 reason codes; no cell values. | `DiagnosticTrace_IsBoundedAndContainsNoWorksheetValues`; wizard gate |
| 23 | Required-column generation | A | `ensureColumn` creates barcode, productName, purchasePrice. | `EnsureRequiredColumns` creates the same keys. | ALIGNED | Generated barcode still blocks apply until manually mapped/corrected. | Ambiguous and incomplete cases |
| 24 | Empty-column removal | A | `pruneTotallyEmptyColumns`. | `DropEmptyColumns`. | ALIGNED | Existing merged-cell behavior remains covered separately. | PR #85 reader parity tests |
| 25 | Summary/footer exclusion | A | `isSummaryRow` also recognizes shifted aggregate patterns. | `FilterSummaryRows` preserves the proven PR #85 token/identity heuristic. | ANDROID_AHEAD | Shifted-aggregate expansion was not mixed into this four-capability port. | Summary golden case; existing footer tests |
| 26 | Localized numeric parsing | A | `parseNumber` / `parseAnalysisNumber`. | `SupplierImportAnalyzer.ParseNumber`. | ALIGNED | `1.234,56`, `1,234.56`, `1234,56`, `1234` remain stable. | Locale numeric golden case |
| 27 | Leading-zero barcodes | A | `readPoiRows` uses zero-padding formats. | Streaming reader/ClosedXML fallback preserves formatted cell text. | ALIGNED | Reader strategy is unchanged. | Leading-zero golden and PR #85 reader tests |
| 28 | Duplicate barcode last-wins | A | `ImportAnalyzer.analyzeStreamingDeferredRelations` replaces `Pending.lastRow`. | `SupplierImportAnalyzer.Analyze` replaces `PendingRow.Values`. | ALIGNED | Quantity is not summed. | Duplicate golden and analyzer test |
| 29 | Original source row numbers | A | Canonical `rowNumber` or producer index flows to errors/warnings. | `SupplierExcelRow.RowNumber` flows through analysis and preview. | ALIGNED | Multi-row headers do not renumber data. | Metadata/two-row and duplicate cases |
| 30 | Missing barcode behavior | A | `validateRow` produces a blocking row error. | Step 3 retains an editable warning row; `BuildSyncPreview` blocks until barcode is fixed or skipped. | WIN7POS_AHEAD | Four-step WPF repair path is intentionally richer. | Missing-barcode PR #85 corpus; apply selftest |
| 31 | Missing new-product identity | A | `validateRow` requires item, product name, or second name. | `Analyze` warns; `ValidateFinalRow` blocks before apply. | ALIGNED | Win7 defers the blocker to the editable step. | Analyzer/selftest coverage |
| 32 | Missing retail price | A | New products require positive retail price. | New products block in `ValidateFinalRow`; bulk helper is explicit. | ALIGNED | Purchase price never silently fills retail price. | Analyzer test; apply selftest |
| 33 | `realQuantity` precedence | B | `ImportAnalyzer.analyzeStreamingDeferredRelations`: realQuantity, then quantity. | Existing `BuildEditableRow`: realQuantity, then quantity. | ALIGNED | Existing shared behavior retained; not newly expanded. | RealQuantity-plus-quantity golden case |
| 34 | Discount/discountedPrice | B | `ImportAnalyzer.analyzeStreamingDeferredRelations` validates discount and derives final purchase price. | Headers are recognized, but Win7 catalog editable/apply rows keep canonical purchasePrice. | ANDROID_AHEAD | Receiving discount semantics intentionally excluded. | Receiving-only golden case |
| 35 | Old prices | B | Import analyzer maps old purchase/retail fields into Android product history state. | Headers are recognized; Win7 apply derives price history from DB/current edits, not Android old-price columns. | ANDROID_AHEAD | Android receiving history semantics intentionally excluded. | Receiving-only golden case; apply selftest |
| 36 | Complete state | B | `ExcelViewModel.completeStates`, history and export methods. | `complete` is not auto-mapped into Win7 catalog rows. | PLATFORM_SPECIFIC | No WPF receiving-state UI added. | Receiving-only golden case |
| 37 | Manual mapping overrides | A | `ExcelViewModel.setHeaderType` / `restoreOriginalHeader`. | `SupplierImportAnalyzer.ApplyColumnOverrides`; Step 2 column enable/key controls. | ALIGNED | Ambiguous fields are intentionally handed to manual mapping. | UI selftest; wizard gate |
| 38 | Final preview fingerprint | A | `ImportApplyDiagnostics.importFingerprintShort` is apply diagnostics, not a row-complete preview authorization token. | `BuildSyncFingerprint`; preview fingerprint is rechecked by the workflow. | WIN7POS_AHEAD | Preserve existing stale-preview protection. | Every golden case stores fingerprint; apply tests |
| 39 | Apply-time revalidation | C | `ImportAnalyzer` receives a DB product snapshot, then `InventoryRepository.applyImport` atomically applies its request. | `SupplierExcelImportWorkflowService.ApplyAsync` rebuilds preview and `SupplierExcelImportApplier` rereads within apply. | WIN7POS_AHEAD | The apply loop is deliberately unchanged in this PR. | Supplier apply selftest |
| 40 | Rollback | C | `InventoryRepository.applyImport` wraps `applyImportAtomically` in Room `withTransaction`. | `SupplierExcelImportApplier.ApplyAsync` uses one SQLite transaction and rollback. | ALIGNED | Platform-specific transaction APIs, same atomic outcome. | Android ImportAnalyzer/repository tests; Win apply selftest |
| 41 | Outbox behavior | C | Android marks dirty catalog state and uses Android sync-event mechanisms. | Win7 enqueues `catalog_import_outbox` in the same SQLite transaction. | PLATFORM_SPECIFIC | No cross-port of either sync runtime. | Win catalog outbox gate |
| 42 | Streaming, x86 bounds, cancellation | C | Android uses resource policy and streaming analysis/chunks. | Win7 reader limits, no `AsDataSet`, and 64-row cooperative cancellation remain unchanged. | PLATFORM_SPECIFIC | Win7/net48/x86 constraints govern implementation. | Reader bounds/cancellation tests |
| 43 | File/URI readers | C | Android URI + Apache POI + Jsoup in `readAndAnalyzeExcelDetailed`. | Win7 ExcelDataReader + ClosedXML fallback + internal HTML parser. | PLATFORM_SPECIFIC | No dependency or reader strategy change. | PR #85 XLSX/XLS/HTML parity tests |
| 44 | UI lifecycle | C | Compose/ViewModel coroutine lifecycle, generated receiving screens, history. | Four-step WPF wizard and cancellable Analyze. | PLATFORM_SPECIFIC | No XAML redesign or mobile workflow port. | UI selftest; wizard gate |
| 45 | Existing-product lookup | C | Android analyzer receives `currentDbProducts`; callers may materialize the relevant app snapshot. | `LoadExistingProductsForTableAsync` performs targeted barcode lookup. | WIN7POS_AHEAD | Preserve PR #85 memory/query behavior. | 20k harness; workflow tests |
| 46 | Backup before apply | C | Room apply has transaction rollback but no Win7-style file backup. | `CreateBackupBeforeApplyAsync` runs before authorized apply. | WIN7POS_AHEAD | Win7 operational recovery remains mandatory. | Supplier apply selftest |
| 47 | Price history | A | `applyImportAtomically` records IMPORT/IMPORT_PREV price points. | Supplier applier records `IMPORT` history inside the same transaction. | ALIGNED | Schema-specific details remain platform boundaries. | Android repository tests; Win apply selftest |

## Golden corpus

`tests/fixtures/supplier-import/current-android-parity-corpus.json` contains 16 deterministic cases and expected normalized output for:

1. Chinese + Spanish compound header.
2. Chinese + English compound header.
3. Header split over two rows.
4. Metadata followed by two header rows.
5. Clear headerless data.
6. Ambiguous headerless numeric columns.
7. Quantity versus sequential row number.
8. Purchase price versus multiplication total.
9. Almost-equal barcode candidates.
10. One-row insufficient/ambiguous evidence.
11. Duplicate barcode last-wins.
12. Summary/footer filtering.
13. Leading-zero barcode.
14. Localized numeric formats.
15. realQuantity plus quantity.
16. Receiving-only keys present but excluded from Win7 catalog field semantics.

Each expected record freezes data row index, header presence/mode/rows, merged headers, canonical key/source/confidence per column, selected candidates, rejected ambiguous fields, bounded candidate scores/reasons, source row numbers, dropped summary count, normalized warning codes, final editable rows, and SHA-256 of the Win7 sync-preview fingerprint.
