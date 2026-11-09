# ASUS-W7POS-019 — Handoff Admin / onboarding

## Raccordo corrente — legacy reale e conferma in qualifica, 9 novembre 2026

Admin148 autenticato PASS sul Worker a68ebec2/deployment1cd437f1: overview
staff-unavailable e reader Staff safe autorizzato osservati. Docs Admin b5c2dbc,
software REVIEW. Nessuna nuova SQL, autorizzazione o credenziale modificata.

Windows installato1823697: collegamento Start Menu e profilo conservato
NEW_SHOP_019 verificati nel processo reale, login e catalogo vuoto/workbench
PASS. Analisi UI dei sette originali e piano dei soli quattro legacy coincidono
con i controlli indipendenti. A/B applicabili, CLP esplicito; moderni bloccati da C.
Apply legacy COMMITTED con backup prodotto verificato; errore NullReference UI
postcommit conservato. Riconciliazione esatta PASS, ricevuta/report/dettaglio/CSV
consultati. Nessun retry o undo del lotto valido.

Fix Windows minimo in qualifica: HIST02 riproduce rientranza della conferma,
non prova lo stack originario live. Piano stabile, seconda attivazione negata e
review cambiata respinta; build x86/dialog standard PASS. Occorrono qualifica e
pacchetto exact-SHA prima dell'aggiornamento, poi reopen/dedup sul ledger conservato.
Display dichiarato200%/2880x1800,100/125 pendenti; Win7 già indisponibile.
CSV_INPUT BLOCKED_INPUT. Stessa task019 e nota PR29, overall ACTIVE.

## Storia — checkpoint9novembre precedente all'analisi e all'Apply

Percorso B eseguito senza attendere uno SHA originale irrecuperabile: export
immutabile del Worker servito399720d2 e contratti remoti verificati, reader
TASK140F recuperato, artefatto Linux con provenienza conservato. Primo rollout
PR147/cdebb26: POS login/catalogo vuoto/workbench PASS, console pos_admin FAIL
per guard manager-only. Prima failure preservata, rollback pinned399720d2
verificato dopo arresto del solo POS posseduto; nessuna SQL/chiave/grant cambiata.

Successore PR148 cc7b835/merge9016054 riusa il predicate dei ruoli web già
ammessi;45mirati PASS. CI42638651781 attempt2/Cloudflare42638650988 PASS,
primo prefix container health FAIL conservato. Postmerge CI42641653938 e
Cloudflare42641652917 PASS. Artifact13684508006,57checksum, tree918a24e
equivalente, lock/config/no-bundle verificati. Worker correntea68ebec2,
deployment1cd437f1,100% tag9016054, pubblico PASS; login autenticato successore
in corso. Niente TASK094 live, TASK150 secret put o dispatch generico.

Windows1823697 invariato. Avvio automatico negato dallo strumento; l'utente ha
eseguito il normale avvio sul profilo conservato e il PIN mascherato. Osservati
«catalog confirmed empty / history available» e Historical workspace, vendite
non pronte. Riapertura dal collegamento Start Menu esistente da verificare:
WIN7POS_DATA_DIR configurata solo per utente, nessun dato/trust spostato.

A Getnet VERIFIED con dichiarazione contestuale e riscontri d'identità; alias
conservati distinti. B VERIFIED «Mai usato prima». C moderno MISSING: colonna
FECHA DE VENTA con Z non prova la semantica provider. Analisi UI7XLSX in corso,
eventuale Apply legacy da qualificare separatamente; nessun Apply reale finora.
CSV_INPUT BLOCKED_INPUT. Win7 fisico indisponibile già dichiarato, kit invariato;
DPI100/125 e form import completo non certificati. Stessa nota PR29 e task019,
nessun nuovo backlog o consenso equivalente richiesto. [Matrice corrente](../../reports/ASUS_W7POS_019_RETRY_E2E_RESULT.md).

## Storia — residui reali dell'8 novembre

Autorizzazione già valida; su nuova richiesta esplicita il normale avvio
dell'installato Windows1823697 è riuscito, senza cambiare launcher o protezioni.
Prima autenticazione reale PASS e trust persistito; catalogo500/db_failure,
auditcatalog_v2_empty_manifest. Il processo posseduto è stato fermato dopo
raccolta evidence per interrompere richieste di background; profilo/trust integri.
Nessuna cassa, preview UI degli originali o import reale completati.

Admin PR144 integrata919d100, post-merge CI/Cloudflare PASS; il fallback vecchio
è osservato sulla console staff realmente autenticata, non ancora distribuito.
Nuovo difetto HTTP dimostrato e corretto con PR145 codice3e56a322/mergefdb30f4:
33mirati, CI42522680653/Cloudflare42522680527 PASS,1156foundation+14skip,
67file/4375pgTAP. Ammessa soltanto full fresca terminale shop_scoped zero;
publication fence, delta/legacy/parziali negati e SQL invariati.

Post-merge database fdb fallito due volte in prefix bootstrap prima dei test;
nessun esito SQL dedotto, causa precisa UNKNOWN. Diagnostica CI chiusa PR146
(non una nuova TASK146 Admin), codicef344918/mergef57afa6, CI42525515132/
Cloudflare42525515054 PASS,1157foundation+14skip. Nessun Worker runtime modificato
da questa sola diagnostica. Post-merge f57afa6: CI42526220441 database/Verify
e Cloudflare42526220439 PASS, live E2E e deploy SKIPPED. Nessun nuovo
grant/SQL/secret o deploy.

**Unica azione owner Admin per il rilascio:** riconciliare e pubblicare il
riferimento immutabile dell'albero equivalente al Worker399720d2 del15ottobre,
incluse correzioni TASK140F e configurazione build/staging. Va bene una sorgente
equivalente attestata se lo SHA originale è irrecuperabile. Su quella base
qualificare l'unità minima dei quattro percorsi prodotto PR144+145, oppure
documentare compatibilità della main completa. Target/progetto verificati e
rollback verso399720d2 pronti; nessuna nuova autorizzazione generica richiesta.
Il vecchio albero1f0679b2 non attesta la versione15ottobre. Le RPC U2 sono
presenti anche senza le due righe history isolate: nessun apply SQL dedotto.

Windows riprende HTTP→storico→preview dopo rollout compatibile, senza riusare
una continuazione password scaduta o il vault dell'altro negozio. Getnet A/B/C
restano MISSING nella scheda privata3gruppi/8campioni; l'utente delega il giudizio
senza fornire attestazioni. Policy POS nativa separata. Win7 dichiarato non
disponibile; kit pronto invariato, DPI100/125 e input nativo ancora da attestare.

La nota unica PR29 è riusata, riscontro di questa unità release PENDING;
head137afa5e OPEN/ACTIVE osservata, nessun lavoro mobile eseguito.
[Esiti e responsabilità correnti](../../reports/ASUS_W7POS_019_RETRY_E2E_RESULT.md).
I raccordi successivi sotto sono storia, inclusi review/consenso pendenti.

## Raccordo corrente — 8 novembre 2026

Windows `1823697f1f0c965fb8fd9d85259a862160f12c59` consegnato e installato
su Windows11,41/41 file identici e versione registro verificata. Retry ora
prepara lo stesso storico del login diretto su full autoritativa vuota e
mantiene la cassa bloccata; continuazione protetta e autorità controllate fino
al completamento. CI42497670447, Security42497670514, Release42497670533 e
WPF42497682626 PASS:1734 Core/Data,50 gate,15 righe canoniche,20 functional,
foundation5/37, smoke8/52, load11/11. Reader originali7/7, riepilogo sintetico
visivo effettivo e CSV50000 verificati. [Evidence corrente](../../reports/ASUS_W7POS_019_RETRY_E2E_RESULT.md).

L'autorizzazione al normale accesso è già concessa. Il nuovo avvio normale è
stato respinto dallo strumento con `blocked by policy`, senza processo o nuovi
pull: BLOCKED_POLICY, nessuna ulteriore domanda identica o launcher alternativo.
Installazione riuscita non significa autenticazione/HTTP/UI riusciti. Apply
reale BLOCKED_EVIDENCE: mapping esercente, inizio POS/overlap e prova provider
Z/offset assenti. Policy temporale POS separata per report misti. Win7 fisico
BLOCKED_EXTERNAL; pacchetto e scheda pronti, nessuna qualifica hardware dedotta.

Admin server PR143/1a35034a e migration applicata restano qualificati; nessuna
nuova SQL o modifica live. Il residuo console ha candidato minimo separato:
[PR144](https://github.com/XNIW/merchandise-control-admin-web/pull/144),
codicef8ac2fd6beda0e20d2fa4060c2dad45e78bdc001 e docs19e0b6f,
CI42493312883/Cloudflare42493312831 PASS. Stato REVIEW/ACK owner PENDING,
nessun merge/deploy. Distingue superficie staff indisponibile, deny, errore e
zero autorizzato; non introduce query staff, reader o grant. Owner Admin:
review/rollout e osservazione della console realmente pubblicata.

La [nota unica PR29](https://github.com/XNIW/iOSMerchandiseControl/pull/29#issuecomment-6722613449)
è riusata. RESUME_STATUS pubblicato riconcilia il precedente handoff server409
alle15:10Z; head137afa5e mantiene TASK146 ACTIVE/EXECUTION e PR29 OPEN.
Questo ACK precedente non vale per PR144 o per il nuovo pacchetto Windows.
ACK del nuovo raccordo PENDING. Nessun canale sessione Mac disponibile,
nessuna build/cleanup mobile; Getnet V1 locale separato da mobile_sales_read_v1.

Le sezioni seguenti conservano gli stati e i mandati precedenti. I passaggi
"autorizzazione pendente", "installation NOT_EXECUTED" e diagnosi409 da svolgere
sono superati esclusivamente dalle prove correnti sopra.


## Prosecuzione8novembre — fix resolver applicato da root, REVIEW

Il nuovo mandato ha autorizzato il fix del409: AdminPR #143 è integrata in1a35034a,
migration20261108051044 applicata sul progetto collegato al Worker staging reale.
Shop attivo, lease valida pos_admin, zero righe e zero storia mapping: causa
dimostrata, body prima/dopo pinned e tutto pg_proc eccetto prosrc invariato.
V3 reale ora ok/shop_scoped/done, summary/window0,rows0 e scope-key corretta.
Nessun grant/bridge/articolo/tabelle nuove o nuovo Worker. CI post-merge67/4375PASS.

HTTP/UI normale resta NOT_RUN_POLICY: avvio del prodotto d262ccd respinto con
blocked by policy, processo non nato e nuovi pull0 verificati. Autorizzazione
specifica pendente, nessun altro launcher o retry. Il vecchio409 sotto è storia.
Il canale Mac non è stato trovato; nessun invio/ritorno remoto viene inventato.
La superficie console staff resta distinta e pendente, non corretta da questa SQL.
[Risultato e ripresa](../../reports/ASUS_W7POS_019_CATALOG_409_FIX_RESULT.md).


## Ritorno Windows verificato — 2026-11-07

Client/pacchetto finale: `d262ccd979db8e3407118ffa7275861f12fdb5e2`
(feature959396b, closureQAe832d6a, fence conclusivo d262ccd). CI42432623358, Security42432623359,
Release42432623356 e WPF42432636966 tutti PASS. Canonica15/15,
Core/Data1734/1734 e gate50/50 (contatori esatti dal Release dello stesso SHA),
functional20/20, foundation5/37, smoke8/44 e load11/11. Prodotto/installer,
SBOM/provenance verificati come development-unsigned; installazione NOT_EXECUTED.
QA scaricato40file, smoke esterno8/44 e avvio ordinario su profilo vuoto PASS;
34 file comuni QA/prodotto identici. Reader net48/x86 con DLL QA byte-identiche al prodotto riconcilia7/7
originali invariati, NeedsEvidence=true/CanApply=false; nessun apply reale.

Il login reale precedente5c3327bd resta valido come evidence del proprio
tentativo: policy/trust/scope/mirrorpos_admin accettati, catalog pull409unmapped.
Il nuovo percorso storico passa solo su risposta completa zero qualificata;
non converte il409 in catalogo vuoto, non apre vendite e non modifica recovery.
Il correttivo d262ccd aggiunge inoltre un vincolo persistente dopo full empty:
nessun delta o approvazione generica può riabilitare il vecchio catalogo; solo
una nuova full verificata non vuota lo rimuove nel commit atomico. Regressione
Core/loopback rossa su e832d6a conservata, poi Core116/116 e loopback8/44 verdi.
Nessun nuovo tentativo live dopo il409. Identificativi per la correlazione sono
nel manifest privato Windows, non in questo file. Host staging coincide con
quello indicato dall'utente; deployment/installato Supabase resta da attestare.

Main Admin ricontrollato invariato a d021a4af. Nessun canale agente Mac trovato
anche alla ripresa: invio e risposta **PENDING**, nessun coordinamento remoto
dichiarato. Restano a Mac console staff/overview, provisioning/scope catalogo
vuoto e verifica principal/lease/RPC installati, seguendo i decision point sotto.
Il workflow Admin resta REVIEW dopo le proprie prove; DONE richiede il passaggio
previsto da AGENTS. Il risultato Windows non autorizza grant globali o mapping
catalogo/esercente arbitrari.

Correlation ID: `ASUS-W7POS-019-ONBOARDING-20261107`.
Scope pubblico: `NEW_SHOP_019`. Identità e riferimenti finanziari restano nel
manifest privato; questo handoff non contiene credenziali o materiale di sessione.

## Stato e ownership

| Campo | Stato verificato |
| --- | --- |
| Mandato | Prosecuzione della task019; negozio creato secondo dichiarazione utente, identità/ambiente da verificare |
| Owner Admin/server | Agente Mac, risposta **PENDING**; nessun canale Mac accessibile nell'inventario di questa sessione Windows |
| Owner POS | Agente Windows; bootstrap, catalogo vuoto, storico, harness e pacchetto |
| Coordinamento effettuato | Analisi read-only dei sorgenti Admin; handoff pronto da incollare. Nessun incarico trasmesso a un agente Mac e nessuna sua risposta ricevuta |
| Client alla ripresa | Win7POS main `e924a29549be4c3a144b66bf393ffcb40e751088`, codice/pacchetto qualificato `5c3327bd320293ad48edc7d9cc01ad2466e591b4`; il delta onboarding successivo richiede la propria qualifica |
| Server sorgente letto | Admin main `d021a4af405e64571ed0c9fea8e6e2debef158a6`, ricontrollato prima/dopo la lettura GitHub |
| Checkout Admin Windows | `main` pulito a `d283f76a4f9cd59e63e75c00ef831df0efee53c5`; preservato, nessun fetch/pull/edit/build |
| Deployment/host/progetto Supabase | Windows ha confrontato l'host effettivo POS col default staging del pacchetto; SHA del deployment e progetto Supabase **NOT_VERIFIED**, il main GitHub non dimostra la versione live |
| Operazioni live | Login, RPC, provisioning, migration e deploy **NOT_RUN** da questo sottoincarico |
| Workflow | Handoff verso **REVIEW** dopo le verifiche applicabili; **DONE** riservato alla conferma utente secondo AGENTS Admin |

## Causa statica confermata; riproduzione live pendente

I sintomi ricevuti sono approssimativi: “scope/path non autorizzato dal server”,
“righe mostrate 0”, “scritture OFF”, “contratto sola lettura”. Non sono un codice
HTTP/applicativo acquisito dal deployment.

Nel server SHA sopra, il percorso compatibile è:

1. `GET /shop` e `GET /shop/overview` chiamano
   `getShopSectionForRequest("overview", requestedShopId)`:
   [`/shop` L40](https://github.com/XNIW/merchandise-control-admin-web/blob/d021a4af405e64571ed0c9fea8e6e2debef158a6/src/app/shop/page.tsx#L40),
   [`/shop/overview` L27](https://github.com/XNIW/merchandise-control-admin-web/blob/d021a4af405e64571ed0c9fea8e6e2debef158a6/src/app/shop/overview/page.tsx#L27).
   Un principal courier su `/shop` viene prima indirizzato a `/shop/courier`.
2. Il ramo overview usa `getShopAdminReadModel({view:"overview"})`
   ([shop-section-data.ts L3624](https://github.com/XNIW/merchandise-control-admin-web/blob/d021a4af405e64571ed0c9fea8e6e2debef158a6/src/server/shop-admin/shop-section-data.ts#L3624)).
3. Il read model richiede `settings.view` (oppure `members.view` per members),
   poi rifiuta ogni principal diverso da `personal_account`, anche dopo accesso
   risolto: `status:"unauthorized"`, righe vuote, `readOnly:true`, motivo
   “Shop and member reads require a lease-bound staff read operation that is not
   available for this surface.”
   ([read-model.ts L235](https://github.com/XNIW/merchandise-control-admin-web/blob/d021a4af405e64571ed0c9fea8e6e2debef158a6/src/server/shop-admin/read-model.ts#L235),
   [decision point L257](https://github.com/XNIW/merchandise-control-admin-web/blob/d021a4af405e64571ed0c9fea8e6e2debef158a6/src/server/shop-admin/read-model.ts#L257)).
4. `fallbackSection` trasforma quel modello in `Unauthorized`, `Rows shown=0`,
   `Writes=Off` e `Read-only contract`
   ([shop-section-data.ts L159](https://github.com/XNIW/merchandise-control-admin-web/blob/d021a4af405e64571ed0c9fea8e6e2debef158a6/src/server/shop-admin/shop-section-data.ts#L159)).

Classificazione statica: **superficie staff incompleta dietro un confine di
sicurezza intenzionale**, con fallback UI ambiguo. Questo ramo non dimostra shop
disattivata, catalogo vuoto, dati economici zero, errore SQL/RLS o login POS negato.
Il principal reale, il permesso effettivo e l'esatta risposta live sono ancora da
acquisire; il semplice sintomo non permette di selezionare con certezza il ramo.

`resolveShopAdminDataAccess` distingue `personal_account` e `pos_staff_manager`,
rifiuta uno shop richiesto diverso da quello staff e verifica il permesso
richiesto. Non aggiungere membership personale per autenticare un principal staff.

## Contratto per la correzione Mac

Confermare sul deployment la versione, route e principal. Se overview è prevista
per quel ruolo, completare una lettura staff con shop derivato dal principal,
permesso dell'operazione, lease/versione/revoca rivalidate e DTO minimo. La
`callStaffWebCatalogRead` esistente incapsula la RPC
`shop_catalog_admin_read_v1`; le sue operazioni tipizzate sono solo `entity_page`,
`options`, `product_detail`, `products_by_codes`, `products_page`, `snapshot_page`
([staff-web-lease-bound-rpc.ts L21](https://github.com/XNIW/merchandise-control-admin-web/blob/d021a4af405e64571ed0c9fea8e6e2debef158a6/src/server/shop-admin/staff-web-lease-bound-rpc.ts#L21)).
Non presumere che una di esse implementi già la lettura del profilo/members.
Il client amministrativo resta confinato nel modulo; nessuna query generale o
service-role va esposta al percorso staff/browser.

Se la superficie è deliberatamente esclusa, mantenere il deny con stato utile:
lettura non disponibile per questo accesso, prossima azione consentita e dati
non disponibili. `Rows shown=0` non è un totale economico e `Writes Off` descrive
la pagina, non l'abilitazione dell'intero negozio. Distinguere capability mancante,
permesso negato, errore e risultato autorizzato realmente vuoto.

Verificare shop/staff attivi, credential status/version/expiry/lockout, ruolo e
permission tree attraverso il confine autorevole senza esportare hash. Eventuali
correzioni di provisioning devono essere ripetibili, auditate e limitate allo shop.
Schema/deployment richiedono causa, regressione, target e rollback prima del gate
di pubblicazione del repository; un commit non costituisce fix live.

## Contratto POS separato

`POST /api/pos/auth/first-login` valida l'envelope e chiama il servizio POS;
`GET` è rifiutato con 405. Non usa una sessione web o una chiamata diretta del
client a Supabase Auth. In questo sottoincarico nessuna risposta HTTP live è
stata acquisita.

Al server SHA indicato, `first-login-service.ts` usa `createPosRuntimeRpcClient()`.
`first-login-core.ts` verifica la credenziale, effettua il commit della sessione
e dell'autorizzazione offline, poi richiede la pubblicazione della lease prima
di restituire `success`, policy, shop, staff/roleKey, sessione e device. Un rifiuto
di pubblicazione revoca la sola sessione appena creata nel suo flusso normale.
Le RPC attraversate includono `pos_runtime_first_login_lookup_v1`,
`pos_runtime_first_login_failure_v1` e `pos_runtime_first_login_commit_v3`
([runtime-boundary.ts L424](https://github.com/XNIW/merchandise-control-admin-web/blob/d021a4af405e64571ed0c9fea8e6e2debef158a6/src/server/pos-auth/runtime-boundary.ts#L424)).
Il success body arriva solo dopo il controllo di pubblicazione
([first-login-core.ts L504](https://github.com/XNIW/merchandise-control-admin-web/blob/d021a4af405e64571ed0c9fea8e6e2debef158a6/src/server/pos-auth/first-login-core.ts#L504)).

Verificare separatamente sullo stesso host la policy e
`POST /api/pos/session/heartbeat`. Windows confronterà l'endpoint effettivo
(env, configurazione locale, default pacchetto), binding ufficiale, mirror e
`roleKey=pos_admin`. L'assenza di `import_history` nell'albero web non prova un
difetto dei permessi locali POS.

Un catalogo autorevolmente vuoto non deve abilitare vendita o pagamento. Windows
qualifica un percorso storico circoscritto, distinto da recovery, subordinato
a identità/lease/scope/permessi e alle guardie merchant/tempo/overlap.

## Aggiornamento live Windows — catalogo HTTP 409 `unmapped`

Evidence comunicata dall'owner Windows durante la prosecuzione del 7 novembre
2026, su normale login con prodotto qualificato `5c3327bd`, profilo nuovo posseduto
e password inserita dall'utente nel campo mascherato:

| Osservazione | Esito / limite |
| --- | --- |
| Primo accesso POS | Successo e trust persistito osservati; UI mostra identità shop/device. Non equivale a qualifica heartbeat, merchant mapping o catalogo |
| Host POS | Corrisponde al default staging del pacchetto secondo verifica Windows; versione server installata ancora da attestare |
| Catalogo | `POST /api/pos/catalog/pull`, **HTTP 409**, diagnostica client `stage=server_response`, `code=unmapped`, `catalogStatus=unmapped`, `saleSafe=no`, `requiresRetry=yes` |
| UI | Accesso riuscito e catalogo non scaricato; vendite non aperte |
| Stato vuoto | **NON PROVATO**: rifiuto di scope, senza risposta catalogo completa né totale autorevole zero |
| Retest | Nessun nuovo tentativo live richiesto da questa diagnosi; attesa della risposta Mac sul binding/contratto |

I riferimenti al profilo, evento e screenshot restano nell'evidence privata
Windows. Nessun identificativo reale viene copiato in questo handoff.

### Traccia statica del `409`

Nel main Admin `d021a4af` ricontrollato dopo questa lettura:

1. La route delega a `handlePosCatalogPull`; quest'ultimo risolve la lease,
   verifica sessione/device/credenziale, poi chiama `loadCatalogPageV2`.
2. `loadCatalogPageV2` chiama `pos_catalog_pull_page_for_lease_v3`. Se la RPC
   restituisce uno stato riconosciuto diverso da `ok`, lo propaga; non converte
   genericamente gli errori RPC in `unmapped`
   ([catalog-revision.ts L375](https://github.com/XNIW/merchandise-control-admin-web/blob/d021a4af405e64571ed0c9fea8e6e2debef158a6/src/server/pos-auth/catalog-revision.ts#L375)).
3. Il ramo `page.status === "unmapped"` diventa esplicitamente
   `code:"unmapped"`, `root:"unmapped"`, HTTP409
   ([catalog-pull.ts L1043](https://github.com/XNIW/merchandise-control-admin-web/blob/d021a4af405e64571ed0c9fea8e6e2debef158a6/src/server/pos-auth/catalog-pull.ts#L1043)).
   Un errore di audit produrrebbe invece `db_failure`/HTTP500. Il messaggio
   di questo ramo parla di legacy bridge non mappato: può essere fuorviante per
   uno shop appena creato che non deve avere un legacy bridge.
4. La definizione sorgente della RPC v3 rivalida la lease e delega a
   `pos_catalog_pull_page_v2`
   ([migration completeness L12334](https://github.com/XNIW/merchandise-control-admin-web/blob/d021a4af405e64571ed0c9fea8e6e2debef158a6/supabase/migrations/20260722013109_cross_platform_sync_event_completeness.sql#L12334)).
5. La pagina v2 restituisce `status:"unmapped"` se il resolver risponde
   `blocked`
   ([migration completeness L11365](https://github.com/XNIW/merchandise-control-admin-web/blob/d021a4af405e64571ed0c9fea8e6e2debef158a6/supabase/migrations/20260722013109_cross_platform_sync_event_completeness.sql#L11365)).
6. La definizione del resolver nella migration scope lease contiene:
   `blocked := v_has_blocking_mapping or (v_mapped_owner_id is null and not v_has_shop_rows)`
   ([scope lease L108](https://github.com/XNIW/merchandise-control-admin-web/blob/d021a4af405e64571ed0c9fea8e6e2debef158a6/supabase/migrations/20260722022500_task_139_pos_catalog_scope_lease.sql#L108)).
   `v_has_shop_rows` considera l'esistenza di prodotti, categorie, fornitori o
   prezzi shop-scoped. Il mapping valido richiede stato `mapped`, owner non
   nullo, `verified_at` non nullo e finito, `created_at` finito e
   `disabled_at` nullo. Una riga attiva con campi mancanti/stato diverso o date
   non finite costituisce mapping bloccante.

Implicazione statica: **assenza di righe shop-scoped e assenza di mapping
verificato bastano a produrre `unmapped`**, anche quando lo shop potrebbe essere
realmente vuoto. È una pista concreta sul contratto di provisioning/scope vuoto;
non prova quali righe siano presenti su `NEW_SHOP_019` o quali definizioni siano
installate sul progetto live. Non creare prodotti fittizi o mapping arbitrari
per superare il guard. Un catalogo senza sorgente autorizzata resta diverso da
uno scope autorizzato con totale zero.

Nel client `5c3327bd`, `PosAdminWebClient.PostJsonAsync` deserializza la risposta
di errore e conserva `error.Code` normalizzato; usa `http_409` soltanto se manca
un codice applicativo. Pertanto `unmapped` **arriva dal server**, non è una
classificazione introdotta dal client. `PosCatalogPullService` lo propaga nel
failure outcome; `CatalogIncomplete` lo conserva come catalog status e mantiene
distinti successo del login e catalogo non pronto. `server_response` è lo stage
locale assegnato alla risposta HTTP, non lo stage interno SQL/server. Il DTO
`PosErrorResponse` di quel candidato non acquisisce `root`/`stage` del server.

`requiresRetry=yes` indica che la policy offre un retry manuale dopo un problema
non classificato come deterministico di revisione. Non dimostra transient fault
né autorizza un retest. La policy interna di retry catalogo non include `unmapped`
tra timeout/network/io/db_failure.

### Diagnostica circoscritta richiesta al Mac

Prima di ogni nuovo login/pull sullo stesso account, restituire:

- Host/Worker/deployment SHA e progetto Supabase effettivi; contratto installato
  delle RPC v3/v2, resolver e publication fence, incluse fingerprint e grant
  pertinenti. Confrontare le definizioni installate con le migration sorgenti,
  senza presumere che la cronologia main sia stata applicata.
- Correlazione privata del solo evento osservato: intervallo UTC, request ID
  server, client request ID e CF-Ray se presenti, incident ID locale e alias
  scope. Questi identificativi tecnici si scambiano nel canale privato; nessun
  body di richiesta, token, cookie o password. Windows fornisce i riferimenti
  disponibili dal proprio run, senza nuovo tentativo.
- Evento audit `pos.catalog.pull.failure` della sola Shop verificata e del solo
  intervallo/correlation ID: `code`, risultato, `reason`, `stage`,
  `manifest_requested`, lane e metadati di correlazione redatti. Il server
  aggiunge `request_id`, `client_request_id`, `edge_correlation_hash` e route
  tramite `requestMetadata`. Per `unmapped`, lo stage atteso della prima pagina
  è `manifest`; va acquisito, non inferito dal `server_response` client.
- Diagnostica read-only sul solo shop parametrizzato, tramite strumenti
  autorizzati: esistenza delle quattro classi di righe shop-scoped, presenza di
  mapping attivi, mapping valido secondo i predicati sopra, eventuale mapping
  bloccante e ramo scelto dal resolver. Restituire booleani/count limitati;
  niente righe di altri shop né owner/credential hash. I risultati amministrativi
  servono alla diagnosi e non certificano il percorso staff/POS autenticato.
- Verifica del contratto di provisioning per uno shop senza inventario legacy:
  come viene autorizzato esplicitamente lo scope shop-scoped prima della prima
  riga? Se manca quel contratto, preparare il delta minimo e regressioni
  sintetiche per empty autorizzato, mapping incompleto, cross-shop e lease
  scaduta/revocata; nessun ampliamento implicito del resolver a ogni shop.

Solo dopo binding/contratto verificato, coordinare un singolo retest POS normale.
L'eventuale risposta vuota deve attraversare il percorso autenticato completo,
con summary/generation/shop coerenti e totale zero autorevole; solo allora si
può qualificare il percorso storico del nuovo negozio vuoto. Nessun mapping
inventario stabilisce da solo il mapping esercente Getnet o la semantica temporale.

## Ripresa richiesta al Mac e gate

Prima di modificare Admin leggere AGENTS, MASTER-PLAN e il task effettivo nel suo
checkout. Lo snapshot remoto contiene sezioni datate di prosecuzione TASK-094
(task source in REVIEW) e un indice strutturato storico che riporta IDLE/NESSUNO;
non interpretarlo come autorizzazione a sovrascrivere lavoro o ad attivare un nuovo
task. Riconciliare il contesto col coordinatore e collegare questa task019.

| Gate | Stato attuale | Evidence / sblocco | Owner |
| --- | --- | --- | --- |
| Main Admin sorgente | PASS (sola lettura) | `gh api .../commits/main`; SHA invariato prima/dopo; snapshot privato dei file pertinenti | Windows analisi |
| Decision point compatibile | PASS (sola analisi statica) | Simboli e righe sopra; nessun test runtime inferito | Windows analisi |
| CONSOLE_LOGIN | BLOCKED_EXTERNAL | Login riferito dall'utente; acquisire percorso reale con input mascherato | Mac / utente |
| CONSOLE_READ_SCOPE | BLOCKED_EXTERNAL | Verificare host/deployment/principal e causa effettiva, poi patch minima/regressione | Mac |
| POS_FIRST_LOGIN | PASS sul prodotto `5c3327bd`, evidence Windows | Accesso normale riuscito e trust persistito; non chiude policy/lease corrente o catalogo | Windows |
| Server POS policy/lease live | BLOCKED_EXTERNAL per qualifica completa | Primo accesso riuscito; verificare contratto installato, binding/heartbeat ed expiry sul proprio scope | Mac + Windows |
| POS catalog scope live | PASS RPC / HTTP-UI NOT_RUN_POLICY | Causa corretta con Admin1a35034a e migration applicata; v3 reale ora manifest zero. Un solo normale avvio pendente dopo rifiuto policy | Windows root |
| EMPTY_SHOP_HISTORY live | PASS server / UI NOT_RUN_POLICY | Manifest completo zero fornito dal wrapper v3 dopo fix; il409 originale resta failure storica. Percorso UI ancora da osservare | Windows root |
| Regressioni Admin | NOT_RUN | Nessun delta Admin prodotto da questa analisi; mirate foundation/UI e pgTAP se SQL cambia | Mac |
| Deploy/apply | NOT_RUN | Solo dopo patch reviewable e gate previsti; annotare SHA live e rollback | Unico owner Mac |
| Risposta Mac | PENDING | Handoff pronto da incollare; nessuna consegna/ricezione Mac dichiarata | Coordinatore |

Nel ritorno includere correlation ID, SHA client/server, deployment/host e progetto
verificati, alias scope, route/metodo, HTTP e codice applicativo effettivi,
principal/capability, decision point, atteso/osservato, patch/test/evidence,
cleanup/risorse conservate, owner e prossimo passo. Password, cookie, token,
hash credenziali e profili trust non fanno parte del ritorno.

Serializzare prove live sul medesimo account e cambi di ruolo/sessione/deployment;
nessun test di lockout/revoca sull'account reale. Usare fixture sintetiche per
negativi, cross-shop, scadenza/revoca e rimozione del contenuto precedente.
Il successo con service-role non qualifica l'autorizzazione del principal reale.

Snapshot sorgente preservato nella root privata della task019, sottocartella
`admin-onboarding-source-20261107`; nessun dato operativo, processo o risorsa
remota creati. Nessun cleanup di account, shop o sessioni è stato eseguito.
