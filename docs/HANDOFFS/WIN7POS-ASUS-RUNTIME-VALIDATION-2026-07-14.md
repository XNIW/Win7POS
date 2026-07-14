# Win7POS ASUS Runtime Validation — 2026-07-14

Stato handoff: `PUBLISHED_TO_MAIN_FOR_CODEX_ASUS_RUNTIME`

Runtime Windows/UTM/hardware: `EXTERNAL_TEST_PENDING_CODEX_ASUS`

Test ASUS 1–30: `DEFERRED_TO_CODEX_ASUS`

Questo handoff estende la baseline operativa già descritta in
`docs/reports/2026-07-01_CODEX_ASUS_WINDOWS_QA_TASK.md` e
`docs/QA/WIN7_PHYSICAL_SMOKE_REQUEST.md`. Non sostituisce le evidenze storiche e
non dichiara superati test Windows, staging autenticato o hardware non eseguiti
su questa revisione.

## Revisione e confini

- Repository: `https://github.com/XNIW/Win7POS.git`
- Branch di consegna: `main`
- Commit implementazione immutabile:
  `94bb9573544811ef97c45f74b4ccf3ac85dc10de`
- URL commit:
  `https://github.com/XNIW/Win7POS/commit/94bb9573544811ef97c45f74b4ccf3ac85dc10de`
- Data pubblicazione Mac: `2026-07-14`.
- La normalizzazione portabile del basename in
  `CatalogImportOutboxPayloadBuilder.cs`, già presente localmente prima della
  lane, è stata preservata semanticamente e inclusa nella revisione pubblicata.
- Nessun secret, database locale, output `bin/obj`, PDB, log o artefatto Codex è
  incluso nei commit.
- La SHA tip finale di `origin/main` è comunicata nell'aggiornamento di
  pubblicazione insieme a questo handoff. Il commit implementazione qui sopra
  deve risultarne antenato.

## Architettura implementata

Ogni riga di `sales_sync_outbox` e `catalog_import_outbox` ora conserva il
binding immutabile dell'operazione: `origin_shop_id` quando disponibile,
`origin_shop_code`, identificatore client/idempotenza, `operation_type`,
`schema_version`, payload/hash, timestamp e stato retry. L'enqueue legge lo
snapshot negozio ufficiale nella stessa transazione dell'operazione; senza
shop code ufficiale fallisce e non produce una riga ambigua.

Il drain confronta il binding con la sessione trusted prima di `in_progress` e
prima di qualsiasi HTTP. Una riga sales legacy riceve il binding soltanto quando
il payload persistito, il suo SHA256 e le identità sale/batch forniscono una prova
univoca per-riga dell'origine; lo snapshot ufficiale corrente non è una prova.
Sale/refund/void/import legacy senza prova diventano `failed_blocked` con codice
redatto `legacy_origin_ambiguous`, senza invio e senza riscrivere l'origine.

Le vendite congelano payload canonico, SHA256 e client batch già nell'enqueue;
retry/restart deserializzano e verificano quel payload senza ricostruirlo da dati
locali mutabili. Usano inoltre una lease di 15 minuti per recuperare
`in_progress` dopo un restart. Prepare, ACK, retry e block sono vincolati a stato e
`expectedAttemptCount`; un ACK duplicato/tardivo non modifica lo stato locale.
Sale, refund e void hanno `operation_type` distinto. Batch e shop della risposta
vendite sono verificati prima dell'ACK e dello snapshot. Refund/void sono
rifiutati al repository boundary se l'originale non è coerente e il drain li
differisce finché l'outbox dell'originale non è `acked`.

Cursor, sync mode e marker sale-safe del catalogo sono persistiti con shop
id/code ed epoch in una transazione. Cursor legacy senza binding viene scartato
e forza un bootstrap completo. Un response shop o contratto
schema/policy/capability non coerente viene rifiutato prima dell'apply. Sono
accettati soltanto `delta` e `full_refresh`; il sales sync resta obbligatorio.
Una barriera in-process e l'epoch persistente serializzano pull e transizione
shop. La transizione autorizzata mantiene il lease esclusivo fino al reset dello
snapshot/trust del nuovo shop. Una pull che ha catturato la sessione A la
rivalida dentro la barriera, prima di bind/cursor/HTTP, e viene rifiutata se nel
frattempo lo shop ufficiale è B. Il cursor `full_refresh` viene persistito solo
nel commit autorevole finale; un restart intermedio riparte da zero.
`full_refresh` disattiva le identità remote assenti preservando le righe locali
prima di marcare sale-safe. La transizione shop autorizzata cancella anche il
binding di cursor/sale-safe; una transizione con outbox sales/catalog irrisolto,
incluso `in_progress`, resta bloccata. Restore e catalog stock preservation
trattano ugualmente `pending`, `retry`, `in_progress` e `failed_blocked` come
irrisolti.

Il restore usa un file temporaneo univoco, valida snapshot ufficiale, coerenza
catalogo e binding di qualsiasi outbox irrisolta contro lo shop corrente prima
della copia live, installa con rollback generale e gestisce anche i sidecar WAL.
Dopo restore scarta cursor e sale-safe; la review può essere chiusa dall'app
soltanto dopo integrity PASS, outbox risolte e un nuovo `full_refresh` dello
stesso shop successivo al restore. Il flusso safe-start mantiene la review
bloccante finché questi invarianti non sono soddisfatti. L'ACK catalog import
espone e verifica lo shop autorevole response-side. Refund e void verificano
inoltre che l'origine corrisponda allo shop ufficiale/trusted corrente.

Le identità remote di category/supplier e i tombstone sono persistenti e
non distruttivi; gli import locali non sovrascrivono identità remote e non
riattivano reference tombstoned per collisione di nome. Gli upsert con timestamp
normalizzato minore o uguale al tombstone, inclusi ISO equivalenti, sono stale.

## File modificati

- Migrazioni/data: `DbInitializer.cs`, `SaleRepository.cs`,
  `ShopOfficialSnapshotRepository.cs`, `ProductRepository.cs`,
  `CategoryRepository.cs`, `SupplierRepository.cs`.
- Outbox/sync: `OutboxShopBinding.cs`, `CatalogShopStateRepository.cs`,
  `CatalogShopTransitionBarrier.cs`, `CatalogFullRefreshReconciler.cs`,
  `PosOnlineCompatibilityValidator.cs`, `PosShopTransitionGuard.cs`,
  `RestoreShopSafetyRepository.cs`, `AtomicRestoreInstaller.cs`,
  `CatalogImportOutboxRepository.cs`, `CatalogImportSyncService.cs`,
  `PosSalesSyncRequestBuilder.cs`.
- Import: `CategorySupplierResolver.cs`, `ProductImportApplyService.cs`,
  `ProductDbImporter.cs`.
- WPF online/restore: `PosOnlineBootstrapService.cs`, `PosCatalogPullService.cs`,
  `PosSalesSyncService.cs`, `PosStartOfDaySyncService.cs`, `MainWindow.xaml.cs`,
  dialog first-login/start-of-day, `PosWorkflowService.cs`, DB maintenance
  view/dialog e traduzioni.
- CLI/test: `Program.cs`, `SupplierImportDataTests.cs`,
  `OutboxShopBindingTests.cs`, `PosShopTransitionGuardTests.cs`,
  `RemoteCatalogReferenceTombstoneTests.cs`, `CatalogSafetyInvariantTests.cs`,
  `RestoreShopSafetyTests.cs`.
- Scanner: catalog import/pull, first login, bootstrap/client/linking, security,
  restore e il nuovo `scripts/check-pos-outbox-shop-binding.ps1`.

Le liste tracked/untracked e gli artefatti di trasferimento sono stati
rigenerati sul diff finale revisionato. La patch completa conserva anche la
modifica preesistente dichiarata; la patch lane la esclude e non la attribuisce.

## Evidence Mac reale

| Controllo | Risultato |
| --- | --- |
| `dotnet build` Core/Data/CLI Release | PASS, zero warning/error |
| `dotnet test tests/Win7POS.Core.Tests/... -c Release --no-restore` | PASS, 69/69 |
| filtro `CapturedSession_IsRejectedAfterWaitingBehindShopTransition` | PASS, 1/1; nessun HTTP/apply/bind A e shop B intatto |
| filtro `RemoteCatalogReferenceTombstoneTests` | PASS, 5/5 |
| CLI `--selftest` | PASS, `自检 PASS` |
| CLI sales sync harness | PASS, sale/refund/void e ACK/retry/block |
| CLI shop-cache harness | PASS |
| CLI catalog outbox/reconciliation/fake HTTP harness | PASS |
| CLI SQLite integrity/restore guard | PASS |
| WPF Release x86/net48 | PASS, zero warning/error |
| tutti i 29 `scripts/check-*.ps1` statici | PASS |
| catalog/sales/restore/outbox/security/staging scanner rafforzati | PASS |
| patch completa/lane applicate con `git apply --check --binary` su `git archive HEAD` | PASS |
| release completeness/runtime validator, folder e ZIP | PASS |
| `unzip -t`, PE candidate x86, nessun PDB/CLI/source/DB nel pack | PASS |
| `git diff --check` | PASS |

Il tentativo di eseguire `scripts/win7pos/windows/build-release-x86.ps1
-DryRun` su macOS è correttamente `NOT_RUN`: lo script richiede un Builder
Windows 10/11. Il package candidate è stato prodotto dall'output della build
WPF Release x86/net48 su Mac, validato staticamente e non costituisce prova di
avvio su Windows 7.

## Artefatti da trasferire

Bundle di trasferimento fuori Git:
`win7pos-runtime-handoff-20260714/`

Il bundle viene consegnato separatamente a Codex Asus. Non ricostruire un path
locale Mac e non aggiungere questi binari al repository.

Artefatti primari verificati dal test 1:

| Artefatto | SHA256 | Uso |
| --- | --- | --- |
| `Win7POS-ASUS-runtime-candidate-20260714.zip` | `71d4eccbfbb2bf78cd9a8394406fe2445f9a184624de8515f5f2596cf5b38dda` | runtime candidate x86/net48, ZIP flat |
| `win7pos-wave1.patch` | `952bc9ed14414e57efbec7bf90ba927cca9072e1a3963c18728d8daf088b0a30` | patch binaria tracked lane, esclusa la modifica preesistente |
| `win7pos-wave1-untracked-sources.tar.gz` | `e2a28570e9ad15eab2aa9fe9303a1fd9870a99c004f57e6784490b03ba304787` | nuovi sorgenti/test/scanner |

Artefatti supplementari inclusi nel manifest globale:

| Artefatto | SHA256 | Uso |
| --- | --- | --- |
| `win7pos-working-tree.patch` | `7280c8adb999d84f32ba76afc2abad0033260080098ef3a5262f3bc154f1d1ae` | patch binaria completa dei tracked, inclusa la modifica preesistente dichiarata |
| `pos-admin-web.config.sample` | `74ae3f21e20ac2c199d7752dad89044827cd85e8b8289d06756ce7bd65a0bea3` | esempio staging senza secret |
| `run-pos-smoke.bat` | `6400094cd51e951998dff19b80be41be01de6d0cfedd68625361972bc37ff7e6` | launcher smoke ASUS riusato dal repository |

Trasferire l'intera cartella usando `ARTIFACT-FILES.txt` e `SHA256SUMS.txt`,
ricalcolare SHA256 sul Builder/ASUS e fermarsi in caso di mismatch. Per
ricostruire l'intera working tree applicare `win7pos-working-tree.patch` e poi
estrarre l'archivio untracked; per la sola lane applicare invece
`win7pos-wave1.patch` e lo stesso archivio. Non applicare entrambe le patch. Non
copiare database reali, directory `%ProgramData%` esistenti, file
trusted-device, log utenti o credential store.

Dopo il pull della `main` pubblicata le patch sono soltanto recovery evidence:
non applicarle al checkout Asus.

## Sequenza obbligatoria per Codex Asus

1. Eseguire `git fetch origin`.
2. Eseguire `git status --short --branch` e fermarsi se esistono modifiche non
   coordinate; non scartarle automaticamente.
3. Eseguire `git checkout main`.
4. Eseguire nuovamente `git status --short --branch`.
5. Eseguire `git pull --ff-only origin main`.
6. Verificare che `git rev-parse HEAD` corrisponda alla SHA `origin/main`
   comunicata con la pubblicazione e che
   `git merge-base --is-ancestor 94bb9573544811ef97c45f74b4ccf3ac85dc10de HEAD`
   termini con exit code `0`.
7. Eseguire restore/build/test Windows sul sorgente appena scaricato.
8. Eseguire il runtime Windows 7 e i test 1–30 sotto indicati.
9. Verificare lo staging usando soltanto configurazione e fixture sintetiche
   fornite fuori repository.
10. Verificare stampante, scanner barcode e cash drawer; riportare evidenze
    redatte senza avviare modifiche concorrenti non coordinate.

## Config staging di esempio, senza secret

File equivalente incluso negli artefatti come `pos-admin-web.config.sample`:

```text
AdminWebBaseUrl=https://merchandise-control-admin-web-staging.merchandise-control-admin-web.workers.dev
```

Non aggiungere token, PIN, password, cookie, database URL o service role. Usare
soltanto una fixture/staff account staging predisposta fuori dal repository.

## Comandi ASUS/Builder

```powershell
Get-FileHash .\Win7POS-ASUS-runtime-candidate-20260714.zip -Algorithm SHA256
Expand-Archive .\Win7POS-ASUS-runtime-candidate-20260714.zip C:\Win7POSTest\drop\Win7POS
Set-Location C:\Win7POSTest\drop\Win7POS
powershell -ExecutionPolicy Bypass -File .\check-win7-prereqs.ps1
.\run-pos-smoke.bat
```

Per una build Windows riproducibile dal sorgente revisionato:

```powershell
dotnet restore src\Win7POS.Wpf\Win7POS.Wpf.csproj
dotnet build src\Win7POS.Wpf\Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86 --no-restore
dotnet test tests\Win7POS.Core.Tests\Win7POS.Core.Tests.csproj -c Release --no-restore
powershell -File scripts\win7pos\windows\build-release-x86.ps1 -BuildInstaller
```

## Checklist runtime ASUS — 30 test

Per ogni test registrare comando/azione, timestamp, risultato
`PASS|FAIL|BLOCKED|NOT_RUN`, screenshot o estratto log redatto e path evidence.

1. **Integrità trasferimento** — i tre SHA256 coincidono esattamente con la tabella.
   Stato: `DEFERRED_TO_CODEX_ASUS`.
2. **Prerequisiti Win7** — Windows 7 SP1, .NET 4.8 e VC++ Runtime x86 risultano disponibili; preflight PASS.
   Stato: `DEFERRED_TO_CODEX_ASUS`.
3. **Contenuto pack** — exe/config/DLL/e_sqlite3 presenti; nessun PDB, CLI, source, DB reale o secret.
   Stato: `DEFERRED_TO_CODEX_ASUS`.
4. **Isolamento dati** — `WIN7POS_DATA_DIR` punta a una directory QA nuova e scrivibile.
   Stato: `DEFERRED_TO_CODEX_ASUS`.
5. **Configurazione staging** — solo base URL HTTPS pubblico, senza path/query/credenziali; resolver la accetta.
   Stato: `DEFERRED_TO_CODEX_ASUS`.
6. **Cold start x86** — app parte senza crash, errore SQLite native o blocco UI; log startup redatto disponibile.
   Stato: `DEFERRED_TO_CODEX_ASUS`.
7. **First login shop A** — shop/staff/PIN fixture validi aprono POS solo dopo catalogo sale-safe.
   Stato: `DEFERRED_TO_CODEX_ASUS`.
8. **Restart trusted session** — chiusura/riavvio riusa DPAPI e heartbeat senza chiedere o mostrare token.
   Stato: `DEFERRED_TO_CODEX_ASUS`.
9. **Catalog full pull** — primo pull drena `hasMore`, riconcilia/inattiva i remote assenti, applica prodotti/prezzi/reference e crea sale-safe.
   Stato: `DEFERRED_TO_CODEX_ASUS`.
10. **Catalog delta/restart** — cursor shop A sopravvive al restart e il delta non ripete il full pull.
    Stato: `DEFERRED_TO_CODEX_ASUS`.
11. **Cash sale** — vendita cash salva ledger, linee, stock movement e outbox nella stessa transazione.
    Stato: `DEFERRED_TO_CODEX_ASUS`.
12. **Card sale** — vendita card salva correttamente ed evita stampa automatica/default non autorizzata.
    Stato: `DEFERRED_TO_CODEX_ASUS`.
13. **Partial refund** — refund conserva original sale, quantità residua e `operation_type=refund`.
    Stato: `DEFERRED_TO_CODEX_ASUS`.
14. **Full void** — void completo è una sola inversione idempotente con `operation_type=void`.
    Stato: `DEFERRED_TO_CODEX_ASUS`.
15. **Sales origin binding** — riga outbox contiene shop A id/code, schema, client id/idempotency e hash redatto.
    Stato: `DEFERRED_TO_CODEX_ASUS`.
16. **Catalog-import binding** — import supplier crea outbox shop A e non conserva path completo o auth.
    Stato: `DEFERRED_TO_CODEX_ASUS`.
17. **Offline/reconnect drain** — offline produce retry bounded; reconnect invia una volta e ACKa senza doppio stock.
    Stato: `DEFERRED_TO_CODEX_ASUS`.
18. **Restart stale in_progress** — kill controllato dopo prepare; dopo lease il retry usa lo stesso payload/hash/client batch con attempt incrementato.
    Stato: `DEFERRED_TO_CODEX_ASUS`.
19. **Duplicate sales ACK** — duplicate/idempotent remoto non duplica vendita, stock o ACK locale.
    Stato: `DEFERRED_TO_CODEX_ASUS`.
20. **Duplicate catalog ACK** — duplicate/idempotent import non duplica product/price history o remote id e include shop autorevole coerente.
    Stato: `DEFERRED_TO_CODEX_ASUS`.
21. **Retry bound** — errori transient applicano backoff e raggiungono `failed_blocked` al limite senza loop infinito.
    Stato: `DEFERRED_TO_CODEX_ASUS`.
22. **Cross-shop drain** — riga shop A con sessione shop B diventa `failed_blocked` prima di HTTP e senza riscrivere origine.
    Stato: `DEFERRED_TO_CODEX_ASUS`.
23. **Legacy proven backfill** — payload sales legacy hashato/provato per shop A conserva binding A anche se lo snapshot corrente è B.
    Stato: `DEFERRED_TO_CODEX_ASUS`.
24. **Legacy ambiguous block** — sale/refund/void/import senza prova restano unbound e diventano `failed_blocked` senza HTTP.
    Stato: `DEFERRED_TO_CODEX_ASUS`.
25. **Switch con outbox** — A→B è negato con qualsiasi sales/catalog pending/retry/in_progress/failed_blocked.
    Stato: `DEFERRED_TO_CODEX_ASUS`.
26. **Switch dopo drain/race** — A→B autorizzato attende una pull A concorrente, incrementa epoch e resetta prodotti/reference attivi, pending price, cursor/sale-safe/binding e mirror staff A; A non ripopola la cache.
    Stato: `DEFERRED_TO_CODEX_ASUS`.
27. **Tombstone** — product/category/supplier remoti diventano inattivi con timestamp; nessun hard delete o riuso locale.
    Stato: `DEFERRED_TO_CODEX_ASUS`.
28. **Backup/restore cross-shop** — snapshot/outbox B con trust A è rifiutato prima della copia; restore same-shop richiede nuovo full-refresh e chiusura review prima delle vendite.
    Stato: `DEFERRED_TO_CODEX_ASUS`.
29. **Printer/cash drawer** — test printer esplicito; vendita resta salvata se hardware fallisce; drawer solo su printer fisico configurato.
    Stato: `DEFERRED_TO_CODEX_ASUS`.
30. **Scanner e privacy** — barcode scanner inserisce il prodotto; log/evidence non contengono PIN/password/token/path sensibili.
    Stato: `DEFERRED_TO_CODEX_ASUS`.

## Criteri di accettazione e lane successiva

- I gate repository devono restare verdi sul sorgente esatto trasferito.
- Tutti i test 1–28 sono obbligatori; 29–30 possono essere `BLOCKED` solo con
  hardware realmente indisponibile e prova del blocco, mai convertiti in PASS.
- Qualunque invio cross-shop, ACK senza attempt token, riuso cursor cross-shop,
  hard delete tombstone o secret in log è un fallimento bloccante.
- Conservare DB/evidence sintetici con scope esplicito; non pulire dati non creati dal test.
- La fase di handoff resta `PUBLISHED_TO_MAIN_FOR_CODEX_ASUS_RUNTIME`; il runtime
  globale resta `EXTERNAL_TEST_PENDING_CODEX_ASUS` e ciascuno dei 30 test resta
  `DEFERRED_TO_CODEX_ASUS` fino all'esecuzione ASUS. Solo l'utente può
  confermare `DONE`.
