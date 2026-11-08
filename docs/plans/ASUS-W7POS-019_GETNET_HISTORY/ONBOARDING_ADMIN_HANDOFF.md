# ASUS-W7POS-019 — Handoff Admin / onboarding

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
| POS catalog scope live | BLOCKED_EXTERNAL | HTTP409 `unmapped` osservato; diagnosi resolver/provisioning e retest coordinato sopra | Mac |
| EMPTY_SHOP_HISTORY live | BLOCKED_EXTERNAL | Il409 non è catalogo vuoto; occorre scope autorizzato e risposta completa autorevole | Mac + Windows |
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
