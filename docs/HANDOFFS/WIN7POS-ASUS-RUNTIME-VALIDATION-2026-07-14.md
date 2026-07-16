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

- Repository: `https://github.com/XNIW/Win7POS.git`
- Branch di consegna: `main`
- Commit implementazione immutabile:
  `dc162aeff484b576ef21565338cf3d5d492285d4`
- URL commit:
  `https://github.com/XNIW/Win7POS/commit/dc162aeff484b576ef21565338cf3d5d492285d4`
- Data pubblicazione Mac: `2026-07-15`.
- La normalizzazione portabile del basename in
  `CatalogImportOutboxPayloadBuilder.cs`, già presente localmente prima della
  lane, è stata preservata semanticamente e inclusa nella revisione pubblicata.
- Nessun secret, database locale, output `bin/obj`, PDB, log o artefatto Codex è
  incluso nei commit.
- La SHA tip finale di `origin/main` è comunicata nell'aggiornamento di
  pubblicazione insieme a questo handoff. Il commit implementazione qui sopra
  deve risultarne antenato.

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

L'autorizzazione offline è ora una lease fail-closed di massimo 12 ore. Il
client salva il `serverTime` autenticato ricevuto da first-login/heartbeat,
l'istante locale di ricezione e l'expiry della sessione nel trusted store
protetto DPAPI; timestamp legacy incompleti, rollback dell'orologio o scadenza
esatta negano l'accesso. Un unico guard di processo mantiene un high-water e
protegge login PIN locale, permessi cached, override, cambio operatore e commit
finale della vendita. La finestra attiva pianifica inoltre la scadenza precisa.
Un diniego online esplicito cancella il trust senza cancellare outbox, catalogo
o mirror locali.

Ogni nuova riga refund/void inviata al server include additivamente
`clientOriginalLineId`, derivato dalla riga originale SQLite già validata. Un
reversal legacy senza binding completo viene bloccato in preflight prima della
rete con `reversal_original_line_missing`; sale normali e contratti esistenti
restano compatibili.

Refund e void ora condividono anche la stessa economia autorevole del RPC
Admin. Il gross è sempre la somma delle sole righe item; `DISC:*` e `TAX:*`
restano fuori da selezione, binding e payload reversal. Discount e tax della
quota corrente sono la differenza tra il target cumulativo proporzionale e la
quota precedente effettiva, con arrotondamento PostgreSQL `numeric` half-away
from zero. Ogni payload precedente viene riletto dall'outbox immutabile,
verificato contro SHA256 e ricalcolato in ordine sale id. Una storia legacy
gross-only, corrotta, bloccata o economicamente incoerente fallisce chiusa senza
riscrivere payload/hash. Le reversal offline coerenti possono essere create in
sequenza, ma il drain invia la successiva solo dopo l'ACK di tutte le precedenti
dello stesso originale. Header `gross/discount/tax/net/paid`, payment e totale
locale devono coincidere prima di enqueue e nuovamente prima della rete.

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
  `RestoreShopSafetyTests.cs`, `PosOfflineAuthorizationLeasePolicyTests.cs` e
  `PosSalesSyncRequestBuilderTests.cs`.
- Lease/reversal: `PosOfflineAuthorizationLeasePolicy.cs`,
  `PosOfflineAuthorizationLeaseGuard.cs`, `PosTrustedDeviceSession.cs`,
  `PosTrustedDeviceStore.cs`, `OperatorSession.cs`, `PermissionService.cs`,
  `OverrideAuthService.cs`, dialog access/switch, `PosViewModel.cs`,
  `PosOnlineTransportContracts.cs`, `ReversalEconomicsPolicy.cs`,
  `PosReversalEconomicsReader.cs`, `PosSalesSyncRequestBuilder.cs`,
  `SaleRepository.cs`, `RefundViewModel.cs`, `PosWorkflowService.cs` e
  `PosSalesSyncService.cs`.
- Scanner: catalog import/pull, first login, bootstrap/client/linking, security,
  restore, `scripts/check-pos-outbox-shop-binding.ps1` e il nuovo
  `scripts/check-pos-offline-authorization-lease.ps1`, oltre a
  `scripts/check-pos-reversal-economics.ps1`.

Le liste tracked/untracked e gli artefatti di protezione sono stati verificati
sul diff finale revisionato prima di fetch e branch. La patch completa e
l'archivio dei cinque untracked ricostruiscono la sorgente iniziale; la piccola
patch successiva conserva la sola correzione del fixture TASK-081.

## Evidence Mac reale

| Controllo | Risultato |
| --- | --- |
| `dotnet build` Core/Data/CLI Release | PASS, zero warning/error |
| `dotnet test tests/Win7POS.Core.Tests/... -c Release --no-restore` | PASS, 95/95 |
| filtri lease/reversal/tombstone/shop binding | PASS, 27/27 |
| policy economics reversal mirata | PASS, 6/6: discount/tax, no-discount, half-away, partial successive, full residual, storia incoerente |
| integrazione payload/partial/full/legacy | PASS, 3/3: item-only, header/payment esatti, ACK ordering, payload/hash legacy invariati |
| filtro `CapturedSession_IsRejectedAfterWaitingBehindShopTransition` | PASS, 1/1; nessun HTTP/apply/bind A e shop B intatto |
| filtro `RemoteCatalogReferenceTombstoneTests` | PASS, 5/5 |
| CLI `--selftest` | PASS, `自检 PASS` |
| CLI sales sync harness | PASS, sale/refund/void e ACK/retry/block |
| CLI shop-cache harness | PASS |
| CLI catalog outbox/reconciliation/fake HTTP harness | PASS |
| CLI SQLite integrity/restore guard | PASS |
| WPF Release x86/net48 | PASS, zero warning/error |
| tutti i 31 `scripts/check-*.ps1` statici | PASS |
| scanner lease offline/reversal line binding | PASS, 22/22 controlli |
| scanner economia reversal | PASS, 12/12 controlli |
| catalog/sales/restore/outbox/security/staging scanner rafforzati | PASS |
| patch completa/untracked ricostruiti su `git archive HEAD`; hash del fix harness verificato | PASS |
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
| `Win7POS-ASUS-runtime-candidate-20260714.zip` | `9e489fcbcc770159ea99f748b3feb38c05d28ec4f409719a382e054455d4cd84` | runtime candidate x86/net48 rigenerato dal commit implementazione, ZIP flat |
| `Win7POS-release-pack/Win7POS.Wpf.exe` | `c7d03302a3bc4fd52e15eb46deb467efe41ea044e4f022af5c8aa9bd3e5c36a9` | eseguibile PE x86 candidato |
| `Win7POS-release-pack/Win7POS.Core.dll` | `892b25aaff16845af6621f8b7d6667d32e3f6b128077156bd86f4b465cc2259d` | policy proporzionale condivisa |
| `Win7POS-release-pack/Win7POS.Data.dll` | `b8040ef4e3ccbf774962c2ea244e1366e3123f3fbe74b83c2bb8e6ace37f25fd` | reader/builder/repository reversal |
| `Win7POS-release-pack/VERSION.txt` | `148c38e807a77bbcc36c83cc943fe55463c910734ec5e7d08d6296b17a08d2b3` | identità sorgente e classificazione runtime |

Artefatti supplementari di protezione pubblicazione, conservati fuori Git:

| Artefatto | SHA256 | Uso |
| --- | --- | --- |
| `canonical-working-tree.patch` | `0463d942de2e098b5e052356f06c01af5964241fc3675f4164fa5092b1231ff2` | patch binaria completa protetta prima di fetch/branch |
| `canonical-untracked-sources.tar.gz` | `fe60510f4d005743f974ea4569a32507ec51c5fcb0814b1c5cc60436f4081952` | cinque nuovi sorgenti/test/scanner protetti |
| `post-gate-harness-fix.patch` | `91d36b8ead0a2ba7335334f3805c290ffc7ca1a24e0e99c5c38a47bd78bc051c` | correzione fixture TASK-081 dopo il gate iniziale |
| `pos-admin-web.config.sample` | `74ae3f21e20ac2c199d7752dad89044827cd85e8b8289d06756ce7bd65a0bea3` | esempio staging senza secret |
| `run-pos-smoke.bat` | `6400094cd51e951998dff19b80be41be01de6d0cfedd68625361972bc37ff7e6` | launcher smoke ASUS riusato dal repository |

Trasferire l'intera cartella usando `ARTIFACT-FILES.txt` e `SHA256SUMS.txt`,
ricalcolare SHA256 sul Builder/ASUS e fermarsi in caso di mismatch. Per
recuperare una sorgente pre-pubblicazione usare soltanto il bundle di protezione
`win7pos-release-publication-20260715` e verificare prima il relativo
`SHA256SUMS.txt`. Le vecchie patch wave1 nel bundle storico sono superate dalla
`main` pubblicata e non vanno applicate. Non copiare database reali, directory
`%ProgramData%` esistenti, file
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
   `git merge-base --is-ancestor dc162aeff484b576ef21565338cf3d5d492285d4 HEAD`
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
| Package audit | PASS | Nessun pacchetto vulnerabile/deprecato nei 5 progetti solution più il progetto performance standalone; fallback secret scan senza finding; scanner dedicati non installati |

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

## Matrice ASUS 1–30 pubblicata dalla main

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
7. **First login shop A** — shop/staff/PIN fixture validi aprono POS solo dopo catalogo sale-safe e salvano il `serverTime` autenticato della lease.
   Stato: `DEFERRED_TO_CODEX_ASUS`.
8. **Restart trusted/offline lease** — chiusura/riavvio riusa DPAPI e heartbeat senza mostrare token; offline consente operazioni soltanto entro 12 ore e blocca scadenza esatta, timestamp legacy incompleto e rollback dell'orologio.
   Stato: `DEFERRED_TO_CODEX_ASUS`.
9. **Catalog full pull** — primo pull drena `hasMore`, riconcilia/inattiva i remote assenti, applica prodotti/prezzi/reference e crea sale-safe.
   Stato: `DEFERRED_TO_CODEX_ASUS`.
10. **Catalog delta/restart** — cursor shop A sopravvive al restart e il delta non ripete il full pull.
    Stato: `DEFERRED_TO_CODEX_ASUS`.
11. **Cash sale** — vendita cash salva ledger, linee, stock movement e outbox nella stessa transazione.
    Stato: `DEFERRED_TO_CODEX_ASUS`.
12. **Card sale** — vendita card salva correttamente ed evita stampa automatica/default non autorizzata.
    Stato: `DEFERRED_TO_CODEX_ASUS`.
13. **Partial refund** — refund conserva original sale/riga, quantità residua, `operation_type=refund` e `clientOriginalLineId` corretto.
    Stato: `DEFERRED_TO_CODEX_ASUS`.
14. **Full void** — void completo è una sola inversione idempotente con
    `operation_type=void`, sole righe item, `clientOriginalLineId` e quota
    discount/tax residua esatta; un reversal legacy privo del binding o con
    economia gross-only si blocca prima di HTTP senza mutare payload/hash.
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

## Evidenze di chiusura dei PASS 3 e 6

### Test 3 — Contenuto pack finale (PASS)

- Stato precedente chiuso: il pack `a9c0ab3...` era obsoleto.
- Evidenza finale: `dist\Win7POS\VERSION.txt`, installer, ZIP univoco, SHA256,
  PE `0x014c` e validator folder/ZIP sono registrati dopo il commit documentale
  in `C:\Dev\Win7POS-QA\2026-07-14_ASUS_RUNTIME\evidence\final-release-evidence.txt`.
- Comando riproducibile:

```powershell
$env:WIN7POS_DOTNET_EXE='C:\Dev\dotnet10\dotnet.exe'
$env:ISCC_EXE=(Resolve-Path "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe").Path
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\win7pos\windows\build-release-x86.ps1 -BuildInstaller
pwsh -NoProfile -File scripts\check-release-pack-completeness.ps1 -ReleasePackSource dist\Win7POS -WriteManifests
& $env:ISCC_EXE installer\Win7POS.iss
$zip = "dist\Win7POS_$((Get-Date).ToString('yyyyMMdd_HHmmss')).zip"
if (Test-Path $zip) { throw "Release ZIP already exists: $zip" }
Compress-Archive -Path dist\Win7POS\* -DestinationPath $zip -CompressionLevel Optimal
pwsh -NoProfile -File scripts\check-release-pack-completeness.ps1 -ReleasePackSource dist\Win7POS
pwsh -NoProfile -File scripts\check-win7-runtime-release-validation.ps1 -ReleasePackSource dist\Win7POS
pwsh -NoProfile -File scripts\check-required-gates.ps1 -ReleasePackSource dist\Win7POS
pwsh -NoProfile -File scripts\check-release-pack-completeness.ps1 -ReleasePackSource $zip
pwsh -NoProfile -File scripts\check-win7-runtime-release-validation.ps1 -ReleasePackSource $zip
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
  supplier e price non disponibili; nessun DB staging drenato né risposta
  autenticata dalla quale stabilire presenza e canonicalizzazione checksum.
- Evidenza: screenshot login; directory evidence priva di screenshot conteggi o
  export SQLite staging.
- Completato comunque: contratto summary, full drain, audit exactness,
  riconciliazione, identity map, sale barrier e test mismatch/duplicate/orphan;
  un eventuale checksum non comparabile resta correttamente `Unverified`.
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
