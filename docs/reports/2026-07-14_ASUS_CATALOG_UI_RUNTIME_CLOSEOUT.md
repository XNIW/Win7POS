# ASUS-W7POS-012 → ASUS-W7POS-018 — Catalog/UI/Runtime closeout

Data: `2026-07-14`

Branch: `integration/asus-catalog-ui-runtime-20260714`

Stato: `PARTIAL_CLOSURE_EXTERNAL_VALIDATION_REQUIRED`

Il report registra soltanto risultati realmente eseguiti. I test software
locali dimostrano gli invarianti indicati, ma non sono presentati come prova di
staging autenticato, Windows 7 o hardware fisico.

## 1. Esito complessivo

`NOT DONE` rispetto alla Definition of Done del task master.

Completati nel perimetro locale: integrazione non distruttiva di remediation e
`origin/main`; exactness fail-closed e sale barrier; batch SQLite single-writer;
policy delta/full e diagnostica; UI inventory/remediation; test dinamici e
checker; benchmark sintetici; infrastruttura release x86/net48.

Non certificati: catalogo staging reale contro il valore atteso `19.762` e i
conteggi autorevoli category/supplier/price; UI autenticata nelle sei
configurazioni display; Windows 7 SP1; install/uninstall elevati; stampante
ricevute, scanner e cash drawer fisici. La causa e il comando esatto per ogni
`FAIL`/`BLOCKED_EXTERNAL` sono riportati nell'handoff canonico
`docs/HANDOFFS/WIN7POS-ASUS-RUNTIME-VALIDATION-2026-07-14.md`.

## 2. Task ledger ASUS-W7POS-012…018

| Task | Stato | Obiettivo/scope completato | File principali | Validazione/evidenza | Commit di chiusura |
|---|---|---|---|---|---|
| ASUS-W7POS-012 | DONE | Remediation locale e shop-scoped sync integrati con merge normale; baseline Windows raccolta | merge repository, workflow, checker, architettura sync | ancestry `31b3de2`, `94bb957`, `8c39e13` → HEAD; build/gate finali PASS | `00a3fe00c72d57fe4af1b2febda942cac035b7f8` |
| ASUS-W7POS-013 | BLOCKED_EXTERNAL | Contratto summary, audit/reconcile, identity exactness, diagnostica, repair e sale barrier implementati | contract, `CatalogFullRefreshReconciler`, `CatalogShopStateRepository`, product/reference repositories, pull service, UI status | Core tests/checker PASS; staging Admin/SQLite assenti | software `1a56641`; closure `bb4178b`; runtime staging — |
| ASUS-W7POS-014 | BLOCKED_EXTERNAL | Apply per pagina su una connessione/transazione, prepared commands, rollback e benchmark locali | `RemoteCatalogBatchRepository`, benchmark/tests | 2k controlled e 19.762 synthetic PASS; full staging non eseguito | `507da33`, `793b238`, `259484c`; closure `bb4178b` |
| ASUS-W7POS-015 | BLOCKED_EXTERNAL | Trigger delta/full, ripresa oltre 8 pagine, pin snapshot/cursor cross-run, fail-closed repair, status UI | pull/state/status/shop settings/sale repository | regressioni software PASS; restart staging reale non eseguito | `bb4178b` |
| ASUS-W7POS-016 | BLOCKED_EXTERNAL | 42 XAML censiti; 38 superfici; 31 dialog; remediation P1 e localizzazione | dialog/import/POS XAML e localization/checker | build/checker statici PASS; matrice DPI autenticata non eseguita | `48a3e0d`, `ea144b2`, `8d6ff4e`; closure `bb4178b` |
| ASUS-W7POS-017 | BLOCKED_EXTERNAL | Build/release scripts e validator disponibili; host/tool/hardware inventariati | release workflow/scripts/installer | Pack/ZIP locali validati; Win11 x64 non elevato, nessun Win7 o hardware POS | workflow `5ee9084`; provenance pack finale in evidence esterna |
| ASUS-W7POS-018 | BLOCKED_EXTERNAL | Full rerun, release, report, push branch e draft PR completati nel perimetro autorizzato | report finali e release evidence | software/release locali PASS; DoD globale bloccata da staging, Win7 e hardware | closure software `bb4178b`; HEAD/PR post-commit in evidence esterna |

Per i task bloccati, la parte software è stata completata e reviewata; lo stato
complessivo resta `BLOCKED_EXTERNAL` perché il criterio di accettazione include
una prova esterna non sostituibile.

## 3. Branch e SHA iniziali/finali

| Voce | SHA/valore |
|---|---|
| Branch | `integration/asus-catalog-ui-runtime-20260714` |
| Remediation locale da preservare | `31b3de20c4747e33c422bb495706f79e614f3df2` |
| Shop-scoped sync | `94bb9573544811ef97c45f74b4ccf3ac85dc10de` |
| `origin/main` all'integrazione | `8c39e13c0f7a001956023919dd6bda612288351f` |
| Merge integration | `00a3fe00c72d57fe4af1b2febda942cac035b7f8` |
| HEAD prima della closure non committata | `259484c4f10e6c4cf50848e617264662eae72492` |
| Closure software | `bb4178b506afe5ebc86313f010e2271ca8075f84` |
| HEAD finale post-commit | registrato in `C:\Dev\Win7POS-QA\2026-07-14_ASUS_RUNTIME\evidence\final-release-evidence.txt` |
| `origin/main` pre-final fetch | `8c39e13c0f7a001956023919dd6bda612288351f`; fetch finale registrato nella stessa evidence |

Ancestry già verificata con exit code `0` per `31b3de2`, `94bb957` e
`8c39e13` verso il branch di integrazione.

## 4. Commit creati

| SHA | Subject | Scope |
|---|---|---|
| `00a3fe0` | `Merge remote-tracking branch 'origin/main'` | integrazione conservativa |
| `5ee9084` | `fix(release): restore canonical gates after integration` | release/CI |
| `a9c0ab3` | `test(cli): make Windows runtime harnesses deterministic` | harness runtime locali |
| `1a56641` | `feat: add fail-closed catalog exactness contracts` | contract/audit/persistenza |
| `24168d9` | `docs: record catalog exactness lane evidence` | evidence lane 013 |
| `48a3e0d` | `fix(ui): close reachable WPF usability gaps` | UI remediation |
| `ea144b2` | `test(ui): enforce dialog and supplier UX invariants` | UI/checker |
| `8d6ff4e` | `docs(ui): record reachable surface inventory` | evidence lane 016 |
| `507da33` | `perf: batch remote catalog page writes` | batch SQLite |
| `793b238` | `test: cover catalog batch safety and throughput` | regressioni/benchmark |
| `259484c` | `docs: record catalog batch benchmark evidence` | evidence lane 014 |
| `bb4178b` | `fix: close catalog runtime safety gaps` | exactness/checkpoint, price ownership/ACK, sync, sale safety, WPF lifecycle e regressioni finali |

Il commit documentale che contiene questo report e lo HEAD finale non possono
essere autoreferenziati nel loro stesso contenuto; SHA, subject, push e draft PR
sono registrati post-commit in `final-release-evidence.txt`.

## 5. File modificati

Il diff finale `00a3fe0..HEAD` contiene 66 path, incluso questo nuovo report.
Raggruppamento:

- Release/CI: `.github/workflows/release-pack.yml`.
- Report: questo file,
  `docs/HANDOFFS/WIN7POS-ASUS-RUNTIME-VALIDATION-2026-07-14.md`,
  `docs/reports/2026-07-14_ASUS-W7POS-014_CATALOG_BATCH_PERFORMANCE.md`,
  `docs/reports/2026-07-14_ASUS_W7POS_016_UI_UX_INVENTORY.md`,
  `docs/reports/lanes/ASUS-W7POS-013-catalog-exactness.md`.
- Checker: `check-dialog-standards.ps1`, `check-pos-catalog-pull.ps1`,
  `check-pos-printer-cashdrawer-safety.ps1`, `check-pos-sales-sync.ps1`,
  `check-pos-start-of-day-sync.ps1`, `check-pos-sync-status-ux.ps1`,
  `check-supplier-excel-wizard.ps1`, `check-win7pos-ui-ux-guard.ps1`.
- Contract/CLI/Data: `PosOnlineTransportContracts.cs`, `Program.cs`,
  `DbInitializer.cs`, `CatalogFullRefreshReconciler.cs`,
  `CatalogImportOutboxRepository.cs`, `CatalogImportSyncService.cs`,
  `CatalogShopStateRepository.cs`, `PosOnlineCompatibilityValidator.cs`,
  `PosShopTransitionGuard.cs`, `CategoryRepository.cs`,
  `ProductRepository.cs`, `RemoteCatalogBatchRepository.cs`,
  `SaleRepository.cs`, `SupplierRepository.cs`.
- WPF online/status: `PosCatalogPullService.cs`, `PosSyncStatusReader.cs`,
  `PosWorkflowService.cs`, `PosViewModel.cs`.
- WPF Shop Settings/localization: `ShopSettingsDialog.xaml`,
  `ShopSettingsDialog.xaml.cs`, `ShopSettingsViewModel.cs`,
  `PosLocalization.cs`, `PosTranslations.LegacyReachable.cs`.
- WPF UI inventory/remediation: `ApplyConfirmDialog.xaml`,
  `ImportDataDialog.xaml`, `ImportView.xaml`, `ModernMessageDialog.xaml`,
  `SupplierExcelImportDialog.xaml`, `SupplierExcelImportViewModel.cs`,
  `DailyReportView.xaml`, `BoletaNumberDialog.xaml`,
  `ChangeQuantityDialog.xaml`, `DailyReportDialog.xaml`,
  `DailyReportDialog.xaml.cs`, `HeldCartsDialog.xaml`, `RefundDialog.xaml`,
  `RefundDialog.xaml.cs`, `SalesRegisterDialog.xaml`,
  `SettingsHubDialog.xaml`.
- Performance/test: `tests/Win7POS.CatalogPerformance/Program.cs`, relativo
  `.csproj`, `CatalogBatchPerformanceScenario.cs`,
  `CatalogBatchPerformanceTests.cs`, `CatalogExactnessTests.cs`,
  `CatalogSafetyInvariantTests.cs`, `PosShopTransitionGuardTests.cs`,
  `RemoteCatalogBatchRepositoryTests.cs`, `RestoreShopSafetyTests.cs`,
  `SupplierImportDataTests.cs`, più i nuovi
  `CardSalePersistenceTests.cs`, `RemotePriceIdempotencyTests.cs` e
  `SaleSafetyBarrierTests.cs`.

La lista finale deve essere verificata con:

```powershell
git diff --name-status 00a3fe00c72d57fe4af1b2febda942cac035b7f8..HEAD
```

## 6. Conflitti risolti

Il merge `00a3fe0` ha due parent (`28a52d7` remediation e `8c39e13`
`origin/main`) e non lascia marker o conflitti irrisolti. La review ha verificato
che entrambi i rami restino antenati e che recovery/toast/gate/release e
shop-scoped sync/tombstone/restore guard coesistano.

Non è stato conservato un transcript file-per-file di eventuali conflitti
interattivi; il report non inventa quindi una lista di conflitti. Evidenza
riproducibile:

```powershell
git show -s --format='%H %P' 00a3fe0
git merge-base --is-ancestor 31b3de2 HEAD
git merge-base --is-ancestor 94bb957 HEAD
git merge-base --is-ancestor 8c39e13 HEAD
git diff --check
```

## 7. Risultati gate

| Gate | Esito | Evidenza/limite |
|---|---|---|
| `git diff --check` | PASS | nessun errore whitespace; soli warning LF→CRLF della working copy |
| restore solution | PASS | SDK `C:\Dev\dotnet10\dotnet.exe` |
| build solution Release | PASS | nessun warning/error |
| Core tests | PASS | `152/152` sulla closure software |
| CLI 7 harness canonici | PASS | publish Release isolato, DLL avviata dal runtime firmato; apphost in-tree bloccato da Application Control `0x800711C7` (`smart-app-control-cli.txt`) |
| WPF Release x86/net48 | PASS | `0` warning, `0` errori |
| 31 checker tracked | PASS | 28 non-release individuali + 3 pack-aware; output post-commit in `final-release-evidence.txt` |
| required gates con pack | PASS | `-ReleasePackSource dist\Win7POS`; output post-commit in evidence finale |
| package audit | PASS | nessun pacchetto diretto/transitivo vulnerabile o deprecato; evidence `package-audit.txt` |
| secret scan | PASS con fallback | nessun secret reale; scanner dedicati non disponibili dichiarati al punto 18 |

I valori di pack/ZIP che dipendono dallo HEAD documentale sono attestati nella
evidence post-commit, evitando circolarità nel report tracked.

## 8. Tabella test 1–30

| # | Test | Stato | Evidence essenziale |
|---:|---|---|---|
| 1 | Integrità trasferimento storico | FAIL | I tre artefatti originari non sono presenti; nuovo pack separato |
| 2 | Prerequisiti Win7 | BLOCKED_EXTERNAL | Host Win11, nessun target/VM Win7 |
| 3 | Contenuto pack finale | PASS | Pack pulito post-commit, VERSION allineata, manifest/installer/ZIP e validator PASS; valori in `final-release-evidence.txt` |
| 4 | Isolamento dati | PASS | CLI selftest con DB temporaneo fuori repo |
| 5 | Configurazione staging | PASS | public-config checker, HTTPS senza secret |
| 6 | Cold start x86 | PASS | Smoke pre-auth Release x86 redatto in `wpf-preauth-smoke.txt`; non è prova Win7 |
| 7 | First login shop A | BLOCKED_EXTERNAL | `staging-admin-login-blocked.png` |
| 8 | Restart trusted session | BLOCKED_EXTERNAL | Dipende dal test 7 |
| 9 | Catalog full pull esatto | BLOCKED_EXTERNAL | Nessun conteggio Admin/SQLite staging |
| 10 | Catalog delta/restart | BLOCKED_EXTERNAL | Nessun cursor/sessione staging reale |
| 11 | Cash sale | PASS | CLI SQLite dinamico |
| 12 | Card sale | BLOCKED_EXTERNAL | Persistenza/software locale provata; UI autenticata/spooler non eseguiti |
| 13 | Partial refund | PASS | CLI/sales harness dinamico |
| 14 | Full void | PASS | CLI/sales harness dinamico |
| 15 | Sales origin binding | PASS | sales sync harness |
| 16 | Catalog-import binding | PASS | supplier apply/outbox harness |
| 17 | Offline/reconnect drain | BLOCKED_EXTERNAL | Fake HTTP locale non sostituisce reconnect staging |
| 18 | Restart stale `in_progress` | BLOCKED_EXTERNAL | Lease testata; kill reale prepare→ACK non eseguito |
| 19 | Duplicate sales ACK | PASS | sales sync harness |
| 20 | Duplicate catalog ACK | PASS | catalog import fake HTTP harness |
| 21 | Retry bound | PASS | timeout attempt 11 → `failed_blocked` attempt 12 |
| 22 | Cross-shop drain | BLOCKED_EXTERNAL | Catalog dinamico + sales statico; nessuna sessione A/B reale |
| 23 | Legacy proven backfill | PASS | outbox/Core tests |
| 24 | Legacy ambiguous block | PASS | outbox/Core tests |
| 25 | Switch con outbox | PASS | transition guard tests |
| 26 | Switch dopo drain/race | PASS | transition barrier/epoch tests |
| 27 | Tombstone | PASS | reference tombstone/batch tests |
| 28 | Backup/restore cross-shop | PASS | restore guard harness/tests |
| 29 | Printer/cash drawer | BLOCKED_EXTERNAL | Solo PDF/OneNote virtuali; `host-win7-hardware-inventory.txt` |
| 30 | Scanner e privacy | BLOCKED_EXTERNAL | Nessuno scanner; inventario redatto + privacy scan locale |

Causa, evidence, scope completato e prossimo comando esatto per ciascun
`FAIL`/`BLOCKED_EXTERNAL` sono dettagliati nel documento handoff canonico.

## 9. Conteggio Admin Console

| Entità | Valore | Fonte | Stato |
|---|---:|---|---|
| Prodotti staging | `19.762` attesi | task master, non osservazione runtime | BLOCKED_EXTERNAL |
| Categorie staging | sconosciuto | login Admin richiesto | BLOCKED_EXTERNAL |
| Fornitori staging | sconosciuto | login Admin richiesto | BLOCKED_EXTERNAL |
| Prezzi staging | sconosciuto | login Admin richiesto | BLOCKED_EXTERNAL |

Evidence blocker:
`C:\Dev\Win7POS-QA\2026-07-14_ASUS_RUNTIME\evidence\staging-admin-login-blocked.png`
(SHA256
`8a88cb58b0b9aefa00006244531a421a72e38ce2aa97519875257597792b8969`).

## 10. Conteggi SQLite

Nessun SQLite staging autenticato è stato prodotto; i conteggi staging restano
`BLOCKED_EXTERNAL` e non sono sostituiti dai benchmark.

| Dataset | Prodotti attivi | Remote product ID distinti | Prezzi | Pending price | Completeness |
|---|---:|---:|---:|---:|---|
| Staging reale | sconosciuto | sconosciuto | sconosciuto | sconosciuto | sconosciuto |
| Sintetico batch monolitico | 19.762 | 19.762 nello scenario | 19.762 | 0 | scenario deterministico, non staging |
| Sintetico batch paginato apply-only | 19.762 | 19.762 nello scenario | 19.762 | 0 | apply-only, non prova full reconcile |
| Sintetico full apply+reconcile+verify | 19.762 | 19.762 | 19.762 | 0 | `Verified` in 3/3 iterazioni |

## 11. Duplicati, orfani, pending e tombstone

| Invariante | Staging reale | Software/sintetico |
|---|---|---|
| duplicate remote product ID | sconosciuto — BLOCKED_EXTERNAL | mismatch fail-closed testato |
| duplicate active barcode | sconosciuto — BLOCKED_EXTERNAL | collisione case-insensitive e conflitto identità testati |
| product senza `product_meta` | sconosciuto — BLOCKED_EXTERNAL | audit fail-closed testato |
| category/supplier orphan | sconosciuto — BLOCKED_EXTERNAL | audit e orphan reference-map testati |
| invalid name/barcode/price | sconosciuto — BLOCKED_EXTERNAL | audit fail-closed testato |
| pending remote price | sconosciuto — BLOCKED_EXTERNAL | replay/queue testati; sintetico finale atteso `0` |
| ownership `remote_price_id` | sconosciuto — BLOCKED_EXTERNAL | owner immutabile; drift same-owner fail-closed; quarantine legacy append-only soltanto nel full autorevole |
| ACK catalog import prezzo | non applicabile | transazione all-or-nothing; `RemotePriceIds[]` top-level prevale sul campo legacy sovrapposto, inclusa wildcard tipo vuoto |
| tombstone inattivi | sconosciuto — BLOCKED_EXTERNAL | soft tombstone product/category/supplier testati |
| remote non autorevoli ancora attivi | sconosciuto — BLOCKED_EXTERNAL | full reconcile disattiva e verifica |
| `hasMore` finale | sconosciuto — BLOCKED_EXTERNAL | full drena fino a false; delta riprende dopo 8 pagine |

## 12. Sync mode e trigger full/delta

| Evento | Modalità prevista | Garanzia implementata |
|---|---|---|
| startup con cursor valido | delta | cursor shop/epoch-bound, nessun full inutile |
| sync periodica/reconnect | delta | max 8 pagine/run, checkpoint atomico |
| restart durante catena delta | delta resume | pin versione/summary/mode + cursor fingerprints persistiti |
| bootstrap/cursor assente o legacy | full | reset e full autorevole |
| cursor rifiutato/scaduto | full | repair controllato, niente loop |
| risposta server `full_refresh` | full | boundary autorevole nuovo |
| cambio shop autorizzato | full | epoch++, reset binding/cursor/sale-safe/cache |
| restore | full | review bloccante fino a nuovo full same-shop |
| mismatch/integrity/identity/apply skip | full repair | `Unverified`/`Mismatch`, vendita shop-bound fail-closed |
| comando admin autorizzato | full repair | conferma non distruttiva; dialog non chiudibile mentre busy |

I checkpoint delta sono completi e fail-closed: versione catalogo massima 128
caratteri senza control/surrounding whitespace, fino a 256 fingerprint completi
senza truncation e pin della presenza summary cross-run. Una summary già pinned
non può sparire. Duplicati product/category/supplier sono rifiutati per pagina;
il relink delta usa temp table indicizzate e tocca soltanto i prodotti target.
`repair_required` ammette solo `0`/`1`. Status UI e persistenza vendita usano lo
stesso evaluatore sale-safety con reason code. Il ViewModel Shop Settings si
disiscrive dal cambio lingua, rilascia l'owner e mantiene un busy repair separato.

Il restart staging reale e il reconnect restano `BLOCKED_EXTERNAL`; i test locali
provano la macchina a stati, non il servizio staging.

## 13. Benchmark prima/dopo

Ambiente: Windows `10.0.26200`, SDK `10.0.301`, DB isolato per iterazione.

| Scenario | Legacy mediana | Batch mediana | Miglioramento |
|---|---:|---:|---:|
| Wall 2.000 | `17,324.898 ms` | `308.594 ms` | `56.14x` |
| Throughput 2.000 | `115.44 righe/s` | `6,481.00 righe/s` | `56.14x` |
| CPU 2.000 | `8,109.375 ms` | `312.500 ms` | `25.95x` |
| Working set mediano | `89,567,232 byte` | `104,366,080 byte` | `+16.52%` |
| DB finale | `1,572,864 byte` | `1,847,296 byte` | `+17.45%`, reference map autorevole |

Scale test:

- probe storico pre-closure monolitico 19.762: mediana `963.944 ms`, `20,501.18 righe/s`, pending `0`;
- probe storico pre-closure paginato apply-only 19.762/1.000: mediana `2,352.709 ms`,
  `8,399.68 righe/s`, pending `0`;
- closure paginato full apply+reconcile+verify 19.762:
  mediana `4,747.793 ms`, `4,162.36 righe/s`, CPU `4,937.500 ms`,
  working set `174,350,336` byte, DB `15,253,504` byte, pending `0`, tre
  iterazioni `Verified`.

TRX finali `catalog-performance-final-closure-2000.trx` e
`catalog-performance-final-closure-19762-paged-full.trx` sotto
`C:\Dev\Win7POS-QA\2026-07-14_ASUS_RUNTIME\evidence`. Nessun benchmark è
presentato come staging.

## 14. Screenshot UI e matrice DPI

Inventario statico: 42 XAML totali, 38 superfici interattive, 31 dialog. Gate
owner/sizing/footer/title/shared resources verdi nella validazione finale.

| Profilo | Stato | Screenshot |
|---|---|---|
| 1024×768, 100% | BLOCKED_EXTERNAL | assente |
| 1024×768, 125% | BLOCKED_EXTERNAL | assente |
| 1366×768, 100% | BLOCKED_EXTERNAL | assente |
| 1366×768, 125% | BLOCKED_EXTERNAL | assente |
| 1024×600 best-effort | BLOCKED_EXTERNAL | assente |
| multi-monitor best-effort | BLOCKED_EXTERNAL | assente |

Causa: manca una fixture autenticata e un ambiente display/Win7 controllato.
Completato: remediation P1 statiche, localizzazione EN/ES/IT/ZH, focus barcode,
scroll/footer e UI exactness/repair. Prossimo comando esatto:

```powershell
$env:WIN7POS_DATA_DIR='C:\Win7POSTest\ASUS-20260714\ui-matrix'; & 'C:\Dev\Win7POS\dist\Win7POS\Win7POS.Wpf.exe'
```

## 15. Windows 7

Stato: `BLOCKED_EXTERNAL`.

Evidence host/hardware redatta:
`C:\Dev\Win7POS-QA\2026-07-14_ASUS_RUNTIME\evidence\host-win7-hardware-inventory.txt`.

- Causa: host Windows 11; nessun Win7 SP1 fisico/VM disponibile.
- Evidenza: Windows `10.0.26200`, x64; nessun cmdlet/provider Hyper-V presente.
- Completato: target net48/x86, manifest e prereq checker Win7, SQLite native x86
  validator.
- Prossimo comando esatto su Win7 SP1:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File 'C:\Win7POSTest\Win7POS\check-win7-prereqs.ps1'
```

La sequenza install/login/full/restart/sale/refund/void/restore/offline/power-loss
non è stata eseguita e non è dichiarata PASS.

## 16. Installer e SHA256

| Voce | Valore/stato |
|---|---|
| Pack | `C:\Dev\Win7POS\dist\Win7POS` |
| Installer | path/timestamp post-commit in `final-release-evidence.txt` |
| ZIP univoco | path/timestamp post-commit in `final-release-evidence.txt` |
| VERSION SHA | uguale allo HEAD finale; valore in `final-release-evidence.txt` |
| VERSION TreeState | `clean` |
| Installer SHA256 | valore in `final-release-evidence.txt` |
| ZIP SHA256 | valore in `final-release-evidence.txt` |
| PE WPF | `0x014c` (x86), evidence post-commit |
| PE e_sqlite3 | `0x014c` (x86), evidence post-commit |
| Pack/ZIP validators | PASS: completeness/runtime su folder e ZIP, required gates sul folder; output in evidence post-commit |
| Install/uninstall | BLOCKED_EXTERNAL: processo non elevato |

Il pack obsoleto dello snapshot (`CommitSHA=a9c0ab3...`) è stato sostituito da
una build da worktree pulito post-commit. Il flusso finale include manifest,
ricompilazione Inno dopo i manifest, ZIP univoco e validator folder/ZIP. Comando
base; la sequenza riproducibile completa è nella sezione PASS del test 3
dell'handoff canonico:

```powershell
$env:WIN7POS_DOTNET_EXE='C:\Dev\dotnet10\dotnet.exe'; $env:ISCC_EXE=(Resolve-Path "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe").Path; powershell -NoProfile -ExecutionPolicy Bypass -File scripts\win7pos\windows\build-release-x86.ps1 -BuildInstaller
```

## 17. Stampante, scanner e cassetto

Stato: `BLOCKED_EXTERNAL`.

| Periferica | Evidenza host | Scope software completato | Prossimo comando esatto |
|---|---|---|---|
| Stampante ricevute | solo Microsoft PDF/OneNote virtuali | sale-before-print, print failure non distruttivo, no default/virtual non autorizzato | `Get-CimInstance Win32_Printer | Format-Table Name,DriverName,Default,PortName` |
| Cash drawer | assente | opt-in, soltanto printer fisica configurata, niente apertura card involontaria | `& 'C:\Dev\Win7POS\dist\Win7POS\Win7POS.Wpf.exe'` |
| Scanner barcode | assente | focus/Enter/double-submit/unknown barcode coperti software | `& 'C:\Dev\Win7POS\dist\Win7POS\Win7POS.Wpf.exe'` |

Non sono stati eseguiti 58/80 mm, taglio, accenti/cinese, reprint, drawer o
scanner durante dialog modale.

## 18. Secret scan

Nessun PIN, password, token, cookie o connection string è stato aggiunto ai
documenti o alle evidence. Lo screenshot mostra soltanto campi login vuoti. I
checker repository/release cercano service-role, bearer token e payload
sensibili.

| Controllo | Stato |
|---|---|
| checker `check-security-hardening.ps1` | PASS |
| scan testuale source/report | PASS con fallback regex ad alta confidenza; nessun valore sensibile |
| scan release pack/ZIP | PASS post-commit, registrato in `final-release-evidence.txt` |
| gitleaks | non disponibile; limite dichiarato, non PASS |
| trufflehog | non disponibile; limite dichiarato, non PASS |
| detect-secrets | non disponibile; limite dichiarato, non PASS |

Comando finale senza stampare valori ambiente:

```powershell
rg -n --hidden -g '!**/bin/**' -g '!**/obj/**' -g '!dist/**' '(?i)(service[_-]?role|authorization\s*:\s*bearer\s+[A-Za-z0-9._~+/-]{8,}|mcpos_(device|session)_[A-Za-z0-9_-]{8,})' .
```

Ogni match deve essere revisionato; marker di checker/documentazione non sono
automaticamente un secret.

## 19. Rischi residui

| Rischio | Stato | Impatto | Prossima azione |
|---|---|---|---|
| Exactness staging non osservata | BLOCKED_EXTERNAL | Non è possibile affermare 19.762/Verified o zero duplicati/orfani reali | completare test 7 e 9 con fixture autorizzata |
| Contract Admin checksum/canonicalizzazione non confermato | BLOCKED_EXTERNAL | un eventuale checksum, se presente ma non comparabile, resta correttamente Unverified | acquisire risposta redatta e specifica canonicalizzazione; dettaglio e comando coincidono con test 9 nell'handoff |
| Restart/reconnect/cross-shop reali | BLOCKED_EXTERNAL | fake harness non rileva comportamento del servizio reale | eseguire test 8, 10, 17, 18, 22 |
| UI visual/DPI | BLOCKED_EXTERNAL | possibile clipping/font/translation expansion non rilevabile staticamente | eseguire sei profili UI |
| Win7 | BLOCKED_EXTERNAL | compatibilità runtime/driver non certificata | usare PC/VM Win7 SP1 |
| Installer elevato | BLOCKED_EXTERNAL | install/uninstall non certificati | aprire PowerShell elevata su host QA |
| Hardware POS | BLOCKED_EXTERNAL | spooler/scanner/drawer reali non certificati | collegare periferiche QA |
| Artefatti trasferimento storici assenti | FAIL | SHA originari non ricalcolati | recuperare bundle e lanciare `Get-FileHash` dell'handoff |

Per ogni riga FAIL/BLOCKED_EXTERNAL il documento handoff contiene causa,
evidenza, scope completato e comando esatto.

## 20. Working tree finale

Lo stato post-commit, il push e il draft PR sono attestati fuori dal report per
evitare autoreferenzialità; la release è costruita soltanto con worktree pulito.

| Voce | Valore finale |
|---|---|
| `git status --short --branch` | clean post-commit; output esatto in `final-release-evidence.txt` |
| HEAD | valore post-commit in `final-release-evidence.txt` |
| `VERSION.txt CommitSHA` | uguale allo HEAD finale; valore nella stessa evidence |
| `VERSION.txt TreeState` | `clean` |
| Branch pushed | sì; ref/risultato in `final-release-evidence.txt` |
| Draft PR | URL in `final-release-evidence.txt` e nel messaggio finale |
| Merge PR | non eseguito, come richiesto |

Comandi finali obbligatori:

```powershell
git diff --check
git status --short --branch
git rev-parse HEAD
Get-Content dist\Win7POS\VERSION.txt
```

Il report non può contenere in modo autosufficiente l'hash dell'installer
costruito da un commit che modifica lo stesso report senza introdurre una
dipendenza circolare. I SHA256 finali devono quindi essere registrati come
evidence esterna post-commit e riportati nel messaggio finale/PR, oppure il pack
deve essere ricostruito dopo il commit report e il valore inserito in un commit
successivo esplicitamente documentato.
