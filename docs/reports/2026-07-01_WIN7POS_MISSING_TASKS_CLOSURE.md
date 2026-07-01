# Win7POS Missing Tasks Closure - 2026-07-01

## Stato iniziale

- Branch: `audit/win7pos-full-hardening`
- Commit: `6a21f0a`
- Working tree: non pulito; modifiche preesistenti presenti prima del batch audit e preservate.
- `git diff --check`: PASS.
- Note su modifiche preesistenti: README, online/bootstrap/sync, catalog pull, product repository, start-of-day sync, dialog online e diversi script risultano gia sporchi/non attribuiti a questa chiusura. Non sono stati ripuliti, scartati o stashed.
- Rerun corrente Codex Mac: check Mac e release pack fresco rieseguiti il 2026-07-01; nessun commit, reset o stash.

## Cose mancanti dal full audit

| ID | Area | Severita | Stato precedente | Azione prevista | Stato finale | Evidenza |
|----|------|----------|------------------|-----------------|--------------|----------|
| MT-001 | Smoke WPF reale Windows/ASUS | P1 | Non eseguito | Preparare task ASUS e non dichiarare PASS | RICHIEDE_ASUS | `docs/reports/2026-07-01_CODEX_ASUS_WINDOWS_QA_TASK.md` aggiornato; nessun output ASUS ricevuto. |
| MT-002 | Windows 7/runtime compatibility | P1 | Solo build Mac | Build x86 e handoff Windows/Win7 | RICHIEDE_ASUS | WPF `net48` x86 build PASS su Mac; runtime .NET Framework 4.8/Win7 non testato qui. |
| MT-003 | DPI/multi-monitor/dialog focus | P1 | Non eseguito reale | Check statici + smoke ASUS | PARZIALE | `check-dialog-standards.ps1` -> ALL PASS; DPI/multi-monitor/focus reale richiede Windows. |
| MT-004 | Stampante/ricevuta/ristampa | P1 | Non eseguito hardware | CLI receipt + smoke stampante | PARZIALE | CLI selftest genera preview receipt 42/32 e `自检 PASS`; stampante/ristampa WPF reale RICHIEDE_ASUS. |
| MT-005 | Backup/restore test reale | P1 | Solo statico | Check restore guard + smoke restore test | PARZIALE | `check-win7pos-restore-guard.ps1` -> ALL PASS; restore WPF reale in dir test RICHIEDE_ASUS. |
| MT-006 | Outbox pending/retry/failed_blocked restore guard | P1 | Statico da confermare | Rieseguire guard | VERIFIED_MAC | `check-win7pos-restore-guard.ps1` -> unresolved outbox guard includes pending/retry/failed_blocked. |
| MT-007 | Offline mode reale | P1 | Non eseguito reale | Check offline-first + smoke senza rete | PARZIALE | Online/client/bootstrap/start-of-day/sales sync checks ALL PASS; simulazione rete reale RICHIEDE_ASUS. |
| MT-008 | AdminWebBaseUrl dummy HTTPS / HTTP LAN guard | P1 | Da chiudere | Check staging/config e pack | VERIFIED_MAC | `check-public-staging-config.ps1` -> ALL PASS; pack README/helper richiedono HTTPS e tengono HTTP LAN flag unset. |
| MT-009 | Release pack fresco | P1 | Solo `dist/Win7POS` preesistente | Generare pack fresco e validarlo | DONE_MAC | Fresh pack: `/tmp/win7pos-missing-closure-current-20260701_135330/Win7POS`; zip: `/tmp/win7pos-missing-closure-current-20260701_135330/Win7POS_missing_closure_current_20260701_135330.zip`; completeness folder+zip -> ALL PASS. |
| MT-010 | Installer Inno Setup fresco | P1 | Non eseguito | Cercare ISCC e documentare comando Windows | RICHIEDE_WINDOWS_INNO | `command -v iscc` vuoto su Mac; `installer/Win7POS.iss` e workflow verificati, build installer da eseguire su Windows/CI. |
| MT-011 | Review diff: audit batch vs cambi preesistenti | P2 | Da separare | Classificare file | DONE | Vedi sezione Diff review. |
| MT-012 | CI/release workflow follow-up | P1 | SDK gia corretto; pack da verificare | Verificare workflow e patch sicura se emersa | DONE_MAC / CI_REQUIRED | Workflow SDK `10.0.x`, runner Windows, WPF x86 preservato; aggiunta rimozione PDB in release pack e Windows builder script. |

## Diff review

### File del batch audit precedente

- `.github/workflows/ci.yml`
  - Cosa cambia: `actions/setup-dotnet` da `8.0.x` a `10.0.x`.
  - Rischio: basso; necessario per CLI `net10.0`.
  - Check collegato: build Core/Data/CLI/WPF Release PASS.
- `.github/workflows/wpf-build.yml`
  - Cosa cambia: SDK da `8.0.x` a `10.0.x`; WPF resta `net48`/`x86`.
  - Rischio: basso.
  - Check collegato: `dotnet build ...Win7POS.Wpf.csproj -p:Platform=x86 -p:PlatformTarget=x86` PASS.
- `.github/workflows/release-pack.yml`
  - Cosa cambia: SDK da `8.0.x` a `10.0.x`; in questa fase aggiunta rimozione `*.pdb` dal pack.
  - Rischio: basso; rimozione simboli debug non tocca DLL/EXE runtime.
  - Check collegato: fresh pack locale folder+zip -> completeness ALL PASS.
- `src/Win7POS.Wpf/Localization/PosLocalization.cs`
  - Cosa cambia: audit batch ha corretto il copy IT `payment.cardOverBalance`; nello stesso file ci sono anche traduzioni catalog bootstrap gia presenti nel worktree.
  - Rischio: basso sul fix copy; attenzione review per distinguere le traduzioni preesistenti.
  - Check collegato: `check-pos-revenue-copy.ps1` -> ALL PASS.
- `src/Win7POS.Wpf/Pos/Online/PosSyncStatusReader.cs`
  - Cosa cambia: audit batch ha sostituito label pending sales da `sync.blockedAttention` a `sync.blocked`; il file contiene anche molte modifiche preesistenti su catalog/readiness/status.
  - Rischio: basso sul fix label; review separata richiesta per il resto.
  - Check collegato: `check-pos-sync-status-ux.ps1` -> ALL PASS.
- `scripts/check-pos-sync-status-ux.ps1`
  - Cosa cambia: check i18n-aware, legge traduzioni oltre a XAML/code.
  - Rischio: basso; riduce falsi negativi.
  - Check collegato: script stesso -> ALL PASS.
- `scripts/check-pos-shop-data-readonly.ps1`
  - Cosa cambia: check i18n-aware per copy official/read-only.
  - Rischio: basso.
  - Check collegato: script stesso -> ALL PASS.
- `scripts/check-pos-revenue-copy.ps1`
  - Cosa cambia: check i18n-aware per boleta/PDF, cambio contanti, card over-balance e registro.
  - Rischio: basso.
  - Check collegato: script stesso -> ALL PASS.
- `docs/AI_WORKLOG.md`
  - Cosa cambia: aggiunto audit log e questa chiusura.
  - Rischio: basso, documentazione.

### File toccati in questa fase

- `docs/reports/2026-07-01_WIN7POS_MISSING_TASKS_CLOSURE.md`: nuovo report di chiusura.
- `docs/reports/2026-07-01_CODEX_ASUS_WINDOWS_QA_TASK.md`: task ASUS aggiornato con prompt operativo completo.
- `docs/reports/2026-07-01_WIN7POS_FULL_AUDIT.md`: addendum closure e release pack fresco.
- `docs/AI_WORKLOG.md`: append closure.
- `.github/workflows/release-pack.yml`: rimozione `*.pdb` dal release pack.
- `scripts/win7pos/windows/build-release-x86.ps1`: rimozione `*.pdb` dal drop Windows builder.

### File preesistenti / non attribuiti a questo batch

- `README.md`
  - Perche non lo tocchiamo: sporco prima della chiusura.
  - Rischio: documentazione da review umana.
  - Prossimo passo: includere nella review globale, non revert automatico.
- `scripts/check-pos-catalog-pull.ps1`, `scripts/check-pos-online-bootstrap.ps1`, `scripts/check-pos-online-client.ps1`, `scripts/check-public-staging-config.ps1`
  - Perche non li tocchiamo: modifiche gia presenti prima; sono stati solo eseguiti.
  - Rischio: basso se i check restano verdi; ownership da confermare.
  - Prossimo passo: review del batch online/bootstrap.
- `src/Win7POS.Core/Online/PosAdminWebClient.cs`, `src/Win7POS.Core/Online/PosAdminWebOptions.cs`
  - Perche non li tocchiamo: cambi online/config preesistenti.
  - Rischio: boundary Admin Web e URL guard da validare su Windows/ASUS.
  - Prossimo passo: review + smoke dummy HTTPS/HTTP LAN.
- `src/Win7POS.Data/DbInitializer.cs`, `src/Win7POS.Data/Repositories/ProductRepository.cs`
  - Perche non li tocchiamo: cambi migration/catalog preesistenti.
  - Rischio: P1 dati se review trova regressioni; check legacy/catalog PASS.
  - Prossimo passo: review mirata DB/catalog.
- `src/Win7POS.Wpf/Localization/PosTranslations.LegacyReachable.cs`, `src/Win7POS.Wpf/MainWindow.xaml`, `src/Win7POS.Wpf/MainWindow.xaml.cs`, `src/Win7POS.Wpf/Win7POS.Wpf.csproj`
  - Perche non li tocchiamo: cambi preesistenti legati a startup/online/safe-start.
  - Rischio: UI/startup reale su Windows non coperto da Mac.
  - Prossimo passo: ASUS smoke.
- `src/Win7POS.Wpf/Pos/Dialogs/OperatorLoginDialog.xaml.cs`, `src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml`, `src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml.cs`
  - Perche non li tocchiamo: dialog online gia modificati; standard statici PASS.
  - Rischio: focus/DPI/retry reale.
  - Prossimo passo: ASUS dialog smoke.
- `src/Win7POS.Wpf/Pos/Online/PosCatalogPullService.cs`, `src/Win7POS.Wpf/Pos/Online/PosOnlineBootstrapService.cs`, `src/Win7POS.Wpf/Pos/Online/PosTrustedDeviceStore.cs`, `src/Win7POS.Wpf/Pos/PosWorkflowService.cs`
  - Perche non li tocchiamo: cambi online/catalog/sync preesistenti.
  - Rischio: sale-safe/offline/restore guard da provare runtime.
  - Prossimo passo: ASUS smoke + review.
- File non tracciati preesistenti: `docs/WIN7POS_BOOTSTRAP_SYNC_AUDIT.md`, `scripts/check-pos-first-login-sale-safe-ui.ps1`, `scripts/check-pos-sales-sync.ps1`, `scripts/check-pos-start-of-day-sync.ps1`, `scripts/check-win7pos-restore-guard.ps1`, `src/Win7POS.Wpf/Pos/Dialogs/PosStartOfDaySyncDialog.xaml`, `src/Win7POS.Wpf/Pos/Dialogs/PosStartOfDaySyncDialog.xaml.cs`, `src/Win7POS.Wpf/Pos/Online/PosStartOfDaySyncService.cs`.
  - Perche non li tocchiamo: gia non tracciati prima della chiusura.
  - Rischio: file nuovi da review/decisione commit separata.
  - Prossimo passo: review umana prima di staging.

## Script richiesti ma assenti

| Script richiesto dal prompt | Stato | Equivalente eseguito | Risultato |
|---|---|---|---|
| `scripts/check-online-client-safety.ps1` | script assente | `scripts/check-pos-online-client.ps1` | ALL PASS |
| `scripts/check-online-bootstrap-contract.ps1` | script assente | `scripts/check-pos-online-bootstrap.ps1` | ALL PASS |
| `scripts/check-first-login-sale-safe.ps1` | script assente | `scripts/check-pos-first-login-sale-safe-ui.ps1` | ALL PASS |
| `scripts/check-start-of-day.ps1` | script assente | `scripts/check-pos-start-of-day-sync.ps1` | ALL PASS |
| `scripts/check-catalog-pull-contract.ps1` | script assente | `scripts/check-pos-catalog-pull.ps1` | ALL PASS |
| `scripts/check-sales-sync-contract.ps1` | script assente | `scripts/check-pos-sales-sync.ps1` | ALL PASS |
| `scripts/check-restore-guard.ps1` | script assente | `scripts/check-win7pos-restore-guard.ps1` | ALL PASS |
| `scripts/check-legacy-db-safety.ps1` | script assente | `scripts/check-win7pos-legacy-db-migrations.ps1` | ALL PASS |
| `scripts/check-startup-win7-safe.ps1` | script assente | `scripts/check-pos-startup-win7-safe.ps1` | ALL PASS |
| `scripts/check-product-free-text.ps1` | script assente | `scripts/check-product-dialog-free-text.ps1` | ALL PASS |

## Check eseguiti

| Comando/Test | Ambiente | Risultato | Note |
|--------------|----------|-----------|------|
| `git status --short` | macOS | PASS | Worktree non pulito, riportato. |
| `git branch --show-current` | macOS | PASS | `audit/win7pos-full-hardening`. |
| `git rev-parse --short HEAD` | macOS | PASS | `6a21f0a`. |
| `git diff --stat` | macOS | PASS | 30 file tracked modificati dopo patch PDB; untracked riportati da status. |
| `git diff --name-status` | macOS | PASS | Tracked modifications elencate; untracked separati da status. |
| `git diff --check` | macOS | PASS | Nessun whitespace error. |
| `dotnet --info` | macOS | PASS | SDK `10.0.301`, RID `osx-arm64`. |
| `dotnet restore src/Win7POS.Cli/Win7POS.Cli.csproj` | macOS | PASS | Progetti aggiornati. |
| `dotnet build src/Win7POS.Core/Win7POS.Core.csproj -c Release` | macOS | PASS | 0 warning, 0 errori. |
| `dotnet build src/Win7POS.Data/Win7POS.Data.csproj -c Release` | macOS | PASS | 0 warning, 0 errori. |
| `dotnet build src/Win7POS.Cli/Win7POS.Cli.csproj -c Release` | macOS | PASS | 0 warning, 0 errori. |
| `WIN7POS_DATA_DIR=/tmp/win7pos-codex-missing-selftest-current dotnet run --project src/Win7POS.Cli/Win7POS.Cli.csproj -c Release -- --selftest --keepdb` | macOS | PASS | Vendita/refund/import: `自检 PASS`. |
| `dotnet restore src/Win7POS.Wpf/Win7POS.Wpf.csproj` | macOS | PASS | Restore completato. |
| `dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86` | macOS | PASS | Output `bin/x86/Release/net48/Win7POS.Wpf.exe`, 0 warning/errori. |
| `pwsh -File scripts/check-dialog-standards.ps1` | macOS | PASS | `=== RESULT: ALL PASS ===`. |
| `pwsh -File scripts/check-public-staging-config.ps1` | macOS | PASS | HTTPS workers.dev e no insecure LAN default. |
| `pwsh -File scripts/check-pos-debug-logging.ps1` | macOS | PASS | Redaction e categorie logging PASS. |
| `pwsh -File scripts/check-pos-online-client.ps1` | macOS | PASS | HttpClient/TLS/DPAPI/base URL guard PASS. |
| `pwsh -File scripts/check-pos-online-bootstrap.ps1` | macOS | PASS | Bootstrap/catalog sale-safe PASS. |
| `pwsh -File scripts/check-pos-first-login-sale-safe-ui.ps1` | macOS | PASS | POS open gate sale-safe PASS. |
| `pwsh -File scripts/check-pos-start-of-day-sync.ps1` | macOS | PASS | Preflight blocks restore/outbox/auth denied PASS. |
| `pwsh -File scripts/check-pos-catalog-pull.ps1` | macOS | PASS | Catalog pull/hasMore/tombstone/stock PASS. |
| `pwsh -File scripts/check-pos-sales-sync.ps1` | macOS | PASS | Outbox retry/blocked/no purge PASS. |
| `pwsh -File scripts/check-win7pos-restore-guard.ps1` | macOS | PASS | Restore guard pending/retry/failed_blocked PASS. |
| `pwsh -File scripts/check-win7pos-legacy-db-migrations.ps1` | macOS | PASS | Legacy DB startup harness PASS. |
| `pwsh -File scripts/check-pos-startup-win7-safe.ps1` | macOS | PASS | Startup bounded/TLS/safe-start PASS. |
| `pwsh -File scripts/check-product-dialog-free-text.ps1` | macOS | PASS | Supplier/category free text PASS. |
| `pwsh -File scripts/check-pos-sync-status-ux.ps1` | macOS | PASS | Sync status UX PASS. |
| `pwsh -File scripts/check-pos-shop-data-readonly.ps1` | macOS | PASS | Shop official/read-only PASS. |
| `pwsh -File scripts/check-pos-revenue-copy.ps1` | macOS | PASS | Revenue/payment copy PASS. |
| `pwsh -File scripts/check-release-pack-completeness.ps1 -ReleasePackSource <fresh-pack> -WriteManifests` | macOS | PASS | Required files and manifests PASS. |
| `pwsh -File scripts/check-release-pack-completeness.ps1 -ReleasePackSource <fresh-zip>` | macOS | PASS | Zip expands and required files PASS. |
| PowerShell parse of `scripts/win7pos/windows/build-release-x86.ps1` | macOS | PASS | `PARSE PASS`. |

## Check non eseguiti

| Check | Motivo | Chi deve eseguirlo | Comando/procedura |
|-------|--------|--------------------|-------------------|
| Smoke WPF reale | macOS non esegue runtime WPF/focus/stampante | Codex ASUS | Usare `docs/reports/2026-07-01_CODEX_ASUS_WINDOWS_QA_TASK.md`. |
| Windows 7 runtime | Richiede Win7 SP1 o ambiente runtime equivalente | Codex ASUS | Eseguire smoke con .NET Framework 4.8 e drop x86. |
| DPI/multi-monitor/focus reale | Serve desktop Windows | Codex ASUS | DPI 100/125, 1024x768, 1024x600 best-effort, multi-monitor. |
| Stampante fisica/fallback | Serve periferica o driver Windows | Codex ASUS | Smoke cash/card, ricevuta, ristampa, stampante assente/presente. |
| Installer Inno Setup fresco | `iscc` non disponibile su Mac | Codex ASUS / CI Windows | `iscc installer\Win7POS.iss` o workflow `release-pack.yml`. |
| CI GitHub | Non lanciata da questa sessione | Owner repo/CI | Lanciare `ci.yml`, `wpf-build.yml`, `release-pack.yml` su branch. |

## Bug trovati da ASUS

| ID | Severita | Stato | Fix | Check |
|----|----------|-------|-----|-------|
| ASUS-N/A | N/A | ASUS_NOT_RUN | Nessun output ASUS disponibile da integrare. | Task ASUS aggiornato. |

## Release pack

- Generato fresco: si.
- Percorso cartella: `/tmp/win7pos-missing-closure-current-20260701_135330/Win7POS`
- Percorso zip: `/tmp/win7pos-missing-closure-current-20260701_135330/Win7POS_missing_closure_current_20260701_135330.zip`
- Comando: `dotnet publish Win7POS.Wpf ... -r win-x86 -o <pack>` + CLI publish opzionale + helper staging + README/CHECKLIST/VERSION + validator ufficiale.
- Check: `check-release-pack-completeness` su cartella e zip -> ALL PASS nel rerun corrente.
- DLL native SQLite x86: `e_sqlite3.dll` presente a root pack; `SQLitePCLRaw.provider.e_sqlite3.dll` presente.
- Controllo manuale: rimossi `*.pdb`; nessun `.cs`, `.xaml`, `.ps1`, `appsettings*.json`, `*.Development.*`, `*.config.user`; grep secret senza hit reali. Match residui: testo PIN/password nel README, `publicKeyToken` assembly binding, istruzioni per tenere `WIN7POS_ALLOW_INSECURE_LAN_ADMIN_WEB` unset.

## Installer

- Generato fresco: no.
- Installazione test: no.
- Note: `iscc`/Inno Setup non disponibile su Mac. `installer/Win7POS.iss` dichiara `PrivilegesRequired=admin`, sorgente `dist\Win7POS`, check .NET Framework 4.8 e warning VC++ runtime x86; workflow Windows installa Inno Setup e compila `installer/Win7POS.iss`.

## Rischi residui

| Severita | Rischio | Motivo | Prossimo passo |
|----------|---------|--------|----------------|
| P1 | Smoke WPF reale non eseguito | Mac non copre runtime WPF/driver/focus | Eseguire ASUS task. |
| P1 | Installer non generato localmente | Inno Setup assente su Mac | Eseguire CI Windows o ASUS builder. |
| P1 | Worktree sporco con cambi preesistenti | Molti file non attribuiti a questo batch | Review umana prima di staging/commit. |
| P2 | CI non lanciata | Nessun accesso/trigger usato da questa sessione | Lanciare workflow su branch e controllare artifact. |

## Decisione consigliata

Richiede nuovo giro ASUS prima di dichiarare DONE operativo. Lato Mac la chiusura e pronta per review umana: diff review completata, check rieseguiti, release pack fresco verificato, installer/Windows/hardware correttamente marcati come non eseguiti.

## Stato finale

- Branch: `audit/win7pos-full-hardening`
- Commit: `6a21f0a`
- Working tree: non pulito, intenzionalmente non ripulito.
- File modificati in questa fase: closure report, ASUS task, full audit report, AI worklog, `release-pack.yml`, `scripts/win7pos/windows/build-release-x86.ps1`.
- File non attribuiti/preesistenti: elencati nella Diff review; nessun revert/stash/checkout eseguito.
- Commit: non eseguito.
- Secret: nessun secret inserito.
