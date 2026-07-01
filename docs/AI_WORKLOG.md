# AI Worklog – Win7POS

Cronologia sintetica delle sessioni AI. Aggiornare dopo ogni sessione significativa.

---

## 2026-03-17 – Pulizia doc repo
- Spostati report storici in `docs/reports/` (DIALOG_REVERT_REPORT, ANALISI_MIGLIORAMENTI, CANDIDATI_PULIZIA).
- Archiviato piano eseguito in `docs/plans/_archive/dialog-positioning-standardization-plan.md`.
- Creato `docs/AI_WORKLOG.md` (questo file).

## 2026-03 – Dialog standardization (completata)
- Tutti i 25 dialog: `ShowHeader="False"`, footer/title con shared resources (`ModernStyles.xaml`).
- Positioning via `SourceInitialized`; niente `Left`/`Top` custom; clamp in `DialogShellWindow`.
- Owner policy: `DialogOwnerHelper.GetSafeOwner()` ovunque; nested dialog via `OwnerWindow ?? GetSafeOwner()`.
- Script audit: `scripts/check-dialog-standards.ps1`.

## 2026-03 – Fix tecnici core
- N+1 query in `ImportDiffer` → batch `GetByBarcodesAsync`.
- `Thread.Sleep` → `await Task.Delay` in `WindowsSpoolerReceiptPrinter`.
- `FileLogger`: aggiunto `lock(_writeLock)` per scritture concorrenti.
- `DiscountDialog`: rimosso `GetAwaiter().GetResult()` (blocco UI) → callback async.
- SQL injection in `Program.cs` → query parametrizzata con `@saleId`.

## 2026-03-18 – Audit utenti/ruoli, fix P0/P1 + hardening verifiche
- Branch / commit di partenza: `main` @ `8fea4df` (`Bug 4 schermi`).
- Obiettivo: audit end-to-end utenti/ruoli/permessi/testi UI e fix minimi per `RolesManage`, login/lockout, PIN numerico, sconto oltre limite, compat DB.
- File modificati:
  - `src/Win7POS.Wpf/Infrastructure/Security/{IOperatorSession.cs,OperatorSession.cs}`
  - `src/Win7POS.Wpf/MainWindow.xaml.cs`
  - `src/Win7POS.Wpf/Pos/PosViewModel.cs`
  - `src/Win7POS.Wpf/Pos/Dialogs/{OperatorLoginDialog.xaml.cs,ChangePinDialog.xaml.cs,NewUserDialog.xaml.cs,FirstRunSetupDialog.xaml.cs,DiscountDialog.xaml.cs,DiscountViewModel.cs,UserManagementViewModel.cs,UserManagementDialog.xaml}`
  - `src/Win7POS.Wpf/Pos/UserManagementView.xaml`
  - `src/Win7POS.Data/DbInitializer.cs`
  - `scripts/check-dialog-standards.ps1`
- Fix applicati:
  - `RolesManage` enforced nei command/metodi ruolo, UI ruolo nascosta/read-only senza permesso, save coerente con `PermissionEditMode`.
  - invalidazione/recreazione sicura di `UserManagementViewModel` su cambio operatore per evitare VM/permessi stale.
  - `LoginAsync` ora distingue `Success/Failed/LockedOut`; UI login mostra lockout esplicito.
  - validazione PIN solo numerica in create/change/reset + testi PIN uniformati.
  - `MaxDiscountPercent` applicato davvero; override richiesto su `pos.discount_over_limit`; dialog sconto resta aperto se override negato.
  - `EnsureMigrations()` esteso alle colonne security mancanti in `users`.
  - fix allo script `check-dialog-standards.ps1`: matcher case-sensitive + copertura `SetCurrentValue(Window.LeftProperty/TopProperty, ...)`.
- Verifiche eseguite:
  - `pwsh -File scripts/check-dialog-standards.ps1` → `ALL PASS`.
  - `dotnet build src/Win7POS.Core/Win7POS.Core.csproj --no-restore` → PASS.
  - `dotnet build src/Win7POS.Data/Win7POS.Data.csproj --no-restore` → PASS.
  - grep/code pass mirato su call-site login, Users/Roles, discount, migrazioni DB.
- Verifiche NON eseguite perché bloccate dall'ambiente:
  - build WPF `net48`/x86 reale su Windows non eseguibile da host macOS.
  - smoke test manuali WPF `Users/Roles` e `Discount` non eseguibili senza UI Windows.
  - test con DB vecchio/backup reale in app Windows non eseguibile qui.
- Prossimi passi su Windows:
  - eseguire `dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86`
  - rieseguire `pwsh -File scripts/check-dialog-standards.ps1`
  - fare smoke test manuali: tab/dialog `Users/Roles`, cambio operatore, sconto oltre limite (override negato/concesso), DB vecchio/backup reale con login/lockout/max_discount_percent

## 2026-03-18 – Debug e standardizzazione dialog ruoli / accesso operatore
- Data: `2026-03-18`.
- Obiettivo: correggere i bug UX di `RoleEditDialog` e `OperatorLoginDialog`, ridurre il drift dallo standard dialog e chiudere i problemi su titolo, layout codice ruolo, collisioni in duplica ruolo e focus PIN.
- File modificati:
  - `src/Win7POS.Wpf/Pos/Dialogs/RoleEditDialog.xaml`
  - `src/Win7POS.Wpf/Pos/Dialogs/RoleEditDialog.xaml.cs`
  - `src/Win7POS.Wpf/Pos/Dialogs/UserManagementViewModel.cs`
  - `src/Win7POS.Wpf/Pos/Dialogs/OperatorLoginDialog.xaml`
  - `src/Win7POS.Wpf/Pos/Dialogs/OperatorLoginDialog.xaml.cs`
- Fix applicati:
  - `RoleEditDialog`: aggiunti `TitleText` dinamico e `Grid.ColumnSpan="2"` su `CodeRow`; `Title` fallback normalizzato a `Ruolo`.
  - `RoleEditDialog`: focus iniziale per `Nuovo ruolo` / `Duplica ruolo` / `Rinomina ruolo`; guard `_submitted` contro doppio submit; callback `ValidateCode` con ritorno focus al campo corretto dopo popup di validazione.
  - `UserManagementViewModel`: introdotto `GenerateUniqueRoleCodeAsync` con schema `_copia`, `_2.._99` e fallback raro `DateTime.Now.Ticks`; validazione sincrona del codice duplicato in `NewRoleAsync` e `DuplicateRoleAsync`.
  - `UserManagementViewModel`: sanitizzati gli errori `UNIQUE` per evitare messaggi SQLite grezzi nella status bar.
  - `OperatorLoginDialog`: `SelectionChanged` sulla combo operatore per spostare il focus su `PinBox`; con un solo operatore il dialog apre direttamente con focus sul PIN.
  - Decisioni UX esplicite del batch:
  - `SelectedRole` resta volutamente non preservato dopo `New` / `Duplicate` / `Rename`; nessuna auto-selezione introdotta in questo batch.
  - il nome ruolo viene `Trim()`mato prima del salvataggio; nome vuoto o solo spazi resta bloccato; nomi duplicati con codici diversi restano consentiti.
- Verifiche eseguite:
  - `pwsh -File scripts/check-dialog-standards.ps1` → `ALL PASS`.
  - review del diff sui file toccati e conferma wiring `Owner`, titolo dinamico, validazione codice e focus dialog.
  - spot-check di `src/Win7POS.Wpf/Products/DeleteProductConfirmDialog.xaml[.cs]` e call-site: nessuna patch necessaria in questo batch.
- Verifiche non concluse:
  - `dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86` avviato su host macOS, ma rimasto senza esito conclusivo in questo ambiente.
  - smoke test manuali WPF non eseguibili qui: aperture dialog, caret visibile reale, focus di ritorno dopo chiusura `ModernMessageDialog`, duplica ruolo 2a/3a volta, accesso operatore mouse/tastiera.
- Prossimi passi su Windows:
  - eseguire `dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86`
  - rieseguire `pwsh -File scripts/check-dialog-standards.ps1`
  - fare smoke test `RoleEditDialog`: `Nuovo`, `Duplica`, `Rinomina`, focus iniziale, caret visibile e ritorno focus reale dopo popup di validazione
  - verificare duplica ruolo ripetuta: `*_copia`, `*_copia_2`, `*_copia_3`, e fallback oltre `_99` solo come safety net rara
  - fare smoke test `OperatorLoginDialog`: focus su combo con N>1 operatori, focus diretto su `PinBox` con N=1, e refocus sul PIN dopo selezione o login fallito

## 2026-03-18 – Import/Export Prodotti: audit completo + patch minima
- Data: `2026-03-18`.
- Obiettivo: correggere i bug di normalizzazione supplier/category nell'import Excel multi-sheet, rafforzare diagnostica e summary, garantire round-trip export XLSX -> reimport, migliorare il feedback UX della schermata Prodotti e allineare la policy `PriceHistory` orphan/backup al workflow WPF reale.
- File modificati:
  - `src/Win7POS.Core/ImportDb/ProductDbWorkbook.cs`
  - `src/Win7POS.Core/ImportDb/ProductDbExcelReader.cs`
  - `src/Win7POS.Core/ImportDb/ProductDbAnalysis.cs`
  - `src/Win7POS.Data/Import/CategorySupplierResolver.cs`
  - `src/Win7POS.Data/ImportDb/ProductDbImporter.cs`
  - `src/Win7POS.Core/Import/ImportApplyResult.cs`
  - `src/Win7POS.Wpf/Import/ImportWorkflowService.cs`
  - `src/Win7POS.Wpf/Import/ImportViewModel.cs`
  - `src/Win7POS.Wpf/Import/ProductDbImportViewModel.cs`
  - `src/Win7POS.Wpf/Products/ProductsViewModel.cs`
  - `src/Win7POS.Wpf/Products/ExportDataDialog.xaml`
- Fix applicati:
  - reader Excel: introdotta normalizzazione `trim + collapse whitespace` per supplier/category e aggiunti alias header inglesi del writer (`Name2`, `PurchasePrice`, `RetailPrice`, `SupplierName`, `CategoryName`, `StockQty`) per rendere robusto il reimport XLSX anche senza fallback posizionale.
  - workbook analysis: aggiunti conteggi e warning classificati per duplicati supplier/category, righe inutilizzate/non risolte, `PriceHistory` orphan e cap a 10 warning dettagliati; tracciato anche se i fogli dedicati sono assenti o presenti ma funzionalmente vuoti/sporchi.
  - importer legacy DB: lookup supplier/category allineati a `CategorySupplierResolver.Normalize()` e `PriceHistorySkipped` non piu` silenzioso.
  - resolver/import apply: aggiunti contatori `fromSheet/fromDb/created` per supplier/category e propagati nel risultato apply.
  - workflow import WPF: `LoadParseResultAsync` ora wrappa la lettura Excel in `Task.Run`, conserva `PriceHistoryRows`, separa chiaramente analysis summary vs apply summary, importa `PriceHistory` con policy `keep with warning`, e rende il backup pre-apply best-effort invece che bloccante.
  - overwrite per ID storico: documentata la precedenza dei fogli dedicati e aggiunto summary apply per i casi in cui `INSERT OR REPLACE` sovrascrive un nome supplier/category esistente sullo stesso ID.
  - Products UI: aggiunti `StatusMessage` espliciti per apertura/export annullato/progresso/refresh post-import, errori export distinti per `IOException` e `UnauthorizedAccessException`, e reset di `SelectedProduct` durante `SearchAsync`.
  - dialog export: label CSV chiarita a `CSV (solo prodotti)` e footer/title riallineati agli shared dialog resources.
  - scope boundary esplicitato nel codice: deduplica/migrazione dei duplicati storici gia` presenti nel DB e retention automatica dei backup restano fuori scope di questo batch.
- Verifiche eseguite:
  - `dotnet build src/Win7POS.Core/Win7POS.Core.csproj -c Release` -> PASS.
  - `dotnet build src/Win7POS.Data/Win7POS.Data.csproj -c Release` -> PASS.
  - `pwsh -File scripts/check-dialog-standards.ps1` -> `ALL PASS`.
  - `git diff --check` -> nessun whitespace/error di patch.
  - review mirata dei diff e dei call-site del workflow import/apply/export prodotti.
- Verifiche non concluse:
  - `dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86` non concluso su host macOS/sandbox: il target `net48` WindowsDesktop resta bloccato senza output finale in questo ambiente.
  - smoke test manuali WPF non eseguibili qui: import XLSX con 4 fogli, fogli dedicati vuoti/sporchi, duplicati supplier/category, `PriceHistory` orphan, export XLSX -> reimport, export CSV solo prodotti.
  - verifica DB reale su Windows non eseguibile qui: summary apply, overwrite per stesso ID, backup creato/non creato, refresh catalogo e combo dopo import.
- Prossimi passi su Windows:
  - eseguire `dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86`.
  - rieseguire `pwsh -File scripts/check-dialog-standards.ps1`.
  - fare smoke test della schermata Prodotti: export annullato, export file aperto in Excel, export CSV/XLSX riuscito, refresh catalogo e reset `SelectedProduct`.
  - eseguire i test manuali del batch import: workbook 4 fogli completo, foglio `Suppliers`/`Categories` assente, foglio presente ma solo header o righe sporche, duplicati sporchi supplier/category, `PriceHistory` orphan mantenuto con warning, round-trip export XLSX -> reimport.
  - validare su DB Windows reale il summary apply: `Products(...)`, `Suppliers(fromSheet/fromDb/created)`, `Categories(fromSheet/fromDb/created)`, `PriceHistory(inserted/skipped)` e `Dedicated sheet overwrites(...)`.
  - follow-up fuori scope: valutare una migrazione separata per duplicati storici supplier/category nel DB e una retention automatica della cartella backup.

## 2026-07-01 – Win7POS full audit Mac + hardening CI/check
- Branch / commit di partenza: `audit/win7pos-full-hardening` @ `6a21f0a`; branch creata da `main` per non continuare su main con worktree sporco.
- Obiettivo: audit completo Mac per build, CI, script statici, sicurezza/log, SQLite/outbox/restore, online/Admin Web boundary, dialog WPF e handoff Windows.
- Nota worktree: esistevano gia modifiche non committate su README, online/bootstrap/sync/WPF e nuovi script prima delle patch di questa sessione; preservate senza revert.
- Fix applicati:
  - workflow GitHub `ci`, `wpf-build`, `release-pack`: SDK .NET aggiornato da `8.0.x` a `10.0.x` per allineare `Win7POS.Cli` target `net10.0`.
  - `PosSyncStatusReader`: outbox summary usa label `sync.blocked` (`Bloccate`) invece di `sync.blockedAttention`.
  - `PosLocalization`: copy IT carta over-balance aggiornata a `La carta non può superare il saldo da pagare...`.
  - `check-pos-sync-status-ux.ps1`, `check-pos-shop-data-readonly.ps1`, `check-pos-revenue-copy.ps1`: resi i18n-aware leggendo le traduzioni oltre a XAML/code-behind.
  - creati `docs/reports/2026-07-01_WIN7POS_FULL_AUDIT.md` e `docs/reports/2026-07-01_CODEX_ASUS_WINDOWS_QA_TASK.md`.
- Verifiche eseguite:
  - `git diff --check` -> PASS.
  - `dotnet restore src/Win7POS.Cli/Win7POS.Cli.csproj` -> PASS.
  - `dotnet build` Core/Data/CLI Release -> PASS.
  - `dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86` -> PASS su Mac SDK `10.0.301`.
  - CLI selftest con `WIN7POS_DATA_DIR=/tmp/win7pos-codex-selftest` -> `自检 PASS`.
  - script statici dialog, staging config, debug logging, online client/bootstrap, sale-safe UI, start-of-day, catalog pull, sales sync, restore guard, legacy DB migrations, no eager DB startup, startup Win7-safe, sync UX, shop readonly, revenue copy e product free-text -> ALL PASS.
  - `check-release-pack-completeness -ReleasePackSource dist/Win7POS` -> PASS su drop preesistente.
- Non eseguiti:
  - `systeminfo`, smoke WPF reale, Windows 7 runtime/hardware, stampante, DPI/multi-monitor e installer Inno Setup fresco: richiedono Windows/ASUS.
- Prossima fase: REVIEW + esecuzione `docs/reports/2026-07-01_CODEX_ASUS_WINDOWS_QA_TASK.md`; nessun commit effettuato.

## 2026-07-01 – Win7POS missing tasks closure Mac
- Branch / commit: `audit/win7pos-full-hardening` @ `6a21f0a`; worktree ancora non pulito e non ripulito.
- Obiettivo: chiudere i missing tasks del full audit senza dichiarare smoke Windows/hardware non eseguiti.
- Stato/diff:
  - `git status --short`, `git diff --stat`, `git diff --name-status` e `git diff --check` rieseguiti.
  - Diff review separata in `docs/reports/2026-07-01_WIN7POS_MISSING_TASKS_CLOSURE.md`: batch audit, preesistenti/non attribuiti, file toccati ora.
- Fix/documentazione applicati:
  - creato `docs/reports/2026-07-01_WIN7POS_MISSING_TASKS_CLOSURE.md`.
  - aggiornato `docs/reports/2026-07-01_CODEX_ASUS_WINDOWS_QA_TASK.md` con prompt operativo Windows/ASUS completo.
  - aggiornato `docs/reports/2026-07-01_WIN7POS_FULL_AUDIT.md` con addendum closure.
  - `.github/workflows/release-pack.yml`: release pack ora rimuove `*.pdb` dopo il copy in `dist/Win7POS`.
  - `scripts/win7pos/windows/build-release-x86.ps1`: drop Windows builder ora rimuove `*.pdb` dopo il copy output.
- Verifiche rieseguite:
  - `git diff --check` -> PASS.
  - `dotnet --info` -> SDK `10.0.301`.
  - `dotnet restore` CLI/WPF -> PASS.
  - `dotnet build` Core/Data/CLI Release -> PASS.
  - `dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86` -> PASS, 0 warning/errori.
  - `WIN7POS_DATA_DIR=/tmp/win7pos-codex-missing-selftest-current dotnet run --project src/Win7POS.Cli/Win7POS.Cli.csproj -c Release -- --selftest --keepdb` -> `自检 PASS`.
  - script statici dialog, public staging config, debug logging, online client/bootstrap, first-login sale-safe, start-of-day, catalog pull, sales sync, restore guard, legacy DB migrations, startup Win7-safe, product free text, sync UX, shop readonly, revenue copy -> ALL PASS.
  - PowerShell parse di `scripts/win7pos/windows/build-release-x86.ps1` -> PASS.
- Release pack fresco:
  - cartella: `/tmp/win7pos-missing-closure-current-20260701_135330/Win7POS`.
  - zip: `/tmp/win7pos-missing-closure-current-20260701_135330/Win7POS_missing_closure_current_20260701_135330.zip`.
  - `check-release-pack-completeness` su cartella e zip -> ALL PASS.
  - sweep manuale: rimossi `*.pdb`; nessun sorgente/script/config locale/dev file trovato; match residui solo copy PIN/password, `publicKeyToken` e istruzioni per tenere unset il flag HTTP LAN.
- Non eseguiti:
  - ASUS/Windows smoke reale: `ASUS_NOT_RUN`, task pronto.
  - Installer Inno Setup: `iscc` non disponibile su Mac; richiede Windows/ASUS o CI release workflow.
  - CI GitHub: non lanciata da questa sessione.
- Prossima fase consigliata: nuovo giro ASUS/Windows, poi review umana prima di staging/commit; nessun commit effettuato.
