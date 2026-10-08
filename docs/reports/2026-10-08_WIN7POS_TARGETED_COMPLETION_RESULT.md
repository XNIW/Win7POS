# WIN7POS_TARGETED_COMPLETION_RESULT

Report corrente dell'8 ottobre 2026. Le prove e i fallimenti storici restano nel
[closeout precedente](2026-10-03_FUNCTIONAL_SYNC_OPERATIONS_CLOSEOUT.md).
Questo candidato modifica codice runtime: non eredita la qualificazione del
pacchetto `0f4da73352df`.

## Baseline, scope e integrazione

- Win7POS iniziale: main/origin/main `a1b2f0702543ada56951f33d368ad452b480017b`,
  pulito, nessun commit locale non pubblicato; fetch e controllo worktree/chat/PR
  concorrenti completati. CI `37475005124`, Security `37475004904`, Release Pack
  `37475004981`: SUCCESS sullo SHA esatto.
- Patch in worktree isolato `codex/targeted-completion-20261008`; checkout e
  worktree preesistenti preservati. Nessun force-push, reset o deployment.
- Candidato VALIDATO_LOCALMENTE e review conclusa senza finding aperti.
  Integrazione prevista tramite PR e merge normale autorizzati; la copia finale
  di consegna registra SHA di head e merge, run CI/Security/Release Pack e hash
  del pacchetto, disponibili soltanto dopo l'integrazione.
- Runtime supportato invariato: Core/Data netstandard2.0; WPF net48/x86;
  Windows 7 SP1 come target. Nessuna nuova dipendenza.

## Matrice W1–W6

| Rilievo | Prova prima / causa | Patch | Accettazione corrente |
| --- | --- | --- | --- |
| W1 backup pre-import | CONFERMATO runtime: il vero ApplyAsync pubblica una copia con FK invalida e applica l'import (`apply_success=True`, `backup_valid=False`). Checkpoint e File.Copy live sono separati; il runtime corrente impone journal DELETE, quindi non si dichiara una race WAL riprodotta. Destinazione backup guasta già impediva l'import. | SupplierExcelImportWorkflowService: SqliteOnlineBackup.CreateVerifiedAsync prima delle modifiche. | PASS snapshot coerente con writer concorrente (20 commit), integrity/FK, fault dopo snapshot e prima pubblicazione, cleanup e nessun import su errore. |
| W2 Dispatcher | CONFERMATO net48/x86: 20.000 righe; preview 408,8 ms, apply dry-run 584,3 ms; task già completato al ritorno, zero heartbeat. Gli await SQLite non spostano necessariamente il lavoro dal Dispatcher. | Workflow import: copie dei dati, worker esplicito per DB/CPU, autorizzazioni UI e progress; ViewModel cancellazione preview/apply oltre all'analisi. | PASS heartbeat, input catturato, cancellazione/draft/retry, autorizzazioni sul Dispatcher. Misure isolate sotto. Non attribuito al precedente first-scan. |
| W3 prezzi e campi | CONFERMATO runtime: PurchasePrice 2147483648 genera OverflowException durante ricostruzione preview. | SupplierImportAnalyzer e helper prezzo: parsing decimal esatto, finitezza/range/precisione prima conversione, limiti del payload, errori localizzati senza perdere input. Rounding ToEven esistente preservato. | 121/121 Core import PASS; net48/x86 PASS correzione cella, stale preview/modifica concorrente, doppio apply, rollback/retry, testo DB/outbox. |
| W4 stampa reso/void | CONFERMATO runtime su fixture: preparazione fallita e invio fallito restituiscono il medesimo successo contabile senza esito stampa; INSERT restituisce operator_id NULL. | RefundModels, PosWorkflowService, PosViewModel, PosView, localizzazioni: NotRequested/Accepted/Failed, warning salvato+errore e comando ristampa sul RefundSaleId persistito. | PASS preparazione/invio/successo/off, comando UI doppio retry, riapertura e unico movimento. Accepted indica accettazione del canale, non carta fisica. |
| W5 minimizzazione | CONFERMATO runtime: finestra aperta manualmente con AutoOpen=false non ripristinata senza nuovi snapshot. | CustomerDisplayManager: ricorda finestra nascosta per minimize e la ripristina con policy comune. | CUSTOMERDISPLAY_completion PASS e scenario polish storico PASS. |
| W6 reconnect monitor | CONFERMATO runtime: snapshot prima del debounce riapre; debounce imposta manual-close lasciando visibile; snapshot successivo non aggiorna. | CustomerDisplayManager: cause distinte di soppressione, stessa policy per snapshot/timer/topologia, stato lock indipendente dagli snapshot. | On/off, snapshot-first/debounce-first, close esplicito/nativo, disable, lock/unlock/carrello, timer e focus TextBox reale PASS. Topologia sintetica, hotplug fisico NON_ESEGUITO. |

## Resi: operatore e autorizzazione

Lo schema contiene `sales.operator_id`; il filtro include NULL per compatibilità
con record legacy. I nuovi refund/void conservano l'operatore effettivo, senza
attribuire identità ai record storici. Il percorso originario controllava la UI
ma non usava il confine commit della vendita ordinaria: requisito di lease
revoca/scadenza/cambio operatore effettivamente mancante.

Il grant dedicato viene catturato prima di Demand/override e vincola operatore,
versione autorità, epoch e generation. L'approvazione del supervisore vale per il
solo permesso refund/void richiesto; void richiede entrambi. Il writer usa il
commit fence e la verifica atomica della lease esistenti, con capability scoped
al tipo di movimento. Stock, righe originali/residui, economia, audit e outbox
rimangono nella stessa transazione. Review indipendente senza rilievi aperti.
18/18 Core reversal/writer PASS; smoke WPF PASS per revoca/scadenza/cambio
operatore al commit di refund e void, cambio autorità con lo stesso ID fra
cattura e approvazione, permesso void mancante, refund diretto e override,
void con override. I casi distinti pre-approvazione expiry/revoca e void diretto
non vengono dichiarati come prove autonome. Due cassieri separati e record
legacy NULL verificano attribuzione e filtro senza modificare la policy storica.

Limiti import: purchase 0..Int32.MaxValue, retail 0..Int64.MaxValue; verifica
prima dell'arrotondamento ToEven, parsing massimo 28 cifre significative e 28
posizioni decimali effettive. Non numerici, non finiti, underflow, precisione
eccessiva e overflow markup diventano errori contestuali. Limiti testo del
payload: barcode80, item/supplier/category120, nomi240; fallback legacy validato.
Barcode/item/nomi mantengono spazi interni; supplier/category conservano la
normalizzazione del resolver esistente. Identità DB, metadata e outbox coerenti.

## Repository mobile/Admin e allegati

| Repository | Main corrente verificato | Prova / applicabilità |
| --- | --- | --- |
| MerchandiseControlSplitView | `0ce946b0c30e9498d0d8e4d94722aad9bd22a577` | CI `37699554438` SUCCESS; namespace com.example.merchandisecontrolsplitview, Room v22, PRODUCTION_MIGRATIONS. |
| iOSMerchandiseControl | `aca531bf1f87c94558020b46b4c4be0de24c69da` | CI `37697432811` SUCCESS; SwiftData tramite syncStoreGenerationController.modelContainer. Failure UI del 6 ottobre storici. |
| merchandise-control-admin-web | `02ea44b95d4a05baddbf46f24251f0d5f0dea294` | Aggiornato durante questa sessione da `82af13ef0005ecfb767809bd362327e91692b5ba`, PR131 alle15:46:30UTC. CI `37803517965` SUCCESS; Cloudflare `37803518056` build SUCCESS, deployment staging/prod SKIPPED. Deployment/schema installati non attestati. |
| cashregistersystem | `cbe8e87dc4490bdc4a0c01b1288273f82c558083` | Repo proprietario com.example.cashregistersystem; launcher MainActivity→PosScreenVM→PosViewModel→AppDatabase(pos.db,v3), exportSchema=false, sola MIGRATION_1_2. Ultimo push 2025-09-15, non archived, nessuna CI/PR corrente. Distinto dal riferimento Android attivo verificato. |

Nell'allegato ricevuto esiste soltanto Pasted text.txt: i 25 Kotlin non sono
disponibili e l'identità rispetto al repository non è attestata. Nessuna patch
mobile/Admin eseguita; la chiamata recordSale fire-and-forget nel cashregister
proprietario è evidenza sorgente, non prova runtime del mobile corrente.

Ricontrollo finale Admin: PR131 riguarda l'intento di creazione indirizzi,
con ultima migrazione sorgente `20261008151018_customer_address_create_intent_v3.sql`.
Questo aggiornamento non pubblica la readiness POS: il file canonico è assente
anche sul nuovo SHA. Una migrazione presente nel repository non attesta il suo
apply su staging né la registry live. Gli SHA Android/iOS sono rimasti invariati.

Su richiesta dell'utente è stato controllato anche il progresso Codex Mac:
chat **Completa integrazione e collaudi**, **Correggi blocchi recovery mobile** e
**Ottimizza sync Android e iOS**. Confermano il lavoro recente sul repository
MerchandiseControlSplitView; nessuna ricevuta attuale acquisita per l'uso
installato di cashregistersystem. Il riferimento da considerare è dunque
MerchandiseControlSplitView main `0ce946b`, PR22 del 7 ottobre, schema v22.
`versionName=1.0`/`versionCode=1` non distinguono questi aggiornamenti: usare SHA
e schema. Il checkout Android Windows è 43 commit indietro e resta preservato;
le letture hanno usato origin/main aggiornato, non il checkout arretrato.
Le ricevute Mac sono osservazioni di altre chat, distinte dalla verifica Git/CI
corrente e dall'accettazione autenticata. Il locator dell'owner mobile storico
non risulta leggibile tramite il canale esposto; non sono stati inviati messaggi.

## Prestazioni e ambiente

Host ASUS Zenbook 14 UX3405CA, Intel Core Ultra 7 255H, 16 processori logici,
16.497.893.376 byte RAM; Windows 11 Home Single Language 10.0.26300 x64.
Toolchain global.json SDK 10.0.301; harness CLR 4.0.30319.42000, processo x86.
La stringa OS restituita dal vecchio runtime non sostituisce la versione CIM.
.NET Framework installato sull'host 4.8.1 (Release533509); target compilato net48.

Dataset import: 20.000 righe nuove, SQLite isolato, heartbeat 10 ms. Budget
dichiarato prima della misura: ingresso worker asincrono e Dispatcher che
continua a battere durante DB/CPU. Prima: un solo campione per operazione, dunque
p50/p95 non stimabili. Dopo: un warmup e cinque campioni isolati, nessun build o
test concorrente; p95 nearest-rank coincide con il massimo per n=5.

| Operazione | Prima, singolo campione | Dopo p50/p95/max ms | Ingresso UI ms | Heartbeat / gap massimo |
| --- | --- | --- | --- | --- |
| Preview20k | 408,8ms, 0tick | 381,2 / 389,4 / 389,4 | 4,5–9,6 | 22–24tick / 26,8ms |
| Apply dry-run20k | 584,3ms, 0tick | 459,6 / 497,5 / 497,5 | 5,1–6,2 | 25–30tick / 34,3ms |
| Wizard commit20k | non misurato | 2057,1 (un campione, nessun percentile) | 5,9 | 123tick / 33,9ms |

Dry-run: 40 batch lookup, 41 comandi SQL, zero scritture. Commit: 40 batch,
40.044 comandi SQL applier, 20.000 prodotti, 40.000 history, 3 comandi outbox
nella transazione; 6 connessioni osservate nel percorso completo. Le 44 chiamate
di autorizzazione sono tutte sul Dispatcher. Instrumentazione ProductRepository
non intercetta l'applier: il suo contatore0 non significa zero query.
Commit: delta managed +47.667.776 byte, private +42.958.848 byte, non picchi.
Analisi/render non separati cronometricamente dal preview; peak memory e soak
aperture ripetute non misurati. Il primo tentativo post contaminato da un test
parallelo è conservato e scartato dal confronto. Le prime fixture sperimentali
con requisiti frazionari errati non costituiscono difetti: preservato il corpus
ToEven (1.234,56→1235; 12.5→12), nessun test esistente indebolito.
Nessuna nuova soglia arbitraria e nessun confronto con hardware Win7.

## Journeys, gate, pacchetto e hardware

PASS: restore locked, 49/49 gate canonici, build solution/WPF/harness x86 con
zero warning/errori, 1.248/1.248 test Core/Data (zero skipped), CLI selftest e
backup/restore/failure. PASS wizard preesistente Products/XLSX e
DbMaintenance/XLS-HTML, inclusi diniego senza effetti, cancellazione e
autorizzazioni; un tentativo con parametro `html` non supportato dal generatore
fixture è conservato e corretto usando `xls`, senza modificare il prodotto.
PASS runner funzionale canonico con 17 scenari, immagini/serializzazione,
authorization lease, bounded logging100k e product paging100k. Le nuove quattro
regressioni IMPORT/REFUND/REVERSAL/CUSTOMERDISPLAY sono nel runner reale.
Visuale: 50 screenshot, cinque risoluzioni da1024x768 a1920x1080, focus,
wizard1–4, reso parziale/full void e superfici ricevuta. Review indipendente
completata. Le tre prime failure statiche (signature/worker/commit delegate)
sono conservate; gate aggiornati e rafforzati, nessun controllo disabilitato.

Final60 del candidato `0f4da73352df` resta valido esclusivamente per quel
binding. Scelta esplicita per questo ambito: gate sorgente canonici, intera suite
Core/Data, CLI e backup/restore/fault, build WPF/harness x86, runner funzionale
completo con nuove regressioni, vecchio wizard/autorizzazioni, lease, immagini,
logging100k e paging100k. Il runner funzionale include vendita/ricevuta/restart,
sospesi/recovery, input/scanner, display, load sync/backup e lifetime visuale.
Le modifiche riguardano import, reversal e stato display; nessuna ottimizzazione
della frequenza sync, del carrello hot path o dello scanner. Non si ripete il
soak Final60 e non si attribuisce al nuovo binario una qualificazione Final60.

Win7 SP1, scanner fisico, monitor hotplug, Xprinter e SMB TEST non sono
disponibili: NON_ESEGUITO. L'accettazione futura deve usare lo stesso pacchetto
identificato e comprende installazione/upgrade, avvio offline, Enter/Tab e focus,
vendita/pagamento misto, reso/void/ristampa, sospesi, import/correzione/annullo,
display minimize/reconnect, backup/restore TEST/revisione, riavvio/reconnect.
Stampa: selezione driver/coda, spooler e Notepad, ricevuta/larghezza/encoding,
offline e retry sul documento persistito; una fake printer non attesta la carta.

## Acceptance C01–C12

Nessuna nuova cella live esercitata. Readiness canonica
`docs/HANDOFFS/WIN7POS_ARTICLE_ACCEPTANCE_READY.json`: HTTP404 sia sullo SHA Admin
iniziale `82af13ef` sia sul nuovo main `02ea44b9`, ricontrollato prima del merge.
Non vengono promossi test locali, build o conteggi outbox a PASS live.

| Celle | Stato delle direzioni live supportate / limite |
| --- | --- |
| C01 bootstrap | BLOCKED_READINESS, direzioni supportate POS/Admin/mobile. |
| C02 CRUD | BLOCKED_READINESS. Standalone reference CRUD origin POS non applicabile in article-v1; import può risolvere riferimenti. |
| C03 identità testuale | BLOCKED_READINESS; test locali DB/payload import distinti dalla propagazione live. |
| C04 offline/reopen/reconnect | BLOCKED_READINESS; Admin offline-outbox origin non applicabile, usa RPC online. |
| C05 conflitti | BLOCKED_READINESS, tutte le direzioni supportate. |
| C06 tombstone | BLOCKED_READINESS; standalone reference tombstone origin POS non applicabile; nessuna reactivation mobile dedotta dalla sola mappatura deletedAt. |
| C07 import/prezzi | BLOCKED_READINESS; atomica locale e prezzi verificati su fixture, propagazione live non attestata. |
| C08 vendite/resi/void | BLOCKED_SCOPE; POS→Admin ledger e stock risultante→mobile supportati. Mobile ledger origin/reader non applicabili. Fixture locali economiche sono distinte dal live. |
| C09 immagini/cache | BLOCKED_READINESS, tutte le direzioni supportate. |
| C10 storico prezzi/History | BLOCKED_READINESS; shared-sheet History/session è Android↔Admin↔iOS, POS consumer/origin non applicabile. |
| C11 sessione/generation | BLOCKED_READINESS e scope specifico; test locali autorizzazione non sono una prova live. |
| C12 restore | BLOCKED_SCOPE, richiede owner/finestra/dati TEST recuperabili e artefatti installati. |

Requisito esterno preciso: readiness schema win7pos-article-readiness-v1 in
stato READY, runId del runner, binding SHA client finale/host staging/profilo
DPAPI/scope qa-articles-zero-sales+manifest, deployment/runtime/schema/tre digest
contratto correnti, validità≤2h, deployment verificato nei 15 min precedenti,
contatori503/CPU/memoria/activeQaRuns e scope pulito. Il runner richiede checkout
pulito==origin/main e ReleasePack verificato sullo stesso SHA. Appena disponibile
si esegue POS↔Admin senza attendere mobile; zero-sales non autorizza C08/C12.
Per il mobile servono anche artefatti installati/autenticati e confronti dei
record/revisioni/opId dopo riapertura e seconda propagazione. Nessun incremento
della frequenza polling senza misura dello stadio lento.

Evidenze estese locali: `C:\Dev\_codex-evidence\win7pos-targeted-20261008`;
fixture isolate, log/TRX e ricevute dei runner. Database/token non versionati.

Indice delle prove essenziali sotto tale radice:

- W1 prima: `import-backup-verification-baseline-3/import-backup-verification-baseline.txt`.
- W2/W3 prima: `import-baseline/import-baseline.txt`; log sperimentali in `import-tests-baseline` conservati con le limitazioni sopra.
- W1–W3 dopo: `import-completion-isolated-v2/import-completion.txt` e `import-tests-final-v3/import-final-v3.trx`.
- W4 prima: `refund-baseline-3/functional-completion.txt` (PASS delle asserzioni che riproducono il difetto, non del prodotto).
- W5/W6: `display-baseline/functional-completion.txt`, `display-after/functional-completion.txt`, `display-evidence.md`.
- Gate integrati: `validation/results.json`, `validation/core-data.trx` e log dei runner con percorsi delle fixture/screenshots.
- Review: `independent-review.md`; nessun finding aperto.
- Reversal mirate: radice separata `C:\Dev\_codex-evidence\win7pos-targeted-completion-20261008\auth`, `authorized-reversal.trx`, `reversal-direct-02/functional-completion.txt` e `refund-direct-01/functional-completion.txt`.

La copia finale di consegna di questo stesso report aggiunge gli identificativi
che esistono soltanto dopo il merge (SHA/run/hash); la provenienza del pacchetto
lega autonomamente il binario allo SHA finale senza autoreferenza Git.
