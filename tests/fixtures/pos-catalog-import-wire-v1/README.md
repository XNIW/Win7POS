# Byte reali C# e riproduzione Admin

`baseline-small` proviene da Win7POS `8ede85ee37ed9ecd0af83203df8551cc5a91f6aa`
con il solo nuovo test `CatalogImportInteropEvidenceTests` nel runner Core/Data.
Il test usa builder reale, apply/SQLite, sync, recovery e serializer HTTP del
prodotto. Tutte le credenziali e i dati sono sintetici. I file non sono stati
normalizzati o corretti manualmente; il manifest conserva byte UTF-8 e SHA256.

Il peer loopback perde la risposta ordinaria, fornisce `not_found`, quindi
simula un'accettazione tardiva osservata dal retirement. Permette poi la
creazione della correzione e il percorso lookup/retirement della figlia.
Queste risposte servono soltanto a produrre i body: **non attestano Admin o DB**.

Il replay separato sul vero parser/handler Admin
`62f513f6ca15662e6bbb384a7510977fe68e1267` ha accettato il parser della richiesta
ordinaria (`attemptCount=1`) e rifiutato tutti i sei body U2 con HTTP 400
`validation_failed`, prima di auth/RPC. Si veda la ricevuta JSON nel corpus.
Il caricatore e i fixture di autenticazione/RPC sono quelli del test foundation
ufficiale Admin, senza modificare i sorgenti Admin. Non è una prova PostgreSQL.

`baseline-1001` conserva l'output builder -> SQLite originale: 1001 righe in
266464 byte, un solo batch. Il test di regressione al limite righe ha fallito
nella baseline; il limite byte, da solo, non intercettava il problema.

`candidate-5000` conserva cinque operazioni di un **nuovo import pianificato**,
generate dopo le modifiche al planner. Il test controlla copertura di 5000
barcode senza duplicati, originali immutati, stock locale 1.25 per prodotto e
10000 associazioni allo storico prezzi. I cinque ACK del peer sono sintetici:
la loro presenza non qualifica la riconciliazione Admin, né il recupero di un
originale legacy da 5000 righe. Il corpus è pronto per il replay con PostgreSQL.
Usare una fixture database isolata per ciascuno scenario: il barcode `INTEROP-0`
è presente anche nel corpus piccolo.

`candidate-current-utc` contiene la rigenerazione dopo due correzioni del
prodotto emerse nel replay: date di creazione correnti (i precedenti timestamp
derivati dal fingerprint cadevano nel 2030/2035 e venivano rifiutati dal vero
RPC di correzione) e fingerprint della preview di recupero ridotto al suo
SHA256 (prima occupava circa 394 KiB in ogni envelope). I vecchi corpus restano
intatti. Il recupero `never_sent` contiene ora cinque parti per tutte le 5000
righe, compresi 4999 prodotti rimasti localmente invariati. Una modifica locale
al prezzo genera una sola riga aggiuntiva nello storico; il padre si chiude
soltanto al quinto ACK. Questi ACK sono ancora quelli del peer sintetico.

Il test C# ufficiale ha passato sei casi: percorso piccolo completo, nuovo
import 5000, recupero legacy `never_sent` 5000, e body da 524287/524288/524289
byte. I primi due body di confine sono HTTP reali; quello oltre il limite è
conservato come `candidate`, bloccato prima dell'invio. Il reader HTTP Admin
reale conferma le soglie sia con Content-Length sia senza; il suo parser
accetta tutte le dieci parti valide e i due body al confine. Questa osservazione
non sostituisce handler con database, receipt persistite o deployment servito.

Per generare nuovi byte: impostare `WIN7POS_INTEROP_EVIDENCE_DIR` su una nuova
directory ed eseguire il test Core/Data `CatalogImportInteropEvidenceTests`.
Non sovrascrivere le fixture baseline.

Per il replay (working directory Win7POS), con root Admin qualificata e una
root con il `node_modules/typescript` previsto dal lockfile Admin:

```text
node scripts/qa/replay-catalog-import-wire.mjs ADMIN_ROOT CORPUS_ROOT DEPENDENCY_ROOT baseline-rejection
node scripts/qa/replay-catalog-import-wire.mjs ADMIN_ROOT CORPUS_ROOT DEPENDENCY_ROOT accept
```

La modalità è esplicita: una riproduzione positiva del rifiuto storico non
equivale al superamento del contratto dopo la correzione. Il replay verifica
tutti gli hash prima di leggere il JSON e scrive una ricevuta distinta.
Le prove aggiuntive derivate da ciascun body sono elencate separatamente in
`checks`: il corpus originale non viene riscritto. Il reader HTTP Admin reale
controlla il limite byte sia con Content-Length sia senza. Le varianti tentativo
omesso/zero/positivo verificano separatamente il contratto ordinario e quello
forense. Solo la modalità `accept` richiede che U2 accetti gli originali validi
e rifiuti le alterazioni di identità/hash/shop/device e autenticazione corrente.

Il replay indipendente Asus su Admin `bee0670a6b9b9fd56d2abb9f29e22ccc45293888`
accetta ora gli stessi sette body sia della baseline iniziale sia del corpus
UTC: sette parser e sei handler U2. Il tentativo ordinario omesso/zero resta
rifiutato. Il fixture foundation verifica i token e lo shop nel vero handler;
per gli ID della sessione/device, il caricatore ufficiale fornisce un lease
sintetico. Il nostro controllo verifica il diniego della dipendenza e gli ID
effettivamente inoltrati, senza dichiarare un'autenticazione database reale.

`admin-db-3f2c691` conserva le risposte effettive del database isolato per il
vecchio `candidate-5000`; `admin-db-bee0670` quelle del nuovo corpus UTC,
con 15 postcheck PostgreSQL superati. Il test C# reingesta entrambi gli archivi
senza cambiare richieste o risposte e confronta ogni ID prodotto/prezzo, valori,
storico e completamento del gruppo. I due risultati restano distinti.

`candidate-uncertain-5000` contiene il successivo percorso di recupero per un
originale legacy con esito incerto: byte C# reali, upload completo, retirement,
bozza riaperta, piano e cinque apply. Il README interno distingue le risposte
sintetiche di generazione dalla successiva prova Admin/PostgreSQL richiesta.
Un test separato perde la risposta della terza parte, attende il backoff reale
di 30 secondi e riapre factory/servizio: ripete soltanto l'apply della terza
parte, con body identico, prima di completare le ultime due. Non riscrive tempi,
stati o payload della coda per anticipare il retry.

Tre prove aggiuntive del journal interrompono il recupero dopo la registrazione
del piano: risposta HTTP persa, errore prima della pubblicazione del backup
verificato e rollback dopo l'enqueue SQLite. Prima del primo upload del piano
controllano già sul database intenti, operazioni, documento, hash e barriera
di invio. Dopo la riapertura riusano le stesse identità e gli stessi byte; se la
receipt di registrazione era stata salvata, non ripetono la registrazione.
Nessun apply economico parte prima del commit locale e i cinque ACK successivi
non riapplicano stock o storico. Le risposte di questi tre scenari restano
quelle del peer sintetico: qualificano il journal client e la serializzazione,
non PostgreSQL o un runtime distribuito.

`admin-db-7faecb54` conserva la successiva prova effettiva degli handler Admin
e del database PostgreSQL isolato sugli stessi 39 body di
`candidate-uncertain-5000`, oltre al retry identico dell'apply 036. I due casi
`RecordedAdminMultipartDatabaseResponses_ReenterExactThirtyNineRequestsAndDurableRetry`
sono passati nel runner C# ufficiale, compreso quello con la terza risposta
persa e riavvio dopo il backoff reale. Il client verifica indice, route e hash
di ogni richiesta/risposta, tutti i 5000 ID prodotto e 10000 ID prezzo, stock,
valori e storico; il padre si chiude soltanto dopo il quinto ACK locale.
L'archivio contiene anche il rifiuto SQL dell'originale tardivo
(`identity_retired`) con conteggi e valori invariati. Autenticazione esterna,
sessione/lease e dipendenze restano sintetici, come dichiarato nel manifest:
questa prova non attesta staging, TEST condiviso o autenticazione live.
