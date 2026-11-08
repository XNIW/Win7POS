# ASUS-W7POS-019 — onboarding execution result

Aggiornamento: 2026-11-07, America/Santiago. Correlation ID
`ASUS-W7POS-019-ONBOARDING-20261107`; scope pubblico `NEW_SHOP_019`.

**Correzione finale in qualifica.** Il candidato e832d6a e le sue prove sotto sono intermedi: una nuova regressione ha riprodotto la riattivazione sale-safe dai vecchi dati dopo empty e delta. Non è il candidato finale da consegnare.
**BLOCKED_EXTERNAL** per onboarding live completo, import reale e Win7 fisico.
Nessuna chiusura DONE: il login riuscito e i verdi software non sostituiscono
questi gate. Il ritorno Mac è PENDING.

## Candidato, repository e ambiente

| Campo | Evidenza verificata |
| --- | --- |
| Codice/pacchetto corrente | `e832d6a96d5a736145a5da0cfb9ad2dffeba25dd`, main pubblicato normalmente; feature959396b e closureQAe832d6a |
| Baseline ripresa | POS main locale/remoto pulito e924a295; precedente codice/pacchetto5c3327bd, conservato con la propria evidence |
| Fingerprint candidato pulito | `c2e98ce089d0b8e4d0b1f4a8a5c5d2bb93ec53fb6be0fc92e91cdbab361d6944` |
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

## Comportamento consegnato per catalogo vuoto

La nuova osservazione empty richiede una catena completa di pagine, summary
coerente con tutti i totali zero, conteggi effettivi, shop ufficiale/bound e stessa
generation/sessione/dispositivo/epoch/cursor. Errori, deny, timeout, pagine parziali,
summary incompleto e scope diverso non la producono. Un nuovo pull invalida la
precedente osservazione; repair/restore e cambi di binding la invalidano.

La conferma preserva righe e cursor precedenti e invalida atomicamente solo il
marcatore sale-safe. Non ricrea trust/lease, non attiva vendite e non riconcilia
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
| Core/Data catalog exactness/state | Prova empty persistente separata dalla sale-safety, invalidazione e13 regressioni |
| WPF bootstrap/login/authority | Accesso storico circoscritto, binding generation, rollback e diagnostica separata login/catalogo |
| WPF workbench/report/status/localizzazioni | Riepilogo/import senza cassa o stampa; copy empty veritiero e risorse dialog condivise |
| Harness e packaging QA | Loopback reale first-login→pull→capability e negativi; copia forwarder pinned, config condivisa e completezza runtime verificata |
| Admin/server/schema | Nessuna modifica; handoff pronto, invio/risposta Mac PENDING anche dopo nuova verifica dei canali disponibili |

## Matrice conclusiva dei gate

| Gate | Stato | Candidato/evidenza e limite |
| --- | --- | --- |
| CONSOLE_LOGIN | BLOCKED_EXTERNAL | Login riferito dall'utente; verifica del principal/deployment Mac non eseguita |
| CONSOLE_READ_SCOPE | BLOCKED_EXTERNAL | Causa statica compatibile d021a4af; superficie staff/deployment da correggere o chiarire |
| POS_FIRST_LOGIN | PASS | Tentativo reale5c3327bd accettato; nessun retest live e832d6a dopo il409 |
| POS_LEASE_BINDING | PASS bootstrap | Scope/trust/policy/mirror verificati nel tentativo5c; contratto installato/heartbeat live restano da attestare al Mac |
| EMPTY_SHOP_HISTORY | BLOCKED_EXTERNAL live | Sintetico e832d6a PASS; server reale409 non ha fornito lo zero autorevole |
| GETNET_PREVIEW | PASS | Reader del prodotto e832d6a net48/x86:7/7 originali/oracolo, integri, NeedsEvidence=true, CanApply=false |
| REAL_IMPORT | BLOCKED_EXTERNAL | Utente conferma assenti mapping documentato, inizioPOS/overlap e prova temporale provider; nessun apply sul DB operativo o di prova reale |
| REPORT_EXPORT | PASS sintetico | e832d6a report/CSV inclusi nello smoke; report/export reali restano BLOCKED con apply |
| PACKAGE | PASS | Prodotto/installer/SBOM/provenance e QA verificati; development-unsigned, installazione NOT_EXECUTED |
| WIN7_PHYSICAL | BLOCKED_EXTERNAL | Host SP1 e periferiche identificati non disponibili; nessun PASS Windows11 equivale a Win7 |

Nessun PASS di una guardia che nega chiude l'onboarding reale. L'import originale
resta locale V1: niente nuove tabelle cloud, upload Excel, storico sincronizzato,
RLS o modifiche ai contratti mobile.

## Prove del candidato e832d6a

| Fonte/comando effettivo | Esito |
| --- | --- |
| [CI42418832135](https://github.com/XNIW/Win7POS/actions/runs/42418832135) — orchestratore canonico Windows All | PASS15/15, exit0; functional20/20, foundation5scenari/37assert, smoke8/44, load1scenario/11check |
| [Release42418832168](https://github.com/XNIW/Win7POS/actions/runs/42418832168) | PASS; Core/Data1728/1728,0failed/skipped e gate50/50 confermati dal log dello stesso SHA |
| [Security42418832124](https://github.com/XNIW/Win7POS/actions/runs/42418832124) | PASS, inclusi CodeQL e supply-chain |
| [WPF42418868960](https://github.com/XNIW/Win7POS/actions/runs/42418868960) | PASS, bundle QA separato |
| Focused locali prima del commit | Core110/110; build Release x86; supervisore statico/runtime; dialog39/39 PASS; finale8/44 con fingerprint stabile |
| Gate locale iniziale | FAIL49/50, poi gate lease PASS dopo separazione del ramo qualificato; gate originale invariato. Tutti50 rieseguiti PASS in Release |
| Test-QaPublicBundle e tre Assert-HistoricalSalesQaPass | PASS sugli artifact CI scaricati con digest GitHub verificato |
| Test-QaNet48PayloadBundle + manifest estratto | PASS40/40, forwarder coincidente con pin del checker; nessun DB/trust/Excel reale nel payload |
| Smoke dal QA scaricato, CWD esterna vuota | PASS8/44, exit0, source/fingerprint stabili e cleanup confermato |
| Avvio ordinario WPF dal QA scaricato | PASS alla ripresa: DB inizializzato, POS access visibile, nessuna credenziale/login/trust; processo posseduto chiuso |
| Reader prodotto reale | PASS7/7, assembly runtime corrispondenti al prodotto,4551ms sul build host; non misura Win7 e non applica dati |

Il totale parametrizzato Core1728 proviene dal log Release dello stesso SHA:
la proiezione CI contiene1653 record nominativi e non espone quel totale.
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

Run canonico CI: `Win7POS.QA.20261107T220502828Z.7c1c98cb41734822b300eccbcdd9e949`.
CI artifact13593925422, archive SHA256
`ff7058c2be606caf13440d0a89ba886ba74f1e6b57d1d4419eb1a96325795a3e`.
QA scaricato: `Win7POS.QA.20261107T220911950Z.d2c7761c1e1e495da1bc398a7e2de26f`.

| Identità | SHA256 |
| --- | --- |
| ZIP prodotto x86 | dd5e0b444d16e89c61df8e4471e4da88e926a4c0a08113a3ab269be156e74249 |
| Installer | 9a936078f0d8f5250b141985aaee4a4a9105d864b25f044b0880d8878daccb59 |
| Archive GitHub QA13592372417 | 008e4108b7a9e70cb2766c6b522b677e5a3d3969ba97c4990aa6df415db08c3a |
| ZIP QA | c2aafd01d93d48cea0adac7f180af6b11a8a3e8f612993c522dc90256d39e224 |
| Manifest QA | fe95c4f90cf093587fff1347594101705cfea7f6cb24ed9101c3aee33b652b2e |
| Harness QA | 93f80deae869d0de0c356ba987200fcaa04870c6502365544af7c9db453d4bc4 |
| WPF QA/prodotto | 3996aa96883d76ec0ca51686456567ccf2f1a68b622d233de472b5d797d68d54 |
| Manifest fixture sintetiche | 72cd6f0afc82e418d5676fc329f61761d224e8aade30da98ada4ad30ee64620e |

Verificatore canonico download Release: PASS41 file prodotto,110 file coperti da
checksum,0 signing phases. QA40 file,34 comuni con il prodotto byte-identici.
Il verificatore registra installation=NOT_EXECUTED: l'avvio estratto non è
un'installazione. La consegna non viene presentata come firmata.

Root privata:
`C:\Dev\_codex-private\ASUS-W7POS-019\onboarding-20261107T212334Z.7cb660d9f0ee487eb524ef62ed9b5767`.
Receipt principali: run.private.json, first-login-observed.private.json,
binding-readonly.private.json, ci-final-verification/ci-final-verification.json,
release-download-final/download-verification.json,
qa-delivery/verification.private.json, qa-delivery/package-smoke-summary.private.json,
qa-delivery/startup-resume-result.private.json e candidate-reader/verification.private.json.
Originali, hash/controlli finanziari e identità reali restano privati.

## Prime failure e interruzioni conservate

| Evidence | Esito originario e correzione |
| --- | --- |
| QA companion5c startup | FAIL ValueTuple mancante; ora file/config/closure verificati e avvio e832d6a PASS |
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
| focused-11 | PASS8/44 finale precommit; esiti definitivi attribuiti separatamente al QA scaricato e832d6a |
| Required gates locale | FAIL49/50 sopra; nessuna assertion rimossa per passare |
| Prima verifica manuale QA | INVALID: import Validation mancante e label PASS inutilizzabile; receipt conservato invalid, nuova verifica Stop-on-error PASS |
| Primo avvio QA candidato | NOT_RUN per Esc utente prima dell'osservazione; nessun bypass. Ripresa autorizzata, nuovo profilo e nuova ricevuta PASS |

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
| Console staff e catalog scope409 | Mac: verificare deployment/progetto, principal/lease, mapping e resolver installati; correggere provisioning empty-scope/overview senza grant globali o dati fittizi, regressioni e rollout del repo, poi handoff REVIEW |
| Retest live e832d6a | Windows dopo handoff Mac: stesso host/contratto/scope, normale UI mascherata su profilo identificato; nessuna nuova readiness articoli usata per aggirare il409 |
| Mapping/tempo/overlap Getnet | Utente/responsabile: fonte autorevole stesso esercente/periodo, avvioPOS e overlap. Poi backup/preview/apply su DB di prova vincolato, riapertura/dedup/undo/reimport/report/CSV; mai DB operativo come effetto collaterale |
| Verifica visiva import_summary | Diagnostica NOT_RUN: nessun PNG fittizio o soglia rilassata. Registrare separatamente un'eventuale futura osservazione qualificata |
| Win7 SP1/periferiche | Responsabile fisico: usare il nuovo pacchetto x86 e la scheda esterna con hash/contesto corretti; installazione, DPI/tastiera, scanner/stampa sintetici autorizzati, niente SDK moderno o pwsh sul POS |

Vedere la [scheda esterna](ASUS_W7POS_019_EXTERNAL_QUALIFICATION.md) e il
[registro Execution](../plans/ASUS-W7POS-019_GETNET_HISTORY/EXECUTION.md).
La chiusura documentale non cambia il codice/pacchetto qualificato e832d6a.


## Review conclusiva: regressione empty → delta riprodotta

La review ha individuato un percorso oltre i casi iniziali: old catalog verified,
full cursor e initial_completed restavano validi dopo l'osservazione empty.
Un delta terminale senza summary poteva riusare le righe conservate e ripristinare
sale-safe. Quattro workflow verdi di e832d6a non coprivano questa sequenza.

Test-only sul runtime e832d6a, prima di qualsiasi fix:
- Core AuthoritativeEmpty_DeltaCannotRestoreSaleSafeFromPreviousVerifiedSnapshot:
  FAIL1/1, StoreSaleSafeAsync non lancia il rifiuto atteso; TRX conservato.
- Vero loopback old full → full empty → terminal delta senza summary:
  FAIL7/8,39assert completati, empty_history_terminal_delta_resurrected_old_catalog,
  fingerprint stabile, no timeout, cleanup=true e nessun orfano locale.

Evidence private empty-delta-regression-red e empty-delta-loopback-red. Nuovo
fix: fence durevole indipendente dalla proof effimera; forza Full nel scheduler
anche all'ingresso diretto del servizio, impedisce SaleSafe/ordinarysale e viene
rimosso soltanto dal commit atomico di una full validata non vuota. Nuovi test
negativi/rollback, focus green e CI/package dello SHA correttivo restano da acquisire.
I risultati e artifact e832d6a restano intatti, non riattribuiti al fix.


## Correzione fence: focused green prima del nuovo commit

La regressione Core ora passa; il set pertinente è116/116,0failed/skipped.
Il vero loopback conserva la controprova rossa e passa immutato:8/8,44/44,
runWin7POS.QA.20261108T010014124Z.0105b9698e18432ca55aeef998b659c9,
fingerprint stabile e cleanup confermato. Fullnonempty recupera correttamente;
rollback tardivo e approvazioni generiche non rimuovono il fence. Gate50/50 PASS
nel runlocale successivo. Nessuna soglia, expected exception o assertion rimossa.

Il vincolo è una presenza failclosed in app_settings, fuori dal prefisso della
proof: sopravvive ai suoi clear e alle normali operazioni metadata repair/restore
review/relink; per sostituzioni dell'intero DB valgono inoltre i gate restore e
authority/epoch esistenti, non una promessa di persistenza di un singolo key in
un backup precedente. Unico clearing nel finalizer atomico full, con verifiche
full mode e conteggi active nonzero. Scheduler e servizio diretto forzano Full.
Nuovo candidato/CI/package sono necessari: non riattribuire i verdi e832d6a.
