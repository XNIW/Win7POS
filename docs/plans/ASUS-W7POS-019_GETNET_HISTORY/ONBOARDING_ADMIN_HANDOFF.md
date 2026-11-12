# ASUS-W7POS-019 — Handoff Admin / onboarding

<!-- ASUS019_COMPOSED_CURRENT_START -->
## Stato corrente — 20261112T205249Z, stessa ASUS-W7POS-019 ACTIVE

| Ambito | Prova e risultato corrente |
| --- | --- |
| Requisito ordinario | Autorizzazione POS e PIN Minimondo senza scadenza, come richiesto esplicitamente. Il modello negoziato `persistent-pos-v1` salva autorità DPAPI v6 con testimone HKCU persistente indipendente dal backup. PIN, binding, generazione, versione, tentativi e revoche note restano verificati. Nessuna licenza futura introdotta; vecchie ricevute v1–v5 restano bounded. |
| Installato reale | 248e689, EXE SHAdec0d531 e profilo Minimondo corretti. Primo login online PASS: grant v6 persistent-pos-v1 e testimone HKCU presenti, nessuna scadenza ordinaria; PIN server senza scadenza/versione1. Carrello vuoto e barcode disponibili. Menu→riepilogo→Escape→carrello PASS, input non inviato conservato/selezionato e poi rimosso. Chiusura ordinaria e secondo processo8576 dalle20:26Z PASS con nuovo normale login online e grant v6 permanente. Nessuna credenziale automatizzata; riapertura offline reale distinta e NOT_RUN. |
| Windows248 qualificato | PR138, 248e6891dcab5ce086c36b0fbec1c66ffb487344. All43248176976 PASS16/16, artifact13960748887/SHA2f2c9486 verificato8entry; Security43248176453 PASS; Release43248317257 verificata41file/110checksum, setup15f1e9f2/payloadccfc0b7f. QA43248326349 verificata40file,34comuni esatti. MainWindow dal bundle esatto PASS29205.7167ms, cleanup naturale. Core46/46, smoke WPF bounded/permanente e processi CLR distinti, statico/dialog PASS. |
| ASUS sul payload esatto | Integrated12 PASS740.778s e Final60 PASS3606.695s: tutti tre flag misura/ambiente/stabilità validi, exit0 e cleanup naturale. UI p95 Rows115.415ms/Grid117.887ms, massimi255.563/165.290ms; limiti preregistrati p95≤250ms e massimo≤1000ms invariati. Crescita managed massima1.545MB sotto8MiB. Budget SHA63970e30,41file prodotto e34comuni QA esatti. Non è una qualifica Win7 fisico o periferiche. |
| Merge Windows | PR138 unita normalmente in92dff410e241e5ee7c2a42a82ef4615b624537d5; albero8ae8b6bd identico248. Postmerge Security43272055077 PASS; CI43272054951 FAIL15PASS/1FAIL nello HIST_LOAD_50K. Artifact13970438026/SHA0e3672dc e8entry verificati: measurementCompleted=false per2campioni dispatcher e1memoria persi; environmentValid/thresholdsPassed=true, tutti gli altri assertLoadPASS. Nessun rerun automatico né soglia ridotta. Release43272054924 PASS, download canonico41file/110checksum (main setup6ec6394f, non installato); cassa selezionata248 con le proprie qualifichePASS, distinto dal residuo della misura storica. |
| Server qualificato | b091325, PR152 unita normalmente in5ff8577; albero b5058dd2 identico al candidato e al virtual merge956176f. Verify/CF propri43251761879/881 e main43258018081/735 PASS. Artifact58file/lock/config e replay97file/5356assert/3concorrenze verificati anche dopo merge. FAIL9ec e332 conservati: metadata replay/lint, poi regressione della rilettura clock dopo lock; b091 ripristina integralmente il corpo canonico prima dei soli due controlli di invalidazione persistente. |
| Storico autenticato nuovo composto | Run43254453360, serverb091/client248, PASS12/12 eSQL97/5356/3; artifact13961554676/SHA5849729d verificato. Admin→entrambiPOS senza eco, ACK perso+nuovo CLR/pull prima ACK, duplicato/Undo/correzione, revoca isolata, HTTP409/history_cloud_writer_closed con zero nuovi grant/ricevute, Recovery/baseline e riconciliazioni senza nuovi movimenti; cleanup naturale. Principal bounded posseduti: prova Core/Data+HTTP+SQL+Storage+Worker+browser distinta dall’enrollment permanente WPF reale. Run23 server612/clientaa4 e precedenti qualifiche restano storia attribuita. |
| SQL effettivamente applicato | Solo nuova migrazione20261112143448_asus019_persistent_pos_authorization_v1.sql, SHA29bd59ef; versione cloud20261112174421. Tre corpi funzione esatti, V3 precedente invariata, nuovi RPC riservati a service_role e helper privato non eseguibile direttamente. Nessun dbpush/repair globale o modifica alle29migrazioni storiche. PIN: soltanto l’operatore Minimondo ha scadenzaNULL, versione1 e PIN invariati. Conteggi cloud movement_versions/job/upload/migrationrun0 prima/dopo. |
| HTTP effettivamente pubblicato | Version ecfd6920-bef3-441e-99f6-2a1b83aaf498, deployment8a732699-a226-40cd-a43c-4868f258826c al100%. Artifact congelato13960220868/SHAcd1e6ebe, sorgente956/treeb5058dd2, Wrangler4.123.0 con no-bundle/keep-vars, nessun rebuild. Tutti24binding e runtime conservati; metadati non-assets esatti, chiave gateway mantenuta senza lettura/rigenerazione. GET protetto e form staff HTTP200. Dopo deploy77richieste firmate riuscite, tutte tre lane idle e0risposte con lavoro. |
| Worker operativo osservato | Host7c95955/runtime612 CJS20eb2acd invariati; pacchetto/qualifica5/5, Node24.18.0, account virtuale/DPAPI/ACL, limiti1GiB/un processo e heap640MiB. Stato corrente running PID32268, ma disponibilità continua FAIL: 11 arresti distinti SCM7031 dalle18:35Z alle20:15Z, contatore cumulativo34 include23 FAIL iniziali. Nessun reinstall/rekey/restart manuale. |
| Worker — recupero successivo | Recuperi SCM dopo300s conservati. PID32268 dalle20:20:43Z;1119 richieste firmate riuscite nelle tre lane fino20:41:06Z. DB ancora avviato15:24:07Z dopo unico restart precedente. Causa degli arresti non dimostrata; stato running distinto da servizio stabile. |
| Consegna cassa installata | Installer248 canonico15f1e9f2 exit0,41file esatti/17file profilo preservati al momento installazione; backup3DB integrityok/FK0 e conteggi115tabelle conservati. Prove reali successive: primo/secondo Start online PASS, navigazione e focus PASS. Conteggi49tabelle main invariati rispetto al controllo prima della navigazione. Worker/chiavi invariati; copia Win7-transfer248 SHAeeb50b3f separata, kit1823697 conservato. |

Checkpoint unico privato aggiornato20261112T205249Z. Installato248: enrollment permanente, carrello/barcode, riepilogo→ritorno, chiusura e seconda apertura online PASS. Il nuovo login online genera una nuova ricevuta/sessione: non prova il riuso offline della precedente. Riapertura offline reale e reboot Windows NOT_RUN; prove sintetiche in processi distinti PASS, nessun reboot fisico dichiarato. Revoca server sconosciuta offline non rilevabile immediatamente.

Load diagnostico sul medesimo payload248, primo tentativo su ASUS dopo chiusura POS reale: PASS11/11, tutti tre flag validi,311/311 dispatcher e156/156 memoria, zero campioni persi e nessun cambio a intervalli, priorità, carico o soglie. Profilo17file hash-identico prima/dopo. Questo esito non sostituisce il FAIL postmerge43272054951: causa del gap hosted ancora non dimostrata, nessun rerun CI automatico.

Storico reale: il normale background ha preparato un archivio/backup con hash verificati e creato un upload pending; flow locale upload_created, oggetto Storage assente, nessun job/run/movimento cloud. Sincronizzazione reale incompleta; osservazione HTTP passiva330s conservata separatamente, nessuna richiesta o importazione di prova generata. Non qualificare il solo stato online come storico consegnato.

Win7 fisico BLOCKED_TARGET; periferiche NOT_RUN; Getnet C MISSING e tre moderni non applicati. DPI200%/2880×1800 conservati,100/125 USER_DEFERRED. Nessun Apply/Undo/reimport reale, articolo, vendita di prova o cancellazione di profilo/code. FAIL intermedi e ricevute storiche conservati, nessuna soglia/assert/pin indebolita. Nessun DONE globale.

Advisors dopo DDL conservati come inventario: [RLS senza policy](https://supabase.com/docs/guides/database/database-linter?lint=0008_rls_enabled_no_policy), [search_path](https://supabase.com/docs/guides/database/database-linter?lint=0011_function_search_path_mutable), [RPC anonime](https://supabase.com/docs/guides/database/database-linter?lint=0028_anon_security_definer_function_executable), [RPC autenticate](https://supabase.com/docs/guides/database/database-linter?lint=0029_authenticated_security_definer_function_executable) e [protezione password](https://supabase.com/docs/guides/auth/password-security#password-strength-and-leaked-password-protection). Nessun finding di execute pubblico per la nuova V4; permessi live verificati separatamente. Policy estranee non modificate.
<!-- ASUS019_COMPOSED_CURRENT_END -->

<details>
<summary>Checkpoint precedente182455Z conservato come storia</summary>

## Stato corrente — 20261112T182455Z, stessa ASUS-W7POS-019 ACTIVE

| Ambito | Prova e risultato corrente |
| --- | --- |
| Requisito ordinario | Autorizzazione POS e PIN Minimondo senza scadenza, come richiesto esplicitamente. Il modello negoziato `persistent-pos-v1` salva autorità DPAPI v6 con testimone HKCU persistente indipendente dal backup. PIN, binding, generazione, versione, tentativi e revoche note restano verificati. Nessuna licenza futura introdotta; vecchie ricevute v1–v5 restano bounded. |
| Installato reale | Nuovo248e689 installato dopo un solo UAC umano: setup exit0,41file hash-esatti,17file del profilo conservati,Start verificato. Readback indipendente PASS, EXE SHAdec0d531. Nessun avvio automatico; primo/secondo Start/PIN reali ancora NOT_RUN al checkpoint. Precedente aa4 e relativo loginFAIL conservati come storia. |
| Windows248 qualificato | PR138, 248e6891dcab5ce086c36b0fbec1c66ffb487344. All43248176976 PASS16/16, artifact13960748887/SHA2f2c9486 verificato8entry; Security43248176453 PASS; Release43248317257 verificata41file/110checksum, setup15f1e9f2/payloadccfc0b7f. QA43248326349 verificata40file,34comuni esatti. MainWindow dal bundle esatto PASS29205.7167ms, cleanup naturale. Core46/46, smoke WPF bounded/permanente e processi CLR distinti, statico/dialog PASS. |
| ASUS sul payload esatto | Integrated12 PASS740.778s e Final60 PASS3606.695s: tutti tre flag misura/ambiente/stabilità validi, exit0 e cleanup naturale. UI p95 Rows115.415ms/Grid117.887ms, massimi255.563/165.290ms; limiti preregistrati p95≤250ms e massimo≤1000ms invariati. Crescita managed massima1.545MB sotto8MiB. Budget SHA63970e30,41file prodotto e34comuni QA esatti. Non è una qualifica Win7 fisico o periferiche. |
| Merge Windows | PR138 unita normalmente in92dff410e241e5ee7c2a42a82ef4615b624537d5; albero8ae8b6bd identico248. Postmerge Security43272055077 PASS; CI43272054951 FAIL15PASS/1FAIL nello HIST_LOAD_50K. Artifact13970438026/SHA0e3672dc e8entry verificati: measurementCompleted=false per2campioni dispatcher e1memoria persi; environmentValid/thresholdsPassed=true, tutti gli altri assertLoadPASS. Nessun rerun automatico né soglia ridotta. Release43272054924 ancora RUNNING; cassa selezionata248 con le proprie qualifichePASS, distinto dal residuo della misura storica. |
| Server qualificato | b091325, PR152 unita normalmente in5ff8577; albero b5058dd2 identico al candidato e al virtual merge956176f. Verify/CF propri43251761879/881 e main43258018081/735 PASS. Artifact58file/lock/config e replay97file/5356assert/3concorrenze verificati anche dopo merge. FAIL9ec e332 conservati: metadata replay/lint, poi regressione della rilettura clock dopo lock; b091 ripristina integralmente il corpo canonico prima dei soli due controlli di invalidazione persistente. |
| Storico autenticato nuovo composto | Run43254453360, serverb091/client248, PASS12/12 eSQL97/5356/3; artifact13961554676/SHA5849729d verificato. Admin→entrambiPOS senza eco, ACK perso+nuovo CLR/pull prima ACK, duplicato/Undo/correzione, revoca isolata, HTTP409/history_cloud_writer_closed con zero nuovi grant/ricevute, Recovery/baseline e riconciliazioni senza nuovi movimenti; cleanup naturale. Principal bounded posseduti: prova Core/Data+HTTP+SQL+Storage+Worker+browser distinta dall’enrollment permanente WPF reale. Run23 server612/clientaa4 e precedenti qualifiche restano storia attribuita. |
| SQL effettivamente applicato | Solo nuova migrazione20261112143448_asus019_persistent_pos_authorization_v1.sql, SHA29bd59ef; versione cloud20261112174421. Tre corpi funzione esatti, V3 precedente invariata, nuovi RPC riservati a service_role e helper privato non eseguibile direttamente. Nessun dbpush/repair globale o modifica alle29migrazioni storiche. PIN: soltanto l’operatore Minimondo ha scadenzaNULL, versione1 e PIN invariati. Conteggi cloud movement_versions/job/upload/migrationrun0 prima/dopo. |
| HTTP effettivamente pubblicato | Version ecfd6920-bef3-441e-99f6-2a1b83aaf498, deployment8a732699-a226-40cd-a43c-4868f258826c al100%. Artifact congelato13960220868/SHAcd1e6ebe, sorgente956/treeb5058dd2, Wrangler4.123.0 con no-bundle/keep-vars, nessun rebuild. Tutti24binding e runtime conservati; metadati non-assets esatti, chiave gateway mantenuta senza lettura/rigenerazione. GET protetto e form staff HTTP200. Dopo deploy77richieste firmate riuscite, tutte tre lane idle e0risposte con lavoro. |
| Worker operativo osservato | Host7c95955/runtime612 CJS20eb2acd invariato, qualifica host5/5 e pacchetto7 verificato; Node24.18.0/account virtuale/DPAPI/ACL, limiti nativi1GiB/un processo e heap640MiB. Stesso PID7208 running nel controllo17:54Z; nessun reinstall/rekey/restart manuale del servizio. FAIL iniziali57014/exit1 conservati. Dopo unico restart database15:24Z ripristinate le tre lane; osservazione idle prolungata, non garanzia di disponibilità notturna. Host richiede PC acceso. |
| Consegna cassa installata | Backup17file e3DB integrityok/FK0, conteggi115tabelle conservati. Manifest12ricevute/41file SHAe66309b6 validato prima del consenso; installer canonico15f1e9f2 concluso18:22:55Z,nessun reboot richiesto. Cash-only: Worker/chiavi invariati, POS lasciato chiuso. Atteso Start/PIN mascherato umano ed effettivo enrollment v6 del vecchio format4. Copia separata Win7-transfer248 byte-esatta del ReleasePack SHAeeb50b3f,nessun dato del negozio aggiunto; kit1823697 invariato. |

Checkpoint unico privato aggiornato20261112T182455Z. Restano primo login→carrello/barcode→riepilogo→ritorno, seconda apertura offline e riavvio Windows reale. UAC completato e cassa248 installata; nuova autorizzazione reale non ancora attestata. Residuo indipendente HIST_LOAD_50K: osservazione incompleta conservataFAIL, domanda diagnostica prima di ogni nuova prova; nessun cambiamento al protocollo per trasformarla inPASS. Le prove sintetiche di autorità in processi distinti e oltre il vecchio limite sonoPASS; nessun reboot fisico dichiarato. Revoca server sconosciuta mentre offline non è rilevabile immediatamente.

Win7 fisico BLOCKED_TARGET; periferiche NOT_RUN; Getnet C MISSING e tre moderni non applicati. DPI200%/2880×1800 conservati,100/125 USER_DEFERRED. Nessun Apply/Undo/reimport reale, articolo, vendita di prova o cancellazione di profilo/code. FAIL intermedi e ricevute storiche conservati, nessuna soglia/assert/pin indebolita. Nessun DONE globale.

Advisors dopo DDL conservati come inventario: [RLS senza policy](https://supabase.com/docs/guides/database/database-linter?lint=0008_rls_enabled_no_policy), [search_path](https://supabase.com/docs/guides/database/database-linter?lint=0011_function_search_path_mutable), [RPC anonime](https://supabase.com/docs/guides/database/database-linter?lint=0028_anon_security_definer_function_executable), [RPC autenticate](https://supabase.com/docs/guides/database/database-linter?lint=0029_authenticated_security_definer_function_executable) e [protezione password](https://supabase.com/docs/guides/auth/password-security#password-strength-and-leaked-password-protection). Nessun finding di execute pubblico per la nuova V4; permessi live verificati separatamente. Policy estranee non modificate.

</details>

<details>
<summary>Checkpoint precedente175420Z conservato come storia</summary>

## Stato corrente — 20261112T175420Z, stessa ASUS-W7POS-019 ACTIVE

| Ambito | Prova e risultato corrente |
| --- | --- |
| Requisito ordinario | Autorizzazione POS e PIN Minimondo senza scadenza, come richiesto esplicitamente. Il modello negoziato `persistent-pos-v1` salva autorità DPAPI v6 con testimone HKCU persistente indipendente dal backup. PIN, binding, generazione, versione, tentativi e revoche note restano verificati. Nessuna licenza futura introdotta; vecchie ricevute v1–v5 restano bounded. |
| Installato reale | Ancora aa4ec25, EXE SHA f2f75be2, Start/profilo verificati. Primo login reale FAIL: first-login timeout, fallback offline_attestation_required mostrato genericamente come scaduto. POS chiuso dall’utente. Installazione248 e primo/secondo avvio reale NOT_RUN: non attribuire al pacchetto nuovo il precedente smoke reale d1b. |
| Windows248 qualificato | PR138, 248e6891dcab5ce086c36b0fbec1c66ffb487344. All43248176976 PASS16/16, artifact13960748887/SHA2f2c9486 verificato8entry; Security43248176453 PASS; Release43248317257 verificata41file/110checksum, setup15f1e9f2/payloadccfc0b7f. QA43248326349 verificata40file,34comuni esatti. MainWindow dal bundle esatto PASS29205.7167ms, cleanup naturale. Core46/46, smoke WPF bounded/permanente e processi CLR distinti, statico/dialog PASS. |
| ASUS sul payload esatto | Integrated12 PASS740.778s e Final60 PASS3606.695s: tutti tre flag misura/ambiente/stabilità validi, exit0 e cleanup naturale. UI p95 Rows115.415ms/Grid117.887ms, massimi255.563/165.290ms; limiti preregistrati p95≤250ms e massimo≤1000ms invariati. Crescita managed massima1.545MB sotto8MiB. Budget SHA63970e30,41file prodotto e34comuni QA esatti. Non è una qualifica Win7 fisico o periferiche. |
| Merge Windows | PR138 unita normalmente in92dff410e241e5ee7c2a42a82ef4615b624537d5; albero8ae8b6bd identico248. Postmerge Security43272055077 PASS; CI43272054951 e Release43272054924 ancora RUNNING al checkpoint. Resta selezionato il pacchetto248 già qualificato, senza sostituirlo con un diverso versionstamp di main. |
| Server qualificato | b091325, PR152 unita normalmente in5ff8577; albero b5058dd2 identico al candidato e al virtual merge956176f. Verify/CF propri43251761879/881 e main43258018081/735 PASS. Artifact58file/lock/config e replay97file/5356assert/3concorrenze verificati anche dopo merge. FAIL9ec e332 conservati: metadata replay/lint, poi regressione della rilettura clock dopo lock; b091 ripristina integralmente il corpo canonico prima dei soli due controlli di invalidazione persistente. |
| Storico autenticato nuovo composto | Run43254453360, serverb091/client248, PASS12/12 eSQL97/5356/3; artifact13961554676/SHA5849729d verificato. Admin→entrambiPOS senza eco, ACK perso+nuovo CLR/pull prima ACK, duplicato/Undo/correzione, revoca isolata, HTTP409/history_cloud_writer_closed con zero nuovi grant/ricevute, Recovery/baseline e riconciliazioni senza nuovi movimenti; cleanup naturale. Principal bounded posseduti: prova Core/Data+HTTP+SQL+Storage+Worker+browser distinta dall’enrollment permanente WPF reale. Run23 server612/clientaa4 e precedenti qualifiche restano storia attribuita. |
| SQL effettivamente applicato | Solo nuova migrazione20261112143448_asus019_persistent_pos_authorization_v1.sql, SHA29bd59ef; versione cloud20261112174421. Tre corpi funzione esatti, V3 precedente invariata, nuovi RPC riservati a service_role e helper privato non eseguibile direttamente. Nessun dbpush/repair globale o modifica alle29migrazioni storiche. PIN: soltanto l’operatore Minimondo ha scadenzaNULL, versione1 e PIN invariati. Conteggi cloud movement_versions/job/upload/migrationrun0 prima/dopo. |
| HTTP effettivamente pubblicato | Version ecfd6920-bef3-441e-99f6-2a1b83aaf498, deployment8a732699-a226-40cd-a43c-4868f258826c al100%. Artifact congelato13960220868/SHAcd1e6ebe, sorgente956/treeb5058dd2, Wrangler4.123.0 con no-bundle/keep-vars, nessun rebuild. Tutti24binding e runtime conservati; metadati non-assets esatti, chiave gateway mantenuta senza lettura/rigenerazione. GET protetto e form staff HTTP200. Dopo deploy77richieste firmate riuscite, tutte tre lane idle e0risposte con lavoro. |
| Worker operativo osservato | Host7c95955/runtime612 CJS20eb2acd invariato, qualifica host5/5 e pacchetto7 verificato; Node24.18.0/account virtuale/DPAPI/ACL, limiti nativi1GiB/un processo e heap640MiB. Stesso PID7208 running nel controllo17:54Z; nessun reinstall/rekey/restart manuale del servizio. FAIL iniziali57014/exit1 conservati. Dopo unico restart database15:24Z ripristinate le tre lane; osservazione idle prolungata, non garanzia di disponibilità notturna. Host richiede PC acceso. |
| Consegna cassa preparata | Backup17file esatti e3DB integrityok/FK0, conteggi115tabelle conservati. Manifest qualified-cash248-windows-paths SHAe66309b6 collega12ricevute e41file; ValidateOnly exit0 riconferma pacchetto e profilo invariati. Installer canonico15f1e9f2 non ancora avviato: disponibilità umana richiesta per un solo UAC. Script cash-only lascia POS chiuso e non tocca Worker/chiavi; poi Start/PIN mascherato manuali ed effettivo enrollment v6 del vecchio format4. |

Checkpoint unico privato aggiornato20261112T175420Z. Restano intervento UAC e prove sull’installato: primo login→carrello/barcode→riepilogo→ritorno, seconda apertura offline e riavvio Windows reale. Le prove sintetiche in processi distinti e oltre il vecchio limite sono PASS; nessun reboot fisico dichiarato. Revoca server sconosciuta mentre offline non è rilevabile immediatamente.

Win7 fisico BLOCKED_TARGET; periferiche NOT_RUN; Getnet C MISSING e tre moderni non applicati. DPI200%/2880×1800 conservati,100/125 USER_DEFERRED. Nessun Apply/Undo/reimport reale, articolo, vendita di prova o cancellazione di profilo/code. FAIL intermedi e ricevute storiche conservati, nessuna soglia/assert/pin indebolita. Nessun DONE globale.

Advisors dopo DDL conservati come inventario: [RLS senza policy](https://supabase.com/docs/guides/database/database-linter?lint=0008_rls_enabled_no_policy), [search_path](https://supabase.com/docs/guides/database/database-linter?lint=0011_function_search_path_mutable), [RPC anonime](https://supabase.com/docs/guides/database/database-linter?lint=0028_anon_security_definer_function_executable), [RPC autenticate](https://supabase.com/docs/guides/database/database-linter?lint=0029_authenticated_security_definer_function_executable) e [protezione password](https://supabase.com/docs/guides/auth/password-security#password-strength-and-leaked-password-protection). Nessun finding di execute pubblico per la nuova V4; permessi live verificati separatamente. Policy estranee non modificate.

</details>

<details>
<summary>Checkpoint precedente153827Z conservato come storia</summary>

## Stato corrente — 20261112T153827Z, stessa ASUS-W7POS-019 ACTIVE

| Ambito | Sorgente / prova | Esito attribuito |
| --- | --- | --- |
| Installato reale | aa4ec25, U19CashAa4Install01/U19PersistentPosAuth01 | INSTALLATO41/profile13 conservato,EXE f2f75be2 e Start umano verificati. Login FAIL: first-login timeout e fallback offline_attestation_required, tradotto genericamente come scaduto. Trust reale format4; navigazione e secondo Start aa4 NOT_RUN. Nessun nuovo PIN richiesto durante implementazione. |
| Nuovo requisito autorizzazione | Windows248e689/PR138;server3322b6c/PR152 | IMPLEMENTATO,in qualifica,non installato/distribuito: negoziazione persistent-pos-v1,grantDPAPIv6/testimoneHKCU persistente,heartbeat conserva autorità,timerlogout assente. Core46/46,WPFbounded+permanente/processi distinti,statico/dialog PASS. CI/SQL/Release/ASUS propri ancora da verificare; nessun PASS aa4 trasferito. |
| PIN operatore | risposta esplicita utente12novembre;pin-deadline-applied.private.json | Rimossa soltanto la scadenza14giorni dell’operatore Minimondo:1riga,versione e PIN invariati,cambio manuale/revoca mantenuti. Nessun segreto letto o salvato; altre policy staff invariate. |
| Prima CI nuova modalità | server9ec,run43247498459 | FAIL conservato: metadata di ordinamento replay mancanti e variabilemodule nel nuovo fixture lint. Successore332 aggiorna hash esatto/25assert e nome loader,guardia originale mantenuta; nuovo replay in corso. |

| Offline indipendente | fe069a1, PR137, merge e6ce2ee | PASS Windows15/15,Security2/2,Release41/110,QA40,ASUS Integrated12+Final60. Riapertura in CLR distinti con positivi/negativi; reboot/logoff offline BLOCKED_TIME_CONTINUITY. Postmergee6 CI43153357858,Security43153357836 eRelease43153357805 PASS. |
| Composto cassa+storico | aa4ec25, PR135, merge0705b4ca27cc083ae95a59c1e13db041848d1a7d | PASS All43136421196 (16/16),Security43136426122 (2/2),Release43136430945 (41/110),QA43136435344 (40),ASUS Integrated12+Final60,tre flag misura validi. Main ha lo stesso albero2e768143b9e0897280d014229357e3228e5b2a7f del candidato. Postmerge0705 CI43153687268PASS16/16 conartifact verificato8entry,Security43153687263PASS2/2,Release43153687291PASS e download41/110. Mainpacket diverso nel versionstamp,non ASUStestato/installato: sul PC è installatoaa4 qualificato12+60. Run e6 conservate. Installato41/profile13; prova umana corrente distinta. |
| Coppia storica finale | server612d9bd/clientaa4ec25, run43137863338 | PASS12/12 eSQL96/5331,artifact13909032440/SHA4b07396b verificato. HTTP409/history_cloud_writer_closed reale,zero grant/receipt,Admin→entrambiPOS senza eco,revoca isolata,ACK perso+nuovo CLR,Recovery/baseline e due riconciliazioni senza nuovi movimenti,cleanup naturale. Anche22/server612-client1f PASS12/12 e96/5331. Copertura Core/Data+HTTP+SQL+Storage+Worker+browser, distinta dal WPF installato/Win7. |
| Host Windows | 7c959553d77416a1620ccfa53fb5be15a85db291,run43152837911 | Qualifica PASS5/5 e pacchetto7 verificato, CJS20eb2acd esatto della prova23. Servizio realmente INSTALLATO con Node24.18.0,account virtuale,DPAPI/ACL,limiti nativi1GiB/un processo e heap640MiB. Primo avvio e recuperi SCM: child_exited/exit1; picco privato circa63MB,nessuna prova di superamento memoria. Disponibilità staging limitata al PC acceso,nessun Node sul Win7. FAIL iniziali conservati. Dopo unico restart database,processo7208 mantenuto e177richieste firmate accettate nelle tre lane; osservazione stabilità ancora in corso,nessun riavvio manuale servizio. |
| SQL staging | sorgente612,29 file canonici/SHA invariati | APPLICATE soltanto le29 migrazioni della task,171→200 registrazioni; mapping versione sorgente→cloud conservato. PASS47tabelle con RLS/PK,nessuna lettura anonima/diretta service-role,6corpi critici esatti e5dipendenze precedenti invariate. Snapshot iniziale preinstallazione:runtime/movimenti/job/ricevute/grant/eventi0;ricontrollo aggregato corrente fallito per timeout e non assunto invariato; bucketprivato20MiB con MIME previsti.6pendenti estranee e7alias preesistenti esclusi; nessun dbpush/repair globale. |
| HTTP staging | 612d9bd,version4a2ef3eb-485b-4848-83a0-ddae22cb1b35,deployment688beeab-5d47-41dd-b52e-eefe9cfb5fa2 | Chiave dedicata generata durante installazione,protetta DPAPI/ACL e inviata soltanto via stdin. Clone versione dopo secretput: script/runtime e tutti i binding precedenti ESATTI rispettofaf9;unica aggiunta PAYMENT_HISTORY_WORKER_GATEWAY_SECRET. Nessun rebuild/deploy codice nuovo. Gateway unsigned403;almeno una richiesta firmata accettata13:33:28Z. Accettazione singola distinta dal servizio stabile,ancora FAIL. Nessun valore segreto letto nei report. |
| Convergenza Admin | PR149→84172406,PR150→ea9cfec2,PR151→0d61aaeb2312acdfdaab29df9ca0267e57920b2c | Tre merge normali;tree151 a1c92b64 identico7c. CI7c43153578202/CF43153578259PASS;artifact58file13915165978/SHA191a58f8 e replay96/5331/3concorrenze13915811332/SHA9d2300e4 verificati. Main0d61 CI43155600313/CF43155600256PASS,58file artifact13915862293/SHAfa28c580 eSQL96/5331/3concorrenze13915853428/SHA529b86bc verificati;nessun nuovo deploy da main. CI intermedia14943153580626 CANCELLED automaticamente per i merge149/150 ravvicinati,esito conservato e non contatoPASS;150CI43153635083/CF43153635103PASS. |
| Installazione eseguita | U19CashAa4Install01 e U19HistoryWorkerHost7C9501 | Backup coerente integrity/FK e copia13file verificati prima della manutenzione. Un solo consenso UAC accettato,setupaa4/SHA722b4cf9 terminato exit0,41file prodotto e13file profilo verificati,host7c installato. Ricevute separate per installazione riuscita e fallimento operativo successivo;non ripetere installer né generazione chiave. |

| Diagnosi servizio reale | U19PersistentPosAuth01/backend-restart-observation.private.json | Timeout57014 precedenti su gateway e servizi estranei; metriche autentiche mostrano commitment1.59GiB SEVERE,swap622MiB e95.2%RAM nel campione11:51locale. Singolo restart Dashboard,Postgres riavviato15:24:07Z,controllo1.9s/0blocker;177richieste firmate e tre forme idle. Conteggi cloud movement_versions/job/upload/migrationrun0 alle15:32:14Z. Causa specifica non dimostrata,nessun cambio configurazione/billing/chiave né restart manuale Worker. |

Advisors conservati: [RLS senza policy](https://supabase.com/docs/guides/database/database-linter?lint=0008_rls_enabled_no_policy) intenzionale per tabelle interne negate per default; [cinque RPC SECURITY DEFINER autenticate](https://supabase.com/docs/guides/database/database-linter?lint=0029_authenticated_security_definer_function_executable) con fence attore/shop/sessione/permessi qualificati. [INFO sulle FK](https://supabase.com/docs/guides/database/database-linter?lint=0001_unindexed_foreign_keys) e indici nuovi registrate senza estendere la patch; finding preesistenti conservati. FirstFAIL checkout/account virtuale e due errori del controllo pubblico conservati,nessun deploy ripetuto per quei controlli.

Nessun Apply/Undo/reimport reale, articolo o vendita di prova. GetnetC MISSING; Win7 fisico BLOCKED_TARGET; DPI200%/2880×1800 invariati,100/125 USER_DEFERRED. Reboot offline ancora distinto dalla riapertura app. Execution ACTIVE,nessun DONE globale. Installazione PASS,prima richiesta firmata accettata e servizio stabile FAIL restano distinti;Nuovo grant richiede accesso online valido dopo qualifica/installazione; aa4 ha login FAIL documentato. La regola offline reboot legacy resta storica,il nuovo modello v6 non usa una scadenza ordinaria; qualifica hardware Win7 distinta. Diagnosi timeout: [guida Supabase](https://supabase.com/docs/guides/database/postgres/timeouts).

</details>

<details>
<summary>Checkpoint precedente235241Z conservato come storia</summary>


## Priorità installato/offline — 20261111T235241Z, stessa ASUS-W7POS-019 ACTIVE

Il nuovo addendum separa la consegna della cassa dal residuo storico indipendente.
Identificazione read-only: Start punta al Program Files installato; processo vecchio
2ad42ca, proprietario del DB Minimondo atteso. I Recent pertinenti sono link a una
cartella sorgenti e a uno ZIP, non EXE alternativi. Origine esatta del clic iniziale
non attestata. Nessuna riscrittura di MainWindow: correzione già qualificata in d1b.

Cassa d1b2bc318c8a0aecce9543dc6a96f9a1aaf7cfc3 installata tramite setup canonico
SHA a07430ce29e60eb04d2bc23651ab905beac3bf6ed6e8e12479e83ac230f2bb8e,
Release42967017718. Compatibilità:15 migrazioni esatte,0pendenti/ignote/checksum
divergenti; schema storico,formato trust e contratti HTTP invariati rispetto2ad.
Backup prodotto verificato/integrity+FK e copia completa13file hash-identica;
installer non ha alterato il profilo.41file installati corrispondono al pacchetto.
Log installer: successo/no reboot; exit code non catturato, non inventato.
Nuovo processo8128 da explorer, EXE/versione/hash e proprietà dello stesso DB
verificati. Primo accesso reale: carrello vuoto e barcode disponibili, sync
non modale; sconosciuto apre quick-create esistente, annullata senza salvare;
riepilogo volontario→POS, focus scanner provato digitando e cancellando un
marcatore. Conteggi prodotti/vendite/outbox/storico invariati. Chiusura normale.
Secondo avvio reale NOT_RUN (avvio/PIN mascherato manuali); scanner fisico NOT_RUN.
Qualifiche d1b riusate sul pacchetto identico; nessuna nuova soak per il link.

Offline: d1b mantiene il contratto process-scoped e NON soddisfa la riapertura.
Successore separato da d1b in codex/asus019-offline-reopen. Modello da implementare:
attestazione DPAPI v5 con identità profilo/dispositivo/generazione/operatore e
versione credenziale; testimone protetto fuori dal backup profilo che vincola
esattamente la ricevuta e conserva clock monotono/high-water/tombstone di revoca.
Un nonce volatile per sessione Windows rende distinguibile la perdita della
continuità di boot; QPC avanza il tempo server fra processi senza affidarsi solo
all'orologio civile. File vecchi/alterati/altro profilo o device non riattivano
l'autorità. Mirror/PIN/tentativi e durata esistente <=12h,scadenza staff/sessione
restano obbligatori; nessuna password recuperabile. Prove positive e negative
in processi distinti prima di cambiare l'aspettativa dello smoke.
Riavvio Windows offline BLOCKED_TIME_CONTINUITY: senza clock sicuro attraverso
lo spegnimento non è provata la durata trascorsa; nuova attestazione online dopo
reboot, non semplice rimozione del nonce. Test di reboot simulato/clock rollback
deve negare. Supporto reale del reboot non dichiarato; prossimo passo scegliere
un'origine temporale verificabile compatibile Win7 e qualificarla su target.
Prima attivazione senza trust negata; perdita rete nello stesso processo resta
distinta da blocco/sblocco,nuovo processo e reboot. Revoche server sconosciute
durante offline non rilevabili immediatamente.

Storico preservato: All93fd run43114242774 FAIL15PASS/1FAIL nel crash
HIST_B_COMMIT_AUTHORITY; artifact13899494374 SHA1a8ebfcf verificato (8entry).
Release43114234648 PASS packaging, non installato/qualificato globalmente.
Cinque nuovi assert WPF mancavano nei due cataloghi QA: fix2file da qualificare,
83check protocollo PASS, nessuna assertion precedente rimossa. SQL2c/Remote17
FAIL prima degli assert per duplicato permesso nel seed; fix2024819 idempotente
circoscritto al fixture,nuova run necessaria. Remote15/16 nove casi reali PASS
propri ma overallFAIL; timeout non accettato come writer_closed.

Autorizzata dall'utente l'esecuzione automatica notturna. Nessun nuovo consenso
operativo richiesto; desktop protetto e blocchi strumento rispettati. Nessuna
vendita/import/Undo reale,nessun nuovo prodotto,soglia o timeout indebolito.
200%/2880x1800 conservati,100/125 USER_DEFERRED,Win7fisico BLOCKED_TARGET.
Stati cassa,offline,storico e target fisico distinti; nessun DONE globale.


## Checkpoint storico precedente — 20261111T230040Z, stessa ASUS-W7POS-019

Execution ACTIVE: cassa quotidiana e storico bilaterale. Tutti i file necessari
autorizzati dall'utente. Qualifiche riferite agli SHA e agli artefatti seguenti;
documentazione in checkout separati, candidati delle run attive congelati.

| Scenario | SHA / comando / ambiente | Esito ed evidence | Prossimo gate |
|---|---|---|---|
| Windows composto | 6cdaa2988030b3a140493d6239c8d5007603f55c; Windows-All43080480557, Security43080480554 | PASS16/16 e due job Security PASS; U19Win6cRemote01 verifica8entry, artifact13886737547 SHA2569de74b63, fingerprint6ea13878 | componente qualificato; consegna separata |
| Load composto | HIST_LOAD_50K, stesse soglie/carico | PASS11/11:571/571campioni,drop0,query391.7479ms/500,cold420.5537,maxDispatcher12.3842ms | non attribuisce la causa del vecchio gap059 |
| Release esatta | 6c, Release43080678943; U19Release6c02/download-verification.json | PASS41file prodotto/110checksum; versione1.0.0-dev.6cdaa2988030, installer69c51811,payload0b88ee0f; development-unsigned, non installato | installazione/consegna separata |
| Cassa ASUS sul pacchetto | exact6c,QAartifact13884426672/da3147db,40file; U19Cart6c01 | Integrated12PASS31cicli focus29.410/scroll194.120ms; Final60PASS151cicli/3605.623s focus29.133/scroll194.686ms,zero drop. P95UI Rows212.029/Grid237.805ms;41file prodotto riverificati,budget63970e30 invariato. final-verified.private.json resultSHA8690882e | componente cassa qualificato, installazione/consegna e coppia storica ancora aperte |
| MainWindow minimo | patch privata menu-focus,commitda6539c antenato6c | reverse patch check PASS; a chiusura animazione menu torna RestoreScannerFocus sulla scheda Cassa,altre schede HamburgerFocus. Esatto punto già incluso in All16/16 e soaks6c; autorizzazione utente riconfermata | nessun nuovo delta o PASS trasferito |
| Diagnostica catalogo incollata | catalog.pull/db_failure HTTP500,UTC2026-07-26T12:00:00Z | corrisponde al campione sintetico RUNTIME_OBSERVABILITY,Program.cs2126-2146; non usata come prova di incidente attuale del negozio | nessuna modifica backend dedotta da questo campione |
| Cassa precedente | d1b2bc3,pacchettoa07430ce/payload6b44ffe3 | ASUS PASS31cicli/max102.510ms e151/max97.826ms | risultati storici, non trasferiti al nuovo pacchetto |
| Windows predecessori | 622PASS16/16;0e CI43078277359 eRelease43078654263 | 0e FAIL13PASS/1FAILbuild/2NOT_RUN, U19Win0eRemoteFail01; causa due API moderne nel solo helper condiviso net48, fix84b3015 incluso6c; intera soluzione Release PASS con solo CS8892 preesistente | FAIL conservato, non sommato ai successori |
| Baseline canonica | 17f7081 e5512319, inclusi6c | RED conservati eGREEN74/74 (include51): prova applied originale dopo cambio epoca; delta vuoto avanzato richiede snapshot tramite fallback esistente, schema20/trigger invariati. WPFnet48x86 e dialog standards PASS | E2E completo ancora aperto |
| Admin pubblico precedente | 8ccb5758; CI43066464128,CF43066464229 | Verify/SQL/build PASS, deploySKIPPED; U19Admin8cc01 verifica58file/tree/lock/config, Worker13876372298/7a032d48 e replay13876623226/14fb3bc5 | Worker impacchettato, servizio operativo non attestato |
| Admin candidato nuovo | b61ff823db68f37689a997604edda820256bc7f5, PR151; CI43091893821,CF43091893786 | Verify/SQL/build PASS, deploySKIPPED. U19AdminB6101 verifica58file/tree05d3cb0d/lock/config; Worker13889521659/5dd48490 e replay13889457956/8b33dab0,94file5307assert+3racePASS | servizio isolato reale attestato in Remote15; deploy staging/produzione non attestato |
| Sessione web reale | Remote10,run43085382627,Admin7d371d9/client6c | FAIL403 history_access_denied; cookie presente, propria sessione revocata, credenziale valida e permesso presente. U19AuthenticatedRemote10 artifact13887037812/d9db3c7e; cleanup naturale | login12h oltre credenziale+1h riprodotto e corretto sotto |
| Regressione scadenza | auth b61ff; U19StaffExpiryContract01 ecommitted-binding.private.json | RED2FAIL/4PASS, GREEN6/6: scadenza min(12h,credenziale), guardia runtime invariata; scaduto negato, sessione troppo lunga ancora negata. TypecheckPASS,48targetPASS,1624foundationPASS/7SKIP; sola rinomina loader test per ESLint poi unit6/6 e lint PASS | browser/API reale HTTP200/read=true in Remote15; precedente FAIL conservato |
| Recovery prima prova | Remote11,run43085389040,Admin9f50f08/client938305e | FAIL prima del witness: adattatore QA cercava receipt_json nella risposta Apply già ricevuta. U19AuthenticatedRemote11 artifact13888091004/194184e4; tre client PG rollback/uscita naturale0 | b61ff cattura testo originale dal DB nella stessa transazione col lock, nessuna ricevuta ricostruita |
| Recovery Remote12 | Adminb61ff/client938305e,run43091251050; U19AuthenticatedRemote12 artifact13889193349/923f8b7b | overallFAIL; prima gara Apply/recovery PASS con advisory lock osservato e ricevuta originale. Seconda raggiunge review finale dopo due recovery, FAIL BASELINE_REVIEW_REJECTED; tre PG rollback/uscita0. Il test non ripristinava SET LOCAL dopo COMMIT | correggere soltanto contesto QA e attestarlo; browser non raggiunto |
| Successore Recovery / Remote13 | Adminb6304fe/client938305e,run43094416569 | replay SQL stepPASS,servizio/browser ed exportFAIL. Log recuperato: conflict_source_ready:history_test_client_rejected. Fonte congelata ha superato i controlli precedenti prima di questa fase; ricevute singoli casi ecleanup non esportate,nessun PASS nuovo attestato | causa client interna da recuperare col successore diagnostico |
| Controllo browser / Remote14 | Adminc1b5fc1/client6c,run43094421902; src/SQL/lock/config uguali b61 | replay SQL stepPASS,servizio/browser ed exportFAIL. Stesso codice conflict_source_ready:history_test_client_rejected; accesso/API e controlli precedenti superati nel flusso,ma ricevuta caso non disponibile | non dichiarare il percorso completo PASS |
| Recupero diagnostica Remote13/14 | API log recuperata dopo precedenti HTTP502 | export CreateArtifact fallito dopo5tentativi con HTML inveceJSON; artifact assenti. Codice esterno recuperato,oggetto clientFailure originale perso con il file non esportato | Remote15: copia redatta stdout identica ai byte originali artifact,SHA b6f1a46c; recupero alternativo verificato |
| E2E reale Remote15 | Admin177cbc485c4a1eca685ad11042831af90ac6fd47/client8c5b43550804b64f50080e820d0f34aaef27df1d,run43102737034; U19AuthenticatedRemote15 | overallFAIL in conflict_preparation. Artifact13894880423 SHAe0b33ba8 verificato; SQL94file/5307assert PASS e cleanup naturale. Browser/API HTTP200,read=true; Worker reale isolato SHA20eb2acd | diagnosticare il codice HTTP originale omesso dal vecchio adattatore; nessuna causa prodotto assunta |
| Recovery reale Remote15 | stessi SHA/run; due casi indipendenti nel report | PASS Apply prima di recovery della sessione corrente e PASS due recovery concorrenti. Ricevuta originale SHAda5ee0bf,attese advisory osservate,contesto SET LOCAL ripristinato,hash economico completo invariato08857d62,native0/nuove ricevute bancarie0 | gare nuove chiuse; vecchi FAIL conservati |
| Catena POS reale Remote15 | stessi SHA/run,servizi HTTP+Next+Postgres+Storage+Worker e Admin già aperto | PASS POSA→server→Admin→POSB→POSA,6movimenti/hash2e4ecedb; duplicate PASS stesso hash; Undo PASS5/hash8d1ff879; correction PASS6/hashc95138a2; ACK perso/nuovo processo/pull prima ACK PASS7/hash3f2f43c1. Confronto di7campi economici,no effetti nativi/eco | non sommare casi sovrapposti né dichiarare l’intera suite PASS |
| E2E reale Remote16 | Admin6b98/client09be,run43109044206; U19AuthenticatedRemote16 | overallFAIL,stessi nove casi PASS nella ricevuta propria. Artifact13896212196 SHA2923b9f5; causa macchina catturata B/local_review/timeout,HTTPassente,retryable=true. Cleanup attesta solo figli Next/Worker/CLR,non Docker | timeout non è PASS di conflitto; causa SQL/HTTP da attestare |
| Successore Windows composto | 93fd90c9c25bcb36423dea8b5e5bd7cc8ff95248,PR135; C:/Dev/A19HttpComposed01 | 23mappingPASS,build soluzione/WPFnet48x86/harnessPASS0warning/error,dialogALLPASS. Security43114242861duejobPASS; WPFbuild43114239676PASSsolo build. All43114242774,Release43114234648,QApayload43116109160 RUNNING. Cash/menu/diagnosticaLoad esatti6c | cinque regressioni sul vero importatore WPF compilate,esecuzione All ancora aperta |
| Successore Admin HTTP | 2c784e60a10611b6b88e26acdbd614d0038113da,PR151 | typecheckPASS,101targetPASS,1641foundationPASS7SKIP. Motore SQL originale delegato da bridge SECURITY INVOKER,solo errore dominio40001→PT409. 43assert canonici ripristinati esattiB61;11nuovi in file dedicato,95file/5318assert ATTESI. Registro nuovo hash64124b7d,vecchi pin invariati; prepare locale byte/ordinePASS con solo override Git nel processo | SQL reale e CF/CI43115750045/046 in corso; nessun PASS SQL locale |
| Gate preflight426 | run43113763111,Admin42644e3; U19Admin426DbFail01 artifact13897980910/9e550f90 | VerifyPASS e CF43113763034buildPASS,deploySKIP; SQLFAIL prima di eseguire database per mancata registrazione ordine nuova migrazione. FAIL preservato; corretto nel successore2c | nessun pin precedente adattato o test precedente rimosso |
| E2E autenticato17 | run43115740703,server2c/client93fd effettivi verificati | RUNNING canonicalSQL. Probe sullo stesso contenuto preparato deve attestare rawSQL40001/canonical_economic_conflict,poi veroHTTP409 con solo history_cloud_writer_closed accettato. Revoca/errori temporanei/risposte malformate esclusi localmente dal PASS | completare conflitto e riconciliazione,origineAdmin e revoca isolata senza allentare timeout/workload |
| Core locale componente | C:/Dev/A19ForegroundErrors01,U19ForegroundCore01/core.trx | PASS2008,30m35s,include20mappingprecedenti; sorgenti congelate in foreground-core-frozen-source.private.json. Non sommato ai23successori e non trasferito come PASS dell’intero93fd | All93fd richiesto |
| PR135/136 | 93fd successore in135; d1b2bc3 in136 | d1b è antenato93fd; PosView/MainWindow blob uguali. Nessuna correzione duplicata necessaria | 136 resta aperta fino al gate di consegna composta; nessun merge ora |
| Vincoli esterni | installato2ad42ca,ledger Minimondo e7originali conservati | scala200%/2880x1800 invariata;100/125USER_DEFERRED;Win7fisicoBLOCKED_TARGET;GetnetCMISSING;nessun Apply reale nuovo | prova fisica e consegna non sostituite da CI |

Le run precedenti Remote01-09 e il FAIL Load059 restano nella cronologia;
nessuna soglia, campione o timeout viene allentato per ottenere PASS. Le sorgenti
dei candidati attivi e gli SHA privati precedenti sono riproducibili. Gli artifact
sono verificati per digest, contenuto e binding, non soltanto per esito del job.

Il solo archivio selettivo passa dal BFF; backup completo locale, nessun segreto
nel browser/evidence. Core/Data con servizi reali non attesta WPF, periferiche o
Win7fisico. Nessun merge main, deploy, installazione o DONE. Checkpoint privato
220842Z aggiornato; PR135/151 eTASK146 conservano il proprio storico integrale.

</details>


<details>
<summary>Checkpoint precedente181007Z conservato come storia</summary>


## Composizione verificata — 20261111T181007Z, stessa ASUS-W7POS-019

Execution ACTIVE: cassa quotidiana e storico bilaterale, tutti i file necessari
autorizzati. Qualifiche legate agli SHA indicati; successori e componenti WIP
non ereditano il PASS. Nessun merge main, deploy o DONE.

| Scenario | SHA / comando / ambiente | Esito ed evidence | Prossimo gate |
|---|---|---|---|
| Cassa precedente | d1b2bc3, pacchettoa07430ce/payload6b44ffe3; ASUS Integrated12/Final60 | PASS31cicli/max102.510ms e151/max97.826ms, budget63970e30 invariato | soaks del pacchetto finale composto |
| Windows composto | 6228e6c3, CI43066448526 eSecurity43066448505 | PASS16/16; U19Win622Remote01 verifica8entry artifact13879850970 SHA256dd273615, fingerprintfd5bd3f0 | pacchetto esatto e consegna restano aperti |
| Load composto | HIST_LOAD_50K, medesime soglie/carico | PASS11/11:578/578campioni, drop0, query408.476ms/500, cold429.4557, maxDispatcher osservato3.2664ms | non attribuisce una causa al vecchio FAIL |
| Load059 precedente | CI43057322574, U19Win059RemoteFail01 | FAIL15/16 solo Load:393/394campioni e1drop iniziale; query283.8887ms PASS, maxosservato13.8919ms. Causa UNRESOLVED; nessun blocco host/UI dedotto | FAIL conservato; diagnostica bounded02f51b9 inclusa nel successore,83check puri PASS |
| Windows anteriori | 791fc4f7 PASS16/16; c88 FAIL2/16 | artifact verificati U19Win791Remote01 eU19WinC88RemoteFail01; risultati separati | nessuna somma dei rerun |
| Admin componenti | 8ccb5758, CI43066464128 eCF43066464229 | Verify/SQL/build PASS; U19Admin8cc01 verifica58file, lock/config/treeb116e1f7. Workerartifact13876372298 SHA2567a032d48 | entrambi i deploy SKIPPED, servizio operativo non attestato |
| SQL composto | replay8cc, artifact13876623226 SHA25614fb3bc5 | PASS94file/5307assert+3race preesistenti, receipt verificata U19Admin8cc01 | nuove race recovery NOT_RUN |
| Storage reale | helper996cd7b incluso8cc | riconosce solo SDK StorageApiError HTTP400/code404 oltre404; U19AuthenticatedContract03 typecheckPASS,42target inclusi nei1618foundationPASS/7SKIP | nessuna interpretazione permissiva di altri400/auth/rete |
| E2E autenticato03 | client622/Admin8cc, run43066567889 | FAIL baseline_upload_target_invalid, U19AuthenticatedRemote03 artifact13876473338 SHA256f0d94772; NextRequest normalizza127.0.0.1→localhost, guardia origine correttamente chiusa | fixture configurata col nome canonico, controllo client invariato |
| E2E autenticato04 | client622/Admin5bb98ff, run43070574083 | FAIL initial_baseline/convergence_timeout dopo limite invariato; ultimo turno history_replica_turn_complete, cleanup naturale. U19AuthenticatedRemote04 verifica artifact13877903598 SHA256a2a465ed. Browser NOT_RUN | diagnosticare stati baseline/Worker, nessun roundtrip PASS |
| E2E autenticato05 | client704e8ce/Admin49e4788, run43072749769 | FAIL initial_baseline/convergence_timeout; SQL94/5307PASS, cleanup naturale, casiACK/restart NOT_RUN. U19AuthenticatedRemote05 verifica artifact13881301690 SHA2561f42cbe6 | FAIL conservato |
| E2E diagnostico06 | client6a82ad3/Adminf8f5388, run43073567207 | FAIL iniziale: POSB baseline applied ma read-mode assente; entrambi i run SQL applied,2upload verified, Worker vivo/idle. U19AuthenticatedRemote06 verifica artifact13882437164 SHA256ef73b969, cleanup naturale | meccanismo candidato riprodotto sotto, conferma HTTP successore necessaria |
| Baseline e cambio epoca | componente17f7081, A19BaselineEpoch01 | regressione RED1/2 eGREEN51/51: conserva soltanto prova applied originale coincidente con shop/device/dataset/snapshot/revisioni/checksum dopo freeze; snapshot ignoto negato, nessun nuovo intento/riscrittura | incluso nel successore, Windows-All/E2E propri necessari |
| Baseline e delta vuoto avanzato | componente5512319, stessa root | U19BaselineReadAdvanceRed02 FAIL2/2 riproduce read-mode disabilitato alle revisioni2/3; GREEN74/74 include rifiuti checksum e riuso dei delta invariati. Se la copia fisica non prova la nuova revisione, rollback e unico resnapshot completo già previsto; triggerSQL/Schema20 invariati. Red01 conserva un errore iniziale nei parametri della fixture, corretto nel Red02 | confermare sul servizio reale dopo correzione |
| Compatibilità della correzione | restorelocked/WPF net48x86 U19BaselineReadAdvanceWpf01; standarddialog U19BaselineReadAdvanceDialog01 | buildPASS0warning/error e dialogALLPASS. Nessuna modifica dialog/scale/dati reali | Windows-All composto e pacchetto/soaks finali ancora aperti |
| Origine Admin e revoca | componenti46b5bcd/30b4f57 e policy web per sorgente esatta | build15/16PASS0warning/error, node syntaxPASS; inventario/freeze/export verificati/migrazione/import web e revoca sessione sintetica, esecuzione NOT_RUN | congelare coppia ed eseguire, conflitti e race ancora aperti |
| Successore0e77a1e | CI43078277359, U19Win0eRemoteFail01 | FAIL:13PASS/1FAILbuild/2NOT_RUNdipendenti; artifact13884155683 SHA256ce54f6f1/8entry verificati, fingerprint29f2fc64. WPF, functional, crash, smoke, Load eCloudSync PASS; nessuna riattribuzione Core | correggere helper condiviso net48 e ripetere suite |
| Packaging0e | Release43078654263 | FAIL Build solution: helper OwnedSqliteTestFile usava ThrowIfNullOrWhiteSpace eIsPathFullyQualified non disponibili nel progetto CatalogPerformance net48; nessun artifact installer | causa esatta conservata release0e-build-failure.private.json |
| Correzione helper | 84b3015, composto f1eb91f | controlli equivalenti string.IsNullOrWhiteSpace, percorso rooted/canonico e file esistente; capability originale obbligatoria. Restorelocked+soluzioneReleasePASS U19OwnedFixtureCompatSolution01, solo warning CS8892 preesistente | nessuna nuova API richiesta dal prodotto Windows7 |
| Conflitto fra POS | componenti923b15a/e18803b, composto ae8d951 | build19PASS0warning/error e node syntaxPASS; ownership reale come chiamanteWPF, richiesta divergente del peer, nessun grant/receipt/economia, chiusura automatica dell'intento via esito negativo del supervisor. Esecuzione NOT_RUN | includere nel successore autenticato |
| Candidato distribuito precedente | client0e77a1e/Adminf070efd, run43078306388 | in corso all'osservazione; include correzioni baseline17f/551 e origineAdmin/ACK/restart/revoca | attendere risultato reale, nessun PASS anticipato |
| Pacchetto precursore | Release05943057309469 | PASS scaricato e verificato U19Release05901:41file prodotto/110checksum, development-unsigned; QAbundle40file U19Cart05901 | non è il pacchetto finale; Integrated/Final60 finale NOT_RUN |
| Vincoli esterni | installato2ad42ca e profilo Minimondo/originali conservati | 200%/2880×1800 invariati;100/125 USER_DEFERRED; Win7fisico BLOCKED_TARGET; GetnetC MISSING | nessuna nuova installazione o attestazione sostitutiva |

Le prove E2E01/02 fallite e i FAIL DNS Chromium restano nella cronologia e negli
artifact verificati Remote01/02; le qualifiche Admin3b eWindows791 sono storiche.
Il test Core/Data usa servizi HTTP/Next/PostgreSQL/Storage/Worker reali di test,
ma non attesta WPF, periferiche o Windows7 fisico. La versione/hash browser è
raccolta solo quando si raggiunge effettivamente quella fase.

Il solo archivio selettivo passa dal BFF configurato; il backup completo resta
locale. Nessuna allowlist client Storage, ricevuta inventata, nuovo grant SDK o
fallback v1. I legacy confermati drenano senza riscrivere l'audit; gli intenti
e witness v2 incerti restano bloccanti. Checkpoint privato180532Z e handoff
PR135/151/TASK146 aggiornati, coda storica TASK146 conservata esattamente.


</details>

<details>
<summary>Checkpoint precedente171130Z conservato come storia</summary>


## Composizione precedente — 20261111T171130Z, stessa ASUS-W7POS-019

Execution ACTIVE, due consegne: cassa quotidiana e storico bilaterale.
Autorizzati tutti i file necessari; nessuna nuova funzione o task.
Codepoint candidati: Windowsc9a3355e972f4dd995931e4f24bf4fb6c4f74ab3
(include harness92c3517 e diagnostica02f51b9), Admin996cd7b7475e17679e30347823fafd4378022318.
Il successore documentale va legato alla propria qualifica; nessun PASS
pregresso diventa automaticamente PASS del nuovo SHA.

| Scenario | SHA / comando / ambiente | Esito ed evidence | Prossimo gate |
|---|---|---|---|
| Cassa precedente | d1b2bc3, pacchettoa07430ce/payload6b44ffe3; ASUS Integrated12 eFinal60 | PASS31cicli/max102.510ms e151/max97.826ms, budget63970e30 invariato | qualifica sul pacchetto finale composto |
| Windows composto precedente | 791fc4f7, CI43050673848 eSecurity43050674075 | PASS16/16; U19Win791Remote01 verifica artifact13870814586 SHA256b6537e61/8entry, fingerprint5797937a; target396/396 incluso nel perimetro | non trasferire il PASS al successore |
| Successore059 | CI43057322574, Security43057322848 | Windows-All FAIL15/16, Core e altri14passi PASS; fallisce solo Load. U19Win059RemoteFail01, artifact13874022646 SHA2565b2dbb24/8entry verificati | copertura del campionatore da chiarire |
| Load059 | HIST_LOAD_50K, stesso workload e limiti | query283.8887ms contro500 PASS; max Dispatcher osservato13.8919ms; 393/394campioni e1drop nella prima fase analysis: gate Dispatcher FAIL. Nessuna causa host/UI dedotta | diagnostica limitata, zero tolleranze aggiunte |
| Diagnostica Load | componente02f51b9, composto ca83a08 | U19LoadGap01 PASS11/11,18.8606s, fingerprint6285d981 sul WIP stabile prima del commit;83check puri PASS. Nuovi testimoni wait/memory/GC, senza cambiare clock, intervalli, priorità, soglie o workload | causa del vecchio drop UNRESOLVED; ripetere Windows-All successore |
| API/BFF precedente | Admin3b675654, CI43059296486 eCF43059299287 | Verify, SQL e build PASS; U19Admin3b01 verifica58file, lock/config/treeeb1cd04e. Artifact13873217236 SHA2566477fc81. Entrambi i deploy SKIPPED | Worker impacchettato, servizio operativo non attestato |
| SQL composizione | replay3b, artifact13872648476 SHA256e7232bb1 | PASS94file/5307assert +3race preesistenti; receipt verificata U19Admin3b01 | nuove race recovery NOT_RUN |
| Prova autenticata01 | client059/Admind4f02e5, run43058834395 | SQL PASS; scenario FAIL initial_baseline/history_response_invalid, cleanup naturale. Artifact13874140882 SHA256508ae8cf, U19AuthenticatedRemote01 | FAIL conservato, nessun roundtrip PASS |
| Prova autenticata02 | client413eb4d/Admin9c85b2c, run43062172147 | SQL PASS; baseline contextHTTP200; Storage.info realeHTTP400 con machine code404 e StorageApiError; clientFAIL come sopra. Artifact13875071522 SHA256c4ade33c verificato U19AuthenticatedRemote02 | correzione specifica del confine Storage |
| Correzione Storage | Admin996cd7b, sorgenti stabili520c480a | helper riconosce soltanto SDK StorageApiError400/code404 oltre al404già previsto; generici400/auth/rete restano FAIL. U19AuthenticatedContract03 typecheckPASS,42targetPASS inclusi nei1618foundationPASS/7SKIP su1625 | E2E reale successore, CI e packaging propri |
| Harness successore | client92c3517, schemaasus019-authenticated-history-v2; incluso c9a3355 | build10PASS0warning/error; baseline confrontata con economia originale e audit immutabile; implementati import duplicato, Undo con proprietario originale, correzione dopo Undo e confronto esatto identità/ticks/gross/fee/VAT/net. Il reader SQLite si chiude prima di Apply; stop supervisor atteso e limitato | esecuzione distribuita di questi casi NOT_RUN |
| Storico bilaterale completo | A→server→Admin già aperto→B→A, origine Admin in writer mode prevista | roundtrip autenticato non ancora PASS; ACKperso/riavvio reale/pull prima ACK/revoca/conflitti e race recovery ancora aperti | completare servizi reali di test e casi residui |
| Pacchetto e consegna | release05943057309469 in packaging all'ultima osservazione, Core PASS; QAbundle43057597754 | QA40file verificati artifact13872765644 SHA256ce6e92f8, U19Cart05901; nessuna nuova installazione. Integrated/Final60 finale NOT_RUN | pacchetto finale, binding, soaks, backup/rollback e consegna |
| Vincoli esterni | installato2ad42ca, profilo Minimondo e7originali conservati | 200%/2880×1800 invariati;100/125 USER_DEFERRED; Win7fisico BLOCKED_TARGET; GetnetC MISSING | nessuna attestazione sostitutiva |

Il vecchio c88 Windows-All FAIL2/16 rimane verificato in U19WinC88RemoteFail01;
SQL63f/9fb e fallimenti precedenti restano nella cronologia. Anche i FAIL DNS
di Chromium (CIc351 e run43056918378) sono conservati; quest'ultima non ha
eseguito lo scenario autenticato e fallisce anche l'export per evidence assente.
La CI3b usa Ubuntu24.04 e il Chromium Playwright vincolato dal lockfile; il
servizio E2E separato registrera versione/hash del Chrome alla fase browser, ancora NOT_RUN nelle due prove fallite.

Il client trasferisce solo l'archivio selettivo al BFF configurato, con
dimensione/hash/lease verificati; il backup completo rimane locale. Nessuna
allowlist client per Storage, ricevuta fittizia, nuovo grant SDK o v1 fallback.
Copertura baseline verificata e replica completa drenano i soli legacy senza
riscriverne l'audit; journal/witness v2 incerti restano bloccanti.
Nessun merge main, deploy, mutazione reale o DONE. Qualifica mobile e Win7
fisico restano separate; il test Core/Data non attesta WPF o periferiche.


</details>

<!-- ASUS019_CURRENT_START -->
## Qualifiche precedenti — evidence conservate

Due consegne: cassa quotidiana e storico bilaterale. EXECUTION ACTIVE.
L'utente autorizza tutti i file necessari; soglie e carico restano invariati.
Nessun merge main, deploy, installazione o nuova mutazione reale in questo checkpoint.

| Scenario | SHA / comando / ambiente | Esito ed evidence | Prossimo gate |
|---|---|---|---|
| Cassa PR136 | d1b2bc318c8a0aecce9543dc6a96f9a1aaf7cfc3; CI42967023331, Security42967023327, WPF42967096688, Release42967017718 | PASS; U19ReleaseD1b201 verifica41file/110checksum, installer a07430ce e payload6b44ffe3 | integrare nella coppia finale prima della sua qualifica |
| Cassa ASUS disponibile | d1b2, Integrated12 eFinal60, budget63970e30 invariato | PASS31cicli/max102.510ms e151cicli/max97.826ms; U19D1b2AsusI01/F01, cleanup naturale | Windows11 disponibile, non Win7 fisico o coppia finale |
| Cassa hosted | d1b2, run42970975541; U19D1b2Hosted01 | PASS Integrated33cicli/138.340ms eSoak166cicli/56.051ms; artifact13837190884 digestd100083f39920a95f54b7132f6c9ed4622ce7ea3bde00991c26ae23295106a47 | mantenere distinto da ASUS |
| Windows135 qualificato | 4f07a2d84f06ec065df04021bc62c91b050c4d78; CI43018907144 eSecurity43018907124 | PASS16/16, Load11/11; U19Win4f07Remote01 verifica8entry, artifact13854594896 digest89cab51916750ea5ddad68df4e6831bacec5926a69a99df4c5cd2b6674863c15; fingerprint1624e4ad8a5b45cedca6050129610c3f439af0692444f58b1027964081ce5022 | la baseline locale privata non e ancora integrata in questo candidato |
| Load egeometria cee originali | cee9160 CI43009830230 FAIL14/16, U19WinCeeRemote01; locale U19CeeGeometry01 | misure geometriche PASS ma ConfirmOnce false: fixture loopback rifiutava local_review; caldo532.6015/496.8929/500.4045ms oltre500 | FAIL conservato; nessuna attribuzione hardware/scanner |
| Correzione successore4f07 | celle giorno/ora aggregate una volta; fixture ammette review gia implementata | target23/23, U19CeeGeometry02 PASS22misure/confirm-once/cancel; remoto Loadquery286.2606ms, cold310.1317ms, misura completa valida | stessa soglia500 e stesso carico; non trasferire il PASS al futuro candidato finale |
| Recovery eFAIL storici | timeout fixture TextBoxView/timer2s identificato, U19RecoveryHeap03/04 eRetirement04; focus149FAIL258.573/258.382ms | successori mirati eWindows-All PASS; gate890 fermo17 dopo18 corretto; originali b976/cee eFAILfixture conservati | nessun retry live o cambio arbitrario5/3 |
| Admin151 qualificato | 221573a770b5838796dd8f91f656dc85799a7a1d; CI43009841014, Cloudflare43009840957 | PASS packaging; deploy SKIPPED; U19Admin22101 verifica58file/lock/config/tree; Worker13849982775 digestb6181d2b28a917cbfdfecd2a5fd42bcd4adcd650551d5adf95e635f307808f98 | Worker impacchettato distinto dal servizio operativo |
| Outcome/concorrenza SQL integrati221 | replay90file/5164assert e3scenari reali concorrenti; U19Admin22101 | PASS artifact13849964696 digeste52825578ecea26d9df1a0528735577fc09b2c8fbef4a07aad28a3146f632cd4; nonce/review/witness eoriginali ricevute vincolati | API pubblica v2 ancora chiusa |
| Baseline server privata | 92e725e32a6b77777821e3c1c85cfb63abe3573b; replay43019401370; migrate11650 e121621 additive | PASS92file/5225assert e3concorrenze; U19BaselineUploadSql01 artifact13855186857 digest29a5057ba72a8b13e4b490c08d0a5345de1068196e19fcd3f2876db7052a824a | context, evidenza legacy eupload idempotente privati; nessun nuovo endpoint esposto |
| Contratti Node baseline | 4906bb8ad5acc79237d2076b3d184e57dc1cb82b | typecheck PASS,9mirati PASS, foundation1588PASS/7SKIP su1595; U19BaselineCodec01/02; unicoFAILCRLF corretto con blob Git esatti, pin invariati | source integrato identico nel codepoint5442650, nuova CI dopo commit documentale |
| Baseline client privata | ce9e583567885f541b080725a7e44f76d73d3f13; C:/Dev/A19BaselineCompose01 | Store22689e7 target344/344 eCloud1/1 net48/x86; orchestratore83/83, U19BaselineFlow01; vecchi333 eFAILpin78 preservati | collegare host/trasporto; cambio sessione primaApply richiede resume/review verificato |
| Lettura canonica privata | marker sticky solo dopo ricevuta baseline e replica completa compatibile; ever_web/pin1–19 invariati | tre prove lettura/rollback PASS incluse nel target; fonte canonica senza doppioni/eco, copia incompleta non pubblicata | nuova20 privata SHA c6527cbfdf9d438da6852f9a3f44b4e24340d60ad3d2800e6fa66ceeea50f16f; non e migrazione distribuita |
| Storico bilaterale | grant/outbox/witness qualificati, baseline in composizione | NOT_RUN E2E autenticato reale A→server→Admin aperto→B→A eorigine Admin | completare baseline/lease recovery, SQLv2+Node boundary+WPF, poi qualifica coppia esatta |
| TASK094 cleanup | sola lettura dei10conteggi fixture gia autorizzati | PASS tutti0; vecchio authFAIL/cleanupNOT_ATTESTED conservato | nessun retry live cieco |
| Consegna eprerequisiti esterni | installato2ad42ca, profilo Minimondo/quattro lotti legacy/setteXLSX invariati | Win7 fisico BLOCKED_TARGET;100/125 USER_DEFERRED,200% conservato; Getnet modernoC MISSING | niente equivalenza fra build/installazione, CI/deploy, Windows11/Win7 |
| Mobile TASK146 | coordinamento nel commento esistente6722613449; nessun AndroidTEST verificato qui | nessun nuovo PASS mobile o targetinventato | aggiornare il solo prefisso, preservando tutta la coda Mac |

Il trasferimento usa soltanto l'archivio selettivo dei sette gruppi di tabelle;
il backup SQLite completo rimane locale. Upload, start, review eApply mantengono
identita persistenti. Fonte eautorizzazione sono ricontrollate; nessun lock SQLite,
gate commerciale o manutenzione backup attraversa HTTP. Nessuna nuova coda/timer.
La prova con risposte controllate e distinta da server/Worker/browser reali.
Non esporre la v2 incompleta e non dichiarare DONE prima dei gate critici.
Checkpoint UTC 2026-11-11T12:49:44.409904+00:00.
<!-- ASUS019_CURRENT_END -->

<details>
<summary>Checkpoint precedente conservato al 20261111T012245Z</summary>

## Convergenza corrente — stessa Execution ASUS-W7POS-019

Addendum di convergenza recepito: due consegne, cassa quotidiana e storico
bilaterale. Nessuna funzione nuova; la precedente matrice di altri domini resta
inventario storico e non amplia il lavoro corrente. Stato ACTIVE, nessun DONE.

| Scenario | Sorgente / binding | Comando / ambiente | Esito | Evidence | Prossimo gate |
|---|---|---|---|---|---|
| Cassa PR136 |149c978c2c169dd8640e7e2d13c0e6ca151f2884; Release42889654511 | CI/Security/WPF/Release e pacchetto esatto | PASS build e Integrated12; FAIL soak60 |42897760117, U19CartSoak14901, artifact13807390665 | attribuire i cicli116/118, correggere causa, nuova qualifica |
| Focus, attribuzione | prodotto149 invariato; solo harness6533c71d68cb15d30ad94f7b809e54a37a721c0e | run-cart-performance Diagnostic, stesso carico/budget; hosted42924975778 | RUNNING, non qualifica | U19Focus149Local02; product-binding; run42924975778 | leggere massimi per ciclo/fase/callback; nessuna causa scanner presunta |
| Windows precedente | fbdde199 + fingerprint6c170539da7fd1af2ba72d0236ef5edad140aab9c8d2f2308eff050fc4a6dd99 | Windows -HistoricalSalesMode All | FAIL conservato | U19CloudAll01 | successore completo su checkpoint nuovo |
| Successori fixture Windows | stesso ramo135 + delta proprio | geometry/shell/Smoke/RequiredGates | PASS9.698s /25.519s /8gruppi52assert55.430s /50su50 | U19CloudFunctionalFix01 e04, U19CloudSmoke04, required-gates05 | non sommare con la suite precedente |
| Recovery | diagnosi fixture; prodotto invariato | net48/x86, cinque aperture, deadline60s e8MiB invariati | FAIL iniziale5contro3 conservato; successore mirato PASS40.545s | U19RecoveryIdentity01, U19RecoveryHeap03, U19RecoveryRetirement04, U19RecoveryHeap04 | suite Windows completa; rilascio assoluto/Win7 non attestato |
| Owner correzione Windows | ramo135 + delta, riferimenti lotto/Apply e cancellazione spool | Ledger64test, mirati4, build19, CloudView06 | PASS; i4 sono sovrapposti | U19CloudSync28/30, build19, CloudView06 3.382s | owner canonico server e ammissione v2 |
| Admin qualificato | e67008ad7668b9ca91b45432e3031427e190cf71; merge CI d137c89561c50a5c7f491fd1b3a0d8c40c2963fb, stesso tree626493033d90264d81fb814e08cad6030d701fdd | CI42914874218; Cloudflare42914874236 | PASS CI e packaging, deploy SKIPPED | U19AdminWorkerE67001:58hash verificati, artifact13809111740 | non confondere bundle con servizio operativo |
| Admin delta locale | owner lotto/Apply, permessi marker per dominio e503 separato | typecheck03/lint01; foundation06 | PASS1547,7SKIP preesistenti,0FAIL su1554 | admin-foundation06.private.log; 109.174s | CI sul commit successore |
| SQL owner shape + marker | 81116906db3f6db32aea989d542ee7e80fa21f3f; migration04af62ef/e9af34c2 | replay canonico Linux isolato42925339887 | PASS80file/4787assert | U19SqlMarker04/complete.json | nessun SQL linked o deploy |
| SQL owner canonico | isolato186f4c67f2aec07847d67ba9cf636aa02d131df2; migration20261110234619 SHA d46c65c96b13585f12994fc360f86cdb847a5508979301e7ab3631c6a4bc6898 | replay isolato42926950143 | RUNNING; parse/18assert plan PASS locale | run42926950143 | raccogliere esito; helper ancora privato |
| Catena v2 e baseline | v1 ancora chiamato dal WPF; API v2 chiusa | Apply/outbox componenti, preparatore e semantica privati | INCOMPLETE, E2E autenticato NOT_RUN | stesso diff135/151 | baseline verificata, autorizzazione contenuto, consegna/ricevuta, WPF, lettura globale |
| Cleanup vecchio TASK094 |42896389034 | nessun retry live | FAIL auth_denied; cleanup zero NOT_ATTESTED | admin016-task094-staging.private.log | recuperare soli ID fixture e verifica read-only, niente purge |
| Mobile / Win7 fisico | handoff TASK146 esistente / target mancante | nessun build mobile; hardware non disponibile | NOT_RUN / BLOCKED_TARGET | commento6722613449; piano Win7 | nessun PASS trasferito da Windows11 |

La diagnostica breve149 attribuisce un'attesa407.506ms alla fase setup, con
callback Render sovrapposti225.670/80.837ms e barcode già focalizzato. È un
campione di avvio locale, non la spiegazione dei massimi258.573/258.382ms dei
cicli116/118 hosted. Le soglie250ms e il workload congelato restano invariati.

Recovery: identificate singolarmente le finestre chiuse, senza HWND, owner-list,
focus o timer prodotto attivi. Il dump privato sintetico individua una catena
DispatcherTimer.FireTick → TextBoxView.OnThrottleBackgroundTimeout → TextEditor /
TextAdaptor / DataGridCellItemAutomationPeer → colonna/DataGrid → Recovery.
Il timer interno net48 dura2s: il vecchio drain poteva terminare prima. Ora
entrambi i fixture sono raccolti alla stessa età minima2100ms dopo l'ultimo
Closed, con gli stessi10 tentativi, deadline60s e confronto memoria8MiB.
Nel successore recovery1/control5; il dump successivo non trova root path verso
le istanze Recovery. Il PASS mirato non cancella il FAIL originale e non qualifica
rilascio assoluto o hardware fisico. Riferimento tecnico Microsoft:
https://source.dot.net/PresentationFramework/MS/Internal/Documents/TextBoxView.cs.html
e https://devblogs.microsoft.com/dotnet/wpf-on-dotnet-7/ . Nessuna dipendenza
aggiunta al prodotto: lettore ClrMD3.1.512801 solo nel tool privato di diagnosi.

SQL marker:42921661653 FAIL per evento fixture forgiato;42923536204 FAIL per
tentativo di revocare il ruolo pos_admin canonico. Successore usa evento reale
del trigger catalogo e ruolo limitato manager modificabile: nessuna protezione
indebolita.42919972944 verde ma head obsoleto non qualifica questi20assert.

Installato verificato2ad42ca,41file, profilo/ledger Minimondo e quattro XLSX
importati conservati. Nessuna installazione, nuovo Apply/Undo/vendita reale o
scrittura del profilo.200% preservato;100/125 USER_DEFERRED, Win7 BLOCKED_TARGET,
Getnet moderno prova C MISSING. Nessun merge/deploy/cutover. Il Worker storico
operativo resta un gate distinto dal Worker web impacchettato.

Checkpoint UTC 2026-11-10T23:54:20.160630+00:00.

</details>

<details>
<summary>Checkpoint precedente 10 novembre 22:27 UTC, conservato alla convergenza</summary>

## Stato corrente — Execution bilaterale attiva, checkpoint 10 novembre 22:27 UTC

Admin151/43f668df + diff: lettore movimenti nominativo, filtri per data commerciale,
cursore vincolato a dataset/revisione/filtro, totali globali filtrati e importi
esatti/null preservati. La pagina mostra provenienza e prova temporale; il JSON
esporta la pagina filtrata con totali globali, senza dichiarare un export completo.
Il polling globale ShopShell esistente (3s, backoff fino30s) pubblica un marker
storico separato; lease staff ricontrollato, nessun timer per vista o audit ogni poll.
Richieste cancellabili, nessuna sostituzione di file/input/focus o posizione commerciale.

Typecheck07/lint02 PASS. Foundation05:1527PASS/7SKIP preesistenti (1534 totali,
91.408s); browser sintetico desktop13PASS6.3s, tablet5PASS2.6s, ritocco etichetta
netto desktop5PASS2.5s. Non sono E2E autenticati o prova di stile del CSS distribuito.
SQL isolato e099bce8918caabcc0b766132a5bfb86bbbf4f4f /42912319575 PASS:
78file/4755assert, artifact13806949466, digest
30b7b9d2f1aa7c2c26491ae38a6349dd2fb2dd0083396582a1b0a507e8a82b6a.
Include le primitive private preparazione/semantica e il lettore/marker37assert.
Tentativi42910563273 FAIL pin CRLF/LF e42911042429 FAIL fixture revoca senza
revoked_at conservati; corretti pin LF e fixture, nessuna protezione indebolita.
Vecchio job live TASK094/42896389034 auth_denied FAIL resta senza retry;
cleanup finally eseguito, conteggio zero finale NOT_ATTESTED.

Client135/fbdde199 + diff: richiesta foreground nella stessa gate prioritaria e
credenziali correnti; preparazione paginata riprendibile20PASS13s; revisioni
monotone/status34PASS23s. CloudView05 PASS3.985s, conferma325ms, copia489ms,
notifica revisione nota332ms: convergenza di componente, distinta da Input250ms.
Windows-All U19CloudAll01 fingerprint6c170539da7fd1af2ba72d0236ef5edad140aab9c8d2f2308eff050fc4a6dd99
FAIL: Core/Data, build, gate e CloudSync PASS; functional19/22 e Smoke50/52
prima del deadline FAIL, Load NOT_RUN dependency_failed. Sorgente invariata nella run.
Geometria corretta nel fixture tramite gate foreground reale loopback: PASS9.698s.
Shell mirata successiva PASS25.519s dopo drain della lettura automatica prima
Close:70checkpoint, stesso budget60s. Tre TIMEOUT diagnostici intermedi conservati.
Recovery mantiene FAIL per weakref chiuse5 contro3 del controllo; nessuna deroga.
Windows-All finale sul nuovo delta resta da completare.

PR136/149c978 resta congelata: CI, Security, WPF e Release PASS;41/40file e34comuni
identici verificati. Qualifica42897760117: Integrated12min PASS, soak60min FAIL
su158cicli, ambiente valido, focus258.573/258.382ms nei cicli116/118 oltre250ms.
Artifact13807390665 digestada85a03ea8e2dc64c86be5fcd8408dcb664eb8c83ffd755bd0fbde72eda4387,
evidence U19CartSoak14901. PR136 aggiornata: nessun retry cieco o budget allargato.

Residui critici aperti: ammissione v2 completa e ingest/ricevute esposte, baseline/
backfill del lotto originale da backup attestato, collegamento WPF al grant v2,
lettura canonica globale e registri inter-postazione; E2E autenticati bilaterali
per dominio NOT_RUN. Il WPF usa ancora grant v1; preparatore v2 non collegato.
Worker operativo/deploy/SQL live/cutover NOT_RUN. Installato2ad42ca e ledger
Minimondo invariati, nessun nuovo Apply/Undo/vendita reale.200% preservato;
100/125 USER_DEFERRED, Win7 BLOCKED_TARGET, Getnet moderno C MISSING.
Stessa ASUS019 ACTIVE: nessun merge o DONE; risultati Windows non attestano mobile.

</details>

<details>
<summary>Checkpoint precedente 10 novembre 21:00 UTC, conservato</summary>

## Stato corrente —5d8e704 qualificato, consegna reale aperta

Stessa ASUS019/R3 ACTIVE: Windows-All15/15 PASS (Core1745, functional22,
crash5, smoke8/52, load11), quattro workflow exact-SHA SUCCESS, pacchetto41/QA40
verificati e34 byte comuni identici. Smoke scaricato8/52 PASS49.397s.
Geometria nativa fixture200% PASS22; nuova matrice100/125 e basse risoluzioni aperta.
Consenso UAC annullato prima dell'avvio: installato ancora a8286c6,10/10 file del
profilo invariati. Installazione e percorso reale5d8e704 NOT_RUN, attesa operatore.
Backup prodotto e riconciliazione registro reale PASS; una ricevuta/un lotto attivo,
nessun nuovo Apply/undo. Win7 fisico NOT_RUN/BLOCKED_TARGET; kit5d8e704 pronto,
vecchi kit invariati. Moderni C MISSING e CSV_INPUT BLOCKED_INPUT separati.
Admin B/C soltanto Planning pubblicato d202332, nessun backend cambiato.
Dettagli e riferimenti: [report canonico](../../reports/ASUS_W7POS_019_RETRY_E2E_RESULT.md).
Le sezioni sotto sono checkpoint storici, non lo stato corrente. Nessun DONE.

</details>
## Raccordo corrente — a8286c6 qualificato e installato, 9 novembre 2026

Windows codice/pacchetto a8286c669a7c0d8ec4f82e1e7a31fc1b5d46eed3,
versione1.0.0-dev.a8286c669a7c, installato41/41 con profilo/trust conservati.
CI42673051923, Security42673051757, Release42674141171 e WPF42675026366
SUCCESS: canonica15, functional20, foundation5/37, smoke8/52 e load11 M/E/S=true.
QA scaricato40 verificato, smoke effettivo49.678s PASS8/52;34comuni QA/prodotto
identici. Prima failure ca4e43760s, full locale Recovery/path/ownership e primi
GitHub Internal server error conservati. Nessuna soglia o assertion allentata.

Admin148 autenticato PASS, Workera68ebec2/deployment1cd437f1 invariati al
refresh readonly; nessun nuovo deploy/SQL/grant/secret. Nuovo EXE aperto
manualmente dal collegamento ordinario sul profilo NEW_SHOP_019, verificato
prima del PIN; login/empty/workbench/import PASS. Backup prodotto del ledger
prima update, profilo byte-invariato durante installer, nessun auto-start;
riconciliazione logica dopo login PASS. LIVE_REOPEN_PENDING: ricevuta/report,
nuovo CSV e reimport duplicate-only sul successore. Nessun undo reale.

Form vuoto100/125% osservato intero, reverse Tab/Enter/Escape osservati;
ripristino200% pendente. A/B VERIFIED, C modern MISSING, CSV_INPUT
BLOCKED_INPUT, Win7/periferiche NOT_RUN/BLOCKED_TARGET già dichiarato.
Kit distinto a828 preparato con byte qualificati;182 resta invariato.
Stessa019/R3, software REVIEW, overall ACTIVE, stessa nota PR29.
[Matrice corrente e prime failure](../../reports/ASUS_W7POS_019_RETRY_E2E_RESULT.md).

## Storia — legacy reale e conferma in qualifica, 9 novembre 2026

Windows ca4e437 non installato: CI42667255330 FAIL per processo smoke oltre60s,
pur con8/52 assert PASS; WPF20/20, Security e Release PASS. Test successor
con una fixture riusata PASS8/52 in50.329s, stesso codice prodotto e soglie;
nuova qualifica exact-SHA in corso. Local full Recovery19/20 e problemi di
receipt path/work ownership restano FAIL/partial conservati, senza retry
Recovery o guard indeboliti. Utente conferma scala100%, prove native pendenti.

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
