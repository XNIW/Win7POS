# ASUS-W7POS-019 — qualifica esterna e scheda operatore Win7

Aggiornamento consegna: 2026-11-07. Software Windows consegnato e verificato;
onboarding reale/Win7 BLOCKED_EXTERNAL. Host prove Windows11, non Win7 fisico.
NEW_SHOP_019 creata secondo dichiarazione utente; primo login reale precedente5c
accettato e binding verificato, poi catalog pull409unmapped. Mac/deployment,
mapping Getnet, inizioPOS/overlap e semantica temporale restano gate separati.
Vedere il [risultato onboarding](ASUS_W7POS_019_ONBOARDING_EXECUTION_RESULT.md).
Prosecuzione8novembre: causa409 corretta con Admin1a35034a e migration applicata;
manifest v3 reale completo zero PASS. Normale HTTP/UI NOT_RUN_POLICY per avvio
respinto, autorizzazione specifica pendente. Identità Windows/card sotto invariate.
[Esito del fix409](ASUS_W7POS_019_CATALOG_409_FIX_RESULT.md).

Il bundle QA precedente5c aveva il companion WPF privo del forwarder: failure
conservata, non riutilizzarlo per l'avvio. La consegna corrente sotto ha closure,
smoke scaricato e avvio ordinario PASS. Rimane development-unsigned;
installation=NOT_EXECUTED. I verdi non certificano Win7 o un apply reale.

| Identità del candidato da qualificare | Valore corrente |
| --- | --- |
| Source SHA codice/pacchetto | `d262ccd979db8e3407118ffa7275861f12fdb5e2` |
| Fingerprint checkout pulito | `6ae00d6f8b1aa6f76a81b97804066a1e5ece2e76a6e419a276ea5a7cd98dff93` |
| ZIP Release x86 / installer SHA256 | ZIP `bee7c288096d57234f087eb63fe4bc85b128f8953eccebf710acc5b383e14716`; setup `27537a857ee7f88f30fc81f4dca664960b072e84ca76f6e876abb03989735f20` |
| ZIP QA net48/x86 SHA256 | `4595610fb0cc3f5084b6157155dc932cc76d8e931f89347a775de5c5f3c76b2e` |
| payload-manifest.json SHA256 | `2fde8d2452b41135dadf7c5629b31f68c44da68a31521d57e9f223b995357014` |
| Manifest fixture SHA256 | `72cd6f0afc82e418d5676fc329f61761d224e8aade30da98ada4ad30ee64620e` |
| Harness QA SHA256 | `2e70201c6679d5888ec0b007e575eed8828d1e44d9bef9d64caf123ce009fc9e` |
| WPF QA/prodotto SHA256 | `933739dd0912c098dda0237d0df67148de433cb17feca6d535d0b1e8848a9940` |
| Run Release / QA | [Release42432623356](https://github.com/XNIW/Win7POS/actions/runs/42432623356) / [WPF42432636966](https://github.com/XNIW/Win7POS/actions/runs/42432636966), PASS |
| CI/Security | [CI42432623358](https://github.com/XNIW/Win7POS/actions/runs/42432623358) / [Security42432623359](https://github.com/XNIW/Win7POS/actions/runs/42432623359), PASS |
| Win7 fisico / periferiche | NOT_RUN / BLOCKED_EXTERNAL |

Download verificati:41 file prodotto,110 checksum, QA40 file e34 comuni
byte-identici. QA scaricato smoke8/44 da CWD esterna, run
Win7POS.QA.20261108T011023899Z.bd7caca208574ff0b1ba247840e1522b,
exit0/fingerprint stabile/cleanup confermato. Avvio ordinario QA alla ripresa PASS
su profilo vuoto senza login; primo tentativo Esc rimane NOT_RUN storico.
La cattura diagnostica import_summary resta NOT_RUN/unavailable, separata dal
PASS del contratto UI; non attribuire una verifica visiva mancante al solo exit0.

Non sostituire questi hash con HEAD documentale, binari intermedi o contesti di
vecchie corse. Il [registro Execution](../plans/ASUS-W7POS-019_GETNET_HISTORY/EXECUTION.md)
distingue codice, documenti, pacchetto e qualifica esterna.

## Applicabilità V1 e confine server

| Superficie | Applicabilità e stato | Evidenza osservata / sblocco |
| --- | --- | --- |
| Import/undo storico → cloud commerciale | `NOT_APPLICABLE` come trasporto V1; isolamento sintetico PASS | `HistoricalSalesImportService` mantiene un ledger locale. Apply/Undo scrivono mapping, batch, file, transazioni, osservazioni e receipt storici; non chiamano writer vendite, stock, fiscale o outbox. Vedere [service](../../src/Win7POS.Data/Import/HistoricalSalesImportService.cs#L127), Apply/Undo e [regressione](../../tests/Win7POS.Core.Tests/Persistence/HistoricalLedgerTests.cs#L15). B-D11 è coperto dalle receipt sintetiche del candidato; nessun apply reale. |
| Route, schema, RLS, funzioni e Storage Supabase nuovi per lo storico | `NOT_APPLICABLE` nella V1 locale | Nessuna route storica è aggiunta al client. La V1 locale non introduce questi oggetti. Il problema onboarding409 richiede invece diagnosi Mac sul deployment; il backend installato non è attestato live. |
| Prima autenticazione e heartbeat esistenti | APPLICABLE_EXISTING; primo login reale5c PASS, manifest reale v3 PASS dopo fix, normale UI NOT_RUN_POLICY | Shop/policy/trust/generation e mirror verificati nel tentativo reale precedente. Route POS first-login e heartbeat restano separate da Supabase Auth e sessione web; contratto installato/heartbeat live da attestare al Mac. Nessuna readiness articoli richiesta per il normale accesso. |
| Autorizzazione import/undo | APPLICABLE_LOCAL; sintetico d262ccd PASS, uso storico reale BLOCKED | Lease, permessi e fingerprint generation vincolati fino al commit. Prova empty distinta da sale-safe/recovery; il409 storico non la produceva; il nuovo manifest RPC la fornisce, uso UI non ancora osservato. Nessun grant automatico a ruoli senza permesso. |
| Binding esercente Getnet → Shop | `APPLICABLE`; reale `BLOCKED_EXTERNAL` | [Workflow](../../src/Win7POS.Wpf/Pos/PosWorkflowService.HistoricalImport.cs#L50), [mapping locale](../../src/Win7POS.Data/Import/HistoricalSalesImportService.cs#L332). Richiede ShopId autorevole, alias/esercente e inizio uso POS provati. La preview dei file non crea una Shop né risolve questi vincoli. |
| Sync vendite/catalogo/ordini esistenti | `APPLICABLE_REGRESSION`, nessuna estensione storica | Le route esistenti restano nel [client](../../src/Win7POS.Data/Online/PosAdminWebClient.cs#L17). [OutboxShopBinding](../../src/Win7POS.Data/Online/OutboxShopBinding.cs#L70) e [sales sync](../../src/Win7POS.Wpf/Pos/Online/PosSalesSyncService.cs#L161) verificano origine/trust. Import e undo non devono aggiungere eventi a questi canali. I sintetici locali/loopback non attestano il server remoto. |
| Semantica oraria esportazione Getnet e report nativi | `APPLICABLE`; reale `BLOCKED_EXTERNAL` | Servono prova privata del significato di Z+offset e delle transizioni, più policy nativa canonica con provenienza/copertura. La policy sintetica `synthetic-fixed-offset-v1` non autorizza dati reali; nessun fallback al fuso del PC. |
| Originali privati | Reader runtime prodotto d262ccd PASS7/7 | Originali/oracolo indipendente e hash invariati prima/dopo, DLL QA net48/x86 verificate byte-identiche al prodotto. NeedsEvidence=true/CanApply=false; nessun apply reale o prova della semantica provider dedotta dal parser. |
| Staging/live QA | `BLOCKED_EXTERNAL` se richiesta; non necessaria per import storico locale | Vale esclusivamente [QA_AUTOMATION_AUTHORIZATION](../QA_AUTOMATION_AUTHORIZATION.md#staging-e-autorizzazione-operativa): profilo DPAPI, scope autorizzato, nuova readiness e Release Pack dello stesso candidato. La readiness articoli con `salesAllowed=false` non autorizza vendite remote. Nessuna suite Staging o mutazione live eseguita; normale primo accesso5c descritto separatamente. |
| Hardware e sistema operativo target | `APPLICABLE`; `BLOCKED_EXTERNAL` | Richiede il medesimo pacchetto x86 su macchina fisica Win7 SP1 identificata, net48 e periferiche reali nello scope della scheda sotto. Un PASS su Windows 11 non soddisfa W7-01. |

Nota storica della review precedente: la skill `supabase:supabase` del plugin Supabase
è stata applicata per questa review: consultati il
[changelog ufficiale](https://supabase.com/changelog), la documentazione
[sessioni](https://supabase.com/docs/guides/auth/sessions) e
[autenticazione Edge Functions](https://supabase.com/docs/guides/functions/auth).
Il cambiamento CLI v3 pubblicato il 2026-11-04 riguarda comandi e deployment
Supabase; qui non si invoca la CLI né si migra il contratto del backend.
Le credenziali del POS restano nel meccanismo esistente; nessuna chiave server
o sessione viene richiesta, copiata o inserita nella consegna QA.

Riferimento primario utile per il portale cileno: Santander/Getnet,
[Manual Portal de Comercios, 19-12-2024](https://banco.santander.cl/uploads/000/051/392/c425ef7e-64d7-4509-9840-17f6e7d47b26/original/GETNET_MANUAL_PORTAL_DE_COMERCIOS_19_12_2024.pdf),
sezione 07 Transacciones. La documentazione del portale/export non ha stabilito
in questa review l'interpretazione autorevole Z+offset dei file ricevuti.
Non trasferire semantiche da API Getnet di altri paesi o da altri fornitori.
EXT-TIME richiede ancora evidenza provider/voucher pertinente allo stesso
esercente e periodo, inclusi confini di giorno e cambio offset.

## Unica scheda operativa — da compilare dal responsabile fisico Win7

Owner unico della sessione: `NOT_ASSIGNED`. Run ID nuovo: `NOT_ASSIGNED`.
Data/ora inizio/fine UTC e locale: `NOT_ASSIGNED`. Finestra finita: massimo
30 minuti; una nuova prova dopo FAIL/TIMEOUT conserva il primo risultato e
richiede un nuovo run ID. Questa scheda non autorizza creazione account,
pagamenti reali, invio fiscale, reset outbox o modifiche di produzione.

| Campo obbligatorio prima dell'avvio | Valore da restituire |
| --- | --- |
| Owner e scope autorizzato | Nome/riferimento owner; identificativo privato dell'autorizzazione; sintetico / analisi privata / eventuale apply reale separati |
| Macchina fisica | Identificativo privato host; Windows 7 SP1, edizione/build, CPU, RAM, storage libero/tipo, net48 e runtime VC++ x86 richiesti dal pacchetto |
| Display e input | Risoluzione effettiva, DPI 100% e 125%, numero monitor, tastiera/layout, modello scanner e modalità input |
| Stampante/periferiche | Modello/driver/porta; tipo prova ordinaria autorizzata; indicare `BLOCKED_EXTERNAL` per periferica mancante |
| Candidato sorgente | `candidate_source_sha=d262ccd979db8e3407118ffa7275861f12fdb5e2`; fingerprint del checkout pulito nella tabella sopra |
| Pacchetto prodotto | `candidate_package_run_id=42432623356`; nome artifact e riferimento download; `candidate_package_sha256=bee7c288096d57234f087eb63fe4bc85b128f8953eccebf710acc5b383e14716`; `candidate_installer_sha256=27537a857ee7f88f30fc81f4dca664960b072e84ca76f6e876abb03989735f20` se distinto |
| QA separata dello stesso SHA | Artifact `Win7POS-QA-net48-x86-d262ccd979db8e3407118ffa7275861f12fdb5e2`; file `Win7POS.QA-net48-x86.zip`; SHA256 nella tabella sopra |
| Manifest QA | SHA256 nella tabella sopra; `schemaVersion=win7pos-net48-qa-payload-v1`; sourceSha uguale al candidato, architecture x86, targetFramework net48;40 file verificati con path/bytes/sha256 coerenti |
| Manifest fixture | SHA256 nella tabella sopra; `fixtureVersion=historical-sales-getnet-v1`; hash di oracle e di entrambi i workbook sintetici verificati nel bundle scaricato |
| Ambiente prova | Directory QA nuova, breve e posseduta fuori da dati POS/checkout; dati di prova distinti da evidence; percorso privato e mappatura conservati dall'owner |
| Shop e tempo reali, se applicabili | ShopId ufficiale e riferimenti privati merchant/inizio POS/policy, approvati e coerenti; oggi `BLOCKED_EXTERNAL`, non compilare con alias sintetici |

La consegna QA proviene dall'opzione manuale del
[workflow WPF](../../.github/workflows/wpf-build.yml#L11) e dal
[validator payload](../../scripts/qa/Win7PosQaPublicEvidence.psm1#L352).
Prima di trasferirla, il responsabile della build verifica la closure runtime
x86 e la presenza di **tutte** le fixture dichiarate, compresi i due `.xlsx`;
la sola presenza del JSON manifest non dimostra la distribuzione completa.
Ricalcolare gli hash dopo il trasferimento, prima dell'estrazione/esecuzione.
Non installare SDK moderni, PowerShell 7 o CLI net10 sul POS Win7; usare il
prodotto/harness net48 x86 consegnato e gli strumenti compatibili già presenti.
Il payload non contiene DB, profili trust, originali privati o credenziali.

### Comandi sul target, dopo l'assegnazione del candidato

Estrarre la QA in `C:\W7QA\payload`; scegliere una nuova root privata
`C:\W7QA\run-<nonce>`, mai una directory dati del negozio. Windows PowerShell
integrato può generare le due identità, senza moduli o SDK:

```powershell
$nonce = [Guid]::NewGuid().ToString('N')
'Win7POS.QA.' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '.' + $nonce
[Guid]::NewGuid().ToString('N')
```

Usare rispettivamente gli output come `runId` e `runNonce` di un nuovo JSON
`context-smoke.json`. Campi obbligatori: `schemaVersion` =
`win7pos-historical-sales-context-v1`, `sourceSha` = candidato40hex,
`sourceFingerprintSha256` = fingerprint qualificato64hex, `harnessSha256`,
`wpfSha256`, `fixtureSha256` = hash SHA256 dei due exe e del manifest fixture
trasferiti, `fixtureVersion` = `historical-sales-getnet-v1`, `timePolicyVersion`
= `synthetic-fixed-offset-v1`. Il responsabile build fornisce SHA/fingerprint
finali della tabella; finché sono `NOT_ASSIGNED` questa prova non è avviabile.
Confrontare gli hash con `payload-manifest.json`; `certutil -hashfile <file>
SHA256` è sufficiente sul target. Non copiare context/receipt di una corsa vecchia.
Per Load sul Win7 identificato aggiungere al contesto fidato
`measurementTargetProfile` = `win7-sp1` e
`measurementTargetIdentitySha256` = hash del manifest hardware qualificato
compilato dall'owner. Conservare il manifest privato e restituirne il riferimento.
Su Win7, l'assenza del profilo e dell'hash rende `environmentValid=false`;
i limiti reference restano riservati agli host reference. Una receipt non può
autoqualificare profilo o hardware. Nessun hash hardware Win7 è oggi assegnato
da questa sessione.

Da `cmd.exe`, con le directory nuove già create e i path sostituiti:

```bat
cd /d C:\Windows
start "" /wait "C:\W7QA\payload\Win7POS.Wpf.UiSmokeHarness.exe" --historical-sales-import-smoke --context-file "C:\W7QA\context-smoke.json" --receipt "C:\W7QA\run-<nonce>\historical-sales-results.json" --data-dir "C:\W7QA\run-<nonce>\data"
echo EXIT=%ERRORLEVEL%
```

L'owner supervisiona questa singola prova entro90s; per Load prepara identità,
context e root nuove e sostituisce flag con `--historical-sales-import-load`
e receipt con `historical-sales-load-results.json`, entro240s. Conservare anche
FAIL/TIMEOUT e le ricevute parziali. Dopo un timeout verificare e fermare soltanto
il processo attribuito a quel run/PID/creationTime, senza retry automatico.
I casi B che richiedono CLI net10 restano sul build host. Le ricevute ritornano
al validatore sul build host: exit0 da solo non qualifica misure o assertion.
Conservare il context autorizzato originale come `Expected`; non ricostruirlo
dalla receipt. Sul build host con PowerShell7.4+ e il checkout candidato:

```powershell
Import-Module ./scripts/qa/Win7PosHistoricalSalesValidation.psm1 -DisableNameChecking
$expected = Read-HistoricalSalesQaJson '<context-load-autorizzato>'
$receipt = Read-HistoricalSalesQaJson '<historical-sales-load-results.json>'
Assert-HistoricalSalesQaPass -Receipt $receipt -Mode Load -Expected $expected
```

| Caso nella stessa sessione | Procedura e oracolo indipendente | Evidenza minima / stato iniziale |
| --- | --- | --- |
| W7-00 identità/installazione | Verificare campi sopra, installare il candidato e riavviare il prodotto dalla directory consegnata, con CWD diversa. Verificare fixture da output, non dal checkout. | Versioni/hash/provenienza, tempi avvio, eventuale errore; `NOT_RUN` |
| W7-01 preview e apply sintetici | Analizzare entrambi i profili con policy sintetica esplicita; confermare CLP legacy e mapping fixture. Oracolo [corpus-v1.json](../../tests/fixtures/historical-sales-getnet-v1/corpus-v1.json): 6 righe sorgente, 5 accettate, 4 vendite, 1 annullo, 1 respinta, saldo signed 3380 CLP e netto noto 3320 CLP. Apply con backup verificato e receipt terminale. | Conteggi e importi confrontati ai letterali, receipt con operation/dataset/revision, hash backup privato; `NOT_RUN` |
| W7-02 reopen/replay/dedup | Chiudere completamente e riaprire; stessi report/receipt durevoli. Ripresentare il medesimo dataset: zero nuovi movimenti attivi. Consultare una receipt terminale senza dipendere dalla disponibilità successiva del file sorgente. | Stato prima/dopo, receipt originale e stato lotto corrente distinti, nessun nuovo outbox commerciale; `NOT_RUN` |
| W7-03 undo/reimport | Annullare il lotto con permesso dedicato; righe inattive e audit conservati, saldo attivo rimosso. Reimportare come nuova operazione: nuova versione/ownership, una sola versione attiva. Un lotto duplicate-only non acquisisce proprietà annullando il primo. | Receipt apply/undo/reimport, confronto stato/query e invarianti locali; `NOT_RUN` |
| W7-04 restore/restart | Ripristinare un backup fixture valido dello stesso shop usando il flusso prodotto. Riavviare; stessi mapping/ledger/receipt del backup, epoch corrente, piano/cursore precedente rifiutato e reimport coerente. Tenere l'owner DB per tutto lo swap. | Risultato integrity/FK e rollback se provato, identità backup/target privata, revision/epoch prima/dopo; `NOT_RUN` |
| W7-05 tempo e copertura | Provare confini D/D+1 e i due segmenti della policy sintetica senza fuso host implicito. Per file moderni reali senza prova: preview `NeedsEvidence`, apply reale bloccato. Copertura Unknown/Conflict/Dirty non deve apparire come zero completo. | Policy/versione/hash e fixture, confronto giorno/ora con oracolo congelato; dati reali/nuova Shop ancora `BLOCKED_EXTERNAL` |
| W7-06 report/CSV misti | F-MIXED-coverage: POS A1000 provato + storico A9000 = saldo ammesso 10000 e media POS1000; U7000 non attribuibile escluso da righe/search/detail/CSV e da conteggi divulgati. Verificare grafico signed con barra negativa e routing dettaglio storico. | Bundle/token coerente, CSV letto da parser indipendente, capture solo sintetica, vecchio token negato dopo mutazione; `NOT_RUN` |
| W7-07 carico e risposta | Fixture F-LOAD/F-MIXED generate prima della misura da seed/versione congelati: 49000 storici, saldo48800000; con 9000 POS +1000 e 1000 reversal -1000: 59000 righe, saldo56800000, 9000 scontrini POS/media1000. Misurare separatamente setup, parse, backup, admission, apply/commit, proof cold/warm, query e CSV. | Misure complete e confronti letterali; budget Win7 §10 del PLAN, nessun aumento dopo FAIL; `NOT_RUN` |
| W7-08 cancellazione/permessi | Cancel durante analisi/attesa/apply prima commit: rollback finito e comandi nuovamente disponibili. Revoca/expiry/cambio operatore negano apply/undo successivo. Seconda istanza sullo stesso DB negata prima init; nessun processo/waiter abbandonato. | Checkpoint, tempi cancel/busy/rollback, dati invariati nei casi negati, cleanup processi posseduti; `NOT_RUN` |
| W7-09 tastiera e DPI | A 100% e 125%: Tab/Shift+Tab, Enter, Escape, scroll anteprima, progress/cancel, report/CSV e dialog annulla/restore. Footer/titoli/owner visibili, nessun clipping o finestra irraggiungibile; i grafici negativi restano distinguibili. | Risoluzione/DPI/layout, checklist e capture sintetiche; `NOT_RUN` |
| W7-10 periferiche ordinarie | Dopo import, scanner sul flusso ordinario con articolo sintetico e stampa ordinaria autorizzata da fixture. Controllare input singolo, focus e contenuto/layout stampa; import/undo da soli non stampano né creano documenti fiscali. | Modelli/driver, osservazione scanner e campione stampa sintetico privato; `BLOCKED_EXTERNAL` finché hardware/scope non identificati |

Gli attesi di carico e i budget sono congelati nel
[PLAN §§10, 12 e 13](../plans/ASUS-W7POS-019_GETNET_HISTORY/PLAN.md#10-performance-da-qualificare),
con F-SMALL/F-CSV/F-LOAD/F-MIXED/F-CRASH. I letterali non vanno ricalcolati
usando la stessa query/serializer che si sta verificando. Per originali reali
usare esclusivamente manifest/audit indipendente privato e riferimenti
provider approvati; non trascriverli in questa scheda pubblica.

L'owner restituisce una copia compilata con identità/hash del candidato, host,
data/ora, risultato di **ogni** caso (`PASS`, `FAIL`, `TIMEOUT`, `NOT_RUN`,
`BLOCKED_EXTERNAL`), primo errore e causa, risultati già ottenuti, tempi/memoria,
ricevute e riferimenti privati delle evidence. Ogni PASS richiede asserzioni
complete e identità coerente; un timeout rimane timeout anche se la copia
diagnostici fallisce. Non riusare ricevute di un altro run.

Restituire pubblicamente solo il riepilogo sanificato e gli artifact QA ammessi
dal proiettore corrente. Conservare DB, backup, dump, trust/sessioni, secret,
percorsi utente e dati finanziari privati nella root evidence privata; screenshot
pubblicabili solo da fixture sintetiche approvate. Assenza di host, package,
Shop, evidenza temporale o periferica resta un blocco esplicito e non diventa PASS.

La [richiesta Win7 storica](../QA/WIN7_PHYSICAL_SMOKE_REQUEST.md) serve solo come
struttura di inventario, mentre [QA_AUTOMATION_AUTHORIZATION](../QA_AUTOMATION_AUTHORIZATION.md#win7-e-hardware)
regola l'autorizzazione corrente. Non ereditare dalla richiesta storica SHA,
run, package, endpoint staging o scope operativo.

## Esito software e limiti della consegna

CI42432623358, Security42432623359, Release42432623356 e WPF42432636966
PASS sullo stesso SHA d262ccd. Core/Data1734/1734, gate50/50,
canonica15/15, functional20, foundation5/37, smoke8/44 e load11.
Il correttivo include il vincolo persistente che impedisce a un delta di
riabilitare vendite su un vecchio catalogo dopo una full empty confermata;
solo una nuova full verificata non vuota rimuove il blocco atomicamente.
La regressione e832d6a resta conservata come rosso e non è il candidato fisico.

Download canonico:41 file payload,110 checksum, SBOM/provenance/attestation e
corrispondenza setup/dist/ZIP. ReleasePack13599432637, Setup13599033504
e dist13599302984; versione1.0.0-dev.d262ccd979db. QA40 file,34 comuni byte-identici;
nessun harness, XLSX, DB o TRX nel payload operativo. Stadio development-unsigned,
installazione NOT_EXECUTED. Ricalcolare gli hash della tabella dopo il trasferimento.

Reader net48/x86:7/7 originali, attesi indipendenti e file invariati,4588ms sul
build host. Esegue DLL QA coincidenti con quelle prodotto; NeedsEvidence=true,
CanApply=false e nessun apply reale. Lo stesso esito non qualifica né i tempi
Win7 né la semantica provider. Il primo Esc e le failure precedenti sono storia
conservata; nessun retry HTTP dopo409. Fix server/RPC qualificato separatamente l'8novembre; console Mac PENDING.
