# ASUS-W7POS-019 — onboarding execution result

## Consegna corrente — 8 novembre 2026

Client/pacchetto `1823697f1f0c965fb8fd9d85259a862160f12c59`, software
qualificato e installato41/41file. Retry empty abilita lo storico senza cassa;
Retry full nonempty ammette l'accesso normale, con una sola autenticazione HTTP,
continuazione protetta5min e rollback dell'autorità posseduta sui fallimenti.
CI15/15,1734Core/Data,50gate,52assert smoke, foundation5/37 e load11 PASS;
QA/prodotto34file comuni identici, reader7/7 e osservazione import_summary PASS.
[Provenienza e gate attuali](ASUS_W7POS_019_RETRY_E2E_RESULT.md).

L'autorizzazione all'avvio reale è stata concessa: il nuovo comando è stato
comunque rifiutato con `blocked by policy`, senza avvio o nuovi pull osservati.
Resta BLOCKED_POLICY, non una richiesta della stessa autorizzazione pendente.
Admin PR144 è candidato REVIEW, non deploy; mapping/tempo/overlap e Win7 restano
BLOCKED_EXTERNAL. Il testo successivo è storia delle consegne precedenti.

Aggiornamento: 2026-11-07, America/Santiago. Correlation ID
`ASUS-W7POS-019-ONBOARDING-20261107`; scope pubblico `NEW_SHOP_019`.

**SOFTWARE_DELIVERED** per il delta Windows e i pacchetti verificati.
**BLOCKED_EXTERNAL** per onboarding live completo, import reale e Win7 fisico.
Nessuna chiusura DONE: il login riuscito e i verdi software non sostituiscono
questi gate. Il ritorno Mac sulla console resta PENDING; il fix catalogo server è descritto sotto.

## Prosecuzione8novembre2026 — causa409 corretta sul server

PR Admin143 integrata in1a35034a e migration singola applicata sul progetto
verificato: la lease reale ora riceve un manifest canonico completo zero.
OID/owner/ACL e tutti gli altri metadata sono invariati. CI post-merge e4375
pgTAP PASS; nessun nuovo Worker o client. HTTP/UI normale resta NOT_RUN_POLICY
per il rifiuto di avvio, nessun processo o nuovo pull. Autorizzazione specifica
pendente; nessun altro launcher. Il409 del7novembre sotto è la prima failure
storica conservata, non lo stato attuale del resolver.
[Risultato circoscritto](ASUS_W7POS_019_CATALOG_409_FIX_RESULT.md).
Console staff, prove Getnet e Win7 restano separati; nessun DONE.

## Candidato, repository e ambiente

| Campo | Evidenza verificata |
| --- | --- |
| Codice/pacchetto corrente | `d262ccd979db8e3407118ffa7275861f12fdb5e2`, main pubblicato normalmente; feature959396b, closureQAe832d6a e correzione conclusiva d262ccd |
| Baseline ripresa | POS main locale/remoto pulito e924a295; precedente codice/pacchetto5c3327bd, conservato con la propria evidence |
| Fingerprint candidato pulito | `6ae00d6f8b1aa6f76a81b97804066a1e5ece2e76a6e419a276ea5a7cd98dff93` |
| Admin main remoto | `d021a4af405e64571ed0c9fea8e6e2debef158a6`, invariato anche alla verifica conclusiva; non prova la versione live |
| Checkout Admin Windows | Main pulito d283f76a all'avvio, preservato: nessun pull/reset/stash/edit/build |
| Host live indicato dall'utente | Staging Workers, coincidente con il default POS; URL completo e identificativi nel manifest privato |
| Piattaforma delle prove locali | Windows11, net48/x86; SDK10.0.301 riusato solo sul build host, nessun aggiornamento framework/package |
| Pubblicazione | Nessuna nuova branch/PR, force push o bypass; chiusura documentale separata dal candidato binario |

La Shop è stata creata secondo dichiarazione utente. Il successivo normale login
POS ha verificato lo scope atteso nel proprio tentativo. Questo non prova il
mapping Getnet, la completezza del provisioning o la versione del deployment.
La priorità env/config/default di PosAdminWebOptions è invariata. I test sintetici
usano profili propri e loopback; nessun vecchio override è stato riutilizzato.

## Console e primo accesso: due cause distinte

Il sintomo console ricevuto era approssimativo: scope/path non autorizzato,
righe0, scritture OFF e contratto sola lettura. Non è stato assunto come codice
applicativo esatto. Sul main Admin indicato, getShopAdminReadModel richiede il
permesso di sezione, poi esclude il principal staff dalla superficie overview
priva di lettura lease-bound; fallbackSection mostra proprio zero righe e
scritture disattivate. È una superficie staff incompleta dietro un confine
intenzionale, con copy ambiguo; non dimostra Shop disattivata o dati economici zero.
La conferma sul deployment/principal reale resta al Mac.

Riferimenti puntuali, route, SQL e verifiche richieste sono nell'
[handoff Admin](../plans/ASUS-W7POS-019_GETNET_HISTORY/ONBOARDING_ADMIN_HANDOFF.md).
Il percorso statico console è read-model.ts L257 e fallbackSection; /shop e
/shop/overview consumano overview. Nessun fix Admin/deploy viene dichiarato.

Il normale primo accesso reale, sul prodotto5c3327bd, è stato eseguito dall'utente
nel campo mascherato, una sola volta: niente credenziale in argv, workflow,
file o report. Le prove del tentativo restano attribuite a quel candidato.

| Stato osservato nel tentativo reale | Esito |
| --- | --- |
| firstLoginSucceeded / policy | True: risposta accettata dal bootstrap, non Supabase Auth diretto |
| trustedSessionPersisted | True; trust persistito e generation attiva |
| Binding ufficiale | Snapshot shop, generation e mirror corrispondono allo scope atteso privato |
| Staff/ruolo | Mirror attivo pos_admin, mappato ad admin locale; nessun nuovo grant necessario, uso sempre subordinato alla lease |
| catalogStarted / catalogCompleted | True / false |
| catalogSaleSafe / CanOpenPos | False / false |
| Route e risultato | POST /api/pos/catalog/pull, HTTP409, code unmapped, stage server_response, requiresRetry=yes |
| Accesso operatore alla cassa | Non completato: UI autenticazione distinta dal blocco catalogo |

Lo zero del profilo locale non qualifica lo shop come autorevolmente vuoto.
Il server restituisce unmapped nel ramo catalog-pull.ts L1043. La traccia statica
RPC porta al resolver che blocca mapping non valido oppure assenza sia di mapping
verificato sia di righe shop-scoped. È una pista concreta da confrontare con il
contratto installato; è distinta dal mapping dell'esercente Getnet. Nessun articolo
fittizio, membership personale o grant globale è stato creato.

Letture SQLite mode=ro hanno verificato binding e assenza di vendite, prodotti,
outbox commerciale e transazioni storiche nel nuovo profilo, senza leggere hash
credenziali/token. Una query iniziale usava un nome chiave shop errato: conservata
privatamente, corretta contro OutboxShopBinding, nessuna modifica del DB.
Dopo il409 il processo posseduto è stato fermato e il profilo mantenuto; nessuna
revoca server e nessun nuovo tentativo live o uso del vecchio harness Staging articoli.

## Correzione conclusiva: empty non deve riattivare il vecchio catalogo

Il candidato intermedio e832d6a aveva quattro workflow verdi ma non copriva
old full verificata → full empty → delta terminale senza summary. La review
ha riprodotto la riattivazione prima del fix: Core1/1FAIL (StoreSaleSafeAsync
non negava), vero loopback7/8FAIL con fingerprint stabile e cleanup confermato.
Il DB sintetico conservato aveva sale-safe persistito, exactnessVerified,
initial-completed e1vecchia riga attiva: nessuna ipotesi basata sul solo nome errore.

Il correttivo d262ccd registra il vincolo persistente
`pos.catalog.full_refresh_required_after_empty`, separato dalla prova temporanea.
Il clear della proof, una nuova generation, repair o approvazioni generiche
non riabilitano vendite. Scheduler e ingresso diretto del servizio richiedono Full;
Data nega SaleSafe e vendita ordinaria finché il finalizer atomico non verifica
full mode e conteggi active nonzero. Solo quel commit rimuove il vincolo; una
failure tardiva prima del commit, nella stessa transazione, lo conserva con
rollback. Le sostituzioni dell'intero DB restano inoltre soggette ai gate
restore/authority/epoch esistenti.

Focused correttivo: Core116/116, loopback8/44, gate50/50. Il rosso è immutato;
il positivo recupera con full nonempty. Vecchie prove e832d6a restano riferite
al candidato intermedio e non attribuite al correttivo.
Run loopback correttivo prima del commit:
`Win7POS.QA.20261108T010014124Z.0105b9698e18432ca55aeef998b659c9`.
Receipt privata in empty-delta-loopback-green-01; Core116 in empty-delta-green-01.

## Comportamento consegnato per catalogo vuoto

La nuova osservazione empty richiede una catena completa di pagine, summary
coerente con tutti i totali zero, conteggi effettivi, shop ufficiale/bound e stessa
generation/sessione/dispositivo/epoch/cursor. Errori, deny, timeout, pagine parziali,
summary incompleto e scope diverso non la producono. Un nuovo pull invalida la
precedente osservazione; repair/restore e cambi di binding la invalidano.

La conferma preserva righe e cursor precedenti e invalida atomicamente il
marcatore sale-safe e impone un vincolo durevole fino a una nuova full nonempty
valida. Non ricrea trust/lease, non attiva vendite e non riconcilia
un catalogo precedente con una risposta non qualificata. Lo stato empty_verified
è distinto da errore: copy e prossima azione sono veritieri solo con generation
e proof correnti; il ruolo non riceve una promessa automatica di accesso storico.

Il primo login offre un workbench storico separato. La capability verifica lease,
permessi e la stessa fingerprint della generation della prova; sessione A e proof
B valide separatamente sono negate. Il ramo qualificato precede il ramo generico
catalogo non sicuro, che continua soltanto a preparare recovery esplicita. In caso
di errore/cancellazione della preparazione, il mirror login viene disconnesso.

Il workbench riusa import, riepilogo/grafico/CSV e tutte le guardie merchant,
periodo, dataset, restore, backup, receipt, dedup e undo. Non crea PosViewModel,
non apre cassa/pagamenti e disabilita stampa. La recovery allowlist è invariata.
Il catalogo valido conserva il flusso normale. Focus iniziale, tastiera, dialog
standard, Dispatcher e scadenza/revoca sono coperti dalle prove sintetiche; gli
errori inattesi degli handler hanno messaggio localizzato e log del solo tipo.

| Ambito modificato | Motivo |
| --- | --- |
| Core/Data catalog exactness/state | Prova empty persistente separata dalla sale-safety, invalidazione e19 risultati empty/fence |
| WPF bootstrap/login/authority | Accesso storico circoscritto, binding generation, rollback e diagnostica separata login/catalogo |
| WPF workbench/report/status/localizzazioni | Riepilogo/import senza cassa o stampa; copy empty veritiero e risorse dialog condivise |
| Harness e packaging QA | Loopback reale first-login→pull→capability e negativi; copia forwarder pinned, config condivisa e completezza runtime verificata |
| Admin/server/schema | Nessuna modifica; handoff pronto, invio/risposta Mac PENDING anche dopo nuova verifica dei canali disponibili |

## Matrice conclusiva dei gate

| Gate | Stato | Candidato/evidenza e limite |
| --- | --- | --- |
| CONSOLE_LOGIN | BLOCKED_EXTERNAL | Login riferito dall'utente; verifica del principal/deployment Mac non eseguita |
| CONSOLE_READ_SCOPE | BLOCKED_EXTERNAL | Causa statica compatibile d021a4af; superficie staff/deployment da correggere o chiarire |
| POS_FIRST_LOGIN | PASS | Tentativo reale5c3327bd accettato; nessun retest live d262ccd dopo il409 |
| POS_LEASE_BINDING | PASS bootstrap | Scope/trust/policy/mirror verificati nel tentativo5c; contratto installato/heartbeat live restano da attestare al Mac |
| EMPTY_SHOP_HISTORY | PASS RPC server / UI NOT_RUN_POLICY | Sintetico d262ccd PASS; dopo fix Admin1a35034a il wrapper v3 reale fornisce zero autorevole. Accesso UI normale ancora da osservare |
| GETNET_PREVIEW | PASS | Reader del prodotto d262ccd net48/x86:7/7 originali/oracolo, integri, NeedsEvidence=true, CanApply=false |
| REAL_IMPORT | BLOCKED_EXTERNAL | Utente conferma assenti mapping documentato, inizioPOS/overlap e prova temporale provider; nessun apply sul DB operativo o di prova reale |
| REPORT_EXPORT | PASS sintetico | d262ccd report/CSV inclusi nello smoke; report/export reali restano BLOCKED con apply |
| PACKAGE | PASS | Prodotto/installer/SBOM/provenance e QA verificati; development-unsigned, installazione NOT_EXECUTED |
| WIN7_PHYSICAL | BLOCKED_EXTERNAL | Host SP1 e periferiche identificati non disponibili; nessun PASS Windows11 equivale a Win7 |

Nessun PASS di una guardia che nega chiude l'onboarding reale. L'import originale
resta locale V1: niente nuove tabelle cloud, upload Excel, storico sincronizzato,
RLS o modifiche ai contratti mobile.

## Prove del candidato d262ccd

| Fonte/comando effettivo | Esito |
| --- | --- |
| [CI42432623358](https://github.com/XNIW/Win7POS/actions/runs/42432623358) — orchestratore canonico Windows All | PASS15/15, exit0; functional20/20, foundation5scenari/37assert, smoke8/44, load1scenario/11check |
| [Release42432623356](https://github.com/XNIW/Win7POS/actions/runs/42432623356) | PASS; Core/Data1734/1734,0failed/skipped e gate50/50 confermati dal log dello stesso SHA |
| [Security42432623359](https://github.com/XNIW/Win7POS/actions/runs/42432623359) | PASS, inclusi CodeQL e supply-chain |
| [WPF42432636966](https://github.com/XNIW/Win7POS/actions/runs/42432636966) | PASS, bundle QA separato |
| Focused locali prima del commit | Core116/116; build Release x86; supervisore statico/runtime; dialog39/39 PASS; finale8/44 con fingerprint stabile |
| Gate locale iniziale | FAIL49/50, poi gate lease PASS dopo separazione del ramo qualificato; gate originale invariato. Tutti50 rieseguiti PASS in Release |
| Test-QaPublicBundle e tre Assert-HistoricalSalesQaPass | PASS sugli artifact CI scaricati con digest GitHub verificato |
| Test-QaNet48PayloadBundle + manifest estratto | PASS40/40, forwarder coincidente con pin del checker; nessun DB/trust/Excel reale nel payload |
| Smoke dal QA scaricato, CWD esterna vuota | PASS8/44, exit0, source/fingerprint stabili e cleanup confermato |
| Avvio ordinario WPF dal QA scaricato | PASS alla ripresa: DB inizializzato, POS access visibile, nessuna credenziale/login/trust; processo posseduto chiuso |
| Reader degli originali con runtime prodotto | PASS7/7, assembly runtime corrispondenti al prodotto,4588ms sul build host; non misura Win7 e non applica dati |

Il probe privato ha eseguito le DLL del QA; il successivo confronto dei loro
hash runtime con il prodotto ne ha attestato l'identità per il PASS
finale del reader. Nessun atteso è ricavato dal parser.

Il totale parametrizzato Core1734 proviene dal log Release dello stesso SHA;
non è ricavato dal conteggio dei record nominativi proiettati dalla CI.
Il load conserva separati measurementCompleted=true, environmentValid=true e
thresholdsPassed=true; soglie e budget invariati. Sono misure del build host.
La CI espone quattro assertion cleanup PASS; non pubblica conteggi per Job,
quindi non viene inventato un numero di residui hosted.

Limite visivo esplicito: hourly_mixed e hourly_negative PASS; la cattura
import_summary è NOT_RUN/unavailable, anche nel QA scaricato. Il raster risultava
uniforme (qa_visual_blank_withheld) ed è stato scartato senza PNG. Il motivo del
render uniforme non è dimostrato; non prova una UI vuota. Il contratto consente
questa diagnostica partial e HIST08 resta PASS5/5, ma quel PASS non attesta la
verifica visiva del riepilogo import. Lettura assistiva manuale e DPI/hardware
Win7 rimangono nella scheda esterna; non sono attribuiti alla sola accessibility tree.

## Artifact ed evidence

Run canonico CI: `Win7POS.QA.20261108T010513694Z.48d7f3fff9ca4cbf93dcf90d91296025`.
CI artifact13599430953, archive SHA256
`ab8822be70a2c71144272486781d0fda080c3d9d91b787c346b654d7ac15b0e0`.
QA scaricato: `Win7POS.QA.20261108T011023899Z.bd7caca208574ff0b1ba247840e1522b`.

| Identità | SHA256 |
| --- | --- |
| ZIP prodotto x86 | bee7c288096d57234f087eb63fe4bc85b128f8953eccebf710acc5b383e14716 |
| Installer | 27537a857ee7f88f30fc81f4dca664960b072e84ca76f6e876abb03989735f20 |
| Archive GitHub QA13598003142 | a1a4c05f47a620e7fda283ff93dfee43ef96097a69ad44b4c4d6b1af4f4b004c |
| ZIP QA | 4595610fb0cc3f5084b6157155dc932cc76d8e931f89347a775de5c5f3c76b2e |
| Manifest QA | 2fde8d2452b41135dadf7c5629b31f68c44da68a31521d57e9f223b995357014 |
| Harness QA | 2e70201c6679d5888ec0b007e575eed8828d1e44d9bef9d64caf123ce009fc9e |
| WPF QA/prodotto | 933739dd0912c098dda0237d0df67148de433cb17feca6d535d0b1e8848a9940 |
| Manifest fixture sintetiche | 72cd6f0afc82e418d5676fc329f61761d224e8aade30da98ada4ad30ee64620e |

Verificatore canonico download Release: PASS41 file prodotto,110 file coperti da
checksum,0 signing phases. QA40 file,34 comuni con il prodotto byte-identici.
Il verificatore registra installation=NOT_EXECUTED: l'avvio estratto non è
un'installazione. La consegna non viene presentata come firmata.

Root privata:
`C:\Dev\_codex-private\ASUS-W7POS-019\onboarding-20261107T212334Z.7cb660d9f0ee487eb524ef62ed9b5767`.
Receipt principali: run.private.json, first-login-observed.private.json,
binding-readonly.private.json, delivery-d262ccd/ci-final-verification/ci-final-verification.json,
delivery-d262ccd/release-download-final/download-verification.json,
delivery-d262ccd/qa-delivery/verification.private.json, delivery-d262ccd/qa-delivery/package-smoke-summary.private.json,
delivery-d262ccd/qa-delivery/startup-result.private.json,
delivery-d262ccd/candidate-reader/verification.private.json,
delivery-d262ccd/qa-product-byte-comparison.private.json e
delivery-d262ccd/cleanup-final.private.json.
Originali, hash/controlli finanziari e identità reali restano privati.

## Prime failure e interruzioni conservate

Aggiunta la controprova e832d6a Core/loopback empty→delta sopra, in
empty-delta-regression-red e empty-delta-loopback-red; nessun esito rosso
riscritto come verde. Il correttivo è qualificato su un nuovo SHA.

| Evidence | Esito originario e correzione |
| --- | --- |
| QA companion5c startup | FAIL ValueTuple mancante; ora file/config/closure verificati e avvio d262ccd PASS |
| focused-01 | FAIL109/110: connessione fixture lasciata aperta prima del secondo DB; lifetime corretto, budget30s invariato |
| focused-02 build | FAIL cast interfaccia/concreto; riferimento sessione catturato correttamente |
| focused-03 primo wrapper | FAIL preflight, output non precreato; nessun worker, nuova root per invocazione corretta |
| focused-03 smoke-02 | FAIL7/8: DB custom fixture diverso dal profilo default immagini; fixture posseduta allineata |
| focused-04 | FAIL7/8: endpoint chiuso ereditato; scope loopback fixture ripristinato nel finally |
| focused-05 | FAIL7/8: contatori nullable non valorizzati; passati conteggi ricevuti senza rilassare check |
| focused-06 | FAIL7/8: owner test non mostrato; sequenza corretta |
| focused-07 | FAIL43/44: focus iniziale; focus al ContentRendered, nessun positioning/sizing |
| focused-08 | PASS8/44 prima del delta copy; non riattribuito automaticamente al candidato finale |
| focused-09 | FAIL7/8: writer errori trasformava vuoto in failure; successo usa il writer generation-guarded appropriato |
| focused-10 | FAIL43/44: owner sintetico fuori workarea per cascata OS; centrato solo owner test, limite1024x768 invariato |
| focused-11 | PASS8/44 finale precommit; esiti definitivi attribuiti separatamente al QA scaricato d262ccd |
| Required gates locale | FAIL49/50 sopra; nessuna assertion rimossa per passare |
| Prima verifica manuale QA | INVALID: import Validation mancante e label PASS inutilizzabile; receipt conservato invalid, nuova verifica Stop-on-error PASS |
| Primo avvio QA e832d6a | NOT_RUN per Esc utente prima dell'osservazione; nessun bypass. Ripresa autorizzata, nuovo profilo e nuova ricevuta PASS |

Le root originarie e i risultati parziali non sono stati riscritti come successi.
Il checkpoint documentale Esc è conservato privatamente prima della riconciliazione.

## Backup, cleanup, privacy e gate aperti

Nessun apply reale: nessun backup operativo o undo reale simulato con restore.
I backup/restore/undo delle fixture sintetiche restano coperti dalla foundation.
Gli originali sono invariati prima/dopo il reader; nessuna vendita fiscale,
mutazione articoli live, outbox reset, grant o revoca sperimentale dell'account.
Tutti i processi locali POS/QA posseduti sono chiusi con identità verificata;
probe reader exit0. DB/profili/log/fixture/backup sintetici restano intenzionalmente
conservati e mappati al run, owner Windows. Nessuna directory generica cancellata.
La sessione reale è conservata per diagnosi, non revocata indiscriminatamente.

Incidente di lettura: durante la prima sessione una lettura troppo ampia ha
mostrato dettagli dell'oracolo privato nell'output strumenti. Comunicato all'utente
e registrato privatamente; i successivi output sono filtrati. I controlli su
repository/artifact non annullano quell'esposizione in chat. Nessuna credenziale,
identità reale o controllo finanziario è introdotto nei documenti finali/pacchetti.

| Gate aperto | Owner e prossimo passo concreto |
| --- | --- |
| Console staff | Mac: verificare la superficie overview/principal e il suo stato veritiero. Il resolver catalogo è stato corretto e verificato separatamente nella prosecuzione8novembre |
| Retest normale d262ccd | Windows dopo autorizzazione pertinente: un solo avvio del medesimo profilo/prodotto, verificando prima gli esiti incerti del comando negato. Server RPC già PASS; nessun launcher alternativo |
| Mapping/tempo/overlap Getnet | Utente/responsabile: fonte autorevole stesso esercente/periodo, avvioPOS e overlap. Poi backup/preview/apply su DB di prova vincolato, riapertura/dedup/undo/reimport/report/CSV; mai DB operativo come effetto collaterale |
| Verifica visiva import_summary | Diagnostica NOT_RUN: nessun PNG fittizio o soglia rilassata. Registrare separatamente un'eventuale futura osservazione qualificata |
| Win7 SP1/periferiche | Responsabile fisico: usare il nuovo pacchetto x86 e la scheda esterna con hash/contesto corretti; installazione, DPI/tastiera, scanner/stampa sintetici autorizzati, niente SDK moderno o pwsh sul POS |

Vedere la [scheda esterna](ASUS_W7POS_019_EXTERNAL_QUALIFICATION.md) e il
[registro Execution](../plans/ASUS-W7POS-019_GETNET_HISTORY/EXECUTION.md).
La chiusura documentale non cambia il codice/pacchetto qualificato d262ccd.
