# 2026-07-01 Win7POS Full Audit

## Stato sessione

- Branch: `audit/win7pos-full-hardening`
- Commit iniziale: `6a21f0a`
- Ambiente: macOS Darwin arm64, `dotnet` SDK `10.0.301`, PowerShell `7.6.3`
- Worktree iniziale: non pulito, con modifiche preesistenti su online/bootstrap/sync/WPF/docs/script. Le modifiche preesistenti sono state preservate.
- Commit: non eseguito.
- Secret: nessun secret inserito.

## Addendum closure missing tasks

- Data closure: `2026-07-01`.
- Report dedicato: `docs/reports/2026-07-01_WIN7POS_MISSING_TASKS_CLOSURE.md`.
- Release pack fresco generato in questa sessione:
  - cartella: `/tmp/win7pos-missing-closure-current-20260701_135330/Win7POS`
  - zip: `/tmp/win7pos-missing-closure-current-20260701_135330/Win7POS_missing_closure_current_20260701_135330.zip`
  - `check-release-pack-completeness` su cartella e zip: `ALL PASS`.
- Hardening packaging aggiunto: `*.pdb` rimossi dal release pack in `.github/workflows/release-pack.yml` e dal drop Windows builder in `scripts/win7pos/windows/build-release-x86.ps1`.
- Installer Inno Setup: `RICHIEDE_WINDOWS_INNO`; `iscc` non disponibile su Mac.
- ASUS/Windows smoke: `ASUS_NOT_RUN`; task aggiornato in `docs/reports/2026-07-01_CODEX_ASUS_WINDOWS_QA_TASK.md`.

## Addendum final ASUS review

- Report finale Mac: `docs/reports/2026-07-01_MAC_FINAL_ASUS_REVIEW_AND_MAIN_MERGE.md`.
- Branch ASUS revisionate:
  - `qa/asus-win7pos-result-20260701` / `63cdaaa`
  - `qa/asus-printer-cashdrawer-hardening-20260701` / `8ba8a25`
- Branch integration: `integration/win7pos-asus-final-review-20260701`.
- Merge commit integration: `63501e0`.
- ASUS/Windows smoke: ora ricevuto e revisionato; build Windows, WPF smoke, release pack, installer e printer/cashdrawer software PASS con limiti hardware dichiarati.
- Fix review Mac: aggiornato `scripts/check-pos-startup-win7-safe.ps1` per accettare il catalogo traduzioni lazy corretto senza reintrodurre static constructor prematuro.
- Gate Mac finali: build Core/Data/CLI/WPF, CLI selftest, dialog standards, startup, online/sync/restore/shop/revenue/product/printer checks tutti PASS.
- Security/artifact scan: nessun secret reale; nessun artifact generato tracciato.
- Decisione: `READY_FOR_MAIN_MERGE`.

## Inventario reale

- `git status --short`: worktree non pulito gia prima delle patch; file modificati in README, script online, core/data/WPF online, dialog bootstrap, sync/start-of-day; file non tracciati per audit bootstrap/sync e nuovi script.
- `git branch --show-current`: inizialmente `main`, poi branch locale creata `audit/win7pos-full-hardening`.
- `git rev-parse --short HEAD`: `6a21f0a`.
- `dotnet --info`: SDK `10.0.301`; runtime `Microsoft.NETCore.App 10.0.9`; OS `Mac OS X 26.5`; RID `osx-arm64`.
- `pwsh -v`: `PowerShell 7.6.3`.
- `uname -a`: `Darwin ... 25.5.0 ... arm64`.
- `systeminfo`: `NOT_RUN`, host non Windows.
- Progetti: `src/Win7POS.Cli/Win7POS.Cli.csproj`, `src/Win7POS.Core/Win7POS.Core.csproj`, `src/Win7POS.Data/Win7POS.Data.csproj`, `src/Win7POS.Wpf/Win7POS.Wpf.csproj`.
- Workflow: `.github/workflows/ci.yml`, `.github/workflows/wpf-build.yml`, `.github/workflows/release-pack.yml`.
- Installer: `installer/Win7POS.iss`, `installer/README_INSTALLER.txt`.
- Cartelle principali `src/`: `Win7POS.Core`, `Win7POS.Data`, `Win7POS.Cli`, `Win7POS.Wpf`.

## Findings

| ID | Area | Severita | File | Evidenza | Rischio | Azione | Stato | Check |
|----|------|----------|------|----------|---------|--------|-------|-------|
| AUD-001 | Build/CI | P0 | `.github/workflows/ci.yml`, `.github/workflows/wpf-build.yml`, `.github/workflows/release-pack.yml`, `src/Win7POS.Cli/Win7POS.Cli.csproj` | CLI target `net10.0`; workflow installavano SDK `8.0.x`; local build passa solo con SDK `10.0.301`. | CI/release pack Windows potevano fallire prima dei test reali. | Aggiornato `actions/setup-dotnet` a `10.0.x` nei tre workflow. | FIXED | `rg -n "dotnet-version" .github/workflows` -> tutti `10.0.x`; build Core/Data/CLI/WPF PASS. |
| AUD-002 | Sync UX | P2 | `src/Win7POS.Wpf/Pos/Online/PosSyncStatusReader.cs`, `scripts/check-pos-sync-status-ux.ps1` | `check-pos-sync-status-ux.ps1` falliva su label sync e outbox; summary non esponeva label `Bloccate` nel dettaglio pending. | Operatore/assistenza potevano vedere una label meno chiara per vendite bloccate; check statico non copriva i18n reale. | `PendingSalesText` usa `sync.blocked`; checker reso i18n-aware leggendo le traduzioni. | FIXED | `pwsh -File scripts/check-pos-sync-status-ux.ps1` -> ALL PASS. |
| AUD-003 | Shop official data | P2 | `scripts/check-pos-shop-data-readonly.ps1` | Check falliva su copy ufficiale/read-only anche se XAML usa `loc:Loc` e traduzioni contengono `Dati negozio ufficiali`, `Master Console`, `cache anche offline`. | Falso negativo nei check release; rischio di bloccare QA o ignorare un controllo utile. | Checker reso i18n-aware leggendo `PosLocalization.cs` e `PosTranslations.Secondary.cs`. | FIXED | `pwsh -File scripts/check-pos-shop-data-readonly.ps1` -> ALL PASS. |
| AUD-004 | Payment/revenue copy | P2 | `src/Win7POS.Wpf/Localization/PosLocalization.cs`, `scripts/check-pos-revenue-copy.ps1` | Check falliva su boleta/PDF, cambio contanti, carta over-balance e documento registro per uso di chiavi i18n; testo IT usava `puo`. | Messaggi pagamento/documento meno verificabili; possibile regressione di copy fiscale/operativa non intercettata. | Checker reso i18n-aware; testo IT aggiornato a `La carta non può superare il saldo da pagare...`. | FIXED | `pwsh -File scripts/check-pos-revenue-copy.ps1` -> ALL PASS. |
| AUD-005 | Security/privacy | P1 | `src/`, `scripts/`, docs operative | Ricerca secret: nessun `sk-`, JWT, private key o assegnazione raw token/secret trovata; `service_role` compare solo in checker/doc come pattern vietato. | Residuo basso; serve smoke con log reali sanitizzati su Windows. | Nessuna patch codice richiesta; mantenuti DPAPI, redazione token e guard logging. | VERIFIED_MAC | `pwsh -File scripts/check-pos-debug-logging.ps1` -> ALL PASS; rg secret scan eseguita. |
| AUD-006 | SQLite/outbox/restore | P1 | `src/Win7POS.Data`, `src/Win7POS.Wpf/Pos`, script guard | Check statici confermano transazioni, outbox idempotente, restore guard su `pending/retry/failed_blocked`, no purge outbox. | Smoke restore fisico non eseguito su Mac. | Nessuna patch codice richiesta. | VERIFIED_MAC / EXTERNAL_SMOKE_REQUIRED | `check-pos-sales-sync`, `check-win7pos-restore-guard`, `check-win7pos-legacy-db-migrations` -> ALL PASS. |
| AUD-007 | Windows 7/hardware/installer | P1 | WPF runtime, printer, Inno Setup, release pack | Build WPF x86 passa su Mac; release pack fresco generato e validato in `/tmp/win7pos-missing-closure-current-20260701_135330`; non eseguito installer Inno o smoke Windows 7/stampante. | Runtime/hardware Win7, stampante, DPI e installer possono ancora avere problemi non visibili da Mac. | Creato/aggiornato task ASUS Windows QA; pack fresco verificato; workflow/drop builder ora rimuovono `*.pdb`. | PARTIAL_FIXED / EXTERNAL_SMOKE_REQUIRED | `check-release-pack-completeness` su fresh folder+zip -> ALL PASS; installer Inno NOT_RUN su Mac. |

## Check eseguiti

| Comando | Risultato | Ambiente | Note |
|---|---:|---|---|
| `git diff --check` | PASS | macOS | Nessun whitespace error. |
| `dotnet restore src/Win7POS.Cli/Win7POS.Cli.csproj` | PASS | macOS / SDK 10.0.301 | Progetti aggiornati. |
| `dotnet build src/Win7POS.Core/Win7POS.Core.csproj -c Release` | PASS | macOS / SDK 10.0.301 | 0 warning, 0 errori. |
| `dotnet build src/Win7POS.Data/Win7POS.Data.csproj -c Release` | PASS | macOS / SDK 10.0.301 | 0 warning, 0 errori. |
| `dotnet build src/Win7POS.Cli/Win7POS.Cli.csproj -c Release` | PASS | macOS / SDK 10.0.301 | Target `net10.0`, 0 warning/errori. |
| `dotnet restore src/Win7POS.Wpf/Win7POS.Wpf.csproj` | PASS | macOS / SDK 10.0.301 | Restore WPF completato. |
| `dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86` | PASS | macOS / SDK 10.0.301 | Output `bin/x86/Release/net48/Win7POS.Wpf.exe`, 0 warning/errori. |
| `env WIN7POS_DATA_DIR=/tmp/win7pos-codex-selftest dotnet run --project src/Win7POS.Cli/Win7POS.Cli.csproj -c Release -- --selftest --keepdb` | PASS | macOS | Vendita, refund, daily summary, import diff/apply: `自检 PASS`. |
| `pwsh -File scripts/check-dialog-standards.ps1` | PASS | macOS / pwsh 7.6.3 | `ALL PASS`, 27/27 `CenterOwner`. |
| `pwsh -File scripts/check-public-staging-config.ps1` | PASS | macOS | HTTPS workers.dev, no LAN insecure override. |
| `pwsh -File scripts/check-pos-debug-logging.ps1` | PASS | macOS | Redaction token/password e categorie sync PASS. |
| `pwsh -File scripts/check-pos-online-client.ps1` | PASS | macOS | HttpClient, TLS 1.2, DPAPI, no Supabase diretto. |
| `pwsh -File scripts/check-pos-online-bootstrap.ps1` | PASS | macOS | First-login/bootstrap/catalog sale-safe guard PASS. |
| `pwsh -File scripts/check-pos-first-login-sale-safe-ui.ps1` | PASS | macOS | POS non apre senza catalog sale-safe. |
| `pwsh -File scripts/check-pos-start-of-day-sync.ps1` | PASS | macOS | Preflight blocca restore/outbox blocked/auth denied. |
| `pwsh -File scripts/check-pos-catalog-pull.ps1` | PASS | macOS | Catalog pull, cursor, tombstone soft delete, stock local pending preserved. |
| `pwsh -File scripts/check-pos-sales-sync.ps1` | PASS | macOS | Outbox idempotente, retry/block, no purge/drop. |
| `pwsh -File scripts/check-win7pos-restore-guard.ps1` | PASS | macOS | Pre-backup, WAL checkpoint, integrity, unresolved outbox guard. |
| `pwsh -File scripts/check-win7pos-legacy-db-migrations.ps1` | PASS | macOS | Legacy DB harness PASS. |
| `pwsh -File scripts/check-win7pos-startup-no-eager-db.ps1` | PASS | macOS | No eager DB startup. |
| `pwsh -File scripts/check-pos-startup-win7-safe.ps1` | PASS | macOS | Startup bounded, TLS 1.2, safe-start. |
| `pwsh -File scripts/check-pos-sync-status-ux.ps1` | PASS | macOS | PASS dopo fix. |
| `pwsh -File scripts/check-pos-shop-data-readonly.ps1` | PASS | macOS | PASS dopo fix. |
| `pwsh -File scripts/check-pos-revenue-copy.ps1` | PASS | macOS | PASS dopo fix. |
| `pwsh -File scripts/check-product-dialog-free-text.ps1` | PASS | macOS | Supplier/category free text guard. |
| `pwsh -File scripts/check-release-pack-completeness.ps1 -ReleasePackSource dist/Win7POS` | PASS | macOS | Valida drop preesistente. |
| `dotnet publish src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86 -r win-x86 --self-contained false -o <fresh-pack>` | PASS | macOS | Fresh release pack prodotto in `/tmp/win7pos-missing-closure-current-20260701_135330/Win7POS`. |
| `pwsh -File scripts/check-release-pack-completeness.ps1 -ReleasePackSource <fresh-pack> -WriteManifests` | PASS | macOS | Required files e manifest `APP-FILES.txt`/`SHA256SUMS.txt` scritti. |
| `pwsh -File scripts/check-release-pack-completeness.ps1 -ReleasePackSource <fresh-zip>` | PASS | macOS | Zip fresh validato. |
| PowerShell parse `scripts/win7pos/windows/build-release-x86.ps1` | PASS | macOS | `PARSE PASS`; esecuzione reale richiede Windows. |

## Audit statico

- Sicurezza: cercati `password`, `pin`, `token`, `secret`, `service_role`, `Authorization`, `DPAPI`, `ProtectedData`, logging e marker secret. Nessun secret raw trovato. DPAPI/trusted store e redaction confermati dagli script.
- Dialog WPF: cercati `Loaded`, `Dispatcher.BeginInvoke`, `Left`, `Top`, `WindowStartupLocation`, `DialogShellWindow`, helper owner/sizing. Le occorrenze residue sono focus/UI scheduling o base class; `check-dialog-standards.ps1` PASS.
- Thread/UI: cercati `.Result`, `.Wait()`, `Thread.Sleep`, `Task.Run`, `async void`, `ConfigureAwait`, `Dispatcher.Invoke`. Nessun blocco sincrono critico emerso dai check; `Task.Run` usato per background sync/cleanup/import.
- Dati SQLite: cercati `DELETE`, `DROP`, `ALTER TABLE`, `PRAGMA`, transaction, backup/restore, outbox, idempotency. Guard no destructive outbox/drop PASS; restore e legacy migration PASS.
- Online: cercati Supabase/AdminWeb/TLS/HTTP/config/sync. Nessun client Supabase diretto; POS passa da Admin Web API; HTTP non-loopback richiede flag dev/test.
- Win7 compatibility: WPF resta `net48`/`x86`; CLI resta `net10.0` solo tooling/harness. Workflow ora installano SDK 10 per CLI.

## Check non eseguiti

| Check | Motivo reale | Owner | Procedura |
|---|---|---|---|
| `systeminfo` | Host macOS, comando Windows-only. | Codex ASUS | Eseguire su Windows builder/test machine. |
| Smoke WPF reale | Mac non valida rendering WPF, focus, DPI, multi-monitor, stampante. | Codex ASUS | Usare `docs/reports/2026-07-01_CODEX_ASUS_WINDOWS_QA_TASK.md`. |
| Installer Inno Setup fresco | Inno Setup non installato/eseguito su Mac; workflow release-pack richiede Windows. | Codex ASUS / CI Windows | Eseguire `.github/workflows/release-pack.yml`, `scripts\win7pos\windows\build-release-x86.ps1 -BuildInstaller` o `ISCC.exe installer/Win7POS.iss` dopo build Windows. |
| Windows 7 runtime/hardware | Richiede VM/hardware Windows 7 e periferiche. | Codex ASUS | Smoke checklist `docs/WIN7_PRODUCTION_SMOKE_CHECKLIST.md`. |

## Rischi residui

- P1: smoke Windows 7 reale non eseguito; possibili problemi runtime .NET Framework 4.8, font/DPI, driver stampante o TLS legacy.
- P1: installer Inno non generato in questa sessione; richiede Windows/Inno Setup.
- P2: CI GitHub non lanciata da questa sessione; workflow da verificare su branch con artifact reali.
- P2: worktree contiene molte modifiche preesistenti non isolate da questa sessione; review finale deve distinguere patch audit da lavoro precedente.

## Handoff

File toccati da questa sessione:

- `.github/workflows/ci.yml`: allinea SDK CI a `10.0.x`.
- `.github/workflows/wpf-build.yml`: allinea SDK WPF workflow a `10.0.x`.
- `.github/workflows/release-pack.yml`: allinea SDK release pack a `10.0.x` e rimuove `*.pdb` dal pack.
- `scripts/win7pos/windows/build-release-x86.ps1`: rimuove `*.pdb` dal drop Windows builder.
- `src/Win7POS.Wpf/Pos/Online/PosSyncStatusReader.cs`: label outbox bloccate chiara.
- `src/Win7POS.Wpf/Localization/PosLocalization.cs`: copy italiana carta over-balance.
- `scripts/check-pos-sync-status-ux.ps1`: check i18n-aware.
- `scripts/check-pos-shop-data-readonly.ps1`: check i18n-aware.
- `scripts/check-pos-revenue-copy.ps1`: check i18n-aware.
- `docs/reports/2026-07-01_WIN7POS_FULL_AUDIT.md`: report audit.
- `docs/reports/2026-07-01_CODEX_ASUS_WINDOWS_QA_TASK.md`: task QA Windows/ASUS.
- `docs/reports/2026-07-01_WIN7POS_MISSING_TASKS_CLOSURE.md`: closure missing tasks e diff review.
- `docs/AI_WORKLOG.md`: sintesi sessione.

Prossima fase: `REVIEW`, con smoke Windows/ASUS per runtime WPF, installer e hardware.
