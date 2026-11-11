# ASUS-W7POS-019 — Importazione storico Getnet

Il perimetro corrente dell'ultimo addendum di convergenza comprende cassa
quotidiana e storico bilaterale. La matrice degli altri domini in
[CLOUD-CONTINUATION](CLOUD-CONTINUATION.md) resta inventario storico.
La tabella corrente distingue qualifiche effettive, fallimenti e gate aperti
di PR135/Admin151 e della consegna cassa136. I riepiloghi precedenti sono
conservati come storia. Installato verificato2ad42ca; nessun rollout o DONE.

<!-- ASUS019_COMPOSED_CURRENT_START -->
## Composizione verificata — 20261111T201629Z, stessa ASUS-W7POS-019

Execution ACTIVE: cassa quotidiana e storico bilaterale. Tutti i file necessari
autorizzati dall'utente. Qualifiche riferite agli SHA e agli artefatti seguenti;
documentazione in checkout separati, candidati delle run attive congelati.

| Scenario | SHA / comando / ambiente | Esito ed evidence | Prossimo gate |
|---|---|---|---|
| Windows composto | 6cdaa2988030b3a140493d6239c8d5007603f55c; Windows-All43080480557, Security43080480554 | PASS16/16 e due job Security PASS; U19Win6cRemote01 verifica8entry, artifact13886737547 SHA2569de74b63, fingerprint6ea13878 | consegna e soaks propri |
| Load composto | HIST_LOAD_50K, stesse soglie/carico | PASS11/11:571/571campioni,drop0,query391.7479ms/500,cold420.5537,maxDispatcher12.3842ms | non attribuisce la causa del vecchio gap059 |
| Release esatta | 6c, Release43080678943; U19Release6c02/download-verification.json | PASS41file prodotto/110checksum; versione1.0.0-dev.6cdaa2988030, installer69c51811,payload0b88ee0f; development-unsigned, non installato | installazione/consegna separata |
| Cassa ASUS sul pacchetto | QAartifact13884426672/da3147db,40file; Invoke-Composed6cAsusQualification02.private.ps1 | Integrated12PASS31cicli,focus29.410/scroll194.120ms,zero drop; U19Cart6c01/integrated-verified.private.json. Final60RUNNING da20:05:13Z, budget63970e30 invariato | risultato Final60 proprio, nessun PASS anticipato |
| Cassa precedente | d1b2bc3,pacchettoa07430ce/payload6b44ffe3 | ASUS PASS31cicli/max102.510ms e151/max97.826ms | risultati storici, non trasferiti al nuovo pacchetto |
| Windows predecessori | 622PASS16/16;0e CI43078277359 eRelease43078654263 | 0e FAIL13PASS/1FAILbuild/2NOT_RUN, U19Win0eRemoteFail01; causa due API moderne nel solo helper condiviso net48, fix84b3015 incluso6c; intera soluzione Release PASS con solo CS8892 preesistente | FAIL conservato, non sommato ai successori |
| Baseline canonica | 17f7081 e5512319, inclusi6c | RED conservati eGREEN74/74 (include51): prova applied originale dopo cambio epoca; delta vuoto avanzato richiede snapshot tramite fallback esistente, schema20/trigger invariati. WPFnet48x86 e dialog standards PASS | E2E completo ancora aperto |
| Admin pubblico precedente | 8ccb5758; CI43066464128,CF43066464229 | Verify/SQL/build PASS, deploySKIPPED; U19Admin8cc01 verifica58file/tree/lock/config, Worker13876372298/7a032d48 e replay13876623226/14fb3bc5 | Worker impacchettato, servizio operativo non attestato |
| Admin candidato nuovo | b61ff823db68f37689a997604edda820256bc7f5, PR151; CI43091893821,CF43091893786 | Verify/SQL/build PASS, deploySKIPPED. U19AdminB6101 verifica58file/tree05d3cb0d/lock/config; Worker13889521659/5dd48490 e replay13889457956/8b33dab0,94file5307assert+3racePASS | Worker operativo e percorso finanziario non attestati |
| Sessione web reale | Remote10,run43085382627,Admin7d371d9/client6c | FAIL403 history_access_denied; cookie presente, propria sessione revocata, credenziale valida e permesso presente. U19AuthenticatedRemote10 artifact13887037812/d9db3c7e; cleanup naturale | login12h oltre credenziale+1h riprodotto e corretto sotto |
| Regressione scadenza | auth b61ff; U19StaffExpiryContract01 ecommitted-binding.private.json | RED2FAIL/4PASS, GREEN6/6: scadenza min(12h,credenziale), guardia runtime invariata; scaduto negato, sessione troppo lunga ancora negata. TypecheckPASS,48targetPASS,1624foundationPASS/7SKIP; sola rinomina loader test per ESLint poi unit6/6 e lint PASS | conferma browser/API reale nel successore |
| Recovery prima prova | Remote11,run43085389040,Admin9f50f08/client938305e | FAIL prima del witness: adattatore QA cercava receipt_json nella risposta Apply già ricevuta. U19AuthenticatedRemote11 artifact13888091004/194184e4; tre client PG rollback/uscita naturale0 | b61ff cattura testo originale dal DB nella stessa transazione col lock, nessuna ricevuta ricostruita |
| Recovery Remote12 | Adminb61ff/client938305e,run43091251050; U19AuthenticatedRemote12 artifact13889193349/923f8b7b | overallFAIL; prima gara Apply/recovery PASS con advisory lock osservato e ricevuta originale. Seconda raggiunge review finale dopo due recovery, FAIL BASELINE_REVIEW_REJECTED; tre PG rollback/uscita0. Il test non ripristinava SET LOCAL dopo COMMIT | correggere soltanto contesto QA e attestarlo; browser non raggiunto |
| Successore Recovery | Adminb6304fe/client938305e,run43094416569 | RUNNING; stesso ruolo gateway,timeout8s/lock6s/search_path esplicitamente ripristinati e osservati dopo ogni COMMIT, SQLSTATE chiuso; digest completo righe economiche prima/dopo. src/SQL/lock/config uguali b61 | due gare nuove separate dalle tre canoniche, poi browser reale |
| Controllo browser indipendente | Adminc1b5fc1/client6c,run43094421902 | RUNNING con harness browser preesistente; prodotto/SQL/config esattamente b61. Src prodotto6c e938 identici; nessuna esclusione nascosta dei casi nella prova completa | confermare sessione/API e scenari finanziari senza dipendere dal test Recovery |
| Scenari finanziari distribuiti | harness Core/Data+Next+Postgres+Storage+Worker+browser, solo fixture proprie | implementati ACK perso dopo ricevuta HTTP, pull prima ACK, nuovo processo sugli stessi DB con capability originale, duplicato, Undo/correzione, origineAdmin in autorita web, revoca/conflitto; percorso completo non qualificato | confrontare7campi economici eassenza di effetti nativi/eco |
| Vincoli esterni | installato2ad42ca,ledger Minimondo e7originali conservati | scala200%/2880x1800 invariata;100/125USER_DEFERRED;Win7fisicoBLOCKED_TARGET;GetnetCMISSING;nessun Apply reale nuovo | prova fisica e consegna non sostituite da CI |

Le run precedenti Remote01-09 e il FAIL Load059 restano nella cronologia;
nessuna soglia, campione o timeout viene allentato per ottenere PASS. Le sorgenti
dei candidati attivi e gli SHA privati precedenti sono riproducibili. Gli artifact
sono verificati per digest, contenuto e binding, non soltanto per esito del job.

Il solo archivio selettivo passa dal BFF; backup completo locale, nessun segreto
nel browser/evidence. Core/Data con servizi reali non attesta WPF, periferiche o
Win7fisico. Nessun merge main, deploy, installazione o DONE. Checkpoint privato
201522Z aggiornato; PR135/151 eTASK146 conservano il proprio storico integrale.
<!-- ASUS019_COMPOSED_CURRENT_END -->

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

## Stato corrente — Execution bilaterale attiva, consegna Windows separata

PR136/149c978: CI42884098486 e Security42884098503 SUCCESS,15/15 passi e
functional22/22. WPF42889661815 e Release42889654511 SUCCESS. Scaricati/verificati
41file prodotto,40QA,34comuni identici e110file checksummed/provenienza.
Evidence privata U19R149Verified01; Integrated12min+soak60min42897760117 in corso,
non PASS. Installato2ad42ca e profilo conservati, nessuna nuova installazione,
vendita o Apply/Undo reale.200% preservato;100/125 USER_DEFERRED.

PR135/fbdde199 con diff locale non pubblicato: outbox atomica e lineage restore,
ACK recuperabile, replica fuori gate cassa, verifica hash in lettura WAL e breve
pubblicazione pointer; richieste operatore sullo stesso gate HTTP2 con credenziali
correnti, cancellazione/drain posseduti e fence generazione/manutenzione.
Foreground/gate/bus33test PASS; ledger/replica/notifiche/stato67test PASS. UI
separa salvato qui, pending, confermato server, attenzione, originali da registrare
e ultima copia ricevuta, senza dedurre allineamento dal badge Online.

WPF CloudView04 PASS3.045s: conferma visibile in253ms e copia in386ms, selezione e
conti locali invariati. Smoke03:8/52PASS57.380s, budget60s invariato; required
50/50PASS; build13 net48/x86 zero warning/error. Fingerprint delle due prove WPF
`14b781992c26d4501e6c8bc3a361c59e2d9ac7794b26e050a1da351c4be44309`.
253/386ms sono convergenze di componente, non Input250ms né rete reale.
I FAIL iniziali restano conservati nel CLOUD-CONTINUATION.

Admin151/43f668df con diff: contratto17/17, boundary36/36, typecheck03 e
foundation03 1484PASS/7SKIP preesistenti. SQL isolato0162205:75file/4664assert PASS;
Verify verde ma vecchio job staging TASK094 FAIL auth_denied, nessun retry.
Cleanup nel finally; conteggi finali zero non attestati. SQL preparazione privata
984d6e4/42897836745:76file/4694assert PASS in modalità isolata senza staging.
La preparazione non crea grant o movimenti: API v2/ammissione/ingest/ricevute,
baseline/backfill del lotto originale, fonte globale canonica e Admin movimenti/
aggregati restano incompleti. Nuovo Windows-All finale ed E2E autenticati bilaterali
per dominio NOT_RUN. Nessun deploy, SQL linked, worker operativo o cutover attestato.

Handoff TASK146 aggiornato nella nota esistente; storia conservata, nessun PASS
mobile. Win7 fisico BLOCKED_TARGET e Getnet moderno C MISSING. ASUS019 ACTIVE,
nessuna nuova task, merge o DONE. Matrice, failure e residui nella stessa
CLOUD-CONTINUATION.md; i checkpoint più vecchi conservano la propria attribuzione.

</details>
## Successor after canonical01 failures — 9 November, ACTIVE

Unpublished local e6aa0e6 canonical Windows-All completed13/15 PASS, source
fingerprint982eb7fed0618d54a346ca76e19d5453ca2af223930afaa9e3480d10015cc690
unchanged, all owned cleanup confirmed. Core1745/1745, required50/50, smoke8/52
(48.971s parent) and load11/11 PASS. Functional21/22: ordinary MainWindow21asserts
PASS but worker failed on database_authority_connections_active at final owner
disposal. Crash2/5 complete, process admission timed out30s after5checks: cart
maintenance probe did not finish. Both failures retained; no qualification/install
claimed for e6aa.

Successor correction: successful POS initialization is once per VM, active init
is joined and disposed continuations stop. Fixture drains only its owned sync/
initialization/backup work before releasing authority; the guard is unchanged.
Shared report importer again marks the current commercial VM busy and restores
its previous state/focus. The maintenance fixture explicitly installs/restores
its valid operator holder and yields fairly within its original8s deadline.
Final authority/version is rechecked after the last awaited query and before
login transfer; a newer-operator injection proves no stale forced logout. Empty
status preserves unrelated warnings; menu uses localized sales/payment summary.

Focused successor PASS16.544s, including real MainWindow, ready scanner/cart,
button/X/Escape, filters/session, single initialization, no-history operator,
stale callback and real cart maintenance/cancel/invariants. Evidence pointer
ux-focused-pre-freeze-02-root.private.txt; required50/50 in
ux-required-gates-03.private.log. Earlier post-canonical focused failure at8s
initialization and the first wrong-setting warning fixture remain preserved.
Native200% synthetic rendered shell/import evidence is private; rendered images
are distinguished from native HWND/card measurements. Canonical successor,
exact-SHA pipeline/package/install, retained-profile path and DPI remain open.
No backend change or real original Apply/undo.

## Checkpoint UX e geometria — implementazione locale 9 novembre

La policy Core distingue autenticazione, shell vuota verificata, catalogo
vendibile e recovery. Login online/offline condividono completamento fenced
(versione operatore, lease, generation/shop, cancellazione); nessuna capability
storico è trasferita alla shell. CanOpenPos conserva significato vendibile;
start-of-day espone separatamente CanEnterAuthenticatedShell. Anche il timeout
rivalida le prove, senza inventare CatalogSaleSafe=true.

MainWindow vuota non crea PosView/VM commerciale; mostra operatore, menu e
messaggio localizzato con Sync Center. Riepilogo e cassa condividono servizio e
factory; import volontario unico, filtri conservati, Escape dal report torna alla
cassa e allo scanner. Payment totals indisponibili mostrano N/A, non zero.

Fixture MainWindow reale PASS: tre chiusure import, PID/HWND/sessione/filtri,
riavvio senza permesso storico, transizione full autorevole, login ready,
scansione → carrello → report → Escape con carrello/focus conservati. Processo
13.297s; geometria22osservazioni10.870s e regressione integrata vendita/ricevuta
1.904s PASS (working tree). Gate50/50 PASS. Prima fixture TIMEOUT60s conservata:
11assert positivi, osservatore modale della transizione bloccato; observer
registrato prima di ShowDialog, stesso budget. Qualifica canonica/candidato,
installazione e percorso reale restano ACTIVE; a828 rimane installato.

## 0. Stato corrente e mandato

Ultimo addendum UX9novembre, stessa Execution/R3: accesso valido e catalogo
autorevolmente vuoto devono aprire la shell autenticata con navigazione/report,
senza abilitare vendite. Storico volontario da riepilogo → Storico → Importa;
Close/X/Escape del figlio conservano processo, sessione, filtri e ritorno alla
cassa. Questo supera il precedente trattenimento obbligatorio nel login storico.
Gate UX ACTIVE insieme alla geometria: patch locale preservata, non ancora
pubblicata; HEAD/main c54be47 e installato a8286c6 verificati21:21UTC. Nessun
runtime attivo. Backup/ledger/trust conservati, nessun nuovo Apply o undo reale.

Addendum Execution9novembre: prima della consegna finale ogni nuovo dialog
operatore deve essere centrato nella work area del monitor pertinente,
contenuto e con titolo/footer raggiungibili, anche annidato e al200%.
La policy CenterOwner è da sostituire nel livello condiviso, mantenendo il
vero owner. Gate geometria ACTIVE: a8286c6 è già installato, display200%
ripristinato e osservato; POS chiuso ordinariamente al checkpoint inattivo.
Nessun nuovo Apply degli originali: dopo il nuovo candidato soltanto reopen,
report/CSV e review del reimport con nuovi0. Mutazioni sulle sole fixture.

Correzione implementata nel livello comune e39root XAML, con owner autentico
invariato. Focused sintetico200%:22misure, prima posizione HWND/card/footer,
sei casi matematici e cancel/confirm singolo PASS; guard50/50 e build x86 PASS.
Recovery nuovo codice30.160s PASS, vecchio weakref FAIL conservato senza causa
inventata. Nuovo candidato geometria da qualificare/installare; a828 rimane
installato.100/125 del nuovo codice e verifica200% finale aperti.

| Campo | Valore |
| --- | --- |
| Fase | Execution A+B+C e onboarding Windows: software consegnato; qualifica reale/fisica aperta |
| Revisione/data | R3 normativo congelato; stato Execution aggiornato2026-11-09, America/Santiago |
| Stato del piano | ACTIVE; software REVIEW. Admin148 autenticato PASS; legacy reale salvato/riconciliato. Nuovo a8286c6 qualificato e installato41/41; prima failure UI/CI, rollback e altri FAIL conservati. Nessun DONE |
| Implementazione | Codice/pacchetto `a8286c669a7c0d8ec4f82e1e7a31fc1b5d46eed3`, versione1.0.0-dev.a8286c669a7c: fix minimo conferma con piano stabile, rientranza negata e review modificata respinta. Rispetto a ca4e437 cambia soltanto la fixture; Retry comune, continuazione bounded, rollback/final authority, onboarding959396b, closureQAe832d6a e fence d262ccd preservati |
| Build, test, harness e qualifica runtime | CI42673051923/Security42673051757/Release42674141171/WPF42675026366 SUCCESS: canonica15, functional20, foundation5/37, smoke8/52 e load11(M/E/S=true). QA scaricato40 verificato, smoke49.678s PASS8/52, cleanup/fingerprint invariati; prodotto41/110checksum e34comuni QA identici. Core1734 e gate50 attribuiti nei report; nessuna qualifica fisica Win7 |
| Nuove prove della prosecuzione | a828 avviato dal collegamento quotidiano sul profilo conservato, verificato prima PIN: login/empty/workbench/import PASS. Ledger e ricevuta esatti dopo update/reopen, SQLite/FK e invarianti commerciali PASS. UI7 e legacy4/report/dettaglio/CSV prima update PASS attribuiti a1823697. LIVE_REOPEN_PENDING sul successore. Form nativo100/125 intero, reverse Tab/Enter/Escape osservati; ripristino200% pendente. A/B VERIFIED, C moderno MISSING, CSV_INPUT BLOCKED_INPUT, Win7 indisponibile |
| Baseline locale/remota verificata | 32618249fc01f29e86c6774d2b2c9262a90f226f |
| Branch e stato iniziale R3 | main; PLAN R2 untracked e modifiche locali di indice/worklog preservate |
| Persistenza | Codice su main; quattro workflow exact-SHA PASS, prodotto/QA scaricati e verificati; reader7/7 PASS con NeedsEvidence; documenti finali separati dai binari |
| Prossimo passo consentito | Concludere consultazione/reimport identico sul successore con zero nuovi e totali invariati, ripristinare200%. Nuovo kit a828 preparato per il target indisponibile; precedente182 immutato. Moderni bloccati da C, CSV ingresso privo di campione. [Risultato corrente](../../reports/ASUS_W7POS_019_RETRY_E2E_RESULT.md); nessun undo del lotto valido o DONE |

Questo e il solo piano canonico. Lo stato corrente risiede qui; la sola voce
ASUS-W7POS-019 nell'[indice](../../CODEX_ASUS_TASKS.md) ne e il collegamento.
Il [worklog](../../AI_WORKLOG.md) registra la sessione, non un secondo piano.
Salvare la task non introduce persistenza runtime delle anteprime.

La richiesta esplicita successiva autorizza Execution A+B+C, test isolati e consegna.
I vincoli Planning sotto sono storia della revisione R3, non il mandato corrente.
Il registro Execution distingue implementazione, prove effettive e gate esterni.

## 1. Fonti, baseline e precedenza

### 1.1 Fonti e limiti

| ID | Fonte e provenienza | Ambito/autorevolezza |
| --- | --- | --- |
| SRC-REQ-EXEC | Richiesta Execution end-to-end ricevuta 2026-11-04 | Mandato corrente A+B+C, automazione, test e consegna autorizzati |
| SRC-REQ-R3 | Richiesta conclusiva R2 -> R3 ricevuta 2026-11-04 | Mandato storico Planning, superato dalla richiesta Execution |
| SRC-REQ-R2 | Richiesta R1 -> R2 | Fonte storica della revisione precedente |
| SRC-REQ-R1 | Precedente richiesta di persistenza/revisione | Fonte storica del mandato R1, preservata |
| SRC-DESIGN-R0 | Precedente piano nella stessa conversazione, non salvato nel repo | Proposta da correggere; non prova di implementazione |
| SRC-PLAN-R1 | PLAN.md R1 del 2026-11-04 indicato dall'utente, disponibile localmente | Target sufficiente dell'audit R2; SHA-256 locale verificato uguale a quello ricevuto: d1a2395231e48822f1ec2ef6781b8b0403942b1a260fb504868bc294ab70694f |
| SRC-PLAN-R2 | PLAN R2 del 2026-11-04, target indicato dall'utente | SHA-256 locale verificato uguale al ricevuto: 3735e6e5784878f06246f570204ec378c5a8a2bf0d94677ac863d7e93d8170f6; nessuna revisione successiva trovata |

| SRC-PRIVATE-AUDIT-v1 | Audit economico privato contenuto nella richiesta iniziale | Dati ricevuti, non misurati; non copiarne nominativi, file, riferimenti o totali in repo/artifact pubblici |
| SRC-PRIVATE-XLSX | Sette workbook originali in percorso privato esterno al checkout | Audit indipendente OOXML eseguito in sola lettura durante Execution; hash prima/dopo e controlli ricevuti coincidenti. Non costituisce qualifica del parser Win7POS; semantica temporale e binding reale restano gate distinti |
| SRC-CODE | Checkout e main GitHub allo SHA sotto | Fonte del comportamento effettivo osservato |
| SRC-RULES | [AGENTS](../../../AGENTS.md), [Dialog Standard](../../DIALOG_STANDARD.md), [QA authorization](../../QA_AUTOMATION_AUTHORIZATION.md) | Regole del repository e confini delle prove |
| SRC-INDEX | Indice Asus e worklog correnti | Numerazione e storia; non autorizzano nuove deroghe |
| SRC-HISTORY | [Completion ledger](../WIN7POS_POST_MERGE_COMPLETION_LEDGER.md) | Chiusure di candidati precedenti; non MASTER-PLAN della funzione Getnet |

Non esiste un MASTER-PLAN locale autorevole per questo lavoro. Non ne viene creato
uno alternativo. Dopo controllo di indice, contenuti Markdown e nomi locali
anche non tracciati, gli ID risultavano usati fino a 018, senza task Getnet:
019 e stato riservato per questa funzione. Nessuna numerazione Mac/Admin e
importata e nessun documento storico viene rinumerato.

Precedenza: richiesta corrente per scope/autorizzazione; regole del progetto per
invarianti; codice del candidato per fatti runtime; audit privato per attesi
da verificare; piani e report storici per proposte/evidenza del proprio SHA.
Una divergenza si registra con fonte e candidato, senza riscrivere la storia.

Legenda usata nel documento: F = fatto letto staticamente; D = dato ricevuto;
P = proposta per futura Execution; M = prova mancante. Una lettura F non e
un PASS runtime. Tutte le decisioni implementative seguenti sono P.

### 1.2 Repo Sync Check

[Repository](https://github.com/XNIW/Win7POS) e
[cronologia main](https://github.com/XNIW/Win7POS/commits/main/) letti tramite
connessione GitHub in sola lettura il 2026-11-04. Main effettiva, origin/main e
HEAD locale coincidono: 32618249fc01f29e86c6774d2b2c9262a90f226f.
Nessun delta dalla baseline di preparazione; nessun fetch ulteriore necessario
in R2. All'avvio R2 erano gia presenti PLAN R1 untracked e modifiche a indice e
worklog: sono la persistenza locale autorizzata, non una task mancante su main.
Nessun branch cambiato e nessuna modifica locale accantonata.

| SHA corto | Messaggio verificato |
| --- | --- |
| 32618249 | Merge pull request #129 from XNIW/codex/residual-qa-recovery |
| 94411d29 | test: retain bounded output probe progress on timeout |
| c33624a3 | fix: realize Recovery action containers before harness interaction |
| a4f21a25 | fix: preserve full diagnostics for failed required gates |
| 1f7327f9 | Merge pull request #128 from XNIW/codex/residual-qa-recovery |
| 0e9580a2 | fix: isolate dialog shadows from animated Recovery content |
| b81f5ab3 | test: expose full-card shadow invalidation in Recovery [skip ci] |
| 3026ce5f | fix: keep trust profiles out of functional QA artifacts |

R3 ha ripetuto il Repo Sync Check via GET GitHub: stesso main, HEAD e
origin/main; gli otto commit sotto restano invariati, nessun delta di codice.
R2 aveva gia verificato la medesima baseline.
Il checksum ricevuto identifica la R1: in questo checkout coincidono anche i
byte locali. Non si attesta un autore ulteriore; l'eventuale assenza di R0 e un
limite storico, non blocca la revisione R1 e non richiede un altro piano Claude.

Questi commit motivano la conservazione di diagnostica completa, timeout e
separazione dei controlli dinamici dall'ombra. Non qualificano la nuova funzione.
Lo stato finale di questa sessione deve contenere soltanto il diff documentale.

## 2. Scope e scelta architetturale

V1: import locale dei pagamenti storici carta, lotti annullabili, audit,
backup/restore e consultazione analitica integrata. WPF net48/x86, Windows 7 SP1;
Core/Data netstandard2.0. Nessun requisito Excel installato, upgrade gratuito
o dipendenza Win10+.

Tabelle dedicate e query analitica comune sono la scelta confermata nel design.
Non si estende Sale per rappresentare eventi senza articoli. F:
Sale usa PaidCash/PaidCard; SaleTransactionWriter.InsertSaleAsync inserisce righe,
aggiorna stock, outbox e held cart; PosSalesSyncRequestBuilder verifica totale
contro righe e prepara metadati boleta. Riferimenti:
[Sale](../../../src/Win7POS.Core/Models/Sale.cs),
[writer](../../../src/Win7POS.Data/Repositories/SaleTransactionWriter.cs),
[builder](../../../src/Win7POS.Data/Online/PosSalesSyncRequestBuilder.cs).

Estendere sales imporrebbe eccezioni a writer, resi, boleta, sync e chiusure.
Il costo di alcune tabelle nuove e inferiore al rischio di attraversare quei
contratti con dati incompleti. Vietati SKU storici, codici scontrino inventati,
vendite native fittizie, pagamento/rimborso carta, stock, cassa, documenti
fiscali e outbox commerciali generati dall'import.

Il Master Console futuro serve al binding reale del negozio e ai permessi:
non autorizza creazione account in questa task e non rende compatibile il
protocollo vendite cloud. Nessun record storico verra lasciato pending nella
normale outbox. Condivisione multi-postazione richiedera una fase server
separata: contratto storico, idempotenza server, tenant scope, audit,
annullamento e propagazione; fuori V1.

## 3. Contratto dati Getnet

### 3.1 Profili, campi e diagnostica

V1 supporta i due layout XLSX verificabili tramite intestazioni, non filename,
nome foglio o indici fissi. Normalizzazione versionata: Unicode NFD, rimozione
diacritici, uppercase invariant, trim e compressione degli spazi. Gli alias
sono espliciti, mai fuzzy. Si conservano intestazione originale e posizione
per diagnostica. Duplicati dopo normalizzazione, alias incompatibili e piu
fogli candidati bloccano la scelta automatica.

| Campo | Legacy | Moderno |
| --- | --- | --- |
| Data vendita | FECHA VENTA | FECHA DE VENTA |
| ID principale | ID TRANSACCIÓN | ID FINANCIERO DEL MOVIMIENTO |
| Riferimenti secondari | COD.AUT | PAYMENT ID, COD.AUT |
| Importo | VALOR TRANSACCIÓN | VALOR TRANSACCION / VALOR VENTA |
| Controllo lordo | Importo transazione | MONTO BRUTO dopo classificazione |
| Stato transazione | ESTADO | ESTADO TRX |
| Tipo movimento | Solo vendita nel contratto legacy osservato | TIPO MOV. |
| Commissione/netto | COMISIÓN, MONTO ABONO | COMISION, IVA COMISION, VALOR NETO |
| Stato liquidazione | Limitato dal layout | ESTADO DEL ABONO separato |
| Sottotipo carta | TIPO | TIPO, PREPAGO |
| Esercente | LOCAL | VENDEDOR, LOCAL, SELLER ID, NUM LOCAL, RUT |
| Terminale/offset | Assenti | TERMINAL, ZONA HORARIA (UTC) |

Obbligatori per riga economica: data vendita, ID principale valido, importo
esatto, stato, ambito esercente risolto. Moderno: anche tipo movimento,
MONTO BRUTO, dichiarazione CLP e offset previsto dalla policy. Il nome esatto
del campo valuta moderno resta da confermare sugli originali.
Legacy: CLP e un'assunzione esplicita del profilo da confermare in preview,
mai una valuta dedotta dal PC.

Gli importi di commissione/IVA/netto sono richiesti per la riconciliazione
completa quando la liquidazione e dichiarata conclusa. Per una vendita
confermata non liquidata possono essere null: lordo incluso, netto sconosciuto.
Un campo opzionale assente non blocca da solo la vendita e non diventa zero.
Se entrambi gli alias dell'importo sono presenti, si leggono separatamente
e si confrontano secondo il contratto; non si sceglie il primo.

D: LOCAL moderno puo essere troncato mentre VENDEDOR e completo. Alcune
colonne finali di clearing hanno contenuti incoerenti. Non si riallineano
colonne a intuito e non si usano per identita o data vendita.

Errori con codice stabile, file locale/foglio/riga/colonna nel dettaglio privato,
campo e azione correttiva; nei log pubblicabili solo codici e contatori.
Una riga economica non interpretabile o un conflitto blocca il lotto selezionato.
Respinte riconosciute e duplicati sono esclusioni attese, visibili in preview.
Nessuna importazione parziale silenziosa.

### 3.2 Economia e identificativi

Classificare prima di sommare:

- Legacy Abonado: vendita accettata osservata. Altri stati non hanno una
  semantica presunta; serve un contratto verificato.
- Moderno Confirmada/Venta: vendita; Confirmada/Anulación: movimento negativo;
  Rechazada: richiesta esclusa dai ricavi, conservata come esito di audit.
- Stato transazione e stato accredito restano indipendenti. Confermata non
  liquidata entra nel lordo commerciale, senza inventare un accredito.
- Legacy: lordo meno commissione positiva uguale accredito.
- Moderno liquidato: lordo piu commissione firmata piu IVA commissione
  uguale netto. Non uniformare i segni dei due formati.
- Int64 CLP, parsing lessicale esatto e aritmetica checked; nessun double nel
  dominio, arrotondamento implicito, cambio valuta o moltiplicazione per CUOTAS.
- Mance/vuelto non zero: errore bloccante V1 finche non e verificata una
  separazione economica; nessuna regola inventata.
- Metodo Carta per tutti; tipo originale conservato. PREPAGO=Si prevale su
  TIPO=Débito per il sottotipo analitico, senza cambiare il checkout.
- Contanti storici, articoli, costo, margine, IVA merce e cassiere assenti:
  non disponibili, non zero ricostruito.

ID e COD.AUT restano stringhe, compresi zeri e lettere; vietati cast Int32.
Vuoto e segnaposto meno non sono ID. Preferire celle testuali; per numeriche
usare il valore lessicale OOXML e il solo formato esplicito sicuro, senza
CurrentCulture. Rifiutare ID non integrali, perdita di precisione, esponenti
ambigui o cifre gia perse da Excel: non possono essere recuperate a intuito.

L'annullamento del campione privato e un evento negativo autonomo. Conservare
vendita positiva e annullamento firmato produce il saldo corretto. COD.AUT non
prova il collegamento; non eliminare anche la vendita e sottrarre il negativo.
Il test pubblico usa importi/ID sintetici, non i riferimenti reali ricevuti.

Netto aggregato: esportare knownNetClp, knownNetCount e eligibleCount; dichiarare
completo solo se la copertura e totale. Somma delle componenti note con mancanti
deve dire "parziale" e mostrare copertura, senza confronto fuorviante al lordo.

### 3.3 Date e policy

Persistenza: raw timestamp, offset originale, business datetime senza Kind Local,
business date, eventuale UTC, origine della policy e versione. Vendita,
liquidazione e importazione sono date distinte.

Legacy: dd/MM/yyyy HH:mm:ss, ora commerciale preservata. UTC nullable; se
richiesto, dipende da regole negozio esplicite/versionate per l'intervallo.
Moderno: suffisso Z e offset hanno semantica non verificata. Preview mostra
entrambe le interpretazioni; applicazione reale bloccata fino alla prova esterna.
Esempio esclusivamente sintetico: 2024-01-31T20:00:00.000Z con offset -4
significa 16:00 secondo ISO, oppure 20:00 se gia ora locale esportata.
Non scegliere dagli orari d'apertura.

Secondi e millisecondi preservati. Ore inesistenti o ambigue non vengono
corrette automaticamente: mantenere il dato commerciale sorgente e richiedere
evidenza per l'UTC ambiguo. Per ISO standard l'istante e univoco e la conversione
usa l'offset esplicito verificato, senza il fuso del PC.
Cambio offset, fine mese e copertura parziale hanno test dedicati.
Gli attesi privati per giorno/mese/ora si congelano solo dopo la scelta provata.
Le policy dei record applicati sono immutabili; cambiarle richiede annulla e
reimport esplicito, non una ricalcolazione silenziosa.
Precisione frazionaria superiore ai millisecondi: conservarla in ticks fino
alla precisione supportata oppure rifiutare il valore con diagnostica; mai
troncarla silenziosamente. Il raw resta sempre disponibile.

## 4. Merchant mapping e identita economica

Correzione R1: audit da solo non basta. Due tabelle dedicate rendono riusabile
la mappatura senza dipendere da nomi simili o dalla memoria dell'operatore:

- historical_merchant_scopes: GUID stabile, shop_id e provider, identificatori
  verificati, revisione, creazione/approvazione, riferimento alla prova.
- historical_merchant_aliases: GUID binding, scope_id, shop/provider, namespace
  dell'alias, valore canonico e originale privato, versione normalizzazione,
  revisione/stato, autore e data di approvazione.

Lookup per (shop_id, provider, alias_namespace, canonical_value) tra binding
attivi; unicita attiva e nessuna scelta "primo risultato". Identificatori
forti e alias riconosciuti devono convergere allo stesso scope; se divergono,
preview bloccata. Nome troncato o RUT corrente da soli non associano il file.
Il binding legacy/moderno e approvato esplicitamente con permesso import,
in base alle prove disponibili, senza hardcode di un negozio.

Nuovi scope/alias proposti restano nel piano in memoria e sono creati nella
stessa transazione dell'import. I successivi lotti riusano il binding persistito.
La preview cattura revisioni e hash di tutti i binding usati.
Aggiunta di alias: nuova riga/revisione e audit; disattivazione soltanto se
non referenziata. Nella V1 uno scope/binding gia usato non puo essere
riassegnato a un altro esercente, fuso o reinterpretato, neppure annullando
un solo lotto: tale modifica potrebbe cambiare la dedup di tutta la storia.
Una correzione semantica richiede un futuro piano esplicito con tutti i record
coinvolti; nessun pulsante generico di remap. Questa restrizione e una decisione
V1, non un percorso architetturale lasciato indeterminato.

Le transazioni conservano scope_id e binding/versione usati: nuove revisioni
non riscrivono lo storico. Mapping, revisioni e audit viaggiano nel backup;
restore ne verifica FK, unicita e coerenza shop. Nessuna sincronizzazione cloud.

Identita evento attivo:
(shop_id, provider, merchant_scope_id, external_namespace, external_id).
Namespace legacy transaction, financial movement e payment restano distinti.
Parser, profilo/versione, file/hash e riga sono provenienza, non identita.
Non creare alias fra ID di famiglie diverse senza una prova specifica.

| Caso | Decisione |
| --- | --- |
| Stessa identita, stessi dati canonici | Duplicato; osservazione audit, zero inserimenti |
| Stessa identita, lordo/valuta/tipo/stato economico/data divergenti | Conflitto bloccante |
| Commissioni/netto entrambi noti ma divergenti | Conflitto finanziario esplicito |
| Liquidazione prima mancante poi disponibile, stato/data o metadati diversi | Segnalazione separata; record esistente invariato nella V1 |
| ID distinti con stesso timestamp/importo | Due eventi; nessuna eliminazione euristica |
| Identita presente soltanto fra record annullati | Reintroduzione/correzione esplicita in preview |
| Overlap fra namespace o vendite native senza ID Getnet certo | Nessun merge: blocco del perimetro incerto fino a prova o scelta di intervallo non sovrapposto |

Per il primo onboarding il periodo raccomandato precede l'avvio operativo POS.
La data d'avvio e una prova esterna, non una costante del parser. Il piano
registra la copertura autorizzata; nessun overlap e incluso due volte per
una semplice conferma generica.

## 5. Schema additivo e cicli di annullamento

Ultima migration F: 0014-catalog-import-plans-and-drafts.
Proposta: 0015-historical-card-imports, ricontrollando il numero prima di
Execution. Registry, checksum e descrizioni 0001-0014 restano immutati.
Aggiungere SQL/detector del nuovo schema senza cambiare il significato dei
detector storici; provare DB nuovi, 0014, ledgerless riconosciuti e schema invalido.

| Tabella proposta | Dati/invarianti |
| --- | --- |
| historical_import_dataset | Una riga: dataset_id UUID persistente e data_revision globale della proiezione report mista, aggiornata atomicamente dai trigger; identita copiata nel backup, non derivata dal percorso o PID |
| historical_merchant_scopes | Scope stabile per shop/provider; chiavi forti verificate univoche quando disponibili |
| historical_merchant_aliases | Binding versionati del paragrafo 4, FK scope/shop/provider |
| historical_import_operations | operation_id PK globale, Apply/Undo, dataset/shop, request_fingerprint, batch_id, receipt terminale immutabile/versionata, UTC, revisione report al risultato; nessuna riga InProgress pubblicata |
| historical_import_batches | batch_id, apply_operation_id UNIQUE/FK, dataset/shop ID/code, provider, manifest profili, importatore/staff, import UTC, stato corrente Applied/Cancelled, batch_revision, contatori/totali, backup reference, undo operation ID e motivo |
| historical_import_files | FK batch, SHA-256/nome originale locale/dimensione, foglio, profilo/parser e policy/versione, valuta e copertura |
| historical_transactions | ID interno nuovo, batch/file proprietari, shop/provider/scope, namespace/ID, riferimenti, business datetime/date e raw/offset/UTC, tipo/stati, importi INTEGER nullable dove sconosciuti, Carta/sottotipo, riga, is_active, supersedes_id opzionale |
| historical_import_audit | Azione e disposition per riga, batch/file/riga, target_transaction_id per duplicati, codici/valori essenziali privati, autore/UTC, annulla/reintroduzione |
| sales_report_native_ownership | Proiezione derivata per sale_id unico/FK: Unknown/CodeOnly/Verified/Conflict/Dirty, shop provato, policy hash e source fingerprint, senza sostituire l'outbox probatoria |
| sales_report_time_policies | Shop/policy/versione/hash, copertura, segmenti UTC canonici, provenienza e approvazione; policy usata immutabile, selezione attiva univoca per shop |

I dati finanziari locali non vengono riversati nei log pubblici. Operatore
importatore distinto da original_cashier_id nullable.

Vincoli: PK e FK RESTRICT, chiavi composite o vincoli equivalenti per assicurare
che file/batch/shop/provider/scope appartengano allo stesso contesto.
supersedes_id deve puntare a record inattivo della stessa identita; niente cicli.
Importi Int64 e checks su tipi/stati/valuta. Non legare shop_id con FK a
app_settings mutabile. Nessuna FK verso Sale per creare un falso evento nativo.

Indice unico PARZIALE sulla chiave economica WHERE is_active=1.
Non UNIQUE(key, active_flag): quella forma permetterebbe un solo record inattivo
e fallirebbe al secondo annullamento. Indici non unici su chiave+storia,
shop/business_date, batch/id, external namespace/ID e audit/target.
Un solo proprietario per ogni versione introdotta; osservazioni duplicate
non trasferiscono proprieta.

La FK batch.apply_operation_id verso operations e DEFERRABLE INITIALLY DEFERRED:
si puo inserire il batch e poi la receipt che lo referenzia nello stesso commit,
senza un ciclo di FK impossibile da soddisfare. Verificare al commit coerenza
di tipo Apply, batch e shop; Undo referenzia il batch gia esistente.

Le chiavi forti esercente hanno UNIQUE(shop_id, provider, key_namespace,
key_value) quando presenti; gli alias attivi hanno il proprio UNIQUE parziale
per namespace/valore. Ogni transazione referenzia anche binding_id e revisione
usati. Gli indici dei mapping sono distinti dalla chiave dell'evento: un nuovo
alias non crea una seconda identita economica.

Undo amministrativo: anteprima di lotto/revisione, righe attive possedute e
totali; capability Undo; transazione che disattiva solo quelle righe, marca
Cancelled, aggiorna batch_revision, receipt Undo e audit nella medesima
transazione; i trigger aggiornano data_revision per la variazione della
proiezione inclusa nei report. Nessun DELETE dei record,
rimborso, restock o restore dell'intero DB. Undo ripetuto con lo stesso operation
ID restituisce la ricevuta; lotto gia annullato con un'altra richiesta e
segnalato senza una seconda modifica economica.

Se B ha osservato duplicati di A, annullare A non mantiene gli eventi tramite B
e non li promuove. Reimport dopo undo crea un nuovo batch e nuovi ID interni
attivi, con eventuale supersedes; vecchi record e osservazioni restano.
Prova obbligatoria: almeno tre cicli import/undo/reimport, stesso evento,
zero duplicati attivi e tre versioni inattive conservabili.

Backup protettivo della migration e backup pre-import sono distinti:
il runner migration protegge l'upgrade; ogni apply/undo richiede il proprio
snapshot verificato prima della mutazione. Preservare journal_mode=DELETE,
synchronous=FULL, foreign keys, timeout e ripristino del locking_mode corrente.
Il fence transitorio gia esistente non e una nuova policy di journaling.

## 6. Shop proof e autorizzazione

### 6.1 Attribuzione delle vendite native

F: sales non ha shop_id. ReceiptShopInfo ha ShopCode, NON ShopId.
SerializeReceiptShopSnapshot serializza quel tipo, mentre ReceiptShopMetadata
e un altro contratto. La validazione di testo/dimensioni della ricevuta non
attesta il tenant; il fallback di stampa usa impostazioni correnti.

La decisione di ownership viene risolta una sola volta per sale_id, evitando
join che moltiplichino i movimenti:

| Livello | Prova | Esito |
| --- | --- | --- |
| N1 | Outbox della stessa vendita, origin_shop_id valorizzato e coerente; client ID/schema/operazione e payload/hash validi, codice senza contraddizioni | Attribuibile allo shop ID provato |
| N2 | Contratto legacy hash-verificato con solo shop code | Code-only; attribuibile a un ID solo con corrispondenza storica autorevole non ambigua |
| N3 | Solo snapshot ricevuta, nome/RUT, data o impostazioni correnti | Non attribuibile |
| NX | Binding/payload/identita discordanti | Conflittuale, escluso e diagnosticato |

La verifica hash locale prova coerenza con il payload conservato, non una firma
server. Non esiste oggi prova che ogni code-only abbia una mappa storica:
in assenza di tale prova resta non attribuibile. Nessun backfill dal negozio
attivo o dalla ragione sociale. Snapshot e riferimenti accessori possono
evidenziare contraddizioni, non promuovere da soli l'ownership.

Solo N1 o N2 con prova completa entrano in report/search/detail/export dello
shop. Non attribuibili e conflittuali restano nel DB e fuori da tali percorsi.
La V1 non introduce una vista globale di transazioni senza tenant. Un futuro
accesso amministrativo separato richiedera un'azione autorizzata esplicita e
un proprio scope; non basta una colonna "sconosciuto".

Storico: shop ID/code autorevoli obbligatori al commit, non derivati dal
filename. Preview senza shop creato ammessa; apply no. Il guard shop corrente
blocca outbox irrisolte e resetta catalogo/utenti, ma non ripartiziona sales:
non dichiarare risolto l'isolamento nativo preesistente.

### 6.2 Capability di import e undo

F: BeginAuthorizationUseAsync e nominalmente generico ma produce un
SaleAuthorizationCommitGuard limitato a Sale/Refund/Void. Non riusarlo fingendo
una SaleKind. P: HistoricalImportAuthorizationCommitGuard, non costruibile dai
consumer ordinari, emesso da un metodo dedicato di OperatorSession.

Campi vincolanti: Apply/Undo, permesso esatto, operator/staff/versione,
shop ID/code/device, generation ID/fingerprint, authorization epoch,
dataset ID, restore/transition epoch, operation ID e fingerprint piano
oppure batch/revisione da annullare. Metodi DemandStillValid e
CommitIfStillValid(minimumRemaining, commitAction). Repository/applier
richiedono obbligatoriamente tale capability; nessun Func<bool> di fallback.

Permessi proposti register.import_history e register.cancel_history, nessun
grant automatico a cashier/supervisor/manager. Admin tramite controllo attuale;
catalogo/etichette custom aggiornati. Mapping come sotto-operazione autorizzata
dell'import, con approvazione esplicita. Non toccare BuildRolePermissionSeeds:
SecuritySeedMaterial entra nel checksum 0006 e nel confronto esatto del detector.

La garanzia da riusare e quella effettiva del writer nativo: fence SQLite
esclusivo, verifica completa della lease, poi final check e COMMIT sincrono
sotto il latch della revoca. Preservare ordine _sync -> Gate; non chiamare il
lease/store guard prendendo _sync mentre si possiede Gate. Alla soglia finale
richiedere il DurableCommitSafetyBudget corrente di 10 secondi.
Questo margine non e un budget di durata dell'intero import.

Revoca prima del punto di linearizzazione: rollback. Revoca dopo il commit
linearizzato: lotto applicato, eventuale consultazione/undo richiede una nuova
autorizzazione. Testare la race immediatamente prima del commit, non soltanto
la perdita permesso prima degli insert. Nessuna elevazione Recovery.

### 6.3 Autorita DB condivisa, entry point e restore

F: il mutex Local\Win7POS.Wpf.SingleInstance e session-local/fail-open.
PosStartupCoordinator.Initialize recupera installazioni restore e inizializza
il DB anche nel safe-start. Il harness crea System.Windows.Application e non
attraversa App.OnStartup; la CLI inizializza/seeda DB anche per daily/export.
Quindi R1 limitata ad App non garantiva il confine.

P: PosDatabaseAuthorityLease in Data, con registro process-local dei contesti.
L'entry point acquisisce un owner prima della prima possibile scrittura,
compresi recovery e migration; operazioni annidate ottengono borrow contati.
Factory, initializer e accessi raw di restore/backup richiedono quel contesto,
senza acquisizioni implicite che nascondano una ownership mancante. Non occorre
riscrivere tutti i repository. Il fallimento e DatabaseInUse/AuthorityUnavailable,
prima di init/seed; nessun override QA e nessuna cancellazione della sidecar.

| Entry point reale | Prima possibile scrittura F | Acquisizione/rilascio P e dataset ammesso |
| --- | --- | --- |
| WPF normale | PosStartupCoordinator.Initialize:126-142, recovery poi EnsureCreated | Bootstrap condiviso prima recovery; owner sul DB configurato fino a stop/drain servizi, chiusura connessioni/pool e shutdown |
| WPF safe-start/Recovery | Medesimo Initialize; safe-start non e readonly | Stessa lease senza deroga; contesa impedisce init/recovery operativo |
| CLI daily/diff/apply/export/backup | Program.InitializeHarnessDbAsync:6405, migration e seed sintetico | Owner command-scoped sul DB risolto prima della funzione; anche i comandi nominalmente di lettura vi aderiscono |
| CLI selftest/crash esistente | CatalogImportProcessCrashSelfTest prepara DB e avvia figlio | Owner parent per prepare; chiude pool/handle e rilascia prima del figlio; figlio acquisisce; parent riacquisisce solo dopo uscita confermata |
| Harness UI, ogni modalita | Program:320 Application; :339 init, seed :6917/:6977 | Dopo validazione --data-dir e prima di qualunque ramo DB; coordinator usa borrow, owner per intera invocazione |
| Nuovo worker crash/verifier | Worker prepara; verifier puo causare journal recovery | Owner separato e sequenziale per lo stesso slot; verifier soltanto OpenExistingForRecovery, niente bootstrap |
| Restore | AtomicRestoreInstaller sostituisce file | Mantiene owner sidecar per swap/rollback; rilascia solo guard no-delete DB durante swap, rebind fisico e epoch prima della ripresa |
| Riavvio dopo crash | Recovery restore precede migration | Owner prima del recovery. Handle OS del processo morto liberato, sidecar lasciata al suo posto |

SqliteConnectionFactory.Open/OpenAsync/initializer verificano DemandOwned; i
percorsi raw ricevono owner/borrow esplicito. I test esercitano gli entry point
reali WPF/harness/CLI, non solo due istanze del helper. Due DB distinti sono
ammessi; i borrow dello stesso processo non creano un secondo owner.

Identita: distinguere slot logico persistente (directory reale verificata +
nome DB normalizzato) e file fisico (volume/file index) che cambia nel restore.
Windows: GetFinalPathNameByHandleW e GetFileInformationByHandle, API compatibili
Win7; verificare parent e file tramite handle, nNumberOfLinks=1 per DB/sidecar.
Rifiutare UNC/rete, componenti reparse/junction, hard link multipli e identita
non verificabili. Case/nomi 8.3/alias drive devono risolversi allo stesso slot;
verificarlo dopo acquisizione, senza equiparare stringa path o dataset_id al
file fisico. DB assente: rivendicare lo slot nella directory verificata prima
di creare il file. Pin dei parent e guard senza FileShare.Delete impediscono
cambi inattesi; un rebind non autorizzato invalida il contesto.

Sidecar FileShare.None mantenuta lifetime; il file residuo non e un lock stale
da eliminare. Il restore owner aggiorna l'identita fisica attesa e conserva
lo slot. Dataset/generation/epoch sono verifiche aggiuntive, non sostituti.
Riferimenti API:
[handle identity](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfileinformationbyhandle),
[canonical handle path](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfinalpathnamebyhandlew).
Le fixture Mac usano vere sidecar in root sintetiche nuove e policy portabile
di test; non qualificano identita/alias Win32. Nessun bypass su un DB operativo
esistente. La piattaforma non qualificata per identita fisica deve rifiutare
l'apertura operativa, mentre logica pura/SQLite isolato restano testabili.

Dentro la transazione verificare dataset, generation persistente, shop,
operatore, restore epoch e revisioni del piano. Restore invalida prima dello
swap e avanza epoch oltre max(live,candidato), come il percorso corrente;
bootstrap reimposta lo stamp del report prima di riabilitare consumer.
Vecchi binari/writer esterni che ignorano il protocollo non sono supportati;
la contesa SQLite resta finita e non e una prova di autorizzazione cross-process.

### 6.4 Contesti espliciti dei DB ausiliari e fixture

F: SqliteOnlineBackup:109 valida temporaryPath tramite un nuovo factory e
:199-205 usa connessioni raw source/destination; l'owner live non copre tali
percorsi. AtomicRestoreInstaller.IsDatabaseValidAsync:450 apre candidate/
rollback/live e oggi converte ogni eccezione in false. RestoreShopSafetyTests
TestDb:410 crea factory/EnsureCreated senza il futuro owner. DemandOwned
limitato al live renderebbe questi percorsi inutilizzabili o indurrebbe bypass.

P: piccolo PosDatabaseAccessContext interno accanto all'owner, non un framework
di autorizzazione: role, operation ID, percorso ESATTO, provenienza, identita
attesa, apertura/creazione ammesse e lifecycle. Emissione solo da ingressi
role-specific: borrow live, source selezionato esistente, allocator partial/
candidate, rollback da marker validato. Nessun permesso wildcard per directory,
adozione implicita o allow-all QA. Il factory accetta anche un contesto AUX
esatto, senza cercare un owner live fittizio o saltare DemandOwned.

| Ruolo/percorso e provenienza | Contesto/apertura consentita | Pool/handle, publish/swap ed errore |
| --- | --- | --- |
| Live source backup, percorso owner operativo | Borrow dello stesso live; raw ReadOnly esistente, no Create/seed | Pooling=false; no-delete guard sino al ritorno backup nativo, poi chiusura |
| Source snapshot restore selezionato dall'utente | SelectedSnapshotSource, path verificato; existing ReadOnly, nessuna migration o recovery scrivente sul sorgente | Pooling=false, chiusura dopo copia; non promuove il backup a live |
| Backup partial allocato dal backup service | BackupPartial del path unico; CreateNew atomico, poi ReadWrite senza Create sul file riservato | Pooling=false; retry solo sul partial posseduto dopo chiusura/rebind, invariati limiti retry gia esistenti |
| Validazione del partial | Stesso AUX ristretto Validate; existing, integrity/FK senza init/seed | Reader/command/connection chiusi e pool assenti prima flush/publish |
| Backup finale scelto secondo policy corrente | PublishTarget esatto, atteso assente; nessuna apertura SQLite RW | Chiudere guard partial; mantenere reservation logica, Move senza overwrite, trasferire Partial -> Published; errore non cancella target estraneo |
| Restore candidate same-directory dall'allocator | AUX proprio; preparazione CreateNew, aperture successive existing; migration solo con grant CandidateMigration esplicito | Pooling=false; trasferimento Preparation -> SealedCandidate insieme al contesto, niente gap di ownership |
| Rollback dal marker installazione validato | RestoreRollback esatto; existing, eventuale recovery tecnico controllato, niente bootstrap | Chiudere validate/seal/no-delete prima Replace/Move, preservando reservation e owner live |
| Nuovo live dopo swap/rollback | Owner slot live conservato, rebind fisico dal solo protocollo restore | Riapertura solo dopo verifica; reset epoch/stamp prima della ripresa writer |
| Ogni DB fixture nuovo in root posseduta | OwnedSqliteTestFile esatto prima Factory/EnsureCreated; contesti separati per source/candidate/live/rollback | Chiudere pool/connessioni, poi rilasciare contesti; teardown esistente limitato alle proprie fixture |

Un borrow live non e valido per partial/candidate. Un AUX di validazione non
abilita migrazione. La destinazione finale di backup conserva la policy
esistente, inclusi i casi remoti gia supportati: non applicare automaticamente
il divieto operativo UNC a un backup. Non cambiare DELETE/FULL.

Lifecycle restore: ValidateAndSealAsync chiude SQLite prima di aprire il seal;
ConsumeForAtomicReplace chiude quel handle prima dello swap. Reservation/
contesto e owner live restano posseduti durante il trasferimento di identita:
candidate -> live, precedente live -> rollback. Stessa disciplina per
pubblicazione backup. Il guard no-delete protegge la fase di lettura, ma viene
chiuso prima del PROPRIO Move/Replace autorizzato; non blocca l'operazione che
deve proteggere. Recheck dei binding/seal nel protocollo, niente reopen generico.

IsDatabaseValidAsync distingue IntegrityInvalid da AuthorityDenied, Cancelled,
Busy, MissingFile e IoFailure: gli ultimi sono errori di protocollo propagati,
non prova di corruzione che autorizza lo stesso ramo rollback. Nessun file
mancante viene creato per "validarlo". Recovery adotta solo path identificati
dal marker valido, non tutti i rollback presenti nella directory.

Bootstrap fixture minimo: OwnedSqliteTestFile nei soli test condivisi, sidecar
reale in root nuova e context prima di EnsureCreated; factory aggiuntive
prendono borrow dello stesso file. Mac qualifica lifecycle/ownership portabile,
non hardlink/reparse/file identity Win32. Nessun bypass per DB operativi.

Regressioni dei percorsi reali: MigrationBackupRestoreTests (backup preledger,
restore/upgrade), OnlineBackupContentionTests (retry/cancel invariati),
RestoreShopSafetyTests (validated source, post-swap rollback),
PersistenceFoundationHardeningTests (seal, source identity, marker, rollback).
Negativi: live-context su partial, path/operation errati, validate mancante,
migration con Validate-only, collisione publish, authority scambiata per
corruption e errore/cancel a ogni trasferimento. Test già previsti in B, nessun
runner aggiunto e nessun teardown eseguito in Planning.

## 7. Replay, nuova mutazione, attese e recupero

### 7.1 Due percorsi, senza dipendenza dai workbook per il replay

Una sola analisi logica produce il piano immutabile: manifest/hash input,
righe essenziali, mapping/policy, scope, esiti dedup, totali e precondizioni
dataset/generation/epoch/revisioni. request_fingerprint identifica il contenuto
della richiesta e il tipo Apply/Undo; le revisioni osservate e le scadenze sono
precondizioni separate, non entrano nell'identita stabile del replay.

Percorso A, consultazione/replay terminale:
1. Autorizzazione ATTUALE alla consultazione del lotto nello shop corretto
   (scope visibilita report, non la sola memoria dell'autorizzazione import).
2. Apertura del DB esistente sotto autorita e query di operation_id. Per il
   replay con richiesta confrontare tipo, dataset/shop e request_fingerprint.
   Identita differente -> OperationIdentityConflict, senza dati di altri shop.
3. Risultato terminale coincidente -> restituzione della receipt originaria
   immutabile e, separatamente, stato/revisione correnti del lotto.
   Nessun workbook, nuovo hash input, backup o nuova applicazione richiesti.
4. Dopo riavvio, elenco lotti e GetOperationReceipt richiedono soltanto scope
   attuale e operation ID; non un piano in memoria. Il replay di un Apply
   poi annullato mostra receipt originaria + Cancelled, senza riattivarlo.
   Per una NUOVA applicazione dopo undo occorrono nuovo operation ID e preview.

Percorso B, nuova mutazione: soltanto quando manca un terminale coincidente.
Tutti i preflight, autorizzazione Apply/Undo, input, backup e recheck restano
obbligatori. La ricerca operation_id e ripetuta nella transazione prima di
mutare: chiude la race dopo il percorso A. Un risultato apparso nel frattempo
viene restituito se coincide; altrimenti conflitto. Un preflight gia iniziato
non autorizza a reinserire un risultato esistente.

Ordine di B, compatibile con restore:
1. Fuori lock: conferma, hash file disponibili senza sharing write/delete,
   permessi, copertura policy/merchant e sufficiente lease residua.
2. Owner DB gia posseduto; backup-automation maintenance -> workflow gate
   condiviso -> authorization-use dedicata -> catalog transition barrier.
3. Exclusive maintenance SQLite, senza connessioni gia possedute; drain finito.
4. Backup pre-apply/undo verificato. Distinto dal backup protettivo migration.
   Deve precedere il fence della connessione apply, evitando autoblocco.
5. Connessione posseduta/identity guard -> exclusive commit fence corrente ->
   transazione; lookup operation ID, recheck dataset/epoch/permessi, mapping,
   precondizioni e dedup. Piano stale -> rollback/revisione esplicita.
6. Insert preparati o disattivazioni, audit, revisioni persistenti e receipt
   terminale nella STESSA transazione. Nessun commit intermedio.
7. Verifica contatori, ultimo cancel, breve confine pubblicazione report,
   final authority/latch nel rispetto _sync -> Gate, COMMIT con margine
   durevole corrente 10 s; pubblicazione stamp committato prima del rilascio.
8. Dopo commit: soltanto invalidazione/sostituzione di cache e risultati in
   memoria, rilascio inverso e ripristino centralizzato di comandi/focus.
   Vietato data_revision++ o aggiornamento mapping/batch persistente postcommit.

La receipt e uno snapshot locale del risultato: non viene rigenerata dai
totali correnti. operation_id e globalmente unico; fingerprint piano non e
univoco globale, permettendo una successiva reimportazione autorizzata.

### 7.2 Revisioni atomiche, manutenzione e attese cancellabili

Un solo data_revision persistente descrive la proiezione economica e di scope
dei report misti (§8.1). Non e il numero di importazioni e puo avanzare piu
volte in una transazione: il token e opaco. Trigger della migration 0015 coprono
anche nativi e proof, con confronti null-safe. Rollback annulla dati e revisione
insieme; overflow o tipo diverso da INTEGER fa fallire la mutazione.

Apply con nuove transazioni/undo che cambia visibilita invalida il report.
Batch duplicate-only, receipt aggiuntiva o nuove osservazioni di audit non
cambiano l'economia/lo scope e NON incrementano data_revision. Nessuna revisione
globale per metadati senza consumatore. batch_revision cambia atomicamente
quando cambia lo stato del lotto; revisioni mapping referenziate restano
immutabili o avanzano solo per una modifica esplicita ammessa da §4.
La receipt registra la revisione al risultato, ma replay dopo revisioni
successive NON richiede che sia ancora la revisione corrente.

Apply/undo sono manutenzione controllata; niente writer POS ordinari concorrenti.
Carrello preservato, comandi/scanner non accodano vendite da eseguire dopo.
Messaggio di pausa e progress restano reattivi; ammissione/focus ripristinati
anche su cancel/fault. Nessun cambio a DELETE/FULL, atomicita o busy_timeout.

Deadline di ammissione proposta: 60 s complessivi per tutti i gate, con token
collegato alla cancellazione utente. Ogni wait usa tempo residuo; drain ha
inoltre il limite corrente 30 s, busy SQLite 5 s. Non presentare il solo drain
come limite delle attese precedenti.

| Punto osservato | Contratto cancellabile futuro |
| --- | --- |
| Workflow gate | WaitAsync(ct) sul gate condiviso, ownership soltanto dopo successo |
| BackupAutomationService.EnterMaintenanceAsync | Gia token-aware; deadline collegata e rilascio nel catch/finally |
| RevocationLatch.EnterAuthorizationUse/MaintenanceAsync | Overload mirato ct, vero AuthorizationUseGate.WaitAsync(ct), propagato da OperatorSession |
| CatalogShopTransitionBarrier | Gia EnterAsync(ct), preservare ordine |
| Factory maintenance gate/drain | Token e finally esistenti, deadline residua |
| OpenAsync/RegisterConnection | Nuovo RegisterConnectionAsync(ct) per evitare l'attesa sincrona GetAwaiter.GetResult durante manutenzione |
| Report publication gate | Acquisizione cancellabile prima del finale; UI solo TryEnter breve, mai attesa/IO |

Ownership trasferita una sola volta: se validazione o costruzione lease fallisce
dopo acquisizione e prima della consegna, il helper rilascia. Se consegnata,
rilascia il chiamante con Dispose idempotente. Vietato Task.WhenAny su
acquisizioni non cancellabili: il waiter abbandonato potrebbe ottenere una
lease dopo il cancel. Provare assenza di waiter e risorse orfani.

Backup nativo, SQLite fence/commit e I/O sincrono gia avviati non si abbandonano
per simulare cancel: attendere il ritorno effettivo, registrare durata/overrun
e controllare il token prima della successiva mutazione. Le garanzie di
riscontro <=1 s valgono per lavoro cooperativo, non nascondono i 5 s busy
o una chiamata nativa pendente. Nessun retry automatico.
Snapshot/export lunghi vengono drenati; i report nuovi hanno letture brevi.

### 7.3 Apertura esistente e ReconcilingOutcome

F: il factory corrente omette Mode e puo creare un DB mancante, poi applica
PRAGMA. P: opener dedicato OpenExistingForRecovery, con owner, FileMode.Open
per identity guard, SQLite Mode=ReadWrite senza Create e senza initializer,
EnsureCreated, seed, migration o riconfigurazione applicativa. File mancante,
identita cambiata o schema non compatibile -> errore esplicito, non DB vuoto.

SQLite puo dover ripristinare un hot journal prima di leggere; una connessione
strettamente readonly puo fallire con SQLITE_READONLY_ROLLBACK. Per questo
l'opener consente il solo recupero tecnico sotto autorita, quindi verifiche in
transazione breve senza mutazioni applicative. Non rimuovere journal a mano.
Riferimenti primari:
[SQLite locking §4.1](https://www.sqlite.org/lockingv3.html#dealing_with_hot_journals),
[SQLITE_READONLY_ROLLBACK](https://www.sqlite.org/rescode.html#readonly_rollback).

ReconcilingOutcome usa quell'opener per cercare operation/receipt. Se il terminale
esiste, restituirlo; se manca dopo recovery riuscito, non dichiarare successo
e non applicare automaticamente. Restano invarianti/sentinel/schema e identita.
Recovery di installazione restore e un passo distinto, esplicito e posseduto:
non si invoca un bootstrap generico per far sembrare valido il DB.

| Evento | Esito |
| --- | --- |
| Doppio clic | Single-flight, stesso operation ID |
| Risposta persa, input rimosso o prossimo backup impossibile | Replay terminale autorizzato riesce senza quei prerequisiti |
| Import/undo o proof cambiati dopo preview | Transazione rifiuta piano stale; nessun ricalcolo silenzioso |
| Cancel precommit/crash precommit | Dati, revisioni e receipt assenti o tutti precedenti; rollback tecnico controllato |
| Crash dopo commit/risposta persa | Dati, revisioni e receipt tutti durevoli; consultazione operation ID |
| Replay dopo undo o altre revisioni | Receipt originaria + stato corrente, nessuna riattivazione |
| Stesso operation ID con altro shop/tipo/fingerprint | Conflitto; zero mutazioni |
| Disco pieno/file inaccessibile/backup fallito per nuova mutazione | FailedBeforeCommit, errore primario preservato |
| Esito I/O incerto | ReconcilingOutcome su esistente; mai creare DB o ripetere apply alla cieca |

Nessun lotto InProgress pubblicato dalla V1; dati/file/audit economico/receipt
terminali diventano visibili soltanto insieme al commit.

## 8. Report, routing e CSV

### 8.1 Consumatore reale e aggregazione

Ingresso F: MainWindow.OnMenuDailyReportClick e PosViewModel.CreateDailyReportViewModel
aprono il tab incorporato; DailyReportView contiene Giornaliero/Storico.
Il dialog alternativo riusa la view. Il testo attuale e Chiusura cassa:
rendere esplicito il prospetto analitico e mantenerlo distinto dalle operazioni
e stampe di chiusura.

Nuovo contratto piccolo ISalesReportQuery con consumatori reali WPF/CSV/grafico.
IDateRangeSalesQuery, DailyTakingsService e SalesQueryAdapter restano nativi:
oggi servono il percorso CLI e non alimentano questi report WPF.
Non introdurre un framework generico di query o trasformare lo storico in Sale.

Scope immutabile e autorizzato: shop ID, data commerciale [from,to),
origine, operatore/visibilita globale, policy ownership e fuso nativo,
dataset/epoch/data revision. Identita riga (Origin, InternalId).
Pagina, ricerca riferimenti, dettaglio, aggregati, grafico e CSV ricevono
lo stesso scope; dettaglio/export lo rivalidano prima della restituzione.

F: void nativo crea un record negativo e marca l'originale senza azzerarne il
totale. Il nuovo prospetto usa movimenti firmati, non copia il filtro del
Registro che esclude gli originali voided e puo sottrarre due volte:

- grossPositiveClp = somma delle vendite positive selezionate;
- reversalsClp = valore assoluto della somma dei movimenti negativi;
- balanceClp = somma firmata di ogni movimento una volta;
- cardClp = somma PaidCard firmata; cashTenderedClp = somma PaidCash;
- changeClp = somma Change; cashClp = somma checked(PaidCash - Change),
  cioe contante netto. Non confondere il denaro consegnato con l'incasso;
- originale + reversal restano entrambi, ciascuno alla propria data.

Esempio sintetico: +1000 al giorno D, -1000 a D+1: D=1000, D+1=-1000,
periodo=0. Il flag voided governa stato/capacita, non riscrive il giorno D.
Stessa regola per l'annullamento Getnet; nessun doppio collegamento per COD.AUT.

F: la vendita salva payment.CashAmountMinor e payment.GetChangeMinor(total)
separatamente (PosWorkflowService:1250-1252), mentre i reversal hanno Change=0.
Il prospetto analitico espone resto e contante netto senza riscrivere le letture
di chiusura. Verificare balanceClp = cashClp + cardClp per record validi; dati
nativi legacy incoerenti producono un'anomalia di riconciliazione e impediscono
di qualificare il prospetto come completo, senza correggere o scartare ricavi
silenziosamente. Fixture obbligatoria con tender maggiore del totale.

Grafico orario usa business datetime e saldo firmato, con barre negative
necessarie; somma uguale al KPI del medesimo filtro. Giorni marcati e singolo
giorno fissano anche l'export. Distinguere scontrini POS, movimenti nativi
negativi, transazioni storiche e annullamenti. Contanti storici/articoli assenti
non sono zeri commerciali. Badge "Importato · Getnet", filtro origine e
subtotali quando utili.

Il ramo nativo converte createdAt con policy fuso negozio esplicita e versionata,
validata per le date, senza ToLocalTime()/SQLite localtime del PC. Non riscrive
i record originari e non converte lo storico gia fissato con policy diversa.

### 8.1.1 Fotografia mista, ownership verificata e paginazione

Un filtro immutabile non congela i dati. SalesReportReadToken comprende
datasetId, restoreEpoch, authorityInstanceEpoch, data_revision,
scopeHash, ownershipPolicyHash e nativeTimePolicyHash. Nessun max(id),
timestamp ultimo aggiornamento o PRAGMA data_version su connessioni diverse.

data_revision e atomica con tutte le sorgenti della proiezione:
- sales INSERT/DELETE e UPDATE di data, importi/pagamenti/resto, kind,
  operatore, code/client ID, relazioni/void e snapshot usato come contraddizione;
- sale_lines usate dal dettaglio, per mantenere lo stesso token anche li;
- sales_sync_outbox: sale/client ID, shop origin, schema/operation, payload/hash
  e campi realmente consumati da N1/N2, non retry/attempt/status di trasporto;
- historical_transactions: inserimento, inclusione, dati economici/scope/data
  consumati; stato batch solo quando modifica l'inclusione effettiva;
- proiezione ownership e mapping quando cambia l'attribuzione ammessa.

Trigger nuovi in 0015, confronti null-safe e CHECK typeof(data_revision)='integer'
con limite Int64. Provare presenza/definizione nel detector; nessun checksum
precedente cambiato. Insert retrodatato e aggiornamento proof invalidano
anche se non cambia il massimo ID. Metadata/audit duplicate-only che non
cambiano la proiezione non invalidano; batch/receipt si consultano freschi.

N1 richiede verifica payload/hash prima dell'aggregazione, non dopo un filtro
SQL su origin_shop_id. sales_report_native_ownership e una proiezione DERIVATA
per sale_id, non un ledger alternativo. Enqueue canonico aggiorna la prova
nella stessa transazione. Trigger sui campi sorgente marcano Dirty e invalidano
il token. Legacy: lettura a blocchi e verifica bounded fuori lock, poi breve
transazione con confronto dei campi/fingerprint sorgente prima della pubblicazione.
Sorgente cambiata -> risultato scartato, non proof validata con dati precedenti.
Missing/Dirty nel perimetro rende il report PendingOwnershipVerification,
non un totale apparentemente completo con righe omesse. Unknown/CodeOnly
senza prova/NX sono decisioni terminali di esclusione secondo §6.1; nessun
fallback a shop corrente. Indice e join univoco su sale_id/proof valida.

La prima ricostruzione puo richiedere piu blocchi; interrompibile senza prove
parziali spacciate per complete. Il suo costo cold e misurato e visibile, non
nascosto nel budget warm. Aggiornamenti della proiezione avanzano data_revision
nella stessa transazione. Non deserializzare tutti i payload a ogni KPI.

Fotografia per consumer:
1. Caricamento iniziale: transazione di lettura BREVE, token/revisione e
   scope, aggregati + grafico + prima pagina nello stesso snapshot; chiusura.
2. Pagina/dettaglio/chunk CSV successivi: nuova transazione breve, confronto
   esatto token e dati limitati. Mismatch -> ReportSnapshotStale, nessun merge
   con risultati precedenti, nessuna continuazione del cursor vecchio.
3. Prima di pubblicare: token e autorita ancora validi; Dispatcher confronta
   versione richiesta/filtro/selezione e sostituisce il bundle con un solo
   stato immutabile. Tutte le superfici riportano lo stesso token.
4. Mutazione rilevata: invalidazione comune di header, grafico, pagine/cursor,
   dettaglio e selezione marcata; refresh esplicito. La vecchia fotografia
   puo restare visibile come precedente solo per mutazioni dati nello STESSO
   scope ancora autorizzato, identificata e con azioni sospese. Su logout,
   revoca o cambio operatore/shop rimuovere subito ogni contenuto finanziario
   precedente; il badge stale non lo rende autorizzato. Nessun mix di revisioni.

Per la race COMMIT/notifica: piccolo SalesReportSnapshotCoordinator per DB.
Tutti i commit ufficiali che modificano la proiezione passano da
CommitAndPublishReportRevision: acquisisce un breve publication gate PRIMA
del finale di autorizzazione, legge la revisione nella propria transazione,
esegue il commit e pubblica lo stamp immutabile prima del rilascio.
Hook mirati in InsertSaleAsync, refund/void, MarkSaleVoided autonomo, mutatori
outbox che cambiano prova, nuovo apply/undo e ownership refresher. Startup/
backfill senza consumer e restore inizializzano lo stamp prima di abilitare UI.
I trigger restano la garanzia dati anche per CLI; il helper chiude la race UI.

Sul Dispatcher solo TryEnter e confronto dello stamp, nessuna attesa/IO,
nessun accesso a _sync o latch. Se occupato, scartare/deferire il bundle;
notifiche dei binding dopo il rilascio, senza comandi annidati nel gate.
Ordine: coordinamento writer -> publication gate -> finale _sync/Gate
preesistente; mai un consumer UI che inverta l'ordine. Test di interleaving
obbligatori, non affidarsi alle sole notifiche asincrone.

CSV: chunk sotto token, temporaneo privato completo/chiuso, poi il protocollo
§8.3.1. Nessun lock DB mantenuto sino al rename. Il punto di autorizzazione
linearizza una fotografia verificata, non l'ultimo dato al momento della
visibilita filesystem; dopo il permit IO e riconciliazione non rileggono il DB.

Ordine totale unico:
(businessLocalTicks DESC, originOrdinal ASC, internalId DESC),
con Native=0/Historical=1 e ticks a precisione supportata; nativi millisecondi
convertiti checked, storico senza troncamenti. Cursor contiene tutta la tupla
piu token. Successivo: tempo minore, oppure tempo uguale/origine maggiore,
oppure tempo/origine uguali e ID minore. Ogni ramo legge pageSize+1, merge
limitato; niente OFFSET crescente o caricamento dell'intera storia.
Collisioni di timestamp e ID fra origini restano distinte. Un inserimento
retrodatato invalida il token, anche se cadrebbe in una pagina gia letta.

Intervalli locali -> UTC: Core riceve una tabella canonica congelata di
segmenti [fromUtc,toUtc), offsetMinutes, con ID/versione/hash/provenienza/
copertura, identica su Mac e net48. Le regole OS correnti Windows/IANA non
sono una fonte implicita. Fuori copertura -> policy non disponibile.
Approvazione e selezione della tabella sono esplicite nel flusso amministrativo;
sales_report_time_policies ne conserva corpo/hash/provenienza nel backup.
Una selezione di versione diversa invalida il token atomicamente, non modifica
le date commerciali gia fissate sulle transazioni Getnet.
Per ogni segmento calcolare la preimmagine dell'intervallo commerciale:
WHERE createdAt >= fromUtcMs AND createdAt < toUtcMs usa idx_sales_createdAt,
senza funzione timezone nel WHERE. Normalizzare intervalli disgiunti e giorni
marcati; fold include entrambi gli istanti, gap nessuno. Agruppamento giorno/
ora applica l'offset costante; cursore tradotto con limiti checked/rounding
esatto verso i millisecondi nativi. Storico usa business datetime persistito.

### 8.1.1.a Copertura e adozione del nuovo prospetto

F: GetDailySummaryAsync/GetDailySummariesRangeAsync correnti non attribuiscono
i nativi tramite N1/N2/N3/NX. Adottando la nuova query i valori possono cambiare
anche prima di importare Getnet: non chiamare questa differenza "zero vendite"
o nasconderla con fallback alle query globali.

Coverage e parte del bundle/token, UI e CSV, distinta dalle somme:
- NativePolicyStatus: NotRequired (storico puro), Ready, Missing, OutsideCoverage.
- AttributionStatus: Pending, VerifiedSubset, CertifiedForSelectedScope.
- SelectionStatus: HasAuthorizedMovements, VerifiedEmpty, NotEstablished.

| Condizione | Valori/UX autorizzati |
| --- | --- |
| Policy nativa necessaria Missing/OutsideCoverage | Prospetto indisponibile e motivo generico/configurazione autorizzata; valori non definiti, niente fallback PC o query globale |
| Proof Missing/Dirty | Verifica pendente; nessun totale complessivo definitivo. Eventuale subtotal dei soli ammessi e chiaramente "parte verificata" |
| Unknown/CodeOnly senza prova/Conflict terminali | VerifiedSubset: solo subtotal ammesso, copertura non certificata |
| Zero ammessi con attribuzione incompleta | "Nessun movimento attribuito nel filtro; copertura non certificata", non zero ricavi del negozio |
| Policy/attribuzione richieste certificate e scope vuoto | VerifiedEmpty: assenza verificata nel solo archivio/origini selezionati, non prova di tutti gli incassi storici del negozio |

Non esporre conteggi/importi degli esclusi, riferimenti/nomi/RUT di record
non attribuiti o altri tenant, neanche nei messaggi di errore/dettaglio/export.
Le azioni suggerite completano configurazione/verifica solo se autorizzate;
non elencano dati esclusi. Lo storico puro non dipende da policy nativa che
non consuma. Copertura Carta e periodo parziale restano visibili;
certificazione dello scope locale non promette tutti i pagamenti del negozio.

Prima dell'import Pending/Unavailable non diventano zeri. Percorsi operativi
di cassa nativi separati e invariati. CSV: record_type=coverage obbligatorio,
chiaramente non economico, con token, policy/stati/copertura e fotografia
autorizzata; colonne monetarie movimento vuote. Seguono soltanto
record_type=movement ammessi. Anche report senza righe conserva il metadata;
nessuna transazione fittizia e nessun dato escluso pubblicabile.

Variante F-MIXED-coverage: nativo A provato +1000, nativo U non attribuibile
+7000, storico A +9000. Somma ammessa10000, media POS1000, VerifiedSubset; tutti i KPI ereditano
l'etichetta della parte attribuibile, senza presentarsi come totali del negozio.
ID/valore7000 e conteggi esclusi assenti da bundle/UI/CSV. Varianti Pending,
policy mancante, storico puro e VerifiedEmpty distinguono indisponibilita
da zero con oracoli letterali.

### 8.1.2 Scontrino medio POS

F: TicketMedioDisplay, PeriodTicketAverageDisplay e MarkedTicketAverageDisplay
dividono NetAmount per SalesCount; HeaderSummary e stampa marcati propagano
lo stesso calcolo. Non applicarlo ai totali misti.

Definizione V1 unica "Scontrino medio POS (vendite)":
NativePositiveSalesClp / NativePositiveReceiptCount nello stesso scope.
Entrambi includono solo vendite native positive, anche se successivamente
stornate; storico e movimenti negativi esclusi da numeratore e denominatore.
Denominatore zero -> nullable/N/D localizzato, non 0. Decimal e arrotondamento
CLP esplicito AwayFromZero, senza double. Giorno/periodo/marcati/header/stampa
e export aggregato trasportano numeratore e denominatore; si sommano e poi
si divide, senza media delle medie. Media Getnet non aggiunta nella V1.

Oracoli sintetici: POS 1000 + Getnet 9000 -> media POS 1000, non 10000;
solo importati o soli negativi -> N/D. Il saldo economico misto rimane distinto.

### 8.1.3 Geometria oraria firmata

F: LoadHourlySalesAsync:515-530 normalizza sul solo massimo positivo;
HourlySalesPoint.BarHeightPx:1479 usa Math.Max(0,...); XAML:281-282 disegna
solo dal fondo. La parita numerica non dimostra un negativo visibile.

Modifica piccola del plot esistente, senza nuova libreria/runner: linea zero
comune e due meta nella stessa altezza disponibile, positivi sopra e negativi
sotto. Scala max(abs(amount)) in decimal anche per Int64.MinValue; solo
coordinate finali UI in double finite. Altezze positive/negative non negative,
segno determina lato/stile, tooltip conserva il valore. Tutti zero -> asse
senza barre; soli negativi -> barre effettive sotto zero. GetHourlyPeakText
distingue il movimento negativo da una giornata senza dati.

HIST07 mantiene hourly_parity e aggiunge hourly_signed_geometry: dopo
UpdateLayout e realizzazione container, BoundsIn/IsRenderedWithin esistenti
verificano asse visibile, ActualHeight negativo>0 sotto zero e dentro plot,
positivo sopra lo stesso asse, nessun clipping. Riusare D/D+1 soli negativi
e variante mista; somme corrette con barre invisibili devono fallire.
Focus/tastiera/DPI/dimensioni minime restano le prove previste.

### 8.2 Permessi, operatore e azioni

Riepilogo globale mantiene DailyCloseView. Registro mantiene RegisterView e
RegisterViewAll. Storico senza cassiere compare solo nella vista globale
autorizzata, mai in "mie vendite" o filtro operatore preciso; importatore non
diventa cassiere. Per i nativi il filtro indica l'operatore del singolo movimento,
anche quando il reso e registrato da un operatore diverso dall'originale.

Il ramo ricerca attuale bypassa il filtro operatore del ramo periodo:
nel percorso comune ricerca/dettaglio/export devono usare le stesse
predicazioni autorizzate, senza fallback unscoped.

Capacita storico: HasItemLines=false, CanRefund=false,
CanVoidNativeSale=false, CanReprintNativeReceipt=false.
Il livello operativo verifica origine/scope prima di qualsiasi inoltro a
GetSaleDetails/reso/ristampa nativi, anche se invocato fuori dal pulsante.
Nessun ID storico numericamente uguale a un SaleId passa a tali metodi.
Dettaglio dedicato di sola lettura: riferimenti/provenienza e
"Dettaglio articoli non disponibile". Nessuna boleta originale simulata.

Sessione cassa, chiusure consolidate, checkout e stampe chiusura restano nativi.
Eventuale stampa analitica usa un renderer dedicato e titolo riconoscibile.

### 8.3 Export tipizzato

F: EscapeCsv corrente sostituisce il punto e virgola con virgola, e lossy.
Serializzatore puro nuovo nel Core, senza estenderne ciecamente l'uso
ai contratti nativi non interessati:

- UTF-8, separatore ;, header/schema stabile; quote e CR/LF secondo CSV,
  doppio apice raddoppiato, nessuna sostituzione distruttiva dei caratteri.
- Testi esterni quotati e neutralizzati se iniziano, anche dopo whitespace/control,
  con =, +, -, @. Regola documentata per colonna Text, conservando grezzo nel DB.
- ClpAmount Int64 invariant senza migliaia: i negativi restano numeri, non
  vengono trasformati dalla protezione applicata ai testi.
- ID e codici rimangono stringhe nei byte, compresi zeri; niente formule Excel
  del tipo ="...". CSV non trasporta tipi: per preservare zeri nell'apertura
  Excel va importata la colonna come Testo. Non promettere auto-detect affidabile.
- Campi minimi: versione/origine/chiave, data commerciale, tipo/importi,
  provider/namespace/riferimento storico, lotto, ID nativo nullable e cassiere
  nullable. Nessun document_status fiscale inventato per Getnet.

SalesReportCsvSerializer/SalesReportCsvRow in Core: header e righe tipizzate,
quoting/neutralizzazione/formattazione, nessun WPF/Data/IO/sessione. Testabile
su Mac senza link indiscriminati di sorgenti WPF. WPF mantiene scelta percorso,
autorizzazione, IO e pubblicazione atomica: SalesReportExportService nel livello
WPF lavora su worker e usa le pagine Data sotto token. La query Data fornisce
la verifica breve del token; il coordinatore e OperatorSession emettono il
permit di §8.3.1, chiudendo tutte le risorse DB prima del rename in WPF.

L'export funzionale con riferimenti e dati reali e locale/privato, soggetto
a permessi e scope, mai artifact CI. Scrittura fuori Dispatcher, temp file
nella destinazione scelta e pubblicazione finale soltanto a completamento;
cancellazione/disco pieno non lasciano un report finale apparentemente completo.

### 8.3.1 Pubblicazione CSV: permit breve e IO fuori dai lock

F: il lock SQLite non serializza ChangeOperatorAuthority/InvalidateAuthorizationState,
che pubblicano sotto il Gate del latch. R2 manteneva SHARED durante rename:
non chiudeva la race di revoca e bloccava writer durante IO non limitabile.

Un solo protocollo minimo:
1. IO su worker: bytes completi in temporaneo privato, stream chiuso, hash/byte
   count; token, copertura e intenzione destinazione/overwrite catturati.
   Nessun dato finale visibile sotto il nome destinazione.
2. Verifica breve scope/token sul DB esistente; chiudere transazione, connessione
   e ogni guard DB. Nessuna lettura lunga sino al filesystem.
3. Validazione completa lease/store fuori dai monitor e dalle connessioni DB,
   catturando proof/versione/scadenza; rilasciare _sync. Poi breve gate del
   SalesReportSnapshotCoordinator e confronto dello stamp committato col token.
   Nessun IO nel successivo callback e nessun _sync riacquisito sotto Gate.
4. OperatorSession usa il confine esistente TryReadOperatorAuthorityIf del
   latch: ricontrollo in-memory di epoch, versione operatore, permesso esatto,
   generation e scadenza; cattura SalesReportPublicationPermit immutabile.
   Bind a export ID, token/policy/copertura, hash/byte, destinazione e policy
   overwrite. Ultimo cancel/scope-version check in-memory e single-flight
   dell'export impediscono doppio avvio. Nessuna SaleKind o guard vendita fittizio.
5. Punto 4 e la linearizzazione. Rilasciare gate, lease di ammissione e monitor;
   soltanto ora iniziare pubblicazione destinazione, fuori da tutti i lock DB
   e autorizzazione. Nessun callback UI/logging/filesystem sotto latch.

Ordine: preflight lease/store (_sync) e rilascio -> coordinator -> Gate con
soli check in-memory. Mai _sync/store/DB/filesystem sotto quel callback.
I writer ufficiali pubblicano lo stamp prima del
rilascio del proprio confine: mutazione prima del permit -> ReportSnapshotStale.
Revoca/cancel prima del permit -> pubblicazione negata. Revoca/cancel o dati
cambiati dopo -> completamento gia autorizzato per il token del permit, senza
ulteriori letture sotto autorita scaduta. Il file non promette di essere lo
stato piu recente al momento della visibilita.

Fase destinazione dopo permit non cancellabile: UI "Finalizzazione", attesa
del ritorno reale, nessun Task.WhenAny che abbandona un publisher. Se occorre
staging same-volume, copiarvi dopo il permit i bytes gia congelati e chiudere
prima della primitive finale; niente lock DB/autorita mentre copia o rename.
Atomicita finale solo con primitive supportata dalla destinazione: se non
garantita, errore esplicito, niente fallback delete+move.

Default NoOverwrite: target presente/apparso -> errore e preservazione.
ReplaceApproved: consenso esplicito per quel percorso prima del permit,
sostituzione atomica supportata, senza cancellazione preventiva. Il consenso
e riferito al path; non si promette un CAS sull'identita sotto writer filesystem
arbitrari. Nessun overwrite automatico dopo un esito incerto.

Esiti separati: authorizationGranted, snapshotAuthorized, publicationOutcome,
destinationIoElapsed e thresholdsPassed. Il budget 1 s e una misura sulla
fixture, non una cancellabilita garantita di rete/storage. Rename riuscito
oltre budget -> Published funzionale e FAIL prestazionale, mai "nessun file".
Dopo errore ambiguo attendere ritorno e riconciliare solo target/temp con
hash/byte del permit: contenuto coincidente -> pubblicazione osservata;
mancata pubblicazione dimostrabile -> NotPublished; diverso/non verificabile
-> PublicationOutcomeUnknown e decisione manuale. Niente retry/letture DB.

Dentro HIST07, casi chiusi: revoke_before/after_permit, data_before/after_permit,
cancel_before/after_permit, destination_error, delayed_success e
preexisting_no_overwrite/approved_replace. Durante IO ritardato dimostrare
writer POS e revoca non bloccati dall'export. Verificare filesystem osservato,
token dichiarato e verdetto prestazionale separati; nessun nuovo runner.

## 9. UX, sicurezza input e reattivita

Stati: Selection -> Analyzing -> PreviewReady / NeedsEvidence / PreviewBlocked.
Poi WaitingMaintenance -> PreparingBackup -> Applying -> Committing ->
Completed oppure ReconcilingOutcome. Prima del commit: CancelRequested ->
RollingBack -> Cancelled. PreviewStale e FailedBeforeCommit hanno motivi
specifici e azioni per riprendere.

Selection: piu XLSX, profili, shop destinazione, esercente, CLP e interpretazione
oraria visibili. Preview: periodo effettivo/parziale, vendite/annullamenti/
respinte/duplicati/conflitti/scarti, lordo e saldo delle sole nuove righe,
netto e copertura distinti. Receipt: conteggi/somme, operation/batch ID,
periodo e gestione lotto; default non distruttivi.

NeedsEvidence non impedisce la preview ma disabilita apply con il dato mancante.
Errori devono distinguere file inaccessibile/corrotto, valuta, formula, header,
conflitto, dati cambiati, lease, backup, manutenzione e disco pieno.
Nessun messaggio generico "successo" con scarti economici nascosti.
Escape annulla prima del commit quando sicuro; durante commit indica attesa
esito. Focus iniziale/ripristino, Tab/Enter e messaggi localizzati coerenti.

Dialog categoria D: DialogShellWindow, CenterOwner, owner
OwnerWindow ?? DialogOwnerHelper.GetSafeOwner(), risorse title/footer comuni;
niente Left/Top, Loaded sizing o clamp fuori base. Non riportare progress/griglie
nell'input dell'ombra corretto dai commit recenti. 1024x768 richiesto,
1024x600 best effort documentato; DPI 100/125%, nested/monitor da qualificare.

Reader: ExcelDataReader 3.9.0 e ClosedXML 0.105.1 gia referenziati, senza upgrade.
Non copiare il reader fornitori che trattiene tutte le stringhe, formatta con
CurrentCulture, arrotonda quasi-interi e puo perdere l'ora DateTime.
Riutilizzare soltanto helper di IO/limiti/XML/cancellazione realmente sufficienti,
con estrazione minima e test dei consumer precedenti se estratti.

Limiti proposti, da verificare contro gli originali PRIMA del congelamento:
20 file, 32 MiB compressi/file e 128 MiB/batch; 4096 entry ZIP/file;
256 MiB decompressi/file, 1 GiB/batch; metadata workbook/relazioni 4 MiB;
60000 righe/foglio, 100000 selezionate, 256 colonne, 2 milioni di celle/file
e 4 milioni/batch, 32767 caratteri/cella,
16 milioni caratteri trattenuti essenziali nel piano.
Contare i byte realmente letti, non fidarsi solo delle dimensioni ZIP dichiarate.
Limite esplicito su shared strings e XML, DTD proibite/XmlResolver null,
nessuna estrazione ZIP su percorso arbitrario. Timeout analisi e token
controllati per blocchi piccoli, anche su XML/shared strings.

Formule nei campi contrattuali sono bloccanti, senza usare cache Excel o
valutazione ClosedXML. Riferimenti workbook esterni non vengono risolti:
dipendenze economiche esterne bloccanti; hyperlink passivi ignorati, mai aperti.
Rifiutare encrypted, xlsm, HTML travestito, XLS o altro formato non supportato
nella V1 con diagnostica precisa. Nessuna esecuzione macro/rete.

Parsing/hash/dedup/backup/apply/CSV su worker; snapshot immutabili, niente accesso
a ObservableCollection UI dal worker. Preview/pagine 200 righe e recycling,
mai tutte le 76 colonne per ogni riga in DataSet duplicati. Progress massimo
5 notifiche/s, non una per record. Query/lookup dedup a blocchi con parametri
entro limite SQLite; paging keyset e aggregazioni SQL, senza caricare tutto
lo storico per un KPI.

## 10. Performance da qualificare

Budget proposti, NON misure. Congelarli nel manifest/protocollo prima delle
corse; cambiarli richiede una revisione motivata e nuova qualifica, mantenendo
il FAIL precedente. Nessun rialzo retroattivo.

| Metrica/unità | Windows build host di riferimento | Win7 SP1 target minimo identificato |
| --- | --- | --- |
| Analisi dataset sintetico 50000 righe, elapsed | <=30 s | <=60 s |
| Apply DB incluso commit, esclusi backup e attesa ammissione | <=15 s | <=30 s |
| Backup verificato su DB fixture di dimensione dichiarata | <=60 s | <=60 s; baseline del DB reale separata |
| Drain manutenzione / busy SQLite | 30 s / 5 s, limiti correnti | Identici |
| Incremento PrivateBytes sul carico | <=150 MiB | <=150 MiB |
| Picco CAMPIONATO PrivateBytes complessivo | <=700 MiB | <=700 MiB |
| Dispatcher heartbeat p95 / massimo | <=100 ms / <=500 ms | Identici |
| Cancel analisi, riscontro dal token | <=1 s | <=1 s |
| Cancel apply cooperativo e rollback | Riscontro <=1 s; rollback <=10 s sulla fixture; chiamate native pendenti misurate a parte | Identico; busy/fence possono richiedere il limite SQLite corrente |
| Query mista aggregato/pagina con proof gia valide, warm | <=500 ms | <=1000 ms |
| IO finale CSV su destinazione fixture qualificata | <=1 s osservato, fuori dai lock; successo oltre budget resta Published | Identico; non garanzia di cancellabilita per destinazioni arbitrarie |
| Ricostruzione cold proof sui 10000 nativi F-MIXED | <=30 s | <=60 s |

Misurare separatamente avvio processo, generazione fixture, parse, dedup,
hashing, proof cold/warm, backup, attesa ammissione, insert/trigger, commit,
rollback, query mista e export. I costi nuovi di revision/proof non sono esclusi
dal servizio. Riconciliare tempo servizio con tempo processo e supervisione;
nessun PASS sulla sola fase piu favorevole.

Protocollo HISTORY-MEASUREMENT-v2 congelato prima delle corse:
- Thread background dedicato, Stopwatch monotono, tick nominali 50 ms; puo
  osservare lo stallo anche mentre il Dispatcher non esegue callback.
  Al massimo un proprio probe Dispatcher pendente, priorita dichiarata.
- Ogni slot: sequence, phaseId, dueTicks, observedTicks, probeId,
  posted/completedTicks o pendingAge. Ogni fase ha start/end monotoni e
  expectedSlots ricalcolato dal validator dalla griglia temporale, non fidato
  dal valore autocompilato. Slot mancanti/saltati registrati come gap.
- Il thread continua a registrare l'eta del pending durante lo stallo.
  Niente burst di campioni inventati al risveglio o recupero retroattivo di
  tick. Callback tardiva conserva la latenza completa dall'originale post.
- Sequenze uniche/ordinate, expected/observed/completed/pending/dropped/overflow
  riconciliati. Gap non spiegati, overflow, drop, pending finale o fasi senza
  campioni obbligatori rendono measurementCompleted=false. Venti superstiti
  non bastano. Almeno 20 probe completi nel carico e tutti gli slot previsti.
- P95 nearest-rank ceil(0.95*N) delle latenze dei probe reali completati;
  nessun outlier/cold rimosso. Il massimo include il lower bound dei pending:
  uno stallo non scompare perche non ha ancora completato.
- PrivateBytes su background ogni 100 ms, propria sequenza/expectedSlots/gap.
  E un PICCO CAMPIONATO, non una misura garantita dell'assoluto picco istantaneo.
  Nessun GC forzato; buffer bounded con overflow esplicito.
- Suspend/resume, clock awake incoerente, sessione/desktop non qualificati
  producono environmentValid=false. Non interpolare attraverso sospensioni.
- Campi separati measurementCompleted, environmentValid, thresholdsPassed.
  PASS richiede tutti e tre veri, receipt/identita valide e supervisione conclusa.
  Il validator cart esistente conserva il proprio contratto, non certifica import.

Registrare CPU/RAM/storage/OS/architettura/CLR e toolchain, versione policy,
manifest e protocollo. Una variazione dei budget e una nuova revisione prima
delle prove successive; il precedente FAIL resta. Full load e smoke distinti:
F-LOAD verifica import, una sola estensione F-MIXED verifica query/join/proof/
filtri, senza moltiplicare i benchmark. Gli originali sono un gate privato.
Il POS non promette throughput vendite durante manutenzione: qualificare
rifiuto dell'ammissione, reattivita UI e ripristino scanner/checkout.

## 11. Inventario strumenti e modifiche future

| Tool/file osservato | Capacita reale | Decisione futura/test |
| --- | --- | --- |
| SupplierExcelImportReader/Limits | Bounds/token/XML, conversioni non adatte | Riuso selettivo; reader Getnet nuovo, precisione/bounds/regressione helper |
| SqliteOnlineBackup | Snapshot verificato pubblicato completo | Riuso; backup prima fence, fail/cancel senza apply |
| SchemaMigrationRunner/Registry | Ledger/checksum/detector e backup migration | Estensione 0015 per dati, operation receipt, proof/revision trigger; immutabilita storico |
| SqliteConnectionFactory | Open puo creare; OpenAsync attende RegisterConnection sync | Authority demand, opener esistente per recovery, registrazione async cancellabile |
| OperatorSession/revocation latch | Commit final fence, ma gate use/maintenance senza token | Capability dedicata e overload ct; race/expiry/ownership waiter |
| App/CLI/harness/coordinator | Bootstrap diversi, alcuni read nominali fanno seed | Lease condivisa Data prima di ogni primo write, tabella entry point §6.3 |
| Writer nativi/outbox/proof refresher | Commit separati nelle facciate interessate | Hook circoscritti CommitAndPublishReportRevision, economia invariata; interleaving UI |
| New-QaPlan | Cinque runner Windows attuali | Tre step storici (smoke/load/crash), otto in All, selezione con dipendenze chiuse |
| Invoke-QaProcess | Ogni chiamata crea work/data root nuova e Job posseduto | Una sola chiamata per CrashProbe; nessun secondo launcher |
| Invoke-QaPlan/Assert-QaEvidence | Partial tests oggi specificamente functional | Raccolta/checkpoint storico su ogni raw status; structural/privacy separata da PASS |
| Export-QaSmokeEvidence | Allowlist nomi/limiti, non catena upload | DTO sicuri anche parziali + proiezione finale/manifest di upload |
| Test-Win7PosAutonomousTests | AST/mock e Runtime PowerShell sintetico | Otto mapping All, negativi di receipt/partial/crash/proiezione, Runtime senza WPF |
| check-owned-catalog-qa / required gates | Node18+, test supervisore e output gate completo | Riusare, nessun gate generale duplicato o output troncato |
| run-functional-completion-smoke | 20 risultati distinti, 60000 ms/processo | Invariato; crash/storico fuori dal functional20 |
| UiSmokeHarness.Program | System.Windows.Application, --data-dir e automated dispatch | Owner prima init/seed, nuovi flag e verifier senza bootstrap |
| Win7PosPerformanceValidation | Specifico cart; concetti completed/environment/threshold | Non usarlo per import; validator storico v2 con sampler background |
| csproj Core.Tests/harness | Solo corpus attuali; harness non copia fixture storiche | Content/CopyToOutputDirectory minimi, hash/lookup da BaseDirectory |
| ci.yml upload autonomous QA | Ricorsivo qa JSON/TXT/LOG/TRX/CSV/PNG, non solo smoke | Upload due percorsi esatti del pubblico validato; test canali/ZIP/manifest |
| wpf-build/release-pack letti | Output WPF/functional/dist, non la root raw storica | Solo wpf-build: bundle QA manuale net48/x86 separato §16.1; Release Pack operativo invariato |
| Suite Win7/Hardware | BLOCKED_EXTERNAL, niente executor fisico | Checklist del candidato, nessuna qualifica dedotta |
| Supabase/staging/cloud | Nessun protocollo storico V1 | Fuori scope, nessuna invocazione |

Il test AST resta una tecnica dei test del supervisore: non e l'API operativa
per i worker. Non dot-source dell'orchestratore con entrypoint operativo.
Se un helper va riusato, estrarre solo funzioni pure in un modulo minimo,
senza duplicare launcher/Job/identita/cleanup.

## 12. Harness, crash posseduto e pubblicazione delle evidence

Tutti i nuovi script/flag/receipt sotto sono PROPOSTI, non disponibili oggi.
Wrapper scripts/run-historical-sales-import-smoke.ps1 con Mode Smoke/Load/
CrashProbe. Il solo processo esterno e avviato da Invoke-QaProcess: stessa
supervisione/Job, root breve isolata, deadline/cancel/cleanup/checkpoint.
Il wrapper invoca il harness sequenzialmente con &, senza Start-Process,
gestione autonoma di WaitForExit/Job, kill per nome, nuovo Invoke-QaProcess,
dot-source operativo o estrazione AST nel runtime. Per i soli holder/contender
B riusa il modulo esistente esteso come §12.2, non un launcher nuovo.

Nuovi flag harness --historical-sales-import-smoke, --historical-sales-import-load,
--historical-sales-import-crash-probe, --historical-sales-import-crash-verify,
--historical-sales-import-foundation;
riuso --data-dir e --diagnostics-dir; contesto nuovo --run-id, --run-nonce,
--source-fingerprint, --fixture-version, --receipt e hash binari/manifest.
D e il --data-dir canonico derivato da WIN7POS_DATA_DIR; nessun DB sotto la
root lunga dei report. Ogni invocazione acquisisce il proprio owner prima DB.

New-QaPlan Windows:
| Step | Dipendenze | Evidence/budget |
| --- | --- | --- |
| historical-sales-import-crash | build-harness, soli componenti B | historical-sales-foundation-v3; supervisore360 s, corpo<=330 s, cinque scenari B sotto |
| historical-sales-import-smoke | build-harness | historical-sales-smoke-v3; supervisore 90 s, corpo <=60 s |
| historical-sales-import-load | build-harness + smoke storico | historical-sales-load-v2; supervisore 240 s, corpo <=180 s |

Nuovo parametro orchestratore -HistoricalSalesMode All/Smoke/Load/CrashProbe,
default All. Seleziona solo i nuovi step; conserva cinque runner, required
gates/build e altre dipendenze. All=8 runner, Smoke=6, CrashProbe=6,
Load=7 includendo il suo prerequisito Smoke. Non lasciare Load dipendente da
uno step escluso. Crash B non dipende dallo smoke C. Failed cleanup blocca
i dipendenti come oggi; un FAIL crash con cleanup confermato lascia raccogliere
gli altri step indipendenti, mantenendo FAIL finale. Functional20 e budget
60000 ms invariati.

### 12.1 Receipt smoke e load

Schema proposto smoke win7pos-historical-sales-qa-v3, protocolVersion=3,
nome privato historical-sales-results.json.
Scenari/assertion obbligatori preservati e completati:

| Scenario ID | Assertion ID obbligatori |
| --- | --- |
| HIST01_PROFILES_PREVIEW | profiles_detected, header_mapping, exact_values, text_ids, policy_visible, invalid_input_blocked |
| HIST02_APPLY_REOPEN | durable_receipt, manifest_totals, reopened_records, operational_invariants |
| HIST03_IDENTITY_DEDUP | unchanged_zero_new, renamed_reordered_overlap_zero, distinct_ids_kept, conflict_blocked |
| HIST04_STALE_AUTH_SCOPE | permission_revoked, operator_changed, shop_changed, epoch_changed, precommit_revocation |
| HIST05_ATOMIC_CANCEL_RECOVERY | cancel_rollback, precommit_fault_rollback, disk_fault_safe, lost_response_reconciled |
| HIST06_BATCH_CANCEL_REIMPORT | owner_only_undo, duplicate_batch_not_owner, three_cycles, audit_retained |
| HIST07_REPORTS_EXPORT | signed_totals, hourly_parity, operator_scope, tenant_scope, detail_routing, csv_contract, native_ticket_average, snapshot_invalidation, export_publication_authorization, export_publication_outcome, coverage_state_no_disclosure, hourly_signed_geometry |
| HIST08_UI_LIFECYCLE | keyboard_focus, dialog_standard, nonblocking_dispatcher, stale_result_ignored, reopened_receipt, retry_empty_ui, retry_expiry_ui, retry_revocation_ui, retry_generation_ui, retry_cancel_ui, retry_normal_late_authority_ui, retry_normal_finalization_fault_ui, retry_normal_ready_ui |

HIST05 failpoint controllati non sono un vero crash: B-D07 e separato.
Envelope chiuso: runId/sourceSha/sourceFingerprintSha256, hash harness/WPF/
fixture, versioni policy/protocollo, start/end/duration monotoni, identita
processo e architettura, status/completed, summary e results, lastCheckpoint.
Niente testo libero da celle/Exception.ToString. Ogni risultato contiene ID,
rawStatus, completed, durationMs, assertionResults e soli counters del manifest.
PASS smoke: tutti otto e ogni assertion completati; summary riconciliato.
Checkpoint atomico dopo ogni scenario e fase, con flush; timeout dopo HIST03
deve conservare quei risultati e l'ultimo checkpoint, senza promuovere i restanti.

Load: historical-sales-load-results.json, schema
win7pos-historical-sales-load-v2, protocolVersion=2. Un solo HIST_LOAD_50K
con check ID: fixture_identity, exact_counters, phase_measurements,
memory_budget, dispatcher_budget, cancellation_budget, mixed_query_budget,
ownership_cold_warm, snapshot_invalidation, maintenance_writer_exclusion,
environment_identity. Contatori F-LOAD/F-MIXED e campioni §10, inclusi
measurementCompleted/environmentValid/thresholdsPassed. La fixture mista
estende la medesima corsa, non un secondo benchmark ridondante.

PID bootstrap PowerShell diverso dal PID harness: correlare runId/nonce,
hash, process creation time, intervallo e Job, non uguaglianza dei PID.
PASS richiede anche exit esterno corretto, cleanup e zero processi rimasti.

### 12.2 CrashProbe: gate B completo, stesso DB e recovery reale

Il comando B e C-CRASH con -HistoricalSalesMode CrashProbe, non C-WINDOWS
default All. Build/gate/Core/CLI e cinque runner preesistenti restano; Smoke/
Load/report C non sono dipendenze. La receipt B contiene esattamente cinque
scenari e tutte le varianti sotto, non certificati dal solo hot journal.

| Scenario B | Assertion/varianti chiuse obbligatorie | Requisiti |
| --- | --- | --- |
| HIST_CRASH_HOT_JOURNAL | same_existing_database, prepared_sentinel, reached_after_spill, expected_abrupt_exit, journal_recovery_completed, atomic_data_and_revision, operational_invariants, owned_process_cleanup | B-D07 |
| HIST_B_COMMIT_AUTHORITY | apply_revoke_before_insert, apply_revoke_precommit, undo_revoke_precommit, expiry_before_commit, operator_permission_changed, linearized_commit_once | B-D03 |
| HIST_B_PROCESS_ADMISSION | real_holder_contender, sqlite_busy_bounded, workflow_cancel_no_orphan, auth_cancel_no_orphan, maintenance_cancel_no_orphan, lock_order_no_deadlock, commands_cart_restored | B-D05/B-D12 |
| HIST_B_ENTRYPOINT_AUTHORITY | wpf_normal_rejected_before_init, wpf_safe_rejected_before_init, cli_rejected_before_seed, harness_rejected_before_init, distinct_databases_admitted, case_shortpath_alias_collide, reparse_hardlink_rejected, restart_reacquires | B-D05/B-D12 |
| HIST_B_BACKUP_RESTORE | backup_validate_publish, aux_scope_reject, candidate_swap_rebind, rollback_restore, epoch_stales_plan, mapping_fk_preserved, reimport_zero, all_handles_released | B-D10/B-D13 |

B usa servizi/capability/backend e bootstrap reali disponibili nella tranche.
Catalogo in HistoricalSalesCrashProbe.cs nel medesimo harness: niente wizard
o report C. Wrapper Mode CrashProbe invoca anche il nuovo flag PROPOSTO
--historical-sales-import-foundation con scenario/variante chiusi e fixture
autonome sotto D; crash/verify ricevono invece lo STESSO path canonico.
Nessun runner aggiunto per requisito.

Concorrenza: & sequenziale basta per crash/verify, non per holder/contender.
Estendere minimamente l'esistente Invoke-Win7PosWaitedProcess in
Win7PosAcceptanceProcessRunner: callback interno OnStarted/Ready, deadline
assoluta comune, identity da handle/PID/creationTime/exeHash, ritorno/cleanup
bounded. Rimuovere il secondo WaitForExit illimitato e il check del solo PID;
test in Test-Win7PosOwnedProcessIdentity esistente. Stesso Job esterno
ereditato, niente launcher parallelo o nuovo Job.

Callback attende readiness nonce-bound e avvia il contendente con lo stesso
helper e tempo residuo. Hook nei veri bootstrap WPF normale/safe, CLI e harness,
dopo owner e prima del primo write; non due copie artificiali del sidecar.
Holder CLI/harness evita che il mutex UI WPF mascheri l'ammissione DB.
Contendente WPF in QA posseduta attraversa il bootstrap vero senza dialogo
inatteso. DB distinti: holder CLI/harness A e contendente B ammessi; non si
promette una seconda UI contro il single-instance WPF preesistente.
Solo root sintetica/nonce validi, niente account/rete/DB operativi.
Sentinel/hash del DB negato invariati, init/seed non eseguiti; errori e timeout
rilasciano gli esatti processi e lease posseduti.

Budget aggregato NUOVO e congelato prima delle prove: 360 s supervisione,
330 s corpo. Hot journal<=90 s (limite R2 preservato), commit authority<=25,
admission<=30, entrypoint<=120, backup/restore<=45, checkpoint/report<=20.
L'aggregato include quattro scenari B aggiunti; non rialza il limite crash,
functional20 o i cinque runner e non converte fallimenti in PASS.
Smoke90/60 e Load240/180 invariati. Superamento -> raw TIMEOUT/FAIL.

Un solo step esterno crea UNA root sintetica nuova D. Wrapper:
1. Genera nonce N, registra run/source/binari/fixture e invoca con &
   harness --historical-sales-import-crash-probe --data-dir D, contesto N.
2. Cattura subito raw exit. Solo exit riservato 79 e checkpoint atteso
   consentono la fase verifier. Uscita/startup diversa -> FAIL, non "crash OK".
3. Invoca con & il medesimo binario --historical-sales-import-crash-verify
   con D/N identici; nessuna nuova root e nessun Invoke-QaProcess interno.
4. Raccoglie receipt, senza cambiare raw status worker; il Job esterno
   supervisiona entrambi. Timeout/cancel/cleanup non confermato impediscono PASS.

Worker, sotto owner:
- Prepara esplicitamente F-CRASH nel DB sintetico: schema corrente, sentinel
  durevole {runNonce,datasetId,fixtureVersion,baselineId}, oracolo dati e
  revisioni iniziali. Checkpoint prepared con identita fisica/slot/hash.
- Chiude la preparazione e usa il vero apply su DB esistente; modifica abbastanza
  pagine per spill reale con DELETE/FULL/cache del prodotto invariati.
- Prima del failpoint verifica journal con header valido e dati realmente
  spillati nel main, non solo un file -journal non vuoto. Checkpoint
  precommit_after_spill contiene nonce/identita, stato fase, hash main prima/
  durante e caratteristiche journal; flush durevole FUORI dalla transazione.
- Termina il SOLO processo corrente tramite helper OS di test per terminazione
  improvvisa con exit 79, senza unwind/dispose/rollback applicativo. Non copiare
  il vecchio launcher CLI e non chiamare questa prova power-loss hardware.
  Il Job e la lease OS rilasciano i handle alla morte del worker.

Verifier:
- Prima di aprire SQLite controlla esistenza, identita attesa, checkpoint
  prepared/failpoint, nonce e caratteristiche del journal. Se manca il DB,
  e diverso, e ricreato o sentinel non coincide, FAIL; niente init/seed.
- Acquisisce owner per lo stesso slot e OpenExistingForRecovery (§7.3),
  Mode=ReadWrite senza Create. L'apertura/lettura puo eseguire il recovery
  tecnico SQLite; non si promette readonly assoluto e non si elimina journal
  dal wrapper. Eventuale SQLITE_BUSY/recovery fallito e FAIL finito.
- Poi transazione breve senza mutazioni applicative: sentinel e baseline
  invariati, dati/revisioni/receipt import precommit assenti, invarianti
  native/stock/cart/outbox/cassa integri, integrity/FK validi.
- Dimostrare journal prima del recovery, spill e recupero completato secondo
  DELETE; non inferire tutto da "nonzero + zero batch". Un'eccezione controllata
  rimane un test distinto. Postcommit/risposta persa resta B-D08.

Receipt historical-sales-crash-results.json, schema
win7pos-historical-sales-foundation-v3, protocolVersion=3: cinque risultati B
e varianti esatte sopra, summary expected=executed=passed=5 per PASS;
run/nonce/source/binari/fixture,
prepared/failpoint/verifier checkpoint, process identity distinte, physical
identity privata, workerRawExitCode=79 e workerRawStatus=FAIL, verifier exit,
sentinel/baseline/recovery/invariants, completed/status esterno.
Il risultato HIST_CRASH_HOT_JOURNAL conserva i check ID:
same_existing_database, prepared_sentinel, reached_after_spill,
expected_abrupt_exit, journal_recovery_completed, atomic_data_and_revision,
operational_invariants, owned_process_cleanup.
Startup fallito, checkpoint mancante, timeout, causa diversa o verifier non
avviato non soddisfano il protocollo. Il PASS esterno certifica gli invarianti
attesi, non riscrive in PASS il raw nonzero del worker.

### 12.3 Diagnosi sicura e certificazione PASS sono contratti distinti

ValidateHistoricalEvidenceStructureAndPrivacy accetta complete, fallite o
parziali con schema chiuso, tipi/enum/versioni/nonce/identita e contenuto sicuro.
Puo mancare la parte non eseguita; checkpoint elenca solo risultati conclusi
piu il punto d'arresto, senza inventare assert. Proprieta sconosciute, chiavi
JSON duplicate, campi sensibili o identita incoerente respingono il payload.

CertifyHistoricalEvidencePass richiede invece tutti scenari/assert richiesti,
counters/metriche/ambiente, causa ed exit attesi, completamento e cleanup.
Un payload strutturalmente valido FAIL/TIMEOUT non e certificabile PASS.
Contesto atteso viene dal run/manifest, non da expected autocompilati.

Invoke-QaPlan estende row.tests e lastCheckpoint per TUTTI i tre tipi storici,
su ogni raw status, PRIMA di eventuale certificazione. Un payload insicuro
resta nel raw privato, escluso dal pubblico con diagnostico enum
qa_history_receipt_privacy_rejected; primo errore invariato. Raccolta sicura
delle parti concluse non cancella TIMEOUT/FAIL/NOT_RUN.

Negativi futuri:
- absent/empty/corrupt/stale/wrong run, duplicate JSON keys, extra fields,
  hash/version/policy/identity differenti;
- zero/missing/duplicate/unknown scenario o assertion; summary/counters errati,
  fractional CLP, somme con respinte, completed falso;
- metriche non finite, gap/drop/overflow/pending/suspend, ambiente non valido,
  soglie superate e file modificato dopo hash;
- exit0 senza assert, worker exit diverso da 79, failpoint non raggiunto,
  DB assente/ricreato/sentinel diversa, verifier non avviato o recovery non provato;
- timeout dopo HIST03 con tre risultati conservati; cancel/report non scrivibile,
  export fallito, residui Job/identita errata, dipendenze bloccate e primo errore.

Test-Win7PosAutonomousTests esercita i blocchi reali con AST solo NEI TEST,
non un runtime alternativo. Runtime resta di soli figli PowerShell sintetici;
il crash DB vero appartiene al gate Windows CrashProbe.

### 12.4 Console, diagnosi disponibile e bundle del run corrente

F: ci.yml carica oggi glob raw; R2 li sostituisce con una coppia sicura.
R3 chiude anche lo stream principale: nel catch finale Write-QaReports
l'orchestratore:610 serializza firstError intero su stderr e :612 stampa
evidence path. Il wrapper functional:216-223 espone fallback analoghi.
Due file di upload sicuri non impediscono questo leak console.

Write-QaPublicConsoleEvent, helper piccolo nello stesso gruppo QA:
DTO chiuso con schemaVersion, runId, suite, stepId, scenarioId/assertionId
opzionali, rawStatus, code, secondaryCode e diagnosticCompleteness.
ID da registro/manifest, status/codici enum; sconosciuti -> unknown /
qa_unclassified_failure. Niente row, Exception.Message/ToString, arguments,
host o path assoluti. Sostituire catch/preflight/report/proiettore e conclusioni,
compresi wrapper pubblicamente invocati. Child stdout/stderr gia catturati
rimangono privati, non vengono ristampati come fallback CI.
Il workflow cattura inoltre stdout/stderr dell'orchestratore e dei wrapper
pubblicamente invocati in un file raw privato e ripubblica solo DTO validati;
non ristampa altre righe. Cosi anche errori prima del catch/module load o
fallback host non passano come output libero. E redirezione dell'invocazione
esistente, non un launcher/Job nuovo; output CI run/flag restano nel canale
controllato. Il collector fallito emette soltanto una costante sicura.
Serializer/DTO fallito -> sola costante
WIN7POS_QA_PUBLIC_EVENT_FAILED code=qa_safe_console_failed.
Run/step ID correlabili, non percorso assoluto. firstError raw intatto.

Raw: OutputRoot/<runId> e root dati brevi conservate, input integri e transcript
completo dei gate falliti. Nessuna pulizia/redazione in-place. Hosted retention
raw limitata al job, dichiarata; originali richiedono host privato autorizzato.
Pubblico deve pero conservare diagnosi UTILE dopo il job, non solo PASS/FAIL:

| Produttore esistente | Dettaglio pubblico sicuro e consumer |
| --- | --- |
| Required gates/Core | Gate/test/assertion-template ID, expected/actual tipizzati, checkpoint, posizione source-relative verificata, transcript ordinato |
| Product-image-profile | Check serializzazione/error mapping, expected/actual, fase e runtime |
| Functional20 | Tutti venti esiti/parti concluse, failed assertion, expected/actual, ultimo checkpoint, tempo/budget/cleanup e capture sintetiche ammesse |
| Authorization-lease | Causa/modo attesi e osservati, alias sintetici epoch/generation, checkpoint expiry/restart |
| Bounded-logging | Counters/drop/budget attesi/osservati, checkpoint flush e cleanup, mai il segreto sentinella |
| Product-paging | Pagina/probe/thread attesi/osservati e misure, contesto sintetico |
| Nuovi tre step storici | Scenari/varianti/permit/copertura/geometry/campioni e checkpoint secondo registri chiusi |

Raccogliere i valori ALL'ORIGINE negli assertion/result helper esistenti:
FunctionalCompletionSmoke oggi conserva ex.Message, quindi solo il proiettore
non puo inventare expected/actual. Per bool esistente expected=true/actual=false,
scalari solo se osservati. Tipi pubblici: enum, bool, contatori/metriche finite,
valori dichiarati sintetici e consentiti dal manifest; stringhe variabili
sensibili diventano withheld, non hash di un segreto. Nessun secondo framework.

check-required-gates mantiene intero transcript raw, non ultimi quattro messaggi.
La proiezione conserva sequenza completa dei template riconosciuti e slot
tipizzati; righe non esportabili -> diagnostic_line_withheld con posizione/
motivo e diagnosticCompleteness=partial. Lacuna visibile, primo errore invariato,
nessuna pretesa di diagnosi completa. Le assertion obbligatorie devono comunque
avere evidence sicura sufficiente; mancanza impedisce certificazione PASS.

Capture functional sintetiche: solo RenderTargetBitmap del controllo/view
previsto, mai desktop. Verificare contesto fixture e testi visual-tree contro
statiche/localizzazione/manifest prima del render; testo inatteso -> capture
esclusa con motivo, non screenshot d'errore arbitrario. Ricodificare PNG senza
metadata, bounds/dimensione/byte count/hash/capturePoint e run/fixture nel
manifest. Riutilizzare punti visuali esistenti; non aggiungere runner.
Se una capture richiesta non e esportabile, dichiarare la lacuna anziche
qualificarla come disponibile dopo il job.

Namespace pubblico UNIVOCO: PublicOutputRoot/<runId>/; default controllato e
sibling dell'OutputRoot raw (in CI runner.temp/qa-public). Final assente,
runId corrente validato, separazione dal raw/reparse/alias e collisioni in
preflight. Nessuna selezione "ultimo bundle", nessun riuso/eliminazione di
run precedenti. Staging sibling nuovo, stesso volume del final.

Entry base chiuse:
qa-results.public.json, qa-summary.public.txt, core-tests.public.json,
historical-sales-smoke.public.json, historical-sales-load.public.json,
historical-sales-crash.public.json, diagnostics.public.json,
runner-diagnostics.public.json.
Piu le sole visual/<capturePoint>.png enumerate dal registro sintetico
validato. Unselected/partial -> stato esplicito, non PASS. Numero/byte/profondita
bounded prima delle prove, niente extra o troncamenti silenziosi di evidence.
CSV finanziari, workbook, DB/backup, trust, dump e path personali restano esclusi.

Ordine di pubblicazione obbligatorio:
1. Validare tutte entry/DTO/capture e scriverle nello staging posseduto.
2. ZIP solo dalla lista validata, chiuso e completo.
3. Calcolare bytes/SHA-256 ZIP.
4. Scrivere upload-manifest.json FINALE esterno: schema/protocollo, run/source/
   fixture e lista entry/hash, bytes/hash ZIP. Non inserirlo nel ZIP, evitando
   il riferimento circolare al digest.
5. Riaprire e verificare cross-binding run, lista esatta, entry/ZIP hash e limiti.
6. Directory.Move dello staging al final del run, atomico sullo stesso volume,
   senza overwrite; ricontrollare coppia pubblicata.
7. Solo ora emettere output CI runId/publicRunId e publicBundleVerified=true;
   default false. Valori ID/enum chiusi, niente path liberi negli output.

Workflow ci.yml: costruire i due path ESATTI del run corrente:
qa-public/<runId>/qa-public.zip e upload-manifest.json. Pre-upload verifica
run/source/digest e coppia completata. always() condizionato al flag verificato
del medesimo step/run attuale, mai un glob sul parent. Un bundle completo di
una QA FAIL puo essere caricato per diagnosi; un projector fallito NON
autorizza un bundle precedente o una coppia parziale. Dopo publish ma prima
flag, un crash lascia bundle non qualificato per upload: dichiarare assenza,
non recuperare "latest". Nessun servizio di retention aggiunto.

Negativi nel test QA esistente: firstError con sentinella poi report non
scrivibile/serializer rotto; codici tecnici utili presenti senza segreto su
stdout/stderr e archivio; transcript completo ordinato con withheld espliciti;
due run sullo stesso parent, collisione, secondo projector fallito,
ZIP nuovo/manifest vecchio e inverso, directory senza flag e identita errata.
Ispezionare byte console, manifest e ZIP e tutti i canali intercettati:
command/process-result, stdout/stderr, TRX, summary, receipt, CSV e PNG inattesi.
Niente fallback raw ne retry automatico.

Export/proiezione obbligatoria fallita dopo PASS -> FAIL; un precedente
FAIL/TIMEOUT/NOT_RUN resta primario, evidenceExportError e lacuna diagnostica
separati. Raw TIMEOUT/BLOCKED_EXTERNAL preservati. Processi posseduti, cleanup
bounded e dipendenti bloccati se non confermato; nessuna pulizia ricorsiva.
wpf-build/Release Pack non introducono raw storico in bin/dist; gli output
diretti dei wrapper interessati seguono la medesima policy console.

## 13. Fixture, matrice eseguibile e comandi futuri

### 13.1 Oracoli

F-SMALL: righe e attesi letterali nel manifest, senza usare parser/query/helper
di produzione per calcolarli. Shop/operatori/ID inventati.

Esempio pubblico: vendita nativa +1000 carta, suo void -1000 registrato da un
altro operatore; vendita contanti +2000 (tender 2500, resto 500) e reso
parziale -500; storico +700 e
annullamento -700. Lordo positivo=3700, rettifiche=2200, saldo=1500,
Carta=0, Contanti=1500; due scontrini POS e due movimenti storici, sei righe.
Operatore della vendita carta e del contante: saldo 2500; operatore void:
-1000. Sconosciuto storico escluso dalle viste personali.
Fixture separate: vendita a D e void D+1; altro shop, tenant ignoto/conflittuale,
lotto annullato e ID interno storico uguale numericamente a un SaleId.

F-CSV: attesi byte/stringa letterali per testo 000123, ID oltre Int32,
A;B"C, multilinea, prefissi formula con spazio/tab e importo tipizzato -700.
Parser CSV indipendente verifica il writer; non fare roundtrip solo con
lo stesso helper.

F-LOAD-v1: 50000 righe, due layout e 76 colonne nel moderno, senza dati reali.
Legacy: 25000 vendite da 1000. Moderno: 23900 vendite da 1000, 100 annullamenti
da -1000 e 1000 respinte da 1000. Attesi letterali: 48900 vendite,
100 annullamenti, 1000 respinte, 49000 accettati, saldo 48800000 CLP.
Seed/versione e hash manifest congelati; date sintetiche e policy controllata.
Tutti gli ID distinti salvo duplicati esplicitamente previsti nelle fixture
di dedup. La somma attesa e dichiarata indipendentemente dal codice sotto test.

F-MIXED-v1: estensione del medesimo load, non nuovi dati reali. Ai 49000
eventi storici accettati F-LOAD aggiungere 10000 movimenti nativi con outbox/
proof sintetiche: 9000 vendite carta +1000 e 1000 reversal -1000, relativi
a originali presenti. Attesi letterali: 59000 righe, saldo 56800000,
9000 scontrini POS e media POS 1000. ID/timestamp collidono fra origini e
sono previsti due segmenti di transizione; native ID e storico ID uguali
non si confondono. Backdated insert +1000 -> 59001 righe/56801000 dopo refresh;
vecchio cursor rifiutato. Varianti piccole separate per shop B, proof
Unknown/Conflict/Dirty, mutazioni fra KPI/grafico/pagine/export e undo.

F-KPI: POS1000 + storico9000 -> media POS1000; solo importati o soli negativi
-> N/D. Giorni marcati con 1 scontrino da1000 e 3 da2000: media aggregata1750,
non media delle medie1500. Manifest con attesi literal, non prodotti dal
medesimo serializer/query sotto test.

Distribuzione futura: contenuti chiusi di tests/fixtures/historical-sales-getnet-v1
in Core.Tests.csproj e UiSmokeHarness.csproj con Content/Link e
CopyToOutputDirectory=PreserveNewest; nessun nuovo package o upgrade/lock change.
Lookup da AppContext.BaseDirectory o AppDomain.CurrentDomain.BaseDirectory,
sottocartella fissa fixtures/historical-sales-getnet-v1, mai working directory
o risalita al checkout. F-SMALL/F-CSV e oracoli letterali distribuiti; F-LOAD/
F-MIXED/F-CRASH generati deterministicamente dal manifest/seed/protocollo con
helper di fixture condiviso nei soli test/harness, senza introdurre logica QA
nel runtime prodotto. Versioni/hash canonici del manifest e dei contenuti
controllati; hash del workbook generato registrato, non fiducia nei timestamp
ZIP non deterministici. Nessun file finanziario privato copiato nell'output.

F-CRASH-v2: manifest sintetico con schema/protocollo corrente, sentinel e
oracolo precommit espliciti, pagine sufficienti allo spill e limite finito di
righe/tempo. La fase prepare e distinta dall'apertura verifier. Recordare
impostazioni SQLite effettive e journal prima/dopo, senza cambiarle per
forzare il PASS; se lo spill richiesto non e dimostrato la prova non passa.

F-R3: estensioni piccole dei manifest esistenti, non nuove suite.
F-MIXED-coverage usa A provato1000/U non attribuibile7000/GetnetA9000 e oracoli
di §8.1.1.a; i dati U non compaiono negli output. F-SMALL D/D+1 e misto
alimentano geometria reale signed. F-CSV include le varianti permit/revoca/
mutazione/cancel/destinazione/overwrite con oracoli filesystem indipendenti.
F-CRASH-v3 aggiunge i quattro subscenari B e relative varianti; nessun consumer C.
Negativi pubblicazione: due run stesso parent, report/proiettore non scrivibile,
sentinelle nei fallback, coppie stale/parziali e capture non declassificabili.

F-PRIVATE: sette originali e audit ricevuto, solo nel perimetro privato.
Attesi per file e complessivi esattamente quelli ricevuti; nessuna tolleranza
su CLP/conteggi. Baseline temporale derivata solo dopo verifica Z.
Il futuro manifest privato con percorsi/hash/attesi dovra essere fornito e
conservato fuori repo/CI. Ora non viene creato ne inventato.

### 13.2 Comandi

I codici C-* sotto abbreviano i comandi, NON sono tool nuovi.
Tutti sono FUTURA EXECUTION, non eseguiti in questa revisione.

C-LOCAL (A/B, Mac o Windows sviluppo):
~~~powershell
pwsh -NoProfile -File scripts/qa/Invoke-Win7PosAutonomousTests.ps1 -Suite Local
~~~

C-WINDOWS (comando canonico esistente; nella feature completa default All
per C integrata, NON comando di completamento B):
~~~powershell
pwsh -NoProfile -File scripts/qa/Invoke-Win7PosAutonomousTests.ps1 -Suite Windows -OutputRoot C:\Dev\_codex-evidence
~~~

C-BUILD (solo build mirata necessaria fuori wrapper, non in parallelo):
~~~powershell
dotnet restore src/Win7POS.Wpf/Win7POS.Wpf.csproj --locked-mode
dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86 --no-restore
~~~

C-QA-UNIT / C-QA-RUNTIME (solo sviluppo del supervisore se fuori gate canonico):
~~~powershell
pwsh -NoProfile -File tests/qa/Test-Win7PosAutonomousTests.ps1
pwsh -NoProfile -File tests/qa/Test-Win7PosAutonomousTests.ps1 -Runtime
~~~

C-CORE-FOCUSED (diagnosi mirata necessaria, non duplicata dopo suite verde):
~~~powershell
dotnet restore tests/Win7POS.Core.Tests/Win7POS.Core.Tests.csproj --locked-mode
dotnet test tests/Win7POS.Core.Tests/Win7POS.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~HistoricalSales|FullyQualifiedName~Getnet"
~~~

C-ALL, futura tranche C integrata, parametro PROPOSTO esplicito:
~~~powershell
pwsh -NoProfile -File scripts/qa/Invoke-Win7PosAutonomousTests.ps1 -Suite Windows -HistoricalSalesMode All -OutputRoot C:\Dev\_codex-evidence
~~~
Raggiunge B + Smoke/Load C con relativi prerequisiti e artifact. Non duplicare
build/test fuori wrapper dopo PASS, salvo nuova modifica o diagnosi motivata.

C-SMOKE/C-LOAD/C-CRASH: nuovi argomenti PROPOSTI dell'orchestratore, non
utilizzabili finche non implementati e verificati. Il wrapper riceve contesto e
nonce dall'orchestratore e non viene lanciato come supervisore alternativo.
~~~powershell
pwsh -NoProfile -File scripts/qa/Invoke-Win7PosAutonomousTests.ps1 -Suite Windows -HistoricalSalesMode Smoke -OutputRoot C:\Dev\_codex-evidence
pwsh -NoProfile -File scripts/qa/Invoke-Win7PosAutonomousTests.ps1 -Suite Windows -HistoricalSalesMode Load -OutputRoot C:\Dev\_codex-evidence
pwsh -NoProfile -File scripts/qa/Invoke-Win7PosAutonomousTests.ps1 -Suite Windows -HistoricalSalesMode CrashProbe -OutputRoot C:\Dev\_codex-evidence
~~~
C-B e alias documentale di C-CRASH, non un altro tool/comando. CrashProbe
raggiunge B-D03/05/07/10/12/13 con cinque scenari e liste chiuse §12.2, senza
componenti C. Default All ha otto runner; CrashProbe e Smoke ne hanno6,
Load ne ha7 perche include Smoke.
I comandi non autorizzano alcuna esecuzione in questa sessione.
C-PRIVATE: procedura UI/benchmark locale privata, dopo gate esterni.
C-WIN7: pacchetto candidato/checklist specifica, non SDK/pwsh sul target.

Prerequisiti F: SDK 10.0.301 e rollForward disable in global.json;
PowerShell 7.4+, Node18+ per check-owned-catalog-qa, lock restore, Windows
interattivo per harness net48/x86. Le librerie restano quelle referenziate.
Non installare SDK/pwsh moderni sul POS Win7. I gate canonici comprendono gia
dialog/required gates/test supervisore: non ripeterli senza una nuova ragione.
Le modifiche al wrapper non autorizzano staging/live.

### 13.3 Matrice di prove future

Stato iniziale di tutte le prove: NOT_RUN; dove manca un asset, BLOCKED con
raw BLOCKED_EXTERNAL. Timeout C-LOCAL/Core: limite corrente 3600 s; C-WINDOWS
mantiene i limiti per step esistenti. S/L indicano 90/240 s; il gate B
aggregato ha 360 s/330 s corpo con sottobudget chiusi §12.2.
Per ogni riga PASS richiede anche identita/evidence valida; mismatch e FAIL.

| ID/requisito | Ambiente e prerequisiti | Fixture/oracolo | Comando; tool/validator; timeout | Atteso e artifact | Dipendenza/sblocco |
| --- | --- | --- | --- | --- | --- |
| A-D01 due profili/header | Core/Data, SDK/Node | F-SMALL, mapping letterale | C-LOCAL; MSTest/TRX; 3600 s step | Riconoscimento ordine/accents, ambigui bloccati; TRX | A implementato |
| A-D02 precisione/ID | Core/Data | F-SMALL/CSV, ID text/numerici unsafe | C-LOCAL; MSTest; 3600 s | Int64 esatto, zero/lettere conservati, perdita respinta; TRX | A-D01 |
| A-D03 stati/segni/netto | Core/Data | F-SMALL, pending/null | C-LOCAL; MSTest; 3600 s | Respinte escluse, segni/reconciliation e coverage corretti; TRX | A-D01 |
| A-D04 tempo | Core/Data, policy sintetica | Ora ambigua/inesistente, offset e confine mese | C-LOCAL; MSTest; 3600 s | Raw/ms/policy e date indipendenti PC; TRX | A-D01 |
| A-D05 bounds/file safety | Core/Data | ZIP/XML corrupt/oversize/formule/link | C-LOCAL; MSTest; 3600 s | Errore finito/cancel, zero esecuzione o rete; TRX | Reader A |
| B-D01 schema/mapping | SQLite sintetico, SDK | Nuovo/0014/ledgerless/invalidi e alias ambigui | C-LOCAL; MSTest/detector; 3600 s | Checksum preesistenti immutati, FK/unique, lookup deterministico; TRX | A; schema B |
| B-D02 dedup/conflict | SQLite sintetico | Duplicati intra/inter-file, rename/reorder/overlap, ID distinti | C-LOCAL; MSTest; 3600 s | Zero nuove solo per identita provata; conflitto rollback; TRX | B-D01 |
| B-D03 commit auth | Windows B, capability dedicata e fixture posseduta | Apply/undo e varianti revoca/expiry/operator | C-B (=C-CRASH); HIST_B_COMMIT_AUTHORITY, elenco §12.2;360/330s gate | Ogni variante obbligatoria, zero effetti negati o commit linearizzato; receipt foundation-v3 | Componenti B, nessun Smoke/Load C |
| B-D04 tenant proof | Core/Data | N1/N2/N3/NX, hash/client/operation errati | C-LOCAL; MSTest; 3600 s | Esclusione coerente search/detail/export, zero fallback; TRX | Query B |
| B-D05 processi/lock | Windows B, holder/contender veri nello stesso Job | DB posseduti, contesa SQLite/lock | C-B; HIST_B_PROCESS_ADMISSION + ENTRYPOINT_AUTHORITY;360/330s gate | Processi sovrapposti, busy finito/no deadlock, negazione prima write; receipt B | Estensione helper esistente e bootstrap B |
| B-D06 cancel/disk/fault | SQLite sintetico | Failpoint ogni fase precommit, spazio simulato | C-LOCAL; MSTest; 3600 s | Rollback completo, primo errore conservato; TRX | Applier B |
| B-D07 crash/journal vero | Windows B, owner e unico Job | F-CRASH, sentinel/spill/checkpoint | C-B; HIST_CRASH_HOT_JOURNAL; gate360/330s, subcase<=90s | Raw79 atteso, stesso DB recuperato, dati+revisione e cleanup; receipt B | Nessuna UI/report C |
| B-D08 replay terminale | Core/Data B, permesso consultazione attuale | Commit, input rimosso, backup impossibile, altre revisioni/undo | C-LOCAL; HistoricalSalesReplayTests PROPOSTI, MSTest/TRX;3600s | Receipt originale e fingerprint conflict, zero file/backup/mutazione; dati/revisione durevoli insieme | B-D02/03; HIST05 e solo regressione supplementare C |
| B-D09 undo/reimport | SQLite sintetico | Tre cicli e lotto duplicate-only | C-LOCAL; MSTest; 3600 s | Una sola versione attiva, ownership e audit conservati; TRX | B-D01/B-D02 |
| B-D10 backup/restore | Windows B e SQLite sintetico | Mapping/batch, backup e restore epoch | C-B; HIST_B_BACKUP_RESTORE + test backup/restore gia esistenti | Reimportzero, piano stale, scope AUX e FK; receipt B/TRX | B-D09 e AUX B |
| B-D11 no effetti operativi | Core/Data | Snapshot tabelle stock/sales/cart/outbox/cassa | C-LOCAL; MSTest; 3600 s | Nessuna variazione import/undo; TRX | Applier B |
| C-H01..H08 UI completa | Windows interattivo, build-harness x86 | F-SMALL/CSV, assertion esatte par.12 | C-WINDOWS; HIST01..08/Assert-QaEvidence; S | Tutti 8 scenari e check completi, receipt smoke | B; wiring C |
| C-R01 semantica movimenti | Core/Data e UI | F-SMALL signed + giorno successivo | C-LOCAL/C-WINDOWS; TRX/HIST07 | Giorno/periodo/Carta/grafico/CSV uguali ad oracolo, no doppio void | Query C |
| C-R02 CSV puro/scope | Mac Core/Data e UI Windows | F-CSV distribuita, ID storico=nativo | C-LOCAL/C-SMOKE; serializer Core, parser indipendente/HIST07 | Quoting/formule/negativi, zero WPF reference Mac, routing negato; TRX/receipt | C-R01 |
| C-P01 full/misto sintetico | Windows x86, ambiente qualificato | F-LOAD + F-MIXED, manifest/protocollo congelati | C-LOAD/C-WINDOWS; HIST_LOAD_50K/validator v2; L | Contatori, proof cold/warm, revisioni e campioni completi entro budget; receipt/metriche | Smoke PASS |
| B-D12 entrypoint/attese | Windows B; fixture portabili solo per lifecycle | Entry reali, alias/DB distinti/cancel | C-B; HIST_B_ENTRYPOINT_AUTHORITY/PROCESS_ADMISSION;360/330s | Seconda autorita prima init, no waiter orfani, comandi ripristinati; receipt B | Mode CrashProbe, non All C |
| B-D13 AUX/fixture | Mac Core/Data e Windows B | Context path/role errati, partial/candidate/rollback/fixture | C-LOCAL e C-B; test esistenti + HIST_B_BACKUP_RESTORE/aux_scope_reject;3600s Core o gateB | Publish/swap reali, nessun bypass/ricreazione, handle rilasciati; TRX/receipt | Context AUX e bootstrap fixture B |
| C-R04 permit/export | Windows C | F-CSV interleaving/errore/delay/overwrite | C-SMOKE/HIST07 export_publication_authorization/outcome;90/60s | Nessun IO sotto lock, deny prima/allow dopo, writer procede, esito osservato distinto da timing; receipt | Permit/coordinator/OperatorSession C |
| C-R05 coverage/geometry | Windows C + Core | F-MIXED-coverage, D/D+1, misto/zero | C-SMOKE/HIST07 coverage_state_no_disclosure/hourly_signed_geometry;90/60s | Totale ammesso10000 e media1000, nessun dato U, barra negativa realizzata; receipt/capture sintetica sicura | UI/query/copertura C |

| C-R03 fotografia/KPI | Core/Data e UI | F-MIXED/F-KPI, insert nativo/void/proof/undo interleaved | C-LOCAL/C-SMOKE/C-LOAD; token/summary validator;3600/S/L | Un token per bundle/export, stale rifiutato, POS average esatta/N-D; TRX/receipt | Trigger e publication hooks C |
| Q-F01 distribuzione | Mac Core/Data e Windows harness da CWD diversa | Content manifest/fixture hash output | C-LOCAL/C-SMOKE; lookup validator;3600/S | Tutte le fixture reperibili senza checkout/CWD, niente originali; TRX/receipt | Content csproj A |
| Q-P01 protocollo misure | Test puri + Windows load | Slot mancanti durante apply, pending, drop/overflow/suspend | C-LOCAL/C-LOAD; validator measurement v2;3600/L | Nessun PASS con venti superstiti/gap o ambiente invalido; TRX/receipt | Sampler/validator C |
| Q-U02 console/diagnosi | PowerShell/Node, soli sintetici | firstError sentinella + report rotto, assert tipizzati/capture | C-QA-UNIT/Runtime o gate canonico; console DTO e diagnostica;limiti correnti | Segreto assente anche stderr fallback, dettagli utili/withheld espliciti, primo errore conservato; console+bundle sicuri | Helper/produttori/proiettore C |
| Q-U03 bundle current-run | PowerShell, parent comune a due run | Collisioni, secondo proiettore fallito, manifest/ZIP stale | C-QA-UNIT/Runtime o gate; manifest/flag validator | Solo due path del run verificato caricabili, nessun vecchio/parziale; artifact pubblici | Namespace/commit directory/workflow C |

| Q-U01 upload effettivo | PowerShell/Node, soli sintetici | Sentinelle command/process/stdout/TRX/receipt/CSV/PNG | C-QA-UNIT/Runtime o gate; proiettore/ZIP manifest;limiti correnti | Nessuna sentinella nei due artifact caricati, raw intatto; partial/errori conservati | Workflow/proiezione C |

| Q-T01 validator negativo | PowerShell/Node, soli mock | Receipt corrotta/stale/zero/extra/mismatch | C-QA-UNIT o gate canonico; AST; timeout corrente | Falsi verdi respinti; output sintetico | Estensione QA |
| Q-T02 supervisione/export | PowerShell, figli posseduti | Timeout/cancel/Job/output/export/prime failure | C-QA-RUNTIME o gate; AST/runtime; limiti correnti | Stato raw e primo errore, cleanup/dependents corretti | Q-T01 |
| Q-T03 privacy | PowerShell/Node | Sentinelle DTO/testo/path/trust | C-QA-UNIT/Runtime o gate; content validator | Zero sentinelle export, nome corretto non basta; report sintetico | Export QA |
| P-REAL-01 originali | Ambiente privato autorizzato | SRC-PRIVATE-XLSX + audit | C-PRIVATE; manifest privato; <=120 s analisi iniziale | Esatti attesi per file/totale, no tolleranza; evidence privata | Originali disponibili |
| P-REAL-02 ora/overlap | Portale/voucher o specifica, profilo shop | Casi temporali e riferimenti privati | Review privata prima apply, nessun tool live implicito | Policy provata e baseline giorno/ora congelata | Prova Getnet+inizio POS |
| W7-01 qualifica fisica | Win7 SP1 identificato, net48, pacchetto x86/hash | Sintetico e originali privati autorizzati | C-WIN7; checklist candidato; sessione finita <=30 min | Import/reopen/undo/restore, tempi/memoria/DPI/tastiera e scanner/stampa ordinaria; evidence privata | Host/pacchetto/periferiche reali; nessuna deroga ereditata |

## 14. Tranche e gate di completamento

A — Core, profili/reader e analisi.
Dipendenze: regole integrate R2 e fixture sanificate/distribuite. Completamento: A-D01..05 e gate
Local pertinenti, source fingerprint e TRX non vuoti. Originali e ora Getnet
possono restare gate esterni espliciti; nessuna funzionalita reale approvata
dai soli sintetici.

B — Schema, merchant mapping, autorita DB, commit capability, apply/undo e backup.
Dipende da A. Completamento: B-D01..13. Sul build host Windows usare C-B/CrashProbe,
con tutti cinque scenari B e varianti certificate, oltre a test supervisore
pertinenti; non usare All come prerequisito B perche seleziona C.
Nella futura patch B anche la chiamata CI di qualifica usa esplicitamente
CrashProbe; solo con C completa passa ad All. Non cablare riferimenti a classi
C inesistenti nel dispatcher B, ne fare PASS di step C non implementati.
build net48/x86 e prove Windows necessarie a fence/processi. In questo
incremento la funzione non e attivata nell'uso ordinario prima del completamento
dei guard. Correzioni alias usati bloccate per scelta V1.

C — UI/report/export e qualifica.
Dipende da B. Completamento software: C-H/R/P (inclusi fotografia/KPI e misure complete),
Q-T/Q-F/Q-P/Q-U (incluse console/diagnosi/current-run), suite Windows
canonica C-ALL con otto runner,
dialog/localizzazione, review del candidato e evidence dello stesso SHA/
fingerprint. CI/package delivery segue la convenzione reale del repo soltanto
nella futura Execution autorizzata; qui non si apre branch/PR.
Completamento richiesto della funzione comprende W7-01 e gate privati
prima dell'import reale. Non ereditare DONE_SOFTWARE/owner-defer da task 018.

## 15. Stati, evidence e blocchi

| Stato riassuntivo | Regola |
| --- | --- |
| PASS | Eseguito, assert completi, identita/evidence valide; zero test non passa |
| FAIL | Requisito violato, timeout o errore tecnico/evidence obbligatoria |
| BLOCKED | Prerequisito/accesso/ambiente mancante; causa e sblocco espliciti |
| NOT_RUN | Non eseguito o incompleto/cancellato; registrare parte avviata e identita |
| PASS_WITH_NOTES | Solo giudizio review con tutti obblighi soddisfatti, note non bloccanti |

Raw TIMEOUT -> riepilogo FAIL, mantenendo raw e causa; raw BLOCKED_EXTERNAL ->
BLOCKED. Cancellazione puo restare raw NOT_RUN anche dopo avvio.
PASS_WITH_NOTES non promuove FAIL/TIMEOUT/NOT_RUN e non maschera Win7 mancante.
Un positivo statico e verifica documentale, non PASS runtime.

Fase PLANNING, revisione piano, implementazione e qualifica sono indipendenti.
PLAN_READY_FOR_REVIEW significa decisioni integrate e gate esterni tracciati,
non feature REVIEW o DONE. REVIEW della feature richiede implementazione
candidata e prove della tranche. DONE richiede gate e consegna richiesti.
Eventuale distinzione futura DONE_SOFTWARE/qualifica esterna deve rimanere
esplicita, senza deroghe prese da altri closeout.

| Blocco/prova mancante | Responsabile/fonte e sblocco | Impatto |
| --- | --- | --- |
| EXT-XLSX | Utente: sette originali integri in posizione privata accessibile e manifest privato degli attesi ricevuti | Contratto/baseline reali non verificati; sintetici non li sostituiscono |
| EXT-TIME | Getnet: prova privata Z+offset/cambio offset; per il report nativo tabella canonica di transizioni approvata con provenienza/copertura | Apply moderno reale e proiezione nativa fuori policy bloccati; nessun fallback al PC |
| EXT-SHOP | Utente crea successivamente profilo; dati autorevoli shop/esercente e periodo avvio POS | Binding/overlap reali non verificati; nessun account creato qui |
| EXT-WIN7 | Host Win7 SP1, periferiche e pacchetto candidato identificati | Qualifica fisica BLOCKED_EXTERNAL, produzione non qualificata |

Checklist W7 futura, specifica del candidato: source/pacchetto SHA256, installer
Release x86 da pipeline, host OS/SP1/net48 e hardware identificati, scope
privato autorizzato, installazione/riavvio, due profili e carico, anteprima/ora,
apply/reopen/undo/restore, memoria/tempi, DPI 100/125%, tastiera, scanner e stampa
ordinaria dopo import. Usare la
[checklist esistente](../../QA/WIN7_PHYSICAL_SMOKE_REQUEST.md) come struttura,
senza copiare vecchi SHA/pacchetti o passi staging. Suite Win7/Hardware oggi
BLOCKED_EXTERNAL non e una prova fisica. Nessun Supabase live.

## 16. FUTURA EXECUTION — mappa dei file

Percorsi relativi alla root del repository, non file creati da questa sessione.

| Esistenti da modificare | Motivo circoscritto |
| --- | --- |
| src/Win7POS.Wpf/App.xaml.cs e Pos/Online/PosStartupCoordinator.cs | Bootstrap owner condiviso prima recovery/init, normale/safe-start e lifetime |
| src/Win7POS.Cli/Program.cs; CatalogImportProcessCrashSelfTest.cs | Owner prima init/seed di ogni comando e handoff parent/child reale |
| src/Win7POS.Wpf/Infrastructure/Security/OperatorSession.cs; Pos/Online/PosOnlineSyncRevocationLatch.cs | Capability dedicata, overload token/deadline senza waiter abbandonati |
| src/Win7POS.Wpf/Pos/PosWorkflowService.cs | Coordinamento manutenzione condiviso, query/CSV e scope routing |
| src/Win7POS.Wpf/Pos/PosViewModel.cs | Composizione dipendenze/ingressi tab e dialog |
| src/Win7POS.Wpf/Pos/DailyReportView.xaml e .xaml.cs | Ingresso Storico, origine/conteggi/prospetto |
| src/Win7POS.Wpf/Pos/Dialogs/DailyReportViewModel.cs | Filtro comune, refresh, analitico separato dalla cassa |
| src/Win7POS.Wpf/Pos/Dialogs/SalesRegisterViewModel.cs e SalesRegisterDialog.xaml | Identita composta, paging, scope/capacita, signed aggregate |
| src/Win7POS.Core/Security/PermissionCodes.cs; Wpf/Pos/Dialogs/UserManagementViewModel.cs | Codici/etichette, nessun cambio ai seed 0006 |
| src/Win7POS.Data/Migrations/SchemaMigrationRegistry.cs e detector corrente pertinenti | Append 0015, operation receipt/proof/time policies e trigger revision, checksum precedenti immutati |
| src/Win7POS.Data/SqliteConnectionFactory.cs; DbInitializer.cs | DemandOwned condiviso, RegisterConnectionAsync(ct), OpenExistingForRecovery senza init |
| src/Win7POS.Data/Repositories/SaleTransactionWriter.cs; SaleReversalWriter.cs; SalesSyncOutboxRepository.cs | Solo hook prova/commit-publication mirati, nessuna modifica economia nativa |
| src/Win7POS.Data/Online/RestoreShopSafetyRepository.cs e solo wiring restore necessario | Invarianti storico, mapping, epoch/invalidazione |
| src/Win7POS.Wpf/Localization/PosLocalization.cs | Registrazione partial traduzioni |
| tests/Win7POS.Wpf.UiSmokeHarness/Program.cs | Flag/automatedRun/dispatch/receipt sicura |
| scripts/qa/Invoke-Win7PosAutonomousTests.ps1 | Tre step/Mode selezionabili, partial collection, structural/PASS validators, proiezione finale |
| tests/Win7POS.Core.Tests/Win7POS.Core.Tests.csproj; tests/Win7POS.Wpf.UiSmokeHarness/Win7POS.Wpf.UiSmokeHarness.csproj | Solo Content/Link/copia fixture, nessun upgrade o lock change |
| .github/workflows/ci.yml | Solo catena pubblicazione QA: due artifact esatti validati invece di glob raw |
| tests/qa/Test-Win7PosAutonomousTests.ps1 | Mapping/negative evidence/privacy/process tests |

SaleReadRepository e helper fornitori si modificano solo per contratti concreti,
mai per riscrittura generale. Non mutare seed storici, economia dei writer o
query CLI di chiusura. I piccoli hook CLI/factory/writer, Content csproj e
upload CI sopra sono necessari ai confini R2; non sono refactor gratuiti.
DailyReportDialog e gia contenitore della view, MainWindow letto per ingresso.
Package, lock e toolchain restano invariati; altri workflow non si cambiano
senza un nuovo collegamento concreto alla root di evidence.

| Nuovi file proposti | Responsabilita |
| --- | --- |
| src/Win7POS.Core/Import/HistoricalSalesImportModels.cs; HistoricalSalesImportRules.cs; HistoricalSalesTimePolicy.cs | Modelli/regole pure; nessun IO |
| src/Win7POS.Core/Import/HistoricalImportAuthorizationCommitGuard.cs | Capability Apply/Undo dedicata |
| src/Win7POS.Core/Reports/SalesReportEntry.cs; SalesReportFilter.cs; SalesReportReadToken.cs; ISalesReportQuery.cs | Contratto analitico/token/KPI realmente consumato |
| src/Win7POS.Core/Reports/SalesReportTimePolicy.cs; SalesReportCsvRow.cs; SalesReportCsvSerializer.cs | Planner di transizioni/intervalli e serializzazione pura portabile |
| src/Win7POS.Data/Import/GetnetLegacyImportProfile.cs; GetnetTransactionsImportProfile.cs; GetnetExcelImportReader.cs | Adapter e IO bounded |
| src/Win7POS.Data/Import/HistoricalSalesImportAnalyzer.cs; HistoricalSalesImportApplier.cs | Analisi unica e applicazione atomica |
| src/Win7POS.Data/Repositories/HistoricalSalesImportRepository.cs; HistoricalMerchantRepository.cs; NativeSalesOwnershipRepository.cs; SalesReportQuery.cs | Operations/receipt, mapping, proiezione proof e query tokenizzate |
| src/Win7POS.Data/Reports/SalesReportSnapshotCoordinator.cs | Stamp commit e verifica breve token per permit; nessun guard DB passato al rename |
| src/Win7POS.Wpf/Pos/SalesReportExportService.cs | Destinazione/autorizzazione, paging su worker, IO CSV e rename atomico usando il serializer Core |
| src/Win7POS.Data/Migrations/HistoricalSalesImportSchema.cs | SQL e invarianti nuovi |
| src/Win7POS.Data/PosDatabaseAuthorityLease.cs | Owner/borrow/identity comuni a WPF, CLI, harness e recovery; non launcher |
| src/Win7POS.Wpf/Import/HistoricalSalesImportWorkflowService.cs | UI sottile, coordinamento e receipt |
| src/Win7POS.Wpf/Pos/Dialogs/HistoricalSalesImportViewModel.cs; HistoricalSalesImportDialog.xaml/.xaml.cs; HistoricalTransactionDialog.xaml/.xaml.cs | Wizard, gestione lotti e dettaglio |
| src/Win7POS.Wpf/Localization/PosLocalization.HistoricalSalesImport.cs | Catalogo en/es/it/zh-CN completo |
| src/Win7POS.Wpf/Pos/SalesReportAnalyticalRenderer.cs, solo se stampa offerta | Renderer con KPI nativi/aggregati comuni; CSV puro nel Core |
| tests/Win7POS.Core.Tests/Import/Getnet*Tests.cs; HistoricalSales*Tests.cs | Profili/regole/tempo/safety con fixture sintetiche |
| tests/Win7POS.Core.Tests/Data/HistoricalSales*Tests.cs; HistoricalMerchant*Tests.cs | Schema/dedup/auth/undo/restore/ownership/query |
| tests/Win7POS.Wpf.UiSmokeHarness/HistoricalSalesImportSmoke.cs; HistoricalSalesImportLoad.cs; HistoricalSalesCrashProbe.cs | Otto smoke, load misto e probe/verifier stesso DB senza nuovo launcher |
| scripts/run-historical-sales-import-smoke.ps1; scripts/qa/Win7PosHistoricalSalesValidation.psm1 | Adapter tre Mode, structural/PASS validator e protocollo misure |
| scripts/qa/Win7PosQaPublicEvidence.psm1 | Sole funzioni proiezione/manifest ZIP; niente API operative di process supervision |
| tests/fixtures/historical-sales-getnet-v1/ e helper fixture condiviso nei soli test/harness | Manifest/oracoli F-SMALL/CSV/LOAD/MIXED/CRASH e generazione riproducibile |

Docs da aggiornare nella futura Execution: questo piano/stato, worklog e
QA authorization limitatamente ai nuovi contratti, senza trascrivere dati privati.

### 16.1 Delta mirato della mappa R3 — FUTURA EXECUTION

Aggiunte rispetto a R2 motivate dai percorsi confermati, non file modificati ora:

| File effettivo/proposto | Motivo R3 e test/consumer |
| --- | --- |
| Esistenti: src/Win7POS.Data/Backup/SqliteOnlineBackup.cs; Online/SqliteRestoreCoordinator.cs; Online/AtomicRestoreInstaller.cs | Context su source/partial/candidate/rollback, chiusure prima publish/swap, errori authority distinti da integrity; B-D10/13 |
| Proposto: src/Win7POS.Data/PosDatabaseAccessContext.cs | Contesto esatto role/path/lifecycle, nessun owner live implicito; factory/backup/restore consumer |
| Esistenti: tests/Win7POS.Core.Tests/Data/RestoreShopSafetyTests.cs; MigrationBackupRestoreTests.cs; OnlineBackupContentionTests.cs; PersistenceFoundationHardeningTests.cs | Bootstrap/borrow e test dei percorsi reali, teardown limitato alle fixture esistenti |
| Proposto: tests/Win7POS.Core.Tests/Data/OwnedSqliteTestFile.cs | Helper minimo portabile per i soli file sintetici posseduti, prima Factory/EnsureCreated |
| Gia mappati: OperatorSession.cs; PosOnlineSyncRevocationLatch.cs; SalesReportSnapshotCoordinator ed ExportService proposti | Permit breve sotto confine attuale, IO fuori lock, test interleaving C-R04 |
| Proposto: src/Win7POS.Core/Reports/SalesReportPublicationPermit.cs | DTO immutabile del risultato di autorizzazione emesso solo dal trusted issuer, non framework/capability vendita |
| Gia mappati: DailyReportViewModel.cs; DailyReportView.xaml; contratti/query/CSV Core | Coverage esplicita, record coverage, geometria signed; HIST07 senza runner nuovo |
| Esistente: scripts/qa/Win7PosAcceptanceProcessRunner.psm1; tests/qa/Test-Win7PosOwnedProcessIdentity.ps1 | Ready callback/deadline/identity e rimozione wait illimitato; concorrenza nel gate B esistente |
| Esistenti: tests/Win7POS.Wpf.UiSmokeHarness/FunctionalCompletionSmoke.cs; scripts/run-functional-completion-smoke.ps1; scripts/check-required-gates.ps1 | Detail tipizzati all'origine/console safe, transcript completo e capture declassificabili |
| Esistenti: tests/Win7POS.Wpf.UiSmokeHarness/AuthorizationLeaseWpfSmoke.cs; ProductPagingWpfSmoke.cs; ProductImageProfileWpfSmoke.cs; Program.cs (risultati logging) | Expected/actual/checkpoint pubblicabili dei quattro runner; non una riscrittura delle suite |
| Gia mappati: orchestratore/test QA, wrapper storico, validator/proiettore, ci.yml | Cinque scenari B, console DTO, dettagli postjob e namespace/flag current-run, conti runner invariati |
| Esistente: .github/workflows/wpf-build.yml, limitatamente al payload QA manuale | Consegna separata net48/x86 descritta sotto, senza installer operativo o raw |

Delivery Win7 se si usa il harness: estendere il workflow WPF gia presente con
opzione manuale di bundle QA; build harness quando richiesta e artifact separato
Win7POS-QA-net48-x86 dello STESSO candidato. ZIP con exe/config, closure
runtime x86 necessaria, fixture sintetiche e manifest SHA256/source/binari.
Niente SDK/pwsh/CLI net10, originali o inserimento automatico nell'installer.
Preparazione da output verificato, non glob di dati QA; test closure/manifest
nei controlli di packaging pertinenti. I cinque scenari B con CLI restano sul
build host; Win7 esegue gli scenari compatibili net48 e la checklist reale,
con istruzioni del candidato e directory QA posseduta. Consegna del payload
non e una qualifica Win7 e non aggiunge un nuovo workflow/runner.

## 17. Audit tracciabile R1

Severita di design: P1 = rischio economico/tenant/autorizzazione/falso PASS;
P2 = affidabilita, UX, ripresa o manutenzione. Tutti i rilievi chiusi sotto
sono "risolto nel design; implementazione/verifica futura", NON prodotto risolto.

| ID/severita | Requisito -> sezione | File/simbolo verificato e provenienza | Rilievo/conseguenza | Correzione integrata e verifica futura |
| --- | --- | --- | --- | --- |
| G00/P2 | Provenienza -> 1/15 | Limite storico R1; SRC-PLAN-R1 ora disponibile e checksum verificato | R1 chiedeva una fonte distinta non necessaria a questo audit | Chiuso in R2 per il target ricevuto; R0 resta solo limite storico, nessun blocco Claude |
| G01/P1 | Tenant proof -> 6.1 | ReceiptShopInfo:13; SerializeReceiptShopSnapshot:2090; F SRC-CODE | Snapshot stampa scambiato per prova ShopId | Gerarchia N1/N2/N3/NX, esclusione uniforme; B-D04 |
| G02/P1 | Legacy binding -> 6.1 | DbInitializer.TryReadLegacySalesOriginShopCode:1510; F | Hash/code non provano automaticamente tenant ID | Code-only richiede prova storica, nessun current fallback; B-D04 |
| G03/P1 | Commit revoca -> 6.2/7 | OperatorSession:802; PosOnlineSyncRevocationLatch:76; writer:126; F | Check prima insert lascia race precommit | Capability dedicata, finale sotto latch e budget 10 s; B-D03 |
| G04/P1 | Processo concorrente -> 6.3 | App:19/136/166; barrier e maintenance; F | Mutex session-local/fail-open, autorita non cross-process | Lease OS lifetime e limiti modello supportato; B-D05 |
| G05/P1 | Lock e contesa -> 7 | RestoreDbAsync:379; factory.AcquireExclusiveCommitFence:333; F | BEGIN IMMEDIATE e apply lungo non equivalgono al fence | Ordine restore-compatible, manutenzione, backup prima fence; B-D05/06 |
| G06/P1 | Mapping persistente -> 4/5 | R0 audit-only; P | Alias non riusabili deterministicamente, dedup instabile | Scope/alias versionati, unique lookup, freeze binding usati; B-D01 |
| G07/P1 | Cicli undo -> 5 | SQLite partial index gia usato nel repo; F/P | UNIQUE(key,active) impedisce piu record inattivi | Partial unique attivi, nuova versione per reimport; tre cicli B-D09 |
| G08/P1 | Economia nativa -> 8 | PosWorkflowService:1541; SaleReversalWriter:423; RegisterVM:330; F | Esclusione originale voided piu negativo sottrae due volte | Registro firmato con originali, data di ciascun movimento; C-R01 |
| G09/P1 | Scope operativo -> 8.2 | RegisterVM:306/426/540 e command; F | Search senza filtro e ID storico possono raggiungere reso nativo | Scope obbligatorio e guard origine anche nel service; C-R02 |
| G10/P1 | CSV sicuro -> 8.3 | EscapeCsv:2839; F | Sostituzione lossy, quote/newline/formule non gestiti | Writer typed, text-only neutralization, limite tipi CSV; C-R02 |
| G11/P1 | Input precisione -> 3/9 | SupplierReader:669/691/709/721; F | Formattazione cultura, quasi-integer e perdita orario | Reader contrattuale, raw numeric e rejection; A-D02/05 |
| G12/P1 | Dati ricevuti -> 1/3/13 | SRC-PRIVATE-AUDIT D, workbook M | Audit confuso con test; copia pubblica divulga dati | Riferimenti privati, oracoli sintetici, gate originali; P-REAL |
| G13/P1 | Ora -> 3.3 | Audit privato D; nessuna prova portale M | Z non confermato sposta giorno/ora e KPI | Policy visibile/versionata, gate reale EXT-TIME; A-D04/P-REAL-02 |
| G14/P2 | Netto incompleto -> 3.2 | Contratto liquidazione D/P | Somma nota presentata come totale completo | Coverage e nullable espliciti; A-D03 |
| G15/P1 | QA evidence -> 12 | New-QaPlan:352; Assert-QaEvidence:437; F | Exit0 o receipt vuota potrebbe simulare PASS | Scenario/check ID esatti, contesto atteso e negativi; Q-T01 |
| G16/P1 | Privacy export -> 12.4 | Export-QaSmokeEvidence:389; Program error catch; F | Allowlist nomi non redige contenuti/eccezioni | DTO chiuso e proiezione pubblica, sentinelle; Q-T03 |
| G17/P2 | Smoke/load -> 10/12 | functional20 e validator carrello; F | Carico mischiato a smoke e validator non pertinente | Otto smoke piccoli, full load separato, budget congelati; C-P01 |
| G18/P1 | Permission seed -> 6.2 | SecuritySeedMaterial/registry0006; F | Nuovi grant possono alterare checksum pubblicato | Codici/catalogo nuovi, nessun seed automatico; B-D01 |
| G19/P2 | Restore/ripresa -> 6.3/7 | Generation repository e CatalogShopState epoch; F | Dataset restaurato puo rendere vecchia preview apparentemente valida | Dataset+epoch+revision/generation, invalidazione e operation recovery; B-D08/10 |
| G20/P2 | Task/evidence states -> 0/15 | Index/ledger/worklog; F | Piano completo confuso con DONE o deroga storica | Stato piano distinto, runtime NOT_RUN e gate esterni nominati |
| G21/P1 | Contante e resto -> 8.1/13 | PosWorkflowService:1250-1252; SaleReadRepository:66; F | PaidCash consegnato confuso con incasso netto gonfia il totale contanti | Tender/resto/netto separati, fixture con resto e riconciliazione; C-R01 |

Riferimenti lineari sopra relativi esclusivamente allo SHA baseline; dopo
modifiche Execution usare i simboli e aggiornare le evidenze del candidato.

### 17.1 Rilievi mirati R2

Stato di ogni R2 sotto: risolto nel design; implementazione/verifica futura.
Non riaprono G01-G21 senza nuova contraddizione e non attestano prodotto corretto.

| ID/severita | Sezione R1 / osservazione verificata | Conseguenza | Decisione integrata / prova futura |
| --- | --- | --- | --- |
| R2-01/P2 | §1/15, R1 locale e hash ricevuto identici | Falso blocco per un altro piano Claude | Provenienza SRC-PLAN-R1 sufficiente, G00 chiuso per R2 |
| R2-02/P1 | §7.1 lookup operation dopo file/backup | Risposta persa non recuperabile senza input | Replay consultivo prima preflight e recheck in tx; B-D08 input assente/backup impossibile/undo/fingerprint |
| R2-03/P1 | §7.1.8 revision postcommit, §5 batch receipt | Dati/revisioni divergerebbero; receipt originaria mutabile | Operation receipt immutabile + revisioni atomiche e duplicate-only senza bump; B-D01/08 |
| R2-04/P1 | §7.3 opener factory Mode omesso, SQLite locking §4.1 | DB assente ricreato o recovery readonly impossibile | OpenExistingForRecovery senza bootstrap, recovery tecnico poi letture; B-D07/08 |
| R2-05/P1 | §6.3 App-only; CLI.Program:6405 e Harness.Program:320/339 | Entry point scrivono prima della lease | Context comune/DemandOwned e matrice entry point/identita/handoff; B-D12 |
| R2-06/P1 | Latch.EnterAuthorizationUseAsync:44 e factory.RegisterConnection:616 | Wait non cancellabile acquisisce dopo cancel | Overload ct, deadline globale e trasferimento lease certo; B-D12, no orphan waiter |
| R2-07/P1 | §8.1 token storico; writer/outbox mutano il nativo | KPI/grafico/pagine/export mescolano versioni | Trigger report revision, proof derivata e publication stamp; C-R03/C-P01 |
| R2-08/P2 | §8.1 query/cursor e localtime attuale | Backdate/tie/DST possono saltare o duplicare righe | Ordine totale ticks/origine/ID, policy segmenti canonici e WHERE UTC indicizzato; C-R01/03 |
| R2-09/P1 | DailyReportVM:171/338/355, header/marked print | Getnet gonfia media scontrino POS | NativePositiveSales/Count, zero->N/D, somme prima divisione; F-KPI/HIST07 |
| R2-10/P2 | §16 serializer WPF e csproj solo corpus esistenti | CSV non testabile Mac, fixture assenti da output | Serializer Core e Content BaseDirectory distribuiti/hash; C-R02/Q-F01 |
| R2-11/P1 | §10 heartbeat Dispatcher/20 superstiti | Stallo/gap/suspend invisibili possono dare PASS | Sampler background, sequenze e tre verdetti separati; Q-P01/C-P01 |
| R2-12/P1 | §12.2 Invoke-QaProcess:268 crea root a ogni call, C-CRASH non agganciato | Verifier puo leggere altro DB o non essere un gate | Un Job/root, due &, nonce/sentinel/spill/recovery/exit79 e terzo step; B-D07 |
| R2-13/P2 | Invoke-QaPlan:492 partial solo functional | Timeout cancella diagnosi storica | Structural/privacy accetta partial safe, raccolta/checkpoint su tutti i raw status; Q-T01/02 |
| R2-14/P1 | ci.yml:108-113 glob raw, command/process/stdout/TRX inclusi | Leak anche con smoke basename corretto | Raw privato + DTO/ZIP/manifest chiuso, upload due path; Q-U01/Q-T03 |

I riferimenti di codice sono dello SHA baseline; sono verifiche statiche.
Originali privati, semantica temporale, binding reale e Win7 restano asset
esterni con gate, non decisioni architetturali lasciate aperte.

### 17.2 Residui confermati R3

Stato di ogni riga: risolto nel design; implementazione/verifica futura.
G01-G21 e R2-01..14 restano chiusi: qui si precisano percorsi non coperti o
contraddizioni concrete della R2, senza riaprire le decisioni solide.

| ID/severita | Controesempio / simbolo allo SHA baseline | Sezione/decisione integrata | Fixture/assertion -> comando/evidence |
| --- | --- | --- | --- |
| R3-01/P1 | SqliteOnlineBackup:109/199, AtomicRestoreInstaller.IsDatabaseValidAsync:450 e fixture TestDb:410: path diverso dal live, catch authority->false | §6.4 AUX esatto, aperture existing, transfer/handle prima publish/swap, errori distinti | B-D13 e HIST_B_BACKUP_RESTORE aux_scope_reject/all_handles_released; C-LOCAL/C-B, TRX/receipt B |
| R3-02/P1 | Latch.ChangeOperatorAuthority:111 e InvalidateAuthorizationState:195 non attendono SQLite; R2 SHARED sino rename | §8.3.1 permit sotto confine esistente, nessun IO/DB sotto monitor, revoca prima/dopo ed esiti separati | HIST07 export_publication_authorization/outcome; C-SMOKE, receipt con token/esito/timing |
| R3-03/P1 | SaleReadRepository:55/87 globali vs nuova attribuzione: nativo escluso diventa apparentemente zero | §8.1.1.a Coverage non numerica senza leak, Pending/Subset/Empty distinti, nessun fallback globale | F-MIXED-coverage/HIST07 coverage_state_no_disclosure; C-SMOKE, UI/CSV ammessi |
| R3-04/P2 | VM.LoadHourlySalesAsync:515 e BarHeightPx:1479, XAML:281: negativi invisibili con somma corretta | §8.1.3 asse zero e due direzioni nell'area corrente, assert geometria realizzata | D/D+1 e misto/HIST07 hourly_signed_geometry; C-SMOKE, receipt/capture sintetica |
| R3-05/P1 | B usava All con C; receipt solo crash non prova auth/process/restore. Helper waited-process ha secondo wait illimitato e PID-only | §12.2 C-B/CrashProbe con cinque scenari B, helper esistente esteso, aggregate budget motivato senza rialzo del crash | B-D03/05/07/10/12/13 -> catalogo chiuso §12.2; C-B, foundation-v3 e TRX |
| R3-06/P1 | Orchestratore:610 stderr firstError intero e :612 evidence path, fallback functional:216-223 | §12.4 console DTO e fallback costante in tutti i rami, nessun raw/path | Q-U02 sentinella+report/serializer rotto; gate QA esistente, stdout/stderr e bundle |
| R3-07/P2 | FunctionalCompletionSmoke:239-247 solo ex.Message; raw hosted sparisce, recenti gate richiedono diagnostica completa | §12.4 detail raccolti all'origine, transcript ordinato e withheld espliciti, capture declassificata | Q-U02/Q-T03 per cinque runner+functional20; assertion tipizzate/capture/diagnostics pubblici |
| R3-08/P1 | qa-public condiviso + always potrebbe usare coppia vecchia/parziale dopo projector fallito | §12.4 namespace per run, ZIP prima digest/manifest, cross-check e publish directory prima flag CI | Q-U03 due run/collisione/stalepair; gate QA esistente, soli due artifact correnti |

Prove statiche positive non diventano PASS runtime. Non restano decisioni
sostanziali aperte nel design R3; originali/ora/binding/Win7 mantengono i gate
esterni §15 e non sono sostituiti dai sintetici.

## 18. Controllo documentale e cronologia

R3 parte dalla R2 locale con hash target verificato e modifiche pregresse
preservate, senza add/stash/commit. Controllo finale: confronto diretto R2->R3
anche per PLAN untracked, link, Mode/count/dipendenze e assertion raggiungibili,
assenza dati privati e stato/diff generale.
Solo PLAN, voce019 e breve worklog sono scrivibili; nessuno script/check runtime
viene eseguito per ispezionare questi documenti.

| Revisione | Data | Motivazione/provenienza |
| --- | --- | --- |
| R0 | Conversazione precedente | Proposta storica non persistita; assenza non blocca R2 |
| R1 | 2026-11-04 | Prima persistenza sanificata; G00-G21; hash target d1a2395231e48822f1ec2ef6781b8b0403942b1a260fb504868bc294ab70694f |
| R2 | 2026-11-04 | R2-01..14 integrati; hash target3735e6e5784878f06246f570204ec378c5a8a2bf0d94677ac863d7e93d8170f6, preservato come provenienza |
| R3 | 2026-11-04 | Residui R3-01..08 integrati nelle sezioni normative, matrice B/C, filemap delta e pubblicazione; soli documenti |

Esito della review documentale R3: PLAN_READY_FOR_REVIEW sulle fonti disponibili;
nessuna incoerenza di design lasciata aperta. Prossimo passo: review di R3 e
gate esterni. Dopo futura autorizzazione Execution riprendere da §14 tranche A:
contratti/regole/parser e fixture sintetiche distribuite, con C-LOCAL; prima
verificare nuovamente main e il numero migration senza toccare ora il codice.
Implementazione NOT_STARTED; build/test/runtime NOT_RUN; qualifica Win7 e
verifica dati reali non effettuate. Nessuna pubblicazione Git.


## Checkpoint storico onboarding 2026-11-07 dopo Esc

Main candidato `e832d6a96d5a736145a5da0cfb9ad2dffeba25dd`, feature959396b,
pubblicati. Security42418832124 e WPF42418868960 PASS; QA scaricato40file
con pin forwarder e smoke esterno8/44 PASS. CI42418832135/Release42418832168
ancora in corso al checkpoint: leggere gli stessi run, senza rilanciarli.
Avvio ordinario QA interrotto dall'utente prima dell'osservazione, NOT_RUN.
Nuovo prodotto/reader reale e chiusura documentale restano da completare.
Tutti i processi locali posseduti chiusi; profili/log/prime failure conservati.
Il report onboarding contiene la ripresa precisa; nessuna qualifica reale o
Win7 promossa, Mac PENDING e prove esercente/tempo/overlap assenti.


## Esito onboarding Windows — 2026-11-07

La ripresa autorizzata ha chiuso CI/Release e la verifica ordinaria QA: quattro
workflow PASS sul codice e832d6a, prodotto/QA e reader privato7/7 verificati.
Il checkpoint Esc sopra è storia, non il prossimo comando. Onboarding live
resta bloccato dal catalog409unmapped, risposta Mac PENDING e prove reali
Getnet assenti; Win7 fisico non eseguito. Nessuna estensione di grant/recovery.
La cattura diagnostica import_summary rimane NOT_RUN, esplicitata nel report.


## Review successiva alla qualifica e832d6a

Regressione nuova riprodotta su Core e loopback prima del fix: un delta poteva
riattivare i vecchi prodotti dopo empty confermato. Il codice e832d6a e i verdi
sopra sono intermedi. Qualificare il fence durevole fino full nonempty, poi
nuovo SHA/pacchetto e documenti. Nessuna soglia/guard/oracolo rilassati.


## Verifica conclusiva del correttivo onboarding — 2026-11-07

Candidato codice/pacchetto d262ccd979db8e3407118ffa7275861f12fdb5e2,
quattro workflow exact-SHA PASS. Core/Data1734, gate50, canonica15,
functional20, foundation5/37, smoke8/44 e load11; download prodotto/QA,
smoke scaricato e avvio ordinario PASS. Reader7/7 originale/oracolo indipendente,
NeedsEvidence=true/CanApply=false; DLL QA byte-identiche al prodotto.
Il rosso e832d6a old-full→empty→delta è conservato. Il vincolo durevole
impone nuova full nonempty verificata prima di vendere; focused116/116 e8/44.
Gli stati pendenti precedenti sono checkpoint storici. Chiusura solo documentale
successiva a d262ccd, nessun nuovo retry live. Mac PENDING, catalog409 e prove
reali/Win7 BLOCKED_EXTERNAL; nessun DONE. Identità e card correnti nel
[risultato onboarding](../../reports/ASUS_W7POS_019_ONBOARDING_EXECUTION_RESULT.md).


## Prosecuzione8novembre2026 — causa catalog409 corretta

AdminPR #143 integrata in1a35034a, post-merge CI/Cloudflare PASS,67 file / 4.375 test pgTAP.
Migration unica applicata, body9622c4d3a4c793a7803d730b8a8c51d4 e tutto il restante
metadata pg_proc invariato; nuova voce history allineata ai byte/versione sorgenti.
Lease reale ora manifest canonico zero e terminale. HTTP/UI normale NOT_RUN_POLICY
per avvio respinto prima di creare processo/pull; autorizzazione specifica pendente.
Windows d262ccd invariato e qualifiche riusate. Nessun import/vendita/nuovo grant,
nessun DONE. Console staff e Getnet/Win7 restano separati.
[Report circoscritto](../../reports/ASUS_W7POS_019_CATALOG_409_FIX_RESULT.md).

<details>
<summary>Checkpoint precedente conservato — stato prima del nuovo addendum bilaterale</summary>

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
