# ASUS-W7POS-019 — onboarding execution result

Prosecuzione del 2026-11-07, correlation ID
`ASUS-W7POS-019-ONBOARDING-20261107`. Scope pubblico: `NEW_SHOP_019`.
Stato: `IN_PROGRESS`; qualifica reale e Win7 ancora `BLOCKED_EXTERNAL`.

## Baseline e ambiente

- POS locale/remoto `main`: `e924a29549be4c3a144b66bf393ffcb40e751088`,
  checkout iniziale pulito; codice/pacchetto precedente `5c3327bd320293ad48edc7d9cc01ad2466e591b4`.
- Admin main remoto: `d021a4af405e64571ed0c9fea8e6e2debef158a6`.
  Il checkout Admin Windows preesistente è pulito a `d283f76a4f9cd59e63e75c00ef831df0efee53c5`
  e rimane preservato. Nessun pull, reset, stash o nuova branch.
- Host console fornito dall'utente: staging Workers, coincidente con il default
  del pacchetto POS. URL completo e scope atteso sono nel manifest privato.
  Deployment e dati autorevoli dello shop non sono ancora verificati.
- Negozio creato e login console riuscito **secondo dichiarazione utente**.
  Questo non certifica identità, ruolo, mapping esercente o inizio uso POS.

## Diagnosi e modifiche

La review statica Admin conferma un percorso compatibile con il sintomo:
`getShopAdminReadModel` richiede il permesso di sezione, quindi esclude il
principal staff dalla superficie senza lettura lease-bound. Il fallback mostra
zero righe e scritture disattivate. La riproduzione console sul deployment resta
`NOT_RUN`: nessuna conclusione sulla disattivazione dello shop è giustificata.
Vedere l'[handoff Admin](../plans/ASUS-W7POS-019_GETNET_HISTORY/ONBOARDING_ADMIN_HANDOFF.md).

Nel POS il preflight full refresh rifiuta zero prodotti attivi. L'accesso
normale richiede ancora catalogo sale-safe: lo storico non è raggiungibile da
quel flusso. La correzione qualifica separatamente un catalogo
autorevolmente vuoto e offre soltanto import/report con identità e lease correnti.

Il normale primo accesso reale è stato eseguito dall'utente nella UI mascherata
del prodotto `5c3327bd`. Accesso accettato, trust persistito, policy accettata
dal bootstrap, generation attiva e snapshot shop ufficiale coincidenti con lo
scope atteso. Mirror attivo `pos_admin`, ruolo locale admin. Le righe di permesso
storico non sono grant aggiuntivi richiesti all'admin: l'uso richiede comunque
lease e autorità operatore valide. La UI non ha completato l'accesso operatore
alla cassa, perché `POST /api/pos/catalog/pull` restituisce **HTTP 409**, codice
**unmapped**, stage **server_response**, `catalogSaleSafe=no`, `requiresRetry=yes`.
Questo non è un catalogo vuoto autorevole. Vendite, outbox commerciale, prodotti
e transazioni storiche nel profilo nuovo risultano zero con lettura SQLite
`mode=ro`, senza estrazione di credenziali o trust.

La review server individua il ramo `status=unmapped` e il resolver scope che
blocca l'assenza simultanea di righe shop-scoped e mapping verificato. È una
pista concreta da verificare sul deployment, distinta dal mapping esercente
Getnet. Il Mac deve qualificare provisioning/scope vuoto senza articoli fittizi.

Prima prova prodotto: l'eseguibile WPF companion del bundle QA precedente
fallisce all'avvio per `System.ValueTuple` mancante. Nessun login tentato,
profilo nuovo, log privato conservato e processo chiuso. Il payload prodotto
contiene la dipendenza e raggiunge il dialogo di primo accesso nello stesso
profilo ancora privo di trust. Il delta bundle copia il forwarder pinned e
richiede runtime/config/fixture completi con regressioni missing DLL/redirect.

Prima regressione mirata `focused-01`: restore exit0, test exit1,
109/110 PASS. Un nuovo test manteneva aperta una connessione del primo DB mentre
inizializzava il secondo; il drain globale raggiungeva il timeout di 30 secondi.
Corretto il lifetime della sola connessione di verifica, conservando assert e
budget; failure originaria mantenuta. `focused-02`: 110/110 PASS, zero skipped,
exit0. WPF/harness Release x86 e supervisore QA statico/runtime PASS in
`focused-03`, dopo correzione del cast all'implementazione `OperatorSession`.

| Prova intermedia conservata | Esito | Errore primario / correzione |
| --- | --- | --- |
| focused-02 build WPF | FAIL | Tipo interfaccia passato alla capability concreta; cast del riferimento catturato, nessuna autorità concessa |
| focused-03 smoke primo preflight | FAIL | Directory output non precreata nell'invocazione manuale; nessun worker avviato; nuova root per invocazione corretta |
| focused-03 smoke-02 | FAIL 7/8,39 assert completati | Fixture bootstrap in DB custom, runtime immagini sul profilo default privo di authority; fixture allineata al profilo QA nuovo posseduto |
| focused-04 smoke | FAIL 7/8,39 assert completati | First-login loopback riuscito, catalogo network_error; fixture ora usa temporaneamente il proprio loopback e ripristina l'endpoint del runner |
| focused-05 smoke | FAIL 7/8,39 assert completati | Contatori nullable del contesto non valorizzati: conferma rigorosa nega; hook corretto con conteggi ricevuti, nessun controllo rilassato |
| focused-06 smoke | FAIL 7/8,39 assert completati | Bootstrap empty completo PASS, owner test non ancora mostrato; corretta sequenza della fixture |
| focused-07 smoke | FAIL 7/8,43/44 assert | Focus iniziale workbench mancante; aggiunto focus in ContentRendered, nessun positioning/sizing |
| focused-08 smoke | PASS 8/8,44/44 | Bootstrap reale loopback con una sola richiesta catalogo, proof/lease/permessi/generation incrociate, tastiera/expiry/revoca e regressioni storiche; fingerprint stabile |
| focused-09 smoke | FAIL 7/8,39 assert completati | Nuovo stato empty_verified: writer errori trasformava stringa vuota in failure; successo ora usa il writer generation-guarded per svuotare il campo |
| focused-10 smoke | FAIL 7/8,43/44 assert | Stato veritiero PASS; owner sintetico fuori workarea per cascata OS, overlay espanso correttamente dalla base; fixture centrata, limite1024x768 invariato |
| focused-11 smoke | PASS 8/8,44/44 | Candidato finale include stato UI empty_verified e negativo proof invalidata; fingerprint stabile, cleanup confermato |

Le failure restano tali nelle root originali. Nessun budget o assert aumentato;
cleanup dei due worker smoke confermato, zero processi residui. La qualifica
canonica del candidato finale resta pendente. Dialog standard `39/39` PASS.
Run smoke PASS: `Win7POS.QA.20261107T215015047Z.c07ab0895d2b46c8b2d9fc526eedbd27`.
Run smoke finale prima del commit:
`Win7POS.QA.20261107T215751363Z.30f02218ac83471dacaf0db44182a328`.
La qualifica Windows canonica All è affidata alla CI sul commit candidato
identificato dopo questi focused; non viene duplicata una suite completa locale
sullo stesso codice. Gli esiti CI e pacchetto restano separati dai focused.

Required gates locali: primo run **49/50 FAIL**, solo gate lease. Il nuovo
ramo storico era annidato nel ramo generico catalogo non sicuro, contro il
divieto statico di autenticazione in quel ramo. Spostato il caso esplicito
`CanPrepareEmptyShopHistory` prima del ramo generico, con tutti i guard e il
finally di rollback invariati. Il gate non è stato modificato: retest lease
**PASS**, build WPF/harness **PASS**, dialog standard **PASS**. La CI riesegue
tutti i50 sul candidato esatto. Aggiunta gestione degli errori recuperabili dei
due handler storico: messaggio localizzato e log del solo tipo di eccezione,
senza payload o credenziali; il primo accesso ripulisce l'autorità preparata.

Il binding della capability include la stessa fingerprint della generation
del lease operatore; una proof DB valida per un'altra generation non basta.
Il reader mostra catalogo vuoto verificato solo quando stato, trust generation e
proof persistita coincidono; un'etichetta stale non sostituisce la prova. Non
presenta questo successo come errore/riprova/riparazione e non promette permessi
storico al ruolo. `CatalogSaleSafe=false` e l'attenzione alle vendite non pronte
restano attivi.
Se la preparazione è cancellata o fallisce, il mirror login appena acquisito
viene disconnesso anche prima della pubblicazione della capability. Il workbench
riusa le guardie import/report; non crea PosViewModel, vendite, pagamenti o
stampe. Il catalogo precedente resta intatto e perde solo il marcatore sale-safe.

## Matrice dei gate

| Gate | Stato corrente | Evidence / prossimo passo |
| --- | --- | --- |
| CONSOLE_LOGIN | BLOCKED_EXTERNAL | Dichiarazione ricevuta; retest con principal effettivo e deployment dal Mac |
| CONSOLE_READ_SCOPE | BLOCKED_EXTERNAL | Percorso statico compatibile; risposta Mac PENDING |
| POS_FIRST_LOGIN | PASS `5c3327bd` | Login ordinario reale accettato; catalogo separatamente bloccato409 |
| POS_LEASE_BINDING | PASS bootstrap `5c3327bd` | Trust/policy persistiti, scope ufficiale/generation/mirror verificati; uso storico live NOT_RUN |
| EMPTY_SHOP_HISTORY | BLOCKED_EXTERNAL live | Catalog scope unmapped409; patch client sintetica da qualificare, zero autorevole non ricevuto |
| GETNET_PREVIEW | PASS precedente | Reader net48/x86 `5c3327bd`: 7/7 originali; NeedsEvidence=true, CanApply=false. Nuovo candidato da riconciliare |
| REAL_IMPORT | BLOCKED_EXTERNAL | Utente conferma prove non ancora disponibili: mapping, inizio POS/overlap, semantica provider; nessun apply reale |
| REPORT_EXPORT | NOT_RUN candidato | Qualifica sintetica precedente valida solo per `5c3327bd` |
| PACKAGE | NOT_RUN candidato | Prodotto precedente verificato; companion QA startup FAIL conservato |
| WIN7_PHYSICAL | BLOCKED_EXTERNAL | Target fisico SP1 e periferiche non disponibili |

## Evidence, risorse e ripresa

Root private nuova `onboarding-20261107T212334Z.7cb660d9f0ee487eb524ef62ed9b5767`
sotto `C:\Dev\_codex-private\ASUS-W7POS-019`. `run.private.json` contiene
scope atteso, profilo nuovo, PID/creation time/exe e ownership Windows.
Nessun secret è scritto nel manifest; nessun dato reale è copiato nel repo.
Un errore di lettura durante la diagnosi ha mostrato dettagli dell'oracolo
privato nell'output strumenti; l'incidente è stato comunicato all'utente e
registrato privatamente. Le successive letture sono filtrate. Controllo dei
28 file modificati/nuovi contro riferimenti shop e hash originali: zero hit;
questo controllo del repository non annulla l'esposizione precedente in chat.
DB/profilo/log sono conservati per diagnosi e futuro retest; nessuna cancellazione.
Il processo UI posseduto è terminato dopo il risultato409, verificando PID,
creation time ed exe, per fermare tentativi di background. Nessuna revoca della
sessione server. Il profilo non è un orfano: owner Windows, riuso solo nello scope
identificato e dopo correzione server/conferma contratto. L'errore iniziale della
query sul nome della chiave shop è conservato separatamente; lettura corretta
contro le costanti `OutboxShopBinding` conferma lo scope, senza cambi al DB.
Gli originali, le vecchie failure, i pacchetti e i relativi manifest sono preservati.

Nessun comando Staging articoli, vendita fiscale, revoca account, grant,
migrazione o deployment eseguito. Un solo writer build/test per checkout;
nessuna suite con fingerprint avviata durante lo sviluppo concorrente.

Owner: Windows per POS/QA/pacchetto; Mac per Admin/provisioning/deployment.
Canale Mac non disponibile nell'inventario dell'app: handoff pronto,
invio e risposta `PENDING`, nessun coordinamento remoto dichiarato.
