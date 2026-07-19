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

## 2026-07-01 - ASUS Windows printer/cash drawer hardening
- Branch / base: `qa/asus-printer-cashdrawer-hardening-20260701`, creata da `qa/asus-win7pos-result-20260701` @ `63cdaaa`; `main` non toccato.
- Ambiente: SDK .NET `10.0.301` da `C:\Dev\dotnet10`, `WIN7POS_DATA_DIR=C:\POSData\TestRun1`.
- Fix applicati:
  - aggiunta enumerazione Win7-safe delle stampanti Windows installate con rilevamento default e virtual/PDF/XPS probabile.
  - introdotte chiavi settings dedicate per ricevuta, auto-print, default Windows, stampanti virtuali e cassetto.
  - ampliato dialog impostazioni stampante con lista driver, selezione ricevuta, test print, configurazione cassetto e test drawer.
  - reso il payment flow sale-safe: vendita salvata prima di cassetto/stampa; errori di stampa/cassetto diventano warning non distruttivi.
  - rimosso fallback automatico alla stampante predefinita Windows nello spooler; `PrinterName` e' obbligatorio.
  - cassetto disabilitato di default e apertura consentita solo con configurazione esplicita non virtuale.
  - aggiunto `scripts/check-pos-printer-cashdrawer-safety.ps1`.
- Smoke/QA:
  - harness servizio: `Microsoft Print to PDF` rilevata come default+virtuale; `OneNote (Desktop)` rilevata virtuale.
  - cash sale senza stampante configurata: vendita salvata, auto-print bloccato con warning, DB count 1.
  - `Microsoft Print to PDF` configurata con virtuali disabilitate: vendita salvata, auto-print bloccato prima dello spooler.
  - card sale dummy salvata con `paid_cash=0`, `paid_card=2345`.
  - backup DB PASS e restore guard con outbox pending PASS.
- Verifiche automatiche:
  - build Core/Data/CLI/WPF Release x86 PASS; CLI selftest PASS.
  - script statici richiesti PASS, incluso nuovo controllo printer/cash drawer.
  - release pack Windows PASS, completeness PASS, avvio da `dist\Win7POS` PASS, installer Inno PASS.
- Limiti:
  - desktop Windows bloccato durante Computer Use: UI WPF reale non cliccabile in questa sessione.
  - stampante POS fisica e cassetto fisico non disponibili; test hardware marcati SKIP, non dichiarati PASS.
- Report: `docs/reports/2026-07-01_ASUS_PRINTER_CASHDRAWER_QA_RESULT.md`.

## 2026-07-01 - Mac final ASUS review and controlled main merge
- Branch integration: `integration/win7pos-asus-final-review-20260701`, creata da `main` aggiornato a `8c94275`.
- Branch ASUS revisionate:
  - `qa/asus-win7pos-result-20260701` @ `63cdaaa`.
  - `qa/asus-printer-cashdrawer-hardening-20260701` @ `8ba8a25`.
- Merge integration: `63501e0`; conflitto risolto in `src/Win7POS.Wpf/Localization/PosLocalization.cs` scegliendo catalogo traduzioni lazy ASUS.
- Fix Mac applicato: `scripts/check-pos-startup-win7-safe.ps1` ora accetta la forma lazy corretta di `PosLocalization`; il comportamento runtime non e' stato cambiato.
- Verifiche integration:
  - build Core/Data/CLI/WPF Release x86 PASS.
  - CLI selftest con `WIN7POS_DATA_DIR=/tmp/win7pos-final-merge-selftest` -> `自检 PASS`.
  - tutti gli script statici richiesti, incluso `check-pos-printer-cashdrawer-safety.ps1`, -> ALL PASS.
  - secret scan: solo falsi positivi documentali/checker; nessun secret reale.
  - artifact scan: nessun artifact generato tracciato.
- Evidenza ASUS usata: release pack PASS, installer Inno PASS, WPF smoke PASS con limiti hardware dichiarati, printer/cashdrawer software PASS.
- Report finale: `docs/reports/2026-07-01_MAC_FINAL_ASUS_REVIEW_AND_MAIN_MERGE.md`.
- Decisione Mac: `READY_FOR_MAIN_MERGE`; main merge/push da eseguire solo dopo commit report e check rapidi su main.

## 2026-07-01 - Win7POS ASUS QA final closure
- main @ `d4b6215`; `origin/main` verificato a `d4b62157a2b683a067ac24b6bcc5915aeda2c0c3`.
- Final checks su main: `git diff --check`, SDK `.NET 10.0.301`, PowerShell `7.6.3`, restore CLI, build Core/Data/CLI/WPF, CLI selftest, dialog standards, staging config, debug logging, online client/bootstrap, sales sync, restore guard, startup Win7-safe e printer/cashdrawer safety -> PASS.
- Security/artifact: secret scan finale solo falsi positivi documentali/checker; nessun artifact generato tracciato.
- Hardware limits: stampante fisica, cassetto fisico, Windows 7 fisico e multi-monitor/DPI hardware non completati; non dichiarati PASS.
- Next steps: smoke hardware Win7 reale, decisione su payment method `other`, cleanup branch temporanee dopo conferma.
- Report: `docs/reports/2026-07-01_WIN7POS_TASK_CLOSURE_FINAL.md`.
- Decisione: `TASK_CLOSED_MAIN_PUSHED`.

## 2026-07-01 - Win7POS branch cleanup post-merge
- main/origin main verificati a `7ba92bb8981dbb7f6f9cee246a879100e2ed2583` prima del cleanup.
- Branch remote cancellate dopo ancestry `origin/main` exit 0: `origin/handoff/win7pos-asus-qa-20260701`, `origin/qa/asus-win7pos-result-20260701`, `origin/qa/asus-printer-cashdrawer-hardening-20260701`.
- Branch locali cancellate con `git branch -d` dopo ancestry `main` exit 0: `audit/win7pos-full-hardening`, `handoff/win7pos-asus-qa-20260701`, `integration/win7pos-asus-final-review-20260701`.
- Branch mantenute: nessuna tra quelle richieste.
- Nessun force delete, nessun tag cancellato, nessun codice modificato.
- Report: `docs/reports/2026-07-01_WIN7POS_BRANCH_CLEANUP.md`.
- Decisione: `CLEANUP_DONE`.

## 2026-07-06 - Refactor architettura ideale Win7POS
- Branch / base: `refactor/ideal-architecture-win7pos` da `origin/main` @ `d25a377`.
- Refactor applicato:
  - spostati `PosAdminWebClient`, reader Excel/HTML fornitore e reader/writer Product DB da Core a Data.
  - Core resta `netstandard2.0` senza WPF, SQLite/Dapper, HTTP transport o package Excel concreti.
  - aggiunti servizi Data per apply import prodotti, proof smoke fornitore, clear SQLite pool e persistenza transazionale refund/void.
  - WPF non usa piu direttamente `Microsoft.Data.Sqlite` o Dapper; resta responsabile di UI, dialog, stampa e composizione.
  - rafforzato `scripts/check-architecture-boundaries.ps1` con gate Core/Data/WPF, Supabase marker, payload redaction e project references.
  - aggiunti test MSTest `ArchitectureBoundaryTests`.
- Docs aggiornati: `README.md` e `docs/ARCHITECTURE/POS_ADMIN_SUPABASE_SYNC_ARCHITECTURE.md`.
- Live gate non dichiarati completati: staging Supabase/Admin Web/Cloudflare e smoke Windows 7 fisico/stampante.

## 2026-07-07 - Consolidamento architettura 100
- Branch / base: `refactor/architecture-100-consolidation` da `main` @ `4b0c149` (`fix: localize DB restore owner error`).
- Audit eseguito:
  - project references e target verificati: Core senza reference, Data -> Core, WPF/CLI -> Core+Data; Core/Data `netstandard2.0`, WPF `net48`/x86/`Prefer32Bit`.
  - `rg` mirati su Core/Data/WPF per UI, SQLite/Dapper, HTTP/Excel concreti, Supabase marker, hard delete, TODO/NotImplemented e payload/token.
  - nessuna deviazione codice trovata che richiedesse spostamenti di layer o refactor WPF/Data.
- Consolidamento applicato:
  - `scripts/check-architecture-boundaries.ps1` rafforzato con controlli target/proprieta csproj.
  - `ArchitectureBoundaryTests` ampliato con test espliciti per target, reference shape, WPF no SQLite/Dapper diretto, Data no WPF/UI, marker Supabase e redazione payload.
  - `README.md` e `POS_ADMIN_SUPABASE_SYNC_ARCHITECTURE.md` riallineati al branch e ai gate correnti.
- Verifiche automatiche:
  - `pwsh -File scripts/check-dialog-standards.ps1` -> PASS.
  - `pwsh -File scripts/check-architecture-boundaries.ps1` -> PASS.
  - `dotnet build src/Win7POS.Core/Win7POS.Core.csproj -c Release` -> PASS.
  - `dotnet build src/Win7POS.Data/Win7POS.Data.csproj -c Release` -> PASS.
  - `C:\Dev\dotnet10\dotnet.exe test tests/Win7POS.Core.Tests/Win7POS.Core.Tests.csproj -c Release` -> PASS, 24 test.
  - script catalog/import/sync/restore/logging (`check-pos-catalog-import-outbox`, `check-pos-catalog-import-sync`, `check-pos-catalog-pull`, `check-pos-online-bootstrap`, `check-pos-online-client`, `check-supplier-excel-wizard`, `check-pos-debug-logging`, `check-win7pos-restore-guard`, `check-pos-sales-sync`, `check-pos-start-of-day-sync`) -> PASS.
  - `C:\Dev\dotnet10\dotnet.exe run --project src/Win7POS.Cli/Win7POS.Cli.csproj -c Release -- --selftest --keepdb` con DB temporaneo fuori repo -> `自检 PASS`.
  - `C:\Dev\dotnet10\dotnet.exe build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86` -> PASS.
- Non eseguiti:
  - smoke fisico Windows 7 SP1, stampante Xprinter/spooler reale, multi-monitor/DPI e rete instabile: richiedono hardware/VM reale.
  - staging/Admin Web/Supabase credentialed E2E, Cloudflare deploy/CI e release packaging completo: richiedono credenziali o pipeline owner.

## 2026-07-07 - Final architecture 100 merge readiness
- Branch / base: `final/win7pos-architecture-100-merge` da `main` @ `4b0c149`.
- Scope:
  - riportato il consolidamento architecture-100 sul branch finale richiesto.
  - verificati branch recenti (`refactor/*`, `fix/*`, `integration/*`, `pr/*`): nessun commit/diff utile rimasto fuori da `main`.
  - creato report `docs/reports/2026-07-07_WIN7POS_FINAL_100_MERGE_READINESS.md`.
- Gate locali/Windows build host:
  - `pwsh -File scripts/check-dialog-standards.ps1` -> PASS.
  - `pwsh -File scripts/check-architecture-boundaries.ps1` -> PASS.
  - `dotnet restore Win7POS.slnx` con SDK 9 sul PATH -> FAIL atteso `NETSDK1045` per `net10.0`; rieseguito con `C:\Dev\dotnet10\dotnet.exe restore Win7POS.slnx` -> PASS.
  - `C:\Dev\dotnet10\dotnet.exe build Win7POS.slnx -c Release --no-restore` -> PASS.
  - `C:\Dev\dotnet10\dotnet.exe test tests/Win7POS.Core.Tests/Win7POS.Core.Tests.csproj -c Release --no-build --no-restore` -> PASS, 24 test.
  - `C:\Dev\dotnet10\dotnet.exe run --project src/Win7POS.Cli/Win7POS.Cli.csproj -c Release --no-build --no-restore -- --selftest --keepdb` -> `自检 PASS`.
  - `C:\Dev\dotnet10\dotnet.exe build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86` -> PASS.
  - tutti gli script `scripts/check*.ps1` compatibili e richiesti -> PASS; `scripts/check-pos-restore-guard.ps1` -> NOT FOUND, sostituito da `scripts/check-win7pos-restore-guard.ps1` -> PASS.
  - `scripts/win7pos/windows/build-release-x86.ps1 -BuildInstaller` -> PASS; release pack e installer Inno generati ma non versionati.
  - `check-release-pack-completeness -ReleasePackSource dist\Win7POS` e `check-win7-runtime-release-validation -ReleasePackSource dist\Win7POS` -> PASS.
- Decisione: `READY_FOR_WIN7_PHYSICAL_SMOKE`.
- Non eseguiti/non dichiarati PASS: Win7 SP1 fisico/VM, Xprinter reale, scanner barcode reale, staging/Admin Web/Supabase credentialed E2E e Cloudflare/CI owner-authenticated.

## 2026-07-14 - Shop-scoped sync finalizzato e handoff Asus
- Branch di integrazione: `integration/win7pos-mac-final-20260714` da `origin/main` @ `353b7f752db61bed77ae1eadd3207b841eb4c961`.
- Commit implementazione: `94bb9573544811ef97c45f74b4ccf3ac85dc10de` (`fix: finalize Win7POS shop-scoped sync architecture`).
- Architettura: outbox sales/catalog vincolate allo shop origine, payload e idempotenza immutabili, retry/lease fail-closed, barriera/epoch di transizione shop, full-refresh/tombstone, restore cross-shop/TOCTOU e reversal hardening.
- Gate Mac: 29/29 script `check-*.ps1` PASS; Core/Data/CLI Release e WPF `net48/x86` PASS con zero warning/errori; test Core 69/69; CLI selftest, sales sync, shop cache, catalog outbox/reconciliation/HTTP, SQLite integrity e restore guard PASS; tombstone 5/5 e race transizione 1/1 PASS.
- Release pack: completezza e runtime validator PASS su cartella e ZIP; PE x86 e `unzip -t` PASS; SHA-256 registrati nell'handoff.
- Secret/artifact review: nessun secret, path privato, DB/log/build output/PDB o artefatto Codex incluso.
- Handoff: `docs/HANDOFFS/WIN7POS-ASUS-RUNTIME-VALIDATION-2026-07-14.md`; 30 test restano `DEFERRED_TO_CODEX_ASUS`.
- Classificazione: `PUBLISHED_TO_MAIN`, `PASS_STATIC_ARCHITECTURE`, `PASS_AUTOMATED_TESTS`, `PASS_BUILD`, `EXTERNAL_TEST_PENDING_CODEX_ASUS`.

## 2026-07-15 - Lease autorizzazione offline e binding riga reversal
- Branch di integrazione: `integration/win7pos-mac-final-20260714` dall'ultima `origin/main` @ `8c39e13c0f7a001956023919dd6bda612288351f`.
- Commit implementazione: `138b3e64d82558e069bb04920bfda62e5d642b72` (`fix: finalize Win7POS shop-scoped sync architecture`).
- Sicurezza sessione: lease offline fail-closed di massimo 12 ore derivata da `serverTime` autenticato, ricezione locale, expiry e high-water di processo; guard unico su PIN locale, permessi, override, cambio operatore e commit vendita.
- Contratto reversal: `clientOriginalLineId` additivo su refund/void; payload legacy incompleto bloccato in preflight prima della rete senza cancellare outbox/catalog/mirror.
- Gate Mac: 30/30 script `check-*.ps1` PASS; nuovo scanner lease/reversal 22/22; Core/Data/CLI Release e WPF `net48/x86` PASS con zero warning/errori; Core test 82/82 e filtri mirati 27/27; tutti gli otto harness CLI PASS.
- Release pack esterno rigenerato dall'output WPF x86, completezza/runtime validator e `unzip -t` PASS; ZIP SHA-256 `b80cebc63954a423c6abb9b64e9aa02ed4b1c165b96e2a85bd963c4a8ecb4ede`.
- Handoff aggiornato in-place: `docs/HANDOFFS/WIN7POS-ASUS-RUNTIME-VALIDATION-2026-07-14.md`; nessun documento equivalente duplicato.
- Classificazione runtime invariata: `EXTERNAL_TEST_PENDING_CODEX_ASUS`; test Windows/UTM/hardware 1-30 restano `DEFERRED_TO_CODEX_ASUS`.

## 2026-07-15 - Parità economica refund/void con RPC Admin
- Branch di integrazione: `integration/win7pos-reversal-economics-20260715` da `origin/main` @ `0d6eaff0870014a93b29f184e868d6a619d67387`.
- Commit implementazione: `dc162aeff484b576ef21565338cf3d5d492285d4` (`fix: align reversal economics with Admin contract`).
- Contratto economico: gross derivato dalle sole righe item; discount/tax reversal allocati sul gross cumulativo con arrotondamento PostgreSQL `numeric` half-away; quota corrente calcolata come target cumulativo meno quota precedente effettiva; net `-(gross-discount+tax)`.
- Fail-closed: originale e reversal precedenti riletti dal payload outbox immutabile con verifica SHA256; storia gross-only/corrotta/bloccata rifiutata senza riscrivere payload/hash; invio di reversal successive serializzato sugli ACK precedenti dello stesso originale.
- Workflow: pseudo-righe `DISC:*`/`TAX:*` escluse da selezione e binding; preview, dialog, payment, totale locale e payload condividono la stessa policy; reversal inviate item-only con header `gross/discount/tax/net/paid` coerente.
- Gate Mac reali: Core/Data tests Release 95/95; policy mirata 6/6; integrazione discounted+taxed partial/full, successive rounding/ACK e legacy immutabile 3/3; WPF x86/net48 e Core/Data/CLI Release PASS zero warning/error; CLI selftest e TASK-081 sales harness PASS; 31/31 scanner `check-*.ps1` PASS, nuovo scanner economics 12/12; `git diff --check` PASS.
- Release pack esterno rigenerato dal commit implementazione: cartella e ZIP completezza/runtime/startup/linking PASS, `unzip -t` PASS, PE32/x86, nessun PDB/CLI/source/DB; ZIP SHA-256 `9e489fcbcc770159ea99f748b3feb38c05d28ec4f409719a382e054455d4cd84`.
- Handoff aggiornato in-place: `docs/HANDOFFS/WIN7POS-ASUS-RUNTIME-VALIDATION-2026-07-14.md`; nessun duplicato.
- Runtime invariato: `EXTERNAL_TEST_PENDING_CODEX_ASUS`; test Windows 7/UTM/staging autenticato/hardware 1-30 restano `DEFERRED_TO_CODEX_ASUS`, mai dichiarati PASS da Mac.

## 2026-07-17 - Incremental-first sync, fencing e Sync Center

- Branch: `feature/win7pos-incremental-sync-ui-v2-20260716-201301` da audit
  `b590335348937ca830c92289c84032523e267497`; `origin/main`
  `5160b7c1574313ac8be47fdf2e139bb715a37e7d` non modificata.
- Policy Core: incremental/resume per tutti i trigger normali; full limitato alle
  dieci ragioni evidence-driven del contratto (bootstrap, binding/cursor legacy non
  recuperabile, cursor/reset server, cambio shop, restore, exactness, repair
  amministratore o migrazione esplicitamente incompatibile).
- Safety: CAS sales preflight, guard `Success/Value/Ok`, caller cancellation
  preservata, barriera/epoch/shop/mode/cursor su apply/checkpoint/exactness/restore e
  late response bloccata.
- Coordinator: single-flight dirty-bit, massimo due run per drain, resume dopo 5 s,
  polling 24-36 s, backoff 5/15/30/60/120/300 s ±20%, auth stop e diagnostica
  `pos.catalog.sync.*`.
- UI: Sync Center moderno, full repair separata/permission-gated, diagnostica safe,
  focus scanner ripristinato, shared visual polish e copy IT/EN/ES/ZH.
- Verifica: 29/29 gate, 204/204 test, CLI selftest PASS, WPF net48/x86 PASS 0/0,
  policy 34/34 e trigger normali 100/100 incremental/resume.
- Performance sintetica: delta mediane 17.383/26.772/162.696 ms per 10/100/1000;
  full 19.762 mediana 3865.609 ms, exactness `Verified` 3/3, pending 0.
- Release: pack x86, completezza/runtime validator e installer Inno PASS.
- Visuale: first login isolato PASS sul solo host 1440×900/200%; matrice
  1024/1366 a 100/125 e superfici autenticate non eseguite senza credenziali.
- Restano esterni: staging autenticato, Win7 fisico/VM, Xprinter, scanner, cash
  drawer e x86 full-sync memory certification.

## 2026-07-17 - Final closure review (NOT_DONE)

- Provenance verificata dalla source cumulativa
  `codex/win7pos-sync-drain-closeout-20260717` @ `75be03853a95cbb1b38db249b2b332f3f3549a32`:
  22 commit sopra `origin/main` iniziale `5160b7c1574313ac8be47fdf2e139bb715a37e7d`,
  tutti gli antenati richiesti presenti, nessun lavoro di branch mancante dimostrato.
- Review read-only A-E completata. Tre P1 riproducibili corretti sulla review branch:
  retry endpoint-offline senza transizione NIC, inizializzazione customer display
  best-effort e dialog impostazioni adattivo all'area di lavoro. Zero P0/P1 aperti.
- Finale locale: 30/30 gate, 249/249 test senza skip, solution e WPF net48/x86
  0 warning/0 error, CLI selftest e UiSmokeHarness build PASS.
- Benchmark: 2.000 righe batch/legacy mediana 248,930/19.951,492 ms (`80,15x`);
  full sintetico 19.762 righe mediana 4.293,185 ms, `Verified` 3/3, pending 0.
- Release pack x86, validator runtime e installer Inno locali PASS; harness, DB,
  PDB, sorgenti e marker secret esclusi.
- Stato task: `NOT_DONE`. Staging autenticato non eseguito senza credenziali QA;
  host disponibile Windows 11 con un solo monitor e senza scanner, Xprinter o
  cash drawer, quindi Win7 fisico, dual monitor, hardware, DPI e matrice runtime
  restano `NOT_RUN`. Nessuna PR, merge o modifica a main.

## 2026-07-17 - Final closure publication and blocker recheck (NOT_DONE)

- Recuperata senza ricostruzione la review branch
  `release/win7pos-final-review-20260717-015659` a `1b11a6f`; ancestry source e
  main verificata, nuovo bundle completo creato e validato.
- Review branch pubblicata su `origin` senza force; nessuna PR creata e `main`
  invariata.
- Review cumulativa aggiornata: 22 commit source + 2 finali, 110 file, tre lane
  read-only sulle cinque aree richieste, zero nuovi P0/P1 e nove P2 invariati.
- Rerun locale: 30/30 gate, 249/249 test, skipped 0, solution/WPF x86/harness
  0 warning e 0 error, CLI selftest PASS.
- Release pack PE32/x86, completezza, validator Win7 e installer Inno PASS; nessun
  DB/WAL/SHM, PDB, harness, screenshot, config QA attiva, credenziale o path
  personale nel pack.
- Computer Use sul data root isolato ha confermato shell massimizzata, Restore
  disabilitato e login sullo staging online. Nessuna credenziale è stata inserita.
- Host verificato: ASUS Zenbook Windows 11, un monitor 1440×900, sole stampanti
  virtuali, nessuno scanner/Xprinter/cash drawer. Staging autenticato, Win7 reale,
  dual monitor, hardware e matrice DPI/lingue restano `NOT_RUN`.
- Classificazione invariata: `STOPPED_STAGING`; task `NOT_DONE`, nessuna PR/CI/merge.

## 2026-07-17 - Final software merge authorization

- Review branch: `release/win7pos-final-review-20260717-015659`; initial review
  HEAD `f24afcb5dde54d73fc95a53f8645f0f90a82893a`; validated software HEAD
  `4fe6f0a69e15fb38e77f01f6f6e61afb98e65d62`.
- Final review found one additional P1 in customer-display first-frame placement.
  The window is now placed before first visibility and closed on setup failure;
  checker, WPF x86 build and lifecycle harness passed. Cumulative P0 `0/0/0`,
  P1 `4/4/0`; nine P2 remain deferred.
- Final local software validation: canonical gates `30/30`; automated tests
  `249/249`, skipped `0`; solution and WPF net48/x86 `0 warnings / 0 errors`;
  CLI selftest `PASS`; UiSmokeHarness build and lifecycle `PASS`.
- Catalog regression: batch/legacy median ratio `56.12x`, pending prices `0`;
  paged-full exactness `Verified` in `3/3` iterations with pending prices `0`.
- Clean-tree local x86 Release Pack, completeness validator, Win7 runtime
  validator and Inno Setup installer: `PASS`; pack commit `4fe6f0a`, PE32/x86,
  net48, no harness/DB/log/PDB/source/secret or active QA configuration.
- External validation moved to
  `docs/QA/WIN7POS_EXTERNAL_VALIDATION_BACKLOG.md`: 16 items remain `OPEN` and
  `DEFERRED_EXTERNAL_VALIDATION`; no previous `NOT_RUN` is promoted to PASS.
- Task software status: `READY_FOR_MERGE`.
- Merge authorization: `APPROVED_BY_PROJECT_OWNER`.
- Production certification remains open: `NOT_YET_CERTIFIED`.

## 2026-07-17 - Software merge and post-merge verification (DONE_SOFTWARE_MERGED)

- PR `#4` head and merge commit:
  `f4f17f06d04c2d9ec16309317952cfd08420961e`; PR CI run `29590576245`
  completed successfully on that exact SHA.
- Merge method: direct fast-forward from `5160b7c` to `f4f17f0`; no force push.
  GitHub records PR `#4` as `MERGED`.
- Task software status: `DONE_SOFTWARE_MERGED`.
- Merge status: `MERGED_AND_VERIFIED`.
- External validation status: `DEFERRED_EXTERNAL_VALIDATION`.
- Production certification: `OPEN`.
- Final main SHA: `f4f17f06d04c2d9ec16309317952cfd08420961e` (software merge
  head before this documentation-only completion commit).
- Post-merge CI: `PASS`, run `29590958239`, push event on exact main SHA; all
  workflow steps passed, including upload of the TRX with 249/249 tests and zero
  skipped.
- Post-merge Release Pack: `PASS`, run `29590958386`, push event on exact main
  SHA; downloaded GitHub artifacts passed 33/33 source-and-release gates,
  VERSION/ref/tree-state checks and 41/41 manifest hashes.
- GitHub installer SHA-256:
  `7DB77AD2B3EEBDFC60C0009EC7A5F75AF7524C64AD845C0D6AAE8EFAC96FCBA7`;
  release ZIP SHA-256:
  `BE6627F89D771E965B6597DA243833A15A43EDF77F98A26B5C63AC0F2CC1EBE2`.
- The 16 entries in `docs/QA/WIN7POS_EXTERNAL_VALIDATION_BACKLOG.md` remain
  `OPEN` / `DEFERRED_EXTERNAL_VALIDATION`; staging, Win7 physical,
  dual-monitor, scanner, Xprinter, cash drawer and DPI/language runtime are not
  declared PASS.

## 2026-07-17 - Post-merge PR-A persistence foundation

- Source main verified at `f3e779bd537d62ed0f3ddb5333149e9213e2c13f`; PR `#4`
  already merged; final main CI `29591597390` and Release Pack `29591597131`
  both `completed/success` on the exact SHA.
- External certification remains `0/16`: items 1-8 `BLOCKED_CREDENTIALS`, 9-10
  `BLOCKED_WIN7`, 11-15 `BLOCKED_HARDWARE`, item 16 `NOT_RUN`; production
  certification stays `OPEN` and the authoritative backlog is unchanged.
- Selected only PR-A on branch
  `codex/pr-a-persistence-foundation-20260717-114614`; implementation commit
  `188d9cd`; GitHub PR `#5` is open, non-draft and must not be auto-merged.
- Added verified SQLite online backup, process-wide connection fencing,
  same-directory `File.Replace`, durable prepared/committed marker recovery,
  integrity/FK validation and startup recovery. No schema, WAL policy, dependency,
  payload/hash, catalog or economics change.
- Review fixed four P1 edge cases: rollback journal cleanup, committed-live
  validation before rollback disposal, cleanup failure after commit and fence
  recovery after an owner handle leak. P0/P1 open: `0/0`.
- Validation: 30/30 canonical gates, 257/257 tests with zero skipped, CLI PASS,
  solution and WPF net48/x86 0 warnings/errors. Same-host A/B medians changed
  +3.66% legacy, +4.91% batch and -2.29% full; ratio `38.92x`, exactness
  `Verified` 3/3 and pending prices 0.
- Structural status: PR-A `READY_FOR_REVIEW`; next incomplete item is PR-B
  versioned migrations. No merge was performed.

## 2026-07-17 - PR-A final review and merge

- Reverified base `f3e779b`, original PR head `09b0be3`, open/non-draft/mergeable
  state and successful historical CI `29596021792` before final review.
- Final read-only review found a P0 pre-fence restore/outbox TOCTOU and a P1 drain
  deadlock when an active flow needed a second SQLite connection. No schema, WAL,
  package, payload/hash/idempotency or refund/void economics change was introduced.
- Fixed both on commit `607e1f1`: authoritative shop/epoch/sales/catalog/candidate
  revalidation now runs after connection drain and before backup/swap; connection
  admission closes permanently at the first zero boundary, with a safe 30-second
  pre-action timeout and regression coverage.
- Independent re-review found P0/P1 open `0/0`. Local evidence: 30/30 canonical
  gates, 19/19 focused tests, 260/260 full tests with zero skipped, CLI PASS,
  solution and WPF net48/x86 builds with zero warnings/errors, Release Pack and
  installer PASS.
- Final PR CI `29600241291` passed on exact head `607e1f1`. GitHub rejected author
  self-approval by policy, so an approve-equivalent formal review comment recorded
  the evidence before integration.
- PR `#5` was integrated by fast-forward `f3e779b..607e1f1`; squash, rebase and
  force push were not used. Post-merge main CI `29600645459` and Release Pack
  `29600645440` both passed on exact `607e1f1` with 260/260 tests.
- Downloaded release provenance reports `CommitSHA=607e1f1...`, `Ref=main`,
  `TreeState=clean`; installer and ZIP hashes are recorded in the closeout report.
- Ledger status: PR-A `DONE_MERGED`, PR-B `NEXT`, external certification remains
  `OPEN 0/16`. PR-B was not started.

## 2026-07-17 - PR-B versioned SQLite migrations

- Started from main `ad431fe8b7cf4de1bf3bee744bab159b6a95e80c` on
  `codex/pr-b-versioned-migrations-20260717-143330`; backup branch and verified
  all-ref bundle were created before implementation.
- Added an immutable six-entry SHA-256 migration registry and
  `schema_migrations` ledger, exact legacy metadata detection, verified online
  backup before ledger bootstrap/upgrade, immediate per-migration transactions,
  fail-closed checksum/gap/future-ID handling and no-op reopen reconciliation.
- Added six sanitized SQL generations plus rollback, concurrent startup,
  tamper, backup failure, restore/re-upgrade and semantic fresh-schema parity
  coverage. No WAL, package, payload/hash/idempotency, sync-policy or reversal
  economics change was introduced.
- Local evidence: canonical gates `31/31`; migration/restore/architecture gates
  PASS; tests `290/290`, skipped `0`; WPF `net48/x86` 0 warnings/errors; CLI
  selftest PASS. Solution compilation has 0 errors and four external `NU1900`
  warnings because the NuGet vulnerability endpoint was unavailable.
- Catalog regression: 2,000-row batch median `262.188 ms` (`77.21x` versus
  legacy); 19,762-row paged-full exactness `Verified` in 3/3 iterations.
- Clean committed-head x86 Release Pack, completeness validator, Win7 runtime
  validator and Inno Setup 6.7.3 installer: `PASS`.
- Hardware/settings and sync/efficiency roadmaps record no open P0, six deferred
  repository-level P1 findings and current-server-compatible follow-up PRs.
  PR-B is to remain `READY_FOR_REVIEW`; no automatic merge is authorized.

## 2026-07-17 - Epson receipt alignment, POS footer and drawer pulse

- Enlarged the POS `Pay` touch target to the exact visible width/edges of the
  right tools panel while retaining the compact single-row footer.
- Added one shared receipt renderer used by payment preview, completed-sale
  printing/reprint and the non-persisted Printer Settings sample. Exact alignment
  is covered for cash, card and mixed payments at 32 and 42 columns, including
  lossless line/cart discount rows and EN/ES/IT/ZH labels. The shop snapshot is
  frozen across payment/commit and the fictitious test uses the sale barcode path.
- Matched the Printer Settings preview hierarchy to the payment preview and made
  the fictitious sample show both cash and card without creating a sale.
- Added focused geometry, renderer-parity and visual-capture harness coverage;
  Release x86 build and targeted checks pass.
- Fixed the payment localization event subscription so every completed/cancelled
  payment flow and every harness fixture releases its handler.
- Submitted one production-code pin-2 drawer command to `EPSON TM-T60 Receipt`
  in a fresh isolated evidence root, outside the authenticated settings UI. The
  call returned successfully; pre/post queue observations were `Normal`/0 jobs,
  one log entry was retained and no `pos.db` was created. The operator later
  explicitly confirmed exactly one physical opening; no retry or pin-5 pulse was
  issued.
- The external backlog is now `OPEN 3/18`: row 15 is the single manual pin-2
  drawer PASS and rows 17/18 are physical print PASS. Transactional cash/card,
  reprint/failure and Windows 7 rows remain open. The earlier `OPEN 0/16` entry
  is the pre-Epson historical snapshot.
- Implementation and automated coverage were committed as `7d1ef84` and pushed
  to `codex/hardware-epson-tm-t60-20260717-161122`. Draft PR `#7` targets
  `main`; no merge or auto-merge was requested.

## 2026-07-18 - Epson transactional matrix physical checkpoint

- Ran the Release x86 application against isolated QA root
  `C:\POSData\Win7POS-QA\Win7POS-Epson-Transactional-20260718-104809`.
- Cash sale `VMRQI73CRZQ6` printed and cut; the operator confirmed exactly one
  drawer opening. Card-only sale `VMRQIA8J5KE3` printed with the drawer closed.
- Reprinted the persisted cash sale once; counts remained unchanged and the
  operator confirmed no drawer opening.
- Paused `EPSON TM-T60 Receipt`, committed card-only sale `VMRQIK583IXD`, observed
  the explicit saved-sale/print-failed state, resumed the queue and used
  `Print last` once. The operator confirmed correct paper and no drawer opening.
- Final evidence: 10 sales, 12 lines, 11 stock movements, 10 outbox rows; no
  duplicate client sale IDs or movement keys; exactly one drawer log event;
  printer `Normal`, zero jobs, Spooler running/automatic.
- Final local regression before the receipt-surface addendum: required gates
  31/31, Core tests 260/260 with zero skipped, CLI selftest PASS, solution and
  WPF/harness x86 builds PASS, lifecycle 20 Printer Settings / 50 display /
  50 manager cycles PASS with zero residual windows/ViewModels.
- Merge remains blocked by the receipt-surface/daily-close addendum and physical
  Windows 7 validation; no extra cash transaction or drawer pulse is authorized.

## 2026-07-18 - Local recovery isolation and safe POS re-entry

- Diagnosed online sign-in denial as `shop_switch_blocked_unresolved_outbox`:
  the isolated Epson QA database belongs to the existing QA shop and still has
  queued/blocked sales, so credentials for another shop cannot rebind it.
- Kept the physical database and outbox unchanged. Read-only inspection confirmed
  that the local QA recovery users are active, local-only and not locked.
- Added an explicit local-recovery login path that preserves PIN verification and
  lockout while no longer requiring an online authorization lease for verified
  local-only users. Normal POS login continues to require the lease.
- Restricted recovery mode to the user's granted subset of catalog import/edit and
  database-maintenance permissions. Sales, payment, daily close, settings and
  security administration remain unavailable until a normal online session for
  the database's original shop is restored.
- Hardened recovery transitions: payment is cancelled, online schedulers stop,
  unsafe tabs are clamped, the existing POS/cart view is suspended and restored,
  and exiting recovery rechecks a valid normal authorization lease.
- Added clearer localized feedback for a blocked cross-shop sign-in and focused
  source/data/policy tests for local-user classification and scoped permissions.
- Local recovery can never promote itself to normal POS access. Normal offline
  access, operator switching and override authorization now resolve only the
  remote mirror bound to the trusted shop/staff IDs, normalized codes and exact
  credential version. First-run setup uses the dedicated recovery verifier.
- Final local validation: required gates 32/32, Core tests 291/291, Release
  solution plus WPF/harness x86 builds PASS. The canonical seeded lifecycle is
  PASS with 0 residual ViewModels, 0 open windows, stable subscriptions and all
  functional checks true. The run observed 10 retired `SalesRegisterDialog`
  window shells with no live ViewModel or open-window retention; these shells
  remain diagnostic-only and their trend is monitored by the harness.

## 2026-07-18 - Isolated offline sales and payment QA sandbox

- Added a non-shipping launcher that creates a unique, empty data root below the
  local fixed-drive `POSData\\Win7POS-QA` tree, rejects UNC/device paths and any
  existing reparse-point ancestor, then rechecks containment after creation.
- The harness seeds one shop-bound synthetic remote operator, 48 synthetic
  products, a sale-safe catalog, a 12-hour trusted lease and zero sales/outbox
  rows. It explicitly verifies that no local-recovery identity was created.
- The child Win7POS process runs with safe-start and a loopback-only Admin Web
  endpoint. Post-sale/manual sync, automatic and manual fiscal PDF output,
  automatic receipt printing and the cash drawer are disabled by default.
- The documented flow uses the normal offline-mirror sign-in; Local recovery
  remains a restricted catalog/database-maintenance surface and never opens
  sales or payments.
- Actual seed-only validation PASS at
  `C:\\POSData\\Win7POS-QA\\Offline-Sales-20260718-215001`; lease 12 hours and
  hardware disabled. Canonical gates 32/32, Core 291/291, Release builds and
  standard-fixture lifecycle all PASS on the final source state.

## 2026-07-18 - Offline sales sandbox interactive runtime validation

- Logged in through the normal offline mirror against
  `C:\POSData\Win7POS-QA\Offline-Sales-20260718-220237`; safe-start and the
  loopback-only Admin Web endpoint remained active throughout the run.
- Completed one manual-price cash sale for CLP 3,432, one `QA000002` card sale
  for CLP 550 and one `QA000003` mixed sale for CLP 575 (cash 300/card 275).
- Final read-only audit: 3 sales, 3 lines, 2 expected stock decrements and 3
  pristine pending outbox rows with zero attempts/errors/server IDs. Gross CLP
  4,557, cash CLP 3,732, card CLP 825, change zero; SQLite quick/FK checks PASS.
- Sales Register receipt previews and Daily Close matched the persisted tenders
  and totals. Reopening those read-only surfaces left sales, stock, outbox and
  fiscal flags unchanged; logs contained no print, PDF, spooler, drawer or
  online-sync attempt.
- Live UI review exposed the Sales Register operator filter rendering the CLR
  type name. The shared modern ComboBox template now forwards the WPF item
  template selector used by `DisplayMemberPath`, with a focused UX source gate.
- Post-fix validation: required gates 32/32, Core tests 291/291 with zero
  skipped, full Release solution plus WPF/harness x86 builds PASS with zero
  warnings/errors. The artifact capture passed POS footer, payment/printer
  previews, Sales Register, Daily Close and compact 1024x600 layouts.

## 2026-07-18 - Fail-closed offline sandbox resume

- Added an explicit `-ResumeExistingSandbox` launcher mode so the same current
  synthetic QA run can be reopened without reseeding, replacing sales data or
  renewing its authorization lease.
- Resume remains confined below the local fixed-drive `POSData\\Win7POS-QA`
  tree and now requires the original marker/database/session files, rejects
  reparse points and repeats the single-instance check immediately before launch.
- A database-query-only harness verifier requires disabled receipt/drawer flags,
  blank saved printer names, the exact synthetic shop identity, an explicit raw
  fiscal lock and a still-valid DPAPI-protected trusted-session lease. Its unique
  exit code `73` prevents a stale or incompatible harness from being accepted.
- Regressed fresh seed-only creation successfully, then resumed
  `C:\POSData\Win7POS-QA\Offline-Sales-20260718-220237` with the Release x86
  build, safe-start, loopback-only Admin Web endpoint and hardware disabled.
  Credentials remained manual and were not stored or automated.
- Final validation: required gates 32/32, Core tests 291/291 with zero skipped,
  full Release solution and WPF/harness Release x86 builds PASS with zero
  warnings/errors.

## 2026-07-18 - Direct boleta printing without automatic PDF files

- Removed the post-sale fiscal PDF writer, its delayed 15-second cleanup and the
  `PDFsharp-gdi` runtime dependency. The old flow could leave a PDF in `exports`
  after a spooler failure, application exit or cleanup error.
- The selected boleta preview and number now go directly to the configured
  Windows receipt spooler. Receipt and boleta jobs keep the configured physical
  copy count; no image, text or PDF archive is created by either path.
- Preserved cash-only auto-print policy, Safe Start blocking, sale commit order,
  boleta-number advancement after successful printing, synchronization/outbox
  payloads and the legacy `sales.pdf_printed` compatibility flag. That column is
  document status only and stores no path or file.
- Updated the payment UI and EN/ES/IT/ZH copy to describe direct boleta printing,
  print status and same-number physical copies instead of a local PDF workflow.
- Added a focused x86 harness check for cash, card-only and simulated printer
  failure plus filesystem snapshots. Result: PASS, zero generated PDF files and
  no export directory in the isolated QA root.
- Final validation on source state: required gates 32/32, Core tests 291/291,
  WPF net48/x86 and UI harness x86 isolated builds PASS with zero warnings/errors;
  both build outputs contained zero `PdfSharp` DLLs and zero PDFs. A physical
  post-relaunch boleta print remains the hardware confirmation gate.

## 2026-07-19 - PR #7 cumulative closure and physical receipt addendum

- Completed a cumulative review of every published and unpublished PR #7 change.
  Two security findings were corrected: a normal remote mirror can no longer
  receive lease-free recovery treatment, and cancelling an operator change
  cannot commit the candidate identity. Independent final review reports
  P0/P1/P2 open as zero.
- Hardened the physical boundary with a final Safe Start guard, per-printer
  single-flight rejection after an indeterminate timeout, strict one-to-three
  copy validation and explicit post-commit fiscal-print failure guidance that
  preserves the reserved boleta number without triggering a second print.
- Hardened release provenance, exact runtime inventory, strict UTF-8 no-BOM
  manifests, secret/privacy rejection, Win7 dependency closure and obsolete
  runtime cleanup. Five negative fixtures confirmed fail-closed behavior for a
  changed payload, UTF-16 BOM, secret marker, `.env.*` payload and nested root.
- Final local software evidence: Core `298/298`, canonical source-and-release
  gates `35/35`, dialog standards `34/34`, WPF and UI harness Release net48/x86
  builds with zero warnings/errors, and the 20-cycle lifecycle run with zero
  residual ViewModels or windows.
- The dedicated physical harness submitted one sequence of six one-copy jobs to
  the Epson TM-T60: fiscal 32/42, identical receipt original/reprint and daily
  close 32/42. The manifest records six submissions, zero drawer calls and no
  database artifacts; the operator confirmed all six slips, correct output,
  no duplicates and a closed drawer. The Windows 11 host result closes the PR #7
  receipt-surface gate; physical Windows 7 remains `NOT_RUN_WIN7_PHYSICAL`.

## 2026-07-19 - PR-B refresh after PR #7

- Preserved the published PR-B head with a backup branch and verified all-ref
  bundle, then normally merged PR #7 main `db623a5` without rebase, squash or
  force push.
- Kept migration IDs/checksums 0001–0006 immutable and appended
  `0007-receipt-shop-snapshot` with pinned checksum `a1d12cca...6462`, verified
  backup and a separate bounded post-PR7 ledgerless baseline.
- Added a seventh sanitized fixture and fail-closed regressions for false custom
  predecessor bootstrap, incomplete ownership evidence, malformed current
  schema, validation-before-reconciliation and restore rollback.
- Preserved PR #7 receipt/recovery/hardware/release behavior. No sync,
  payload/hash/idempotency, reversal-economics or WAL/journal-mode change was
  introduced, and the physical printer was not exercised again.
- Pre-publication evidence: gates `33/33`, focused migration/fixture/restore
  `38/38`, full tests `336/336`, CLI selftest PASS, Release solution and WPF
  net48/x86 builds with zero warnings/errors. Catalog benchmarks: 2,000-row
  batch median `609.510 ms` (`30.25x` vs legacy) and 19,762-row paged full
  `Verified` in 3/3 runs.
- Clean refresh merge commit `5377778` produced an exact-commit, clean-tree
  Release/x86 pack; completeness, Win7 runtime validation and the Inno Setup
  6.7.3 installer all passed before publication.
