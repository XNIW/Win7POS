# Win7POS ASUS Runtime Validation — 2026-07-14

Stato handoff: `PARTIAL_CLOSURE_EXTERNAL_VALIDATION_REQUIRED`

Definition of Done globale: `NOT_MET`

Ultimo snapshot documentale: `2026-07-14T22:43:21-04:00`.

Questo documento resta la source of truth per la matrice ASUS 1–30. Distingue
esplicitamente tre classi di evidenza:

- `PASS`: il comportamento indicato è stato realmente eseguito nel perimetro
  dichiarato;
- `FAIL`: il test obbligatorio non è stato completato o l'artefatto esaminato
  non rappresenta la revisione corrente;
- `BLOCKED_EXTERNAL`: l'esecuzione end-to-end richiede credenziali, un target
  Windows 7 o hardware non disponibili su questo host.

Un test unitario, un checker statico o un fake HTTP sono riportati come scope
software completato, ma non trasformano in `PASS` uno scenario che richiede
login staging, Windows 7 o periferiche fisiche.

## Revisione, host ed evidence root

- Repository: `https://github.com/XNIW/Win7POS.git`.
- Branch: `integration/asus-catalog-ui-runtime-20260714`.
- Remediation locale preservata:
  `31b3de20c4747e33c422bb495706f79e614f3df2`.
- Shop-scoped sync preservato:
  `94bb9573544811ef97c45f74b4ccf3ac85dc10de`.
- `origin/main` integrato:
  `8c39e13c0f7a001956023919dd6bda612288351f`.
- Merge di integrazione:
  `00a3fe00c72d57fe4af1b2febda942cac035b7f8`.
- HEAD pre-closure allo snapshot:
  `259484c4f10e6c4cf50848e617264662eae72492`.
- Closure software commit:
  `bb4178b506afe5ebc86313f010e2271ca8075f84`.
- HEAD finale post-commit, `VERSION.txt`, hash release, stato Git e draft PR:
  valori autorevoli in `C:\Dev\Win7POS-QA\2026-07-14_ASUS_RUNTIME\evidence\final-release-evidence.txt`, generato dopo il commit documentale per evitare provenance autoreferenziale.
- Evidence esterne:
  `C:\Dev\Win7POS-QA\2026-07-14_ASUS_RUNTIME\evidence`.
- Host: ASUS Zenbook 14 UX3405CA, Windows 11 Home Single Language
  `10.0.26200`, OS x64, processo non elevato.
- Toolchain: SDK locale .NET `10.0.301`, Visual Studio Build Tools/MSBuild
  `17.14.40.60911`, PowerShell `7.6.3`, Inno Setup `6.7.3`.
- Rete: Wi-Fi attiva; lo stato della rete non sostituisce l'autorizzazione
  staging.
- Stampanti enumerate: `Microsoft Print to PDF` e `OneNote (Desktop)`, entrambe
  virtuali; nessuna stampante ricevute, scanner o cash drawer fisici rilevati.
- Nessun target/VM Windows 7 disponibile; il cmdlet Hyper-V `Get-VM` non è
  installato su questo host.

## Blocco staging verificato

Il base URL staging pubblico conduce alla pagina di accesso della Console Admin.
Non era disponibile alcuna sessione o fixture autorizzata. Nessun tentativo è
stato fatto di automatizzare il login, recuperare cookie o registrare segreti.

- Screenshot redatto:
  `C:\Dev\Win7POS-QA\2026-07-14_ASUS_RUNTIME\evidence\staging-admin-login-blocked.png`.
- SHA256 screenshot:
  `8a88cb58b0b9aefa00006244531a421a72e38ce2aa97519875257597792b8969`.
- Dimensione: `47,338` byte.
- Timestamp file: `2026-07-14T19:53:28.1442999-04:00`.

Il valore prodotti `19.762` proviene esclusivamente dal task master. Non è stato
letto dalla Console Admin in questa sessione e non è hardcoded nel runtime.
I conteggi autorevoli di categorie, fornitori e prezzi non sono disponibili.

## Evidenze software completate

Il rerun di closure sulla revisione software committata ha prodotto i risultati
seguenti; pack, ZIP e validator post-commit sono attestati separatamente nella
evidence finale esterna:

| Controllo | Stato ultimo eseguito | Evidenza/nota |
|---|---|---|
| Core tests | PASS | `152/152`; include catalog exactness, batch, tombstone, shop transition, sale safety, ACK wildcard e stock dopo rinomina barcode |
| WPF Release x86/net48 | PASS | `0` warning, `0` errori; output `bin\x86\Release\net48` |
| CLI `--selftest --keepdb` | PASS | Vendita cash/refund/void e isolamento DB temporaneo |
| CLI `--task081-sales-sync-harness` | PASS | Origin binding, ACK/idempotenza, retry/block |
| CLI supplier apply | PASS | Backup, transazione, rollback, price history |
| CLI catalog import outbox | PASS | Binding e payload redatto |
| CLI catalog import fake HTTP | PASS | ACK/idempotenza e shop response-side |
| CLI SQLite integrity | PASS | Integrity selftest |
| CLI restore guard | PASS | Restore same-shop/cross-shop policy |
| CLI, 7 harness canonici | PASS | Publish Release isolato in evidence e avvio DLL con `C:\Dev\dotnet10\dotnet.exe`; l'apphost in-tree è bloccato da Application Control `0x800711C7`, documentato in `smart-app-control-cli.txt` |
| 28 checker non-release | PASS | Eseguiti individualmente sul diff di closure |
| 3 checker pack-aware / 31 totali | PASS post-commit | Completeness, runtime validation e required gates su folder/ZIP; output autorevole in `final-release-evidence.txt` |
| Package audit | PASS | Nessun pacchetto vulnerabile/deprecato nei 6 progetti; fallback secret scan senza finding; scanner dedicati non installati |

Comandi canonici finali:

```powershell
& 'C:\Dev\dotnet10\dotnet.exe' restore Win7POS.slnx
& 'C:\Dev\dotnet10\dotnet.exe' build Win7POS.slnx -c Release --no-restore
& 'C:\Dev\dotnet10\dotnet.exe' test tests\Win7POS.Core.Tests\Win7POS.Core.Tests.csproj -c Release --no-restore
& 'C:\Dev\dotnet10\dotnet.exe' build src\Win7POS.Wpf\Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86 --no-restore
pwsh -NoProfile -File scripts\check-required-gates.ps1 -ReleasePackSource dist\Win7POS
pwsh -NoProfile -File scripts\check-release-pack-completeness.ps1 -ReleasePackSource dist\Win7POS
pwsh -NoProfile -File scripts\check-win7-runtime-release-validation.ps1 -ReleasePackSource dist\Win7POS
Get-ChildItem scripts\check-*.ps1 | Where-Object Name -notin 'check-release-pack-completeness.ps1','check-win7-runtime-release-validation.ps1','check-required-gates.ps1' | Sort-Object Name | ForEach-Object { pwsh -NoProfile -File $_.FullName; if ($LASTEXITCODE -ne 0) { throw "Gate failed: $($_.Name)" } }
```

## Catalog exactness e policy sync

Il client implementa contratto `catalogSummary` backward-compatible, stati
`Verified` / `Unverified` / `Mismatch`, audit SQLite fail-closed, diagnostica
shop-bound, riconciliazione full, identity map autorevole, rilevamento
duplicati/orfani/pending e comando amministratore di full repair. Un checksum
fornito è validabile soltanto se è SHA-256/64 hex e la canonicalizzazione locale
è definita; in caso contrario resta `Unverified`, mai `Verified` per
presunzione.

La policy implementata è:

- delta per startup ordinario con cursor valido, sync periodica, reconnect e
  restart;
- massimo otto pagine delta per run, con cursor e pin di versione/summary/catena
  persistiti per la ripresa successiva;
- full refresh per bootstrap, cursor assente/legacy/rifiutato, risposta
  `full_refresh`, cambio shop, restore, mismatch/integrity failure o repair
  amministrativo;
- nessun sale-safe prima di full autorevole riconciliato; un normale delta
  temporaneamente fallito non toglie il sale-safe già valido;
- mismatch, apply skip, identità collassata, snapshot/pin incoerente o cursor
  ciclico richiedono repair e falliscono chiusi;
- checkpoint delta persistente completo e fail-closed, versione catalogo limitata
  a 128 caratteri senza control/whitespace, fino a 256 fingerprint senza
  truncation e pin della presenza summary anche cross-run;
- duplicati product/category/supplier respinti per pagina; relink reference
  incrementale limitato ai target della pagina, senza rewrite globale;
- ownership prezzo remota immutabile: drift same-owner fail-closed; soltanto un
  full autorevole può mettere in quarantine append-only evidenza legacy ambigua;
- ACK catalog import atomico con `RemotePriceIds[]` top-level prioritario anche
  rispetto alla semantica wildcard del legacy `Items[].RemotePriceId`;
- il repository vendita verifica il sale-safe nella stessa transazione delle
  vendite ordinarie shop-bound, usando lo stesso evaluatore reasoned mostrato
  dallo status UI; `repair_required` accetta solo `0`/`1`.

La prova staging resta `BLOCKED_EXTERNAL`: non esiste un DB staging autenticato
da interrogare e non sono disponibili i conteggi Admin autorevoli.

## Performance locale riproducibile

Questi benchmark sono dinamici su SQLite isolato, ma sintetici: non costituiscono
un full refresh staging.

| Scenario | Mediana wall | Throughput mediano | CPU mediana | Nota |
|---|---:|---:|---:|---|
| Legacy per-riga, 2.000 | `17,324.898 ms` | `115.44 righe/s` | `8,109.375 ms` | Closure code, stesso input controllato |
| Batch, 2.000 | `308.594 ms` | `6,481.00 righe/s` | `312.500 ms` | `56.14x` wall/throughput; `25.95x` CPU |
| Batch monolitico, 19.762 | `963.944 ms` | `20,501.18 righe/s` | `1,234.375 ms` | 19.762 prodotti/prezzi, pending `0` |
| Batch paginato apply-only, 19.762 | `2,352.709 ms` | `8,399.68 righe/s` | `2,484.375 ms` | Pagine da 1.000 |
| Batch paginato full apply+reconcile+verify, 19.762 | `4,747.793 ms` | `4,162.36 righe/s` | `4,937.500 ms` | Closure code; 20 pagine, pending `0`, tre iterazioni `Verified` |

Evidence disponibili:

- `C:\Dev\Win7POS-QA\2026-07-14_ASUS_RUNTIME\evidence\catalog-performance-final-closure-2000.trx`;
- `C:\Dev\Win7POS-QA\2026-07-14_ASUS_RUNTIME\evidence\catalog-performance-final-19762.trx`;
- `C:\Dev\Win7POS-QA\2026-07-14_ASUS_RUNTIME\evidence\catalog-performance-final-19762-paged.trx`;
- `C:\Dev\Win7POS-QA\2026-07-14_ASUS_RUNTIME\evidence\catalog-performance-final-closure-19762-paged-full.trx`.

I due probe apply-only a 19.762 sono storici pre-closure. L'incremento DB del
confronto closure 2.000 (`1,572,864` → `1,847,296` byte, `+17.45%`) deriva dalla
persistenza aggiuntiva della reference map autorevole;
entrambi i percorsi persistono anche l'ownership immutabile dei prezzi. Il
working set mediano aumenta di circa `16.52%`. La correttezza e la
tracciabilità dell'identità hanno precedenza sul minor spazio.

## Matrice runtime ASUS 1–30

Il timestamp di ogni riga è il momento dell'ultima classificazione in questa
sessione (`2026-07-14T22:43:21-04:00`) quando non è disponibile un timestamp più
specifico.

| # | Test | Stato | Azione/comando eseguito | Evidenza e perimetro reale |
|---:|---|---|---|---|
| 1 | Integrità trasferimento storico | FAIL | Ricerca dei tre nomi e `Test-Path` sui path di handoff | I tre artefatti storici non sono presenti su questo host; i loro hash pubblicati non sono stati ricalcolati. La nuova release è una prova separata. |
| 2 | Prerequisiti Win7 | BLOCKED_EXTERNAL | Inventario host/VM e toolchain | Host Windows 11; nessun Win7/VM disponibile. |
| 3 | Contenuto pack finale | PASS | Build pulita post-commit, manifest, Inno rebuild, ZIP univoco e validator folder/ZIP | `VERSION.txt` allineato allo HEAD finale e `TreeState=clean`; path/hash/PE in `final-release-evidence.txt`. |
| 4 | Isolamento dati | PASS | CLI `--selftest --keepdb` con DB temporaneo fuori repo | Persistenza dinamica isolata; nessun uso di dati produzione. |
| 5 | Configurazione staging pubblica | PASS | `scripts\check-public-staging-config.ps1` | Base URL HTTPS pubblico, nessun path/query/credenziale nel config tracked. |
| 6 | Cold start x86 | PASS | Smoke pre-auth sull'EXE Release x86 finale, senza tentare autenticazione | Shell e dialog POS access renderizzati; Cancel chiude il processo. Evidence redatta `C:\Dev\Win7POS-QA\2026-07-14_ASUS_RUNTIME\evidence\wpf-preauth-smoke.txt`. Non è prova Win7. |
| 7 | First login shop A | BLOCKED_EXTERNAL | Apertura Console Admin pubblica | Credenziali/fixture staging non disponibili; screenshot login blocker. |
| 8 | Restart trusted session | BLOCKED_EXTERNAL | Non eseguito | Richiede una sessione trusted creata dal test 7. |
| 9 | Catalog full pull esatto | BLOCKED_EXTERNAL | Contratti/audit/test locali completati; tentativo Admin fermo al login | Nessun conteggio Admin o DB staging; `19.762` non è stato osservato. |
| 10 | Catalog delta/restart | BLOCKED_EXTERNAL | Policy e regressioni locali completate | Nessuna sessione staging autenticata per il restart reale. |
| 11 | Cash sale | PASS | CLI `--selftest --keepdb` | Ledger, linee, stock movement e outbox provati dinamicamente nella stessa transazione SQLite isolata. |
| 12 | Card sale | BLOCKED_EXTERNAL | Test repository card e checker no-auto-print completati | Persistenza/software provati localmente, ma il flusso WPF autenticato con stampante configurata/non configurata non è stato eseguito in questa sessione. |
| 13 | Partial refund | PASS | CLI `--selftest --keepdb` e sales harness | Refund dinamico conserva originale/residuo e `operation_type=refund`. |
| 14 | Full void | PASS | CLI `--selftest --keepdb` e sales harness | Inversione dinamica idempotente e `operation_type=void`. |
| 15 | Sales origin binding | PASS | CLI `--task081-sales-sync-harness` | Binding id/code, schema, client id/idempotenza e hash verificati senza loggare payload/segreti. |
| 16 | Catalog-import binding | PASS | CLI supplier apply + catalog import outbox selftest | Binding shop e payload redatto provati su DB isolato. |
| 17 | Offline/reconnect drain | BLOCKED_EXTERNAL | Fake HTTP/retry locale completato | Il reconnect end-to-end della sessione staging non è stato eseguito. |
| 18 | Restart stale `in_progress` | BLOCKED_EXTERNAL | Lease/retry regressions locali completate | Non è stato eseguito un kill reale fra prepare e ACK in una sessione autenticata. |
| 19 | Duplicate sales ACK | PASS | CLI `--task081-sales-sync-harness` | ACK duplicato/tardivo non duplica vendita, stock o stato locale nel harness dinamico. |
| 20 | Duplicate catalog ACK | PASS | CLI `--catalog-import-sync-http-harness` | Fake HTTP dinamico verifica idempotenza, remote identity e shop response-side. |
| 21 | Retry bound | PASS | Sales harness e catalog fake HTTP con errore transient | Backoff bounded; catalog attempt 11 + timeout passa a `failed_blocked` ad attempt 12 senza loop. |
| 22 | Cross-shop drain | BLOCKED_EXTERNAL | Catalog cross-shop dinamico e guard sales statici completati | Nessuna sessione reale A→B per provare entrambi i drain prima dell'HTTP. |
| 23 | Legacy proven backfill | PASS | Core tests/outbox harness | Binding A resta derivato da prova payload/hash, non dallo snapshot B. |
| 24 | Legacy ambiguous block | PASS | Core tests/outbox harness | Ambigui restano unbound e diventano `failed_blocked` senza HTTP. |
| 25 | Switch con outbox | PASS | `PosShopTransitionGuardTests` | Tutti gli stati irrisolti sales/catalog bloccano A→B. |
| 26 | Switch dopo drain/race | PASS | `PosShopTransitionGuardTests` | Barriera/epoch/reset cache e race pull A provati dinamicamente. |
| 27 | Tombstone | PASS | `RemoteCatalogReferenceTombstoneTests` e batch tests | Soft tombstone product/category/supplier, nessun hard delete/riuso locale. |
| 28 | Backup/restore cross-shop | PASS | CLI `--db-restore-guard-selftest` e `RestoreShopSafetyTests` | Rifiuto pre-copia cross-shop e repair/full-refresh same-shop provati su DB isolato. |
| 29 | Printer/cash drawer | BLOCKED_EXTERNAL | `host-win7-hardware-inventory.txt` | Solo PDF/OneNote virtuali; nessuna stampante ricevute o drawer. Checker software safety completato. |
| 30 | Scanner e privacy | BLOCKED_EXTERNAL | `host-win7-hardware-inventory.txt` + scanner sicurezza/log | Nessuno scanner fisico; privacy/secret checker locale completato senza trovare credenziali reali. |

## Dettaglio obbligatorio del FAIL

### Test 1 — Integrità trasferimento storico

- Causa: il bundle `win7pos-runtime-handoff-20260714` e i tre artefatti nominati
  nell'handoff originario non sono disponibili sul filesystem ASUS corrente.
- Evidenza: `Test-Path` restituisce `False` per
  `Win7POS-ASUS-runtime-candidate-20260714.zip`, `win7pos-wave1.patch` e
  `win7pos-wave1-untracked-sources.tar.gz`; nessun file corrispondente è stato
  trovato sotto `C:\Dev` o il profilo utente.
- Completato comunque: ancestry Git verificata; remediation e shop-scoped sync
  sono entrambi antenati del branch di integrazione. La nuova release viene
  costruita e hashata separatamente.
- Prossimo comando esatto, dopo aver copiato il bundle originale in
  `C:\Win7POSTest\handoff`:

```powershell
Get-FileHash 'C:\Win7POSTest\handoff\Win7POS-ASUS-runtime-candidate-20260714.zip','C:\Win7POSTest\handoff\win7pos-wave1.patch','C:\Win7POSTest\handoff\win7pos-wave1-untracked-sources.tar.gz' -Algorithm SHA256
```

## Evidenze di chiusura dei PASS 3 e 6

### Test 3 — Contenuto pack finale (PASS)

- Stato precedente chiuso: il pack `a9c0ab3...` era obsoleto.
- Evidenza finale: `dist\Win7POS\VERSION.txt`, installer, ZIP univoco, SHA256,
  PE `0x014c` e validator folder/ZIP sono registrati dopo il commit documentale
  in `C:\Dev\Win7POS-QA\2026-07-14_ASUS_RUNTIME\evidence\final-release-evidence.txt`.
- Comando riproducibile:

```powershell
$env:WIN7POS_DOTNET_EXE='C:\Dev\dotnet10\dotnet.exe'; $env:ISCC_EXE=(Resolve-Path "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe").Path; powershell -NoProfile -ExecutionPolicy Bypass -File scripts\win7pos\windows\build-release-x86.ps1 -BuildInstaller
```

### Test 6 — Cold start x86 (PASS)

- Evidenza finale redatta:
  `C:\Dev\Win7POS-QA\2026-07-14_ASUS_RUNTIME\evidence\wpf-preauth-smoke.txt`.
- Scope reale: avvio EXE Release x86, shell e dialog POS access renderizzati,
  nessun login automatizzato, chiusura con Cancel; non certifica Windows 7.
- Comando riproducibile:

```powershell
$env:WIN7POS_DATA_DIR='C:\Win7POSTest\ASUS-20260714\cold-start'; New-Item -ItemType Directory -Force -Path $env:WIN7POS_DATA_DIR | Out-Null; & 'C:\Dev\Win7POS\src\Win7POS.Wpf\bin\x86\Release\net48\Win7POS.Wpf.exe'
```

## Dettaglio obbligatorio dei BLOCKED_EXTERNAL

### Test 2 — Prerequisiti Win7

- Causa: host Windows 11; nessun PC/VM Windows 7 SP1 disponibile.
- Evidenza: OS `Microsoft Windows 11 Home Single Language 10.0.26200`; nessun
  provider Hyper-V/cmdlet `Get-VM` disponibile.
- Completato comunque: build net48/x86, manifest Win7, controlli statici .NET
  4.8/VC++ x86 e prereq script.
- Prossimo comando esatto su un target Windows 7 SP1:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File 'C:\Win7POSTest\Win7POS\check-win7-prereqs.ps1'
```

### Test 7 — First login shop A

- Causa: nessuna credenziale/fixture staging autorizzata.
- Evidenza: `staging-admin-login-blocked.png` con hash riportato sopra.
- Completato comunque: contratto first-login, sale-safe gate, double-submit e
  logging redatto passano i controlli software.
- Prossimo comando esatto; inserire la fixture solo interattivamente:

```powershell
$env:WIN7POS_DATA_DIR='C:\Win7POSTest\ASUS-20260714\staging-shop-a'; & 'C:\Dev\Win7POS\dist\Win7POS\Win7POS.Wpf.exe'
```

### Test 8 — Restart trusted session

- Causa: dipende dalla sessione trusted non creabile senza completare il test 7.
- Evidenza: stesso screenshot login; `%LocalAppData%\Win7POS` non conteneva una
  sessione staging preesistente utilizzabile.
- Completato comunque: DPAPI/token redaction e heartbeat sono coperti dai gate.
- Prossimo comando esatto dopo il PASS del test 7:

```powershell
Stop-Process -Name Win7POS.Wpf -ErrorAction SilentlyContinue; & 'C:\Dev\Win7POS\dist\Win7POS\Win7POS.Wpf.exe'
```

### Test 9 — Catalog full pull esatto

- Causa: accesso Admin/POS staging assente; conteggi autorevoli category,
  supplier e price non disponibili; nessun DB staging drenato.
- Evidenza: screenshot login; directory evidence priva di screenshot conteggi o
  export SQLite staging.
- Completato comunque: contratto summary, full drain, audit exactness,
  riconciliazione, identity map, sale barrier e test mismatch/duplicate/orphan.
- Prossimo comando esatto, usando la fixture interattiva e il data dir QA:

```powershell
$env:WIN7POS_DATA_DIR='C:\Win7POSTest\ASUS-20260714\catalog-exactness'; & 'C:\Dev\Win7POS\dist\Win7POS\Win7POS.Wpf.exe'
```

### Test 10 — Catalog delta/restart

- Causa: nessuna trusted session o cursor staging reale disponibile.
- Evidenza: stesso blocker staging; nessun log runtime autenticato.
- Completato comunque: cursor shop-bound, delta 8-page resumption, pin
  cross-run, full triggers e repair policy coperti da regressioni.
- Prossimo comando esatto dopo un full pull PASS:

```powershell
Stop-Process -Name Win7POS.Wpf -ErrorAction SilentlyContinue; & 'C:\Dev\Win7POS\dist\Win7POS\Win7POS.Wpf.exe'
```

### Test 12 — Card sale

- Causa: non è disponibile una sessione WPF staging autenticata né una
  stampante fisica per verificare l'intero comportamento UI/spooler.
- Evidenza: login blocker e inventario stampanti virtual-only.
- Completato comunque: persistenza card dinamica, valori `paid_cash=0` /
  `paid_card>0`, e policy che non abilita auto-print/default non autorizzata.
- Prossimo comando esatto dopo first login:

```powershell
$env:WIN7POS_DATA_DIR='C:\Win7POSTest\ASUS-20260714\card-sale'; & 'C:\Dev\Win7POS\dist\Win7POS\Win7POS.Wpf.exe'
```

### Test 17 — Offline/reconnect drain

- Causa: manca una sessione staging autorizzata sulla quale disconnettere e
  riconnettere realmente la rete.
- Evidenza: login blocker; solo fake HTTP locale disponibile.
- Completato comunque: retry/ACK/idempotenza e nessun doppio stock sono coperti
  dai harness deterministici.
- Prossimo comando esatto dopo first login, prima di eseguire il toggle rete
  controllato:

```powershell
$env:WIN7POS_DATA_DIR='C:\Win7POSTest\ASUS-20260714\offline-reconnect'; & 'C:\Dev\Win7POS\dist\Win7POS\Win7POS.Wpf.exe'
```

### Test 18 — Restart stale `in_progress`

- Causa: non è stato possibile creare una sync staging autenticata e terminare
  il processo nel punto prepare→ACK.
- Evidenza: nessun log kill/restart; lease/retry soltanto nei test locali.
- Completato comunque: payload/hash/client batch immutabili, lease 15 minuti e
  expected-attempt guard sono testati.
- Prossimo comando esatto dopo aver preparato una riga QA `in_progress`:

```powershell
Stop-Process -Name Win7POS.Wpf -Force; Start-Sleep -Seconds 1; & 'C:\Dev\Win7POS\dist\Win7POS\Win7POS.Wpf.exe'
```

### Test 22 — Cross-shop drain

- Causa: nessuna coppia di sessioni/fixture staging shop A e shop B.
- Evidenza: login blocker; catalog guard dinamico e sales guard statico non sono
  un E2E A→B.
- Completato comunque: binding immutabile, block pre-HTTP e transizione
  barrier/epoch coperti da test.
- Prossimo comando esatto con fixture A/B fornite interattivamente:

```powershell
$env:WIN7POS_DATA_DIR='C:\Win7POSTest\ASUS-20260714\cross-shop'; & 'C:\Dev\Win7POS\dist\Win7POS\Win7POS.Wpf.exe'
```

### Test 29 — Printer/cash drawer

- Causa: sono installate solo stampanti virtuali; nessuna stampante ricevute o
  cash drawer fisico.
- Evidenza: `Get-CimInstance Win32_Printer` restituisce soltanto
  `Microsoft Print to PDF` e `OneNote (Desktop)`.
- Completato comunque: vendita-before-print, errore spooler non distruttivo,
  virtual-printer rejection e drawer opt-in/non-card passano il checker.
- Prossimo comando esatto dopo aver collegato stampante e drawer QA:

```powershell
Get-CimInstance Win32_Printer | Format-Table Name,DriverName,Default,PortName; & 'C:\Dev\Win7POS\dist\Win7POS\Win7POS.Wpf.exe'
```

### Test 30 — Scanner e privacy

- Causa: nessuno scanner keyboard-wedge fisico disponibile.
- Evidenza: inventario host/hardware; nessuna evidence di scansione fisica.
- Completato comunque: focus barcode, doppio submit, unknown barcode e
  secret/log redaction hanno copertura software; nessun PIN/token è stato
  acquisito nell'evidence staging.
- Prossimo comando esatto dopo aver collegato lo scanner QA:

```powershell
$env:WIN7POS_DATA_DIR='C:\Win7POSTest\ASUS-20260714\scanner'; & 'C:\Dev\Win7POS\dist\Win7POS\Win7POS.Wpf.exe'
```

## UI/UX runtime matrix

Inventario statico: `42/42` XAML, `38` superfici interattive, `31/31` dialog
derivati da `DialogShellWindow`. Sono stati corretti sizing/scroll, footer e
title condivisi, refund keyboard path, localizzazione EN/ES/IT/ZH, focus barcode
e stato sync/exactness con full repair autorizzato. La matrice visuale non è
stata eseguita e resta `BLOCKED_EXTERNAL` per assenza di fixture autenticata e
profili display/Win7 controllati.

| Profilo | Stato | Evidenza | Prossimo comando esatto |
|---|---|---|---|
| 1024×768, 100% | BLOCKED_EXTERNAL | Nessuno screenshot autenticato | `& 'C:\Dev\Win7POS\dist\Win7POS\Win7POS.Wpf.exe'` |
| 1024×768, 125% | BLOCKED_EXTERNAL | Nessuno screenshot autenticato | `& 'C:\Dev\Win7POS\dist\Win7POS\Win7POS.Wpf.exe'` |
| 1366×768, 100% | BLOCKED_EXTERNAL | Nessuno screenshot autenticato | `& 'C:\Dev\Win7POS\dist\Win7POS\Win7POS.Wpf.exe'` |
| 1366×768, 125% | BLOCKED_EXTERNAL | Nessuno screenshot autenticato | `& 'C:\Dev\Win7POS\dist\Win7POS\Win7POS.Wpf.exe'` |
| 1024×600 best-effort | BLOCKED_EXTERNAL | Nessuno screenshot autenticato | `& 'C:\Dev\Win7POS\dist\Win7POS\Win7POS.Wpf.exe'` |
| Multi-monitor best-effort | BLOCKED_EXTERNAL | Nessun target multi-monitor | `& 'C:\Dev\Win7POS\dist\Win7POS\Win7POS.Wpf.exe'` |

## Release finale

- Pack folder: `C:\Dev\Win7POS\dist\Win7POS`.
- Installer, ZIP univoco, `VERSION.txt CommitSHA`, SHA256 e timestamp: valori
  post-commit autorevoli in
  `C:\Dev\Win7POS-QA\2026-07-14_ASUS_RUNTIME\evidence\final-release-evidence.txt`.
- `VERSION.txt TreeState`: `clean`, verificato sul pack finale.
- PE `Win7POS.Wpf.exe` ed `e_sqlite3.dll`: `0x014c` (x86), con path e hash
  registrati nella stessa evidence.
- Flusso eseguito: build pack pulito, generazione manifest, ricompilazione Inno
  dopo i manifest, ZIP timestamped univoco, validator completeness/runtime su
  folder e ZIP, required gates sul folder.
- Install/uninstall: `BLOCKED_EXTERNAL`, perché il processo non è elevato e
  l'installer richiede privilegi amministrativi. Completati comunque build,
  pack/manifest e validator; prossimi comandi esatti da una PowerShell elevata:

```powershell
$installer = (Get-ChildItem 'C:\Dev\Win7POS\dist' -Filter 'Win7POS*Setup*.exe' -Recurse | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
Start-Process -FilePath $installer -ArgumentList '/VERYSILENT','/NORESTART' -Wait -WindowStyle Hidden
$uninstaller = (Get-ChildItem 'C:\Program Files (x86)\Win7POS' -Filter 'unins*.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT','/NORESTART' -Wait -WindowStyle Hidden
```

## Esito

La closure software locale è sostanziale ma la missione globale non è `DONE`.
Restano obbligatori: conteggi staging esatti e `Verified` su dati reali, UI
autenticata/DPI, Windows 7 SP1, install/uninstall elevati e hardware fisico. Il
branch non deve essere unito a `main` sulla sola base dei proxy locali.
