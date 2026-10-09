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
