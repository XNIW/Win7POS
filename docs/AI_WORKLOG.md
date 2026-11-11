# AI Worklog – Win7POS

<!-- ASUS019_COMPOSED_CURRENT_START -->
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
<!-- ASUS019_COMPOSED_CURRENT_END -->

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
Cronologia sintetica delle sessioni AI. Aggiornare dopo ogni sessione significativa.

---

## 2026-11-09 — ASUS019 successor qualification ACTIVE

Canonical01 e6aa13/15, Core1745, smoke8/52 and load11PASS; functional worker
cleanup and crash/cart admission FAIL retained. Corrected per-VM initialization,
owned-work drain and importer commercial busy scope; final version fences and
newer-operator regression retained. Focused successor16.544s and50required gates
PASS. Exact successor canonical/CI/package/install and real retained-profile
navigation/review are pending. a828 remains installed; original ledger unchanged.

## 2026-11-09 — ASUS019 authenticated shell and geometry

Implemented pure Core shell admission, version-fenced empty login transfer,
separate sale/start-of-day admission and guarded timeout, empty MainWindow without
commercial VM, shared report/service/import composition and unavailable amounts.
Real MainWindow fixture verifies close button/X/Escape, PID/HWND/session/filters,
restart without history permission, full transition, ready scanner/cart and
Escape focus return. Focused13.297s PASS; geometry10.870s/integrated1.904s PASS;
Core140selected and required50/50 PASS. Initial modal-observer TIMEOUT retained;
fixed fixture ordering, no timeout change. Canonical qualification/delivery and
real retained-profile review remain ACTIVE. No backend or original ledger mutation.

## 2026-11-09 — a8286c6 qualificato e installato, stessa ASUS019

- Nuovo requisito UX accolto nella stessa Execution: shell dopo accesso valido anche con catalogo autorevolmente vuoto, vendite negate; storico volontario dal riepilogo e close senza logout/uscita. Supera la precedente UX obbligatoria.21:21UTC main/HEAD c54be47 e installato a828 verificati, nessun runtime; snapshot privato immutabile del delta geometria prima degli overlap. Nuovo gate UX ACTIVE, nessun PASS trasferito dal vecchio login/workspace.

- Geometria comune implementata:39root Manual, veri owner invariati, overlay centrato sulla work area; normale misurato prima di show, minimi/cap coerenti e clamp size-only. Messaggi/conferme lunghi scorrono nel corpo, footer esterno. Red sintetico autentico riprodotto, intermedio contenuto ma decentrato conservato; focused22misure192DPI PASS10.065s, primo HWND/card/footer, lingue, sei expected geometrici, cancel zero-write/confirm once e move/resize PASS. Logger first_render solo geometria senza testi finanziari. Build x86/guard50 PASS; Recovery nuovo codice30.160s PASS dopo sostituzione della vecchia aspettativa owner con work/card/CTA. Vecchio weakref FAIL conservato senza diagnosi causale inventata. Candidato geometria da congelare/qualificare/installare;100/125 nuovi e200% finale aperti.

- Addendum utente integrato nella stessa R3: nuova policy comune di centro work area, vero owner e titolo/footer raggiungibili a200%/nested. Gate ACTIVE prima della consegna finale.200% ripristinato/observato; form vuoto decentrato, footer visibile, nessun clipping inventato. POS chiuso ordinariamente inattivo prima del harness; sugli originali niente nuovo Apply, solo review duplicate-only dopo il successore.

- CI42673051923/Security42673051757/Release42674141171/WPF42675026366 SUCCESS: canonica15, functional20, foundation5/37, smoke8/52 e load11 con M/E/S=true. QA scaricato40 verificato, smoke49.678s PASS8/52, cleanup/fingerprint/payload invariati. Prodotto41/110checksum,34file comuni QA identici; installato41/41, versione1.0.0-dev.a8286c669a7c. Soglie/assert invariati; first FAIL ca4e437, Recovery/path/ownership e job initialization GitHub conservati.
- Backup prodotto prima update; profilo/trust byte-invariati durante installer, primo consenso UAC annullato senza mutazioni, secondo exit0 e nessun auto-start. Collegamento quotidiano aperto dall'utente, EXE/profilo verificati prima PIN: login/empty/workbench/import PASS. Ledger/ricevuta logici esatti dopo riapertura, SQLite/FK/invarianti commerciali PASS. LIVE_REOPEN_PENDING per ricevuta/report/nuovo CSV/reimport sul successore; nessun undo del lotto valido.
- Form nativo100/125% intero, reverse Tab/Enter/Escape osservati; ripristino200% pendente. Admin148 autenticato PASS e servito invariato; A/B VERIFIED, C moderno MISSING, CSV_INPUT BLOCKED_INPUT. Target Win7 già indisponibile; nuovo kit del codice cambiato preparato,182 immutato. Software REVIEW, complessivo ACTIVE; [matrice corrente](reports/ASUS_W7POS_019_RETRY_E2E_RESULT.md).

## Storia — 2026-11-09, legacy reale salvato e correzione conferma ASUS019

- ca4e437 CI42667255330 FAIL:8/52 assert PASS ma corpo59.625s più avvio/uscita oltre budget processo60s; Load NOT_RUN. WPF42667293657 functional20/20, Security42667255175 e Release42667255023 PASS; nessuna installazione. Stesso prodotto, regressione test riunita in una fixture con stale zero-write poi commit singolo/rientranza negata: run06 PASS8/52,50.329s processo, HIST02 3.352s, cleanup/no orphan. Nuova qualifica exact-SHA necessaria, soglie e assert invariati.
- Full locale conservato FAIL: Core1734 PASS, Recovery19/20 per retention weakref4/5; nessun retry invariato. Smoke/crash non partiti per root evidence troppo profonda e suffisso atomico net48. Crash scaricato short-root2/5, poi guard ownership rifiuta W7HP noncanonico: non indebolito. Foundation5/37 hosted PASS distinta. Scala100% confermata dall'utente; prove native e ripristino200% pendenti.

- Admin148 nuovo login autenticato PASS: overview staff-unavailable e reader Staff safe osservati sul Worker a68ebec2/deployment1cd437f1; docs b5c2dbc, software REVIEW.
- Installato1823697: avvio quotidiano Start Menu/profilo conservato, login mascherato, catalogo vuoto e workbench PASS. UI analisi7/piano legacy4 confrontati con oracolo indipendente; A/B validi, CLP esplicito, moderni bloccati da C.
- Apply legacy committato con backup prodotto verificato; UI NullReference dopo commit conservata. Riconciliazione riga per riga PASS, ricevuta/report/dettaglio/CSV reali PASS. Nessun retry o undo del lotto valido; SQLite/FK e invarianti commerciali PASS.
- Regressione nell'esistente HIST02: servizio e conferma singola PASS; rientranza della conferma riproduce lo stesso errore postcommit, stack live originale non disponibile. Fix piccolo del ViewModel, piano stabile/seconda attivazione negata/review cambiata respinta; build x86/dialog standard PASS, qualifica exact-SHA aperta.
- Run diagnostici01-04 conservati FAIL; HIST08 incontra lock image-cache del POS reale concorrente. POS chiuso normalmente, nessuna cache/trust cancellata. Fresh run05 PASS8/52,53.276s entro60s, cleanup confermato e nessun orfano; canonical/exact-SHA package pendenti. Reopen/dedup corretto e DPI100/125 pendenti; host dichiarato200%/2880x1800, Win7 già indisponibile, CSV_INPUT BLOCKED_INPUT. ACTIVE, nessun DONE.

## Storia — 2026-11-09, rollout prima dell'analisi e dell'Apply reali

- Windows1823697 invariato, nessuna reinstallazione o nuova suite completa. Percorso B Admin: bundle servito esportato readonly, reader TASK140F recuperato e artefatto Linux proveniente dalla CI conservato, senza SQL/grant/secret put.
- PR147/cdebb26: POS installato avviato manualmente dall'utente dopo denial del tool, login/catalogo vuoto/workbench PASS. Console pos_admin FAIL per guard manager-only; prima failure conservata, processo task fermato e rollback pinned399720d2 verificato. Inventario RPC non provava equivalenza semantica.
- PR148/9016054 corregge il solo resolver usando il predicate di ruolo già esistente.45mirati PASS, CI42638651781 attempt2/Cloudflare42638650988 PASS; prefix health FAIL attempt1 conservato. Postmerge42641653938/42641652917 PASS. Artifact13684508006 verificato57file, ora Workera68ebec2/deployment1cd437f1/tag9016054, pubblico PASS, nuova prova autenticata in corso.
- Profilo conservato NEW_SHOP_019 selezionato via WIN7POS_DATA_DIR utente; collegamento ordinario esistente inventariato, riapertura effettiva da verificare prima del PIN. Nessun launcher o spostamento DB/trust. DB readonly integrity/FK PASS, dati commerciali/storici0.
- Sette originali XLSX riindicati/hash invariati; analisi UI ancora da completare. A VERIFIED dichiarazione contestuale corroborata, B VERIFIED mai usato prima; C moderno MISSING, Z lessicale insufficiente. Legacy distinto dal gate moderno, CSV_INPUT BLOCKED_INPUT, Win7 fisico già indisponibile/kit invariato. [Stato corrente e limiti](reports/ASUS_W7POS_019_RETRY_E2E_RESULT.md). Nessun DONE.

## 2026-11-08 — Residui reali ASUS019, stessa task

- Windows1823697 invariato, nessuna reinstallazione o suite completa ripetuta. Su nuova richiesta utente avvio normale dell'installato PASS; password solo nella PasswordBox, autenticazione verificata. Catalogo reale500/db_failure, auditcatalog_v2_empty_manifest; prima failure preservata, nessuna full o cassa aperta.
- Admin PR144 merge919d100 e post-merge CI/Cloudflare PASS. Nuovo fix HTTP PR145 codice3e56a322,33mirati e CI42522680653/Cloudflare42522680527 PASS,1156foundation+14SKIP,67/4375pgTAP; mergefdb30f4. Full fresca terminale shop_scoped zero ammessa, delta/legacy/parziali e publication fence preservati; nessuna SQL.
- Post-merge database42523315476 FAIL prefix bootstrap due volte prima dei test, Verify/Cloudflare PASS. Nuova diagnostica chiusa PR146 codicef344918, unit privacy/security/lint e CI42525515132/Cloudflare42525515054 PASS,1157foundation+14SKIP; mergef57afa6 e post-merge CI42526220441/Cloudflare42526220439 PASS (live E2E/deploy SKIPPED). Nessuna nuova TASK146 Admin, nessun generic CI dispatch o import live.
- Worker399720d2 invariato: target/progetto/rollback verificati, sorgente equivalente corrente e unità release ancora da riconciliare con owner. Console staff realmente autenticata mostra il vecchio fallback; nessun deploy o grant inventato.
- Scheda Getnet privata con3gruppi/8campioni, originali invariati. A/B/C MISSING dopo ricerca manifest/mail/provider; OneGMR UTC riguarda CSV/SFTP, applicabilità XLSX non provata. L'utente delega il giudizio senza nuovi fatti e conferma Win7 assente. Apply negato; policy nativa separata.
- Render client completo sintetico EN/IT a192DPI osservato; no prova100/125 o tastiera nativa per limiti superficie/input. Nessuna scala cambiata. DB reale integrity/FK PASS, vendite/outbox/transazioni storiche0. Processi posseduti fermati, trust/backup conservati; rimozione bytecode test negata da policy, file trattenuto. [Report corrente](reports/ASUS_W7POS_019_RETRY_E2E_RESULT.md).

## 2026-11-08 – Retry e qualifica1823697 (stessa ASUS-W7POS-019)

- Riprodotto il Retry Connect409→empty sul prodotto d262ccd:1auth/2catalog, storico nascosto. Fix comune Connect/Retry con SecureString readonly5min, proof readonly dall'unico guard esistente, busy prima degli await e riesame authority post-risposta/finale. Nessun secondo first-login HTTP o cache priming.
- Due rossi validi dopo review: normal completion tardiva e sessione rimasta autenticata dopo fault. Ora rollback della sola versione posseduta tramite CAS. Fence d262ccd intatto; gate/allowlist/budget invariati.
- Codice1823697 su main, CI42497670447/Security42497670514/Release42497670533/WPF42497682626 PASS. Core1734/1734, gate50/50, canonica15/15, functional20, foundation5/37, smoke8/52 e load11 con tre flag misura true. Prime failure conservate.
- Release41file/110checksum, QA40/34comuni byte-identici, development-unsigned. Installato1823697 e registry/versione verificati,41/41file identici, nessun POS auto-start. Backup online verificato e DB invariato; nessun import reale.
- Reader con nuove DLL prodotto7/7 PASS,4487ms, driver binario/oracolo/originali invariati, NeedsEvidence=true/CanApply=false. Tre regioni visive sintetiche osservate, import_summary leggibile. CSV50000 righe/snapshot:5656.8755ms totale,12.6125ms IO finale<=1000; query365.7015ms, cleanup0residui. Nessuna qualifica Win7 dedotta.
- Admin PR144 codicef8ac2fd6 qualificato4375pgTAP/1144foundation+14SKIP/UI48, draft REVIEW senza merge/deploy. Mac PR29 head137afa5e OPEN/ACTIVE; ACK server documentato nel RESUME_STATUS, nuovo ritorno Windows distinto. Avvio reale rifiutato dopo autorizzazione, prove esercenti e Win7/DPI/periferiche aperte; task non DONE. [Report corrente](reports/ASUS_W7POS_019_RETRY_E2E_RESULT.md).

## 2026-11-08 – Fix server del catalog409 (stessa ASUS-W7POS-019)

- Causa reale dimostrata con sole letture: shop active/lease valida pos_admin, zero righe e nessuna storia mapping; resolver old body9ef09cf7 restituisce legacy_owner_bridge/null/blocked e audit manifest/unmapped.
- AdminPR #143 integrata normalmente in1a35034a; nuova migration body9622c4d3, OID/owner/ACL e tutto pg_proc eccetto prosrc invariati. Primo CI555f502 bloccato dal pin CRLF; corretto solo pin canonico LF su d864c740, primo errore conservato.
- Post-merge CI42451262145/CF42451262142 PASS:67 file / 4.375 test pgTAP, foundation1.131 PASS / 14 SKIP / 0 FAIL, UI48/pairing2, nessun deploy Worker. Windows d262ccd invariato, suite pregresse riusate.
- Apply reale unico, versione sorgente20261108051044 e statementSHA canonico; allineato soltanto il nuovo record history generato dal connettore. V3 reale ora manifest shop_scoped completo zero, terminale, scope-key corretta; nessun articolo/grant/nuova tabella o dato commerciale alterato.
- Verifica normale HTTP/UI NOT_RUN_POLICY: avvio respinto con blocked by policy, processo non nato e nuovi pull0. Autorizzazione specifica pendente; nessun launcher alternativo/retry. Backup/inversa/log/prime failure/worktree conservati; nessun import Getnet/vendita/reset/revoca.
- [Risultato circoscritto](reports/ASUS_W7POS_019_CATALOG_409_FIX_RESULT.md); console staff, prove Getnet e Win7 restano distinti, nessun DONE.

## 2026-11-07 – Prosecuzione onboarding nuovo negozio (ASUS-W7POS-019)

- Chiusura software sul correttivo d262ccd979db: CI42432623358, Security42432623359, Release42432623356 e WPF42432636966 PASS. Core/Data1734/1734, gate50/50, canonica15/15, functional20, foundation5/37, smoke8/44 e load11 con tre flag true. I verdi e832d6a sotto sono intermedi: la regressione successiva è conservata.
- Prodotto scaricato41file/110checksum, QA40file e34 comuni identici; smoke CWD esterna8/44 e avvio ordinario senza login PASS. Reader DLL QA verificate identiche al prodotto7/7, attesi indipendenti/file invariati,4588ms, NeedsEvidence=true/CanApply=false. Nessun apply reale; development-unsigned, installation NOT_EXECUTED.
- Fence persistente fino full nonempty verificata: Core116/116, loopback8/44 e gate50/50 locali PASS dopo rosso immutato. import_summary capture NOT_RUN distinta da UI PASS; Mac PENDING, catalog409/prove reali/Win7 BLOCKED_EXTERNAL. Tutti i processi locali posseduti chiusi, risorse conservate con owner; chiusura solo docs, nessuna suite duplicata e nessun retry live.

Checkpoint precedenti della stessa sessione, conservati come storia:

- Fix fence ora focusedgreen: Core116/116, loopback8/44 e gate50/50. Blocca delta/approvazioni generiche; fullnonempty valida recupera, rollback conserva blocco. Nuovo commit/CI/pacchetti necessari, nessun riuso improprio dei verdi e832d6a.


- Review dopo i verdi e832d6a: nuova regressione Core1FAIL e loopback7/8FAIL riproduce sale-safe ripristinabile sui vecchi dati dopo full empty e delta. Runtime invariato durante entrambi i rossi, fingerprint stabile/cleanup confermato. Chiusura sospesa, fence durevole fino full nonempty in qualifica; nuovo candidato/pacchetti necessari. La riconciliazione documentale e832d6a è conservata privatamente come snapshot, non persa o promossa al fix.


- Ripresa dopo Esc completata: CI42418832135, Security42418832124, Release42418832168 e WPF42418868960 tutti PASS su e832d6a. Canonica15/15, Core1728/1728 e gate50/50 (log Release stessoSHA), functional20, foundation5/37, smoke8/44 e load11 con tre flag true. Nessun nuovo avvio workflow o suite duplicata.
- Prodotto scaricato41file/110checksum development-unsigned; QA40file e34 condivisi identici. Smoke QA CWD esterna8/44 e avvio ordinario senza login PASS alla ripresa. Reader DLL prodotto7/7 originali/oracolo integri, NeedsEvidence=true/CanApply=false,4551ms; nessun apply reale. Primo Esc, failure e verifica manuale invalida conservati distintamente.
- Cattura diagnostica import_summary NOT_RUN (raster uniforme scartato); contratto UI PASS distinto da qualifica visiva. Handoff Mac aggiornato ma invio/risposta PENDING; catalog409, prove merchant/tempo/overlap e Win7 restano BLOCKED_EXTERNAL, nessun DONE. Tutti i processi locali posseduti chiusi, profili/log/fixture mantenuti con owner. Chiusura documentale separata dal codice qualificato.

I punti di avanzamento sotto e il checkpoint Esc conservano la storia della sessione; lo stato conclusivo è nel report collegato.


- Ripresa da main locale/remoto pulito `e924a295`, consegna precedente `5c3327bd`; Admin remoto `d021a4af`, checkout Admin Windows preesistente preservato. Nessun reset/stash/branch automatico.
- NEW_SHOP_019 creata secondo dichiarazione utente; host console staging coincidente con default POS. Causa statica fallback overview staff compatibile; deployment non verificato e handoff Mac PENDING.
- Avviata correzione accesso storico circoscritto per catalogo autorevolmente vuoto, senza promuovere sale-safe o recovery. Primo avvio companion QA precedente FAIL per ValueTuple assente; payload prodotto raggiunge primo accesso su profilo nuovo isolato. Failure/log privati conservati.
- [Risultato onboarding](reports/ASUS_W7POS_019_ONBOARDING_EXECUTION_RESULT.md) conserva gate separati, ownership e ripresa; verifiche candidato in corso, apply reale e Win7 non attestati.
- Login POS reale del prodotto precedente riuscito tramite input mascherato utente: trust, snapshot ufficiale, generation e mirror pos_admin corrispondono allo scope. Catalogo HTTP409 unmapped, non empty autorevole; processo posseduto fermato, profilo/log conservati. Il resolver server statico blocca assenza righe shop-scoped/mapping: handoff Mac aggiornato, deployment ancora da verificare. L'utente conferma assenza prove esercente/inizioPOS/tempo.

## 2026-11-04 – Revisione conclusiva R3 storico Getnet (ASUS-W7POS-019)

- Aggiornato lo stesso [PLAN canonico](plans/ASUS-W7POS-019_GETNET_HISTORY/PLAN.md): R2 locale uguale allo SHA-256 ricevuto, main/HEAD invariati `32618249fc01f29e86c6774d2b2c9262a90f226f`; lavoro locale e audit precedenti preservati.
- R3 `PLAN_READY_FOR_REVIEW`: integrati R3-01..08 su contesti DB ausiliari/fixture, permit CSV e IO fuori lock, copertura senza divulgazione e geometria negativa, cinque scenari B autonomi, console sicura, diagnosi postjob e bundle univoco del run corrente. Matrice/assertion/comandi e delta filemap allineati; otto runner All e functional20 preservati.
- Solo piano, voce019 e worklog; implementazione `NOT_STARTED`, build/test/runtime `NOT_RUN`, nessuna pubblicazione Git o operazione DB/live. Prossimo passo consentito: review R3; futura Execution solo su richiesta, iniziando dalla tranche A.

## 2026-11-04 – Revisione mirata R2 storico Getnet (ASUS-W7POS-019)

- Aggiornato lo stesso [piano canonico](plans/ASUS-W7POS-019_GETNET_HISTORY/PLAN.md), preservando la R1 locale e la sua cronologia: SHA-256 iniziale uguale al target ricevuto; main GitHub/HEAD invariati su `32618249fc01f29e86c6774d2b2c9262a90f226f`. R1 e fonte sufficiente, nessun blocco per un altro piano Claude.
- R2 `PLAN_READY_FOR_REVIEW`: integrati R2-01..14 su replay terminale senza input, revisioni atomiche, recovery esistente, lease di tutti gli entry point/attese cancellabili, fotografia mista/KPI POS, CSV Core/fixture distribuite, campionamento e crash sullo stesso DB/Job. Definiti otto runner All, diagnosi parziali sicure e proiezione/manifest per upload pubblico; originali privati e gate esterni preservati.
- Solo PLAN, voce019 e worklog; implementazione `NOT_STARTED`, build/test/runtime `NOT_RUN`, nessun DB, live, commit o pubblicazione Git. Prossimo passo: review R2, nessuna Execution automatica.

## 2026-11-04 – Planning persistente storico Getnet (ASUS-W7POS-019)

- Creata la [task canonica](plans/ASUS-W7POS-019_GETNET_HISTORY/PLAN.md) dopo verifica della numerazione e assenza di un piano Getnet locale; baseline main/HEAD `32618249fc01f29e86c6774d2b2c9262a90f226f`, checkout inizialmente pulito.
- Piano R1 `PLAN_READY_FOR_REVIEW`: integrate correzioni su tenant proof nativo, capability/fence al commit, autorita DB e manutenzione, mapping persistente, cicli undo/reimport, movimenti firmati/resto e CSV tipizzato. Specificati smoke/load separati, receipt/validator/allowlist di contenuto, matrice gate e tranche A/B/C. Dati finanziari e riferimenti personali restano privati; fonte Claude distinta, originali, ora Getnet, binding reale e Win7 hanno blocchi espliciti.
- Solo documenti autorizzati; implementazione `NOT_STARTED`, build e test runtime `NOT_RUN`; nessun DB, servizio live o pubblicazione Git.

## 2026-10-16 – Evidenza completa per i gate falliti dopo PR128

- PR128 integrata normalmente in `1f7327f` dopo regressione causale, review e CI/Security sul candidato `0e9580a`: 1.599 Core/Data, 50 gate, visual e 19 scenari, Recovery 11/11 in 30,152 s con 60.000 ms invariati. Checkout allineato senza toccare lo stash preesistente.
- Release Pack postmerge `38978013394` FAIL 49/50 sul gate sintetico `check-owned-catalog-qa`. Nessun artifact: il wrapper conserva solo una tabella troncata, selezionando anche nomi TAP passati con `missing`. La causa del test figlio storico non è recuperabile e resta UNKNOWN; una prova locale sintetica PASS non la spiega.
- Corretto esclusivamente l'output dei gate falliti: trascrizione completa con nome e exit code, prima del riepilogo. Regressione per errore lungo seguito da TAP passati, FAIL con exit 0 e successo; classificazione e suite conservate. Nessun nuovo avvio Asus, modifica del timeout Recovery o retry del workflow invariato. Pacchetto e CI postmerge restano da qualificare sul nuovo SHA.
- La CI postmerge `38978013368` conserva separatamente Recovery FAIL in 30,039 s, dieci gruppi PASS e il primo interrotto da `OpenButton().Single()` senza pulsante visuale, dopo tre refresh sync. Il modello contiene già la riga; gli altri diciannove scenari e gli altri undici step sono PASS, cleanup verificato. Il harness ora completa il layout pendente prima della stessa ricerca stretta del pulsante reale e verifica il binding al batch corrente. Il primo flusso forza un refresh della collection senza yield prima dell'apertura per esercitare la rigenerazione dei container; nessun assert preesistente, gruppo, carico o budget rimosso. Verifica hosted del nuovo candidato ancora necessaria.
- Release Pack `38981128005` su `c33624a`: il transcript ora identifica il timeout del probe sintetico stdout/stderr a 15 s, dopo 73 test Node e collector identity PASS. Non sono conservati i suoi prefissi: causa ancora non dimostrata, distinta dal Recovery. Il probe mantiene 512 scritture alternate e sentinelle; aggiunge ingresso e tempi monotoni prima delle stesse scritture, snapshot limitato di primo/ultimo output, CPU e versione PowerShell prima del cleanup. Nessun buffer, nuovo launcher, aumento del budget o retry del workflow invariato. Test sintetico locale PASS; serve la nuova evidenza hosted per distinguere startup, avanzamento delle scritture e uscita.

## 2026-10-16 – Recovery: input dell'ombra separato dai controlli dinamici

- Ripresa da `3026ce5`; riusato il prefisso nativo `32ddfbd` con stack di tutti i worker, senza nuove tracce. Mappatura `Image/Load`: thread 6216 con 9.846 stack WPF, 8.580 Direct3D e 8.365 WARP; altri sette worker con WARP e CPU osservata. Stack di scheduling e traccia parziale, non un profilo CPU completo. Il `DialogShellWindow` è invariato fra i due SHA.
- Causa candidata: effetto dell'ombra sul contenitore di griglia e progress, che amplia il ridisegno quando cambiano i figli. Regressione in sette stati reali, valutata dopo tutti gli assert originali del primo gruppo. Patch limitata alla base: bordo decorativo vuoto dietro ai controlli, stessi parametri e bounds; root, sizing, owner e hit-test conservati. Undici gruppi, quattordici catture, carichi 5.000/1.000 e 60.000 ms invariati.
- Baseline test-only `b81f5ab`, run `38975083907`: TIMEOUT 60.085,986 ms con cleanup; sette osservazioni `isolated=False` e sagoma invariata, primo gruppo FAIL sulla nuova regressione dopo tutti gli assert originali, successivi sei PASS, ottavo incompleto, quattordici catture conservate. Il corpo functional inizia a 18.331,295 ms: il costo iniziale resta distinto dall'effetto sui controlli.
- Build WPF/harness Release x86 e standard dialog PASS; review statica senza rilievi bloccanti. Verifica hosted corretta, CI e Security dell'HEAD finale devono essere riscontrate nelle ricevute prima dell'integrazione. Nessun rilancio Asus o indagine sui permessi; Staging e Win7 restano separati.

## 2026-10-16 – Recovery: progress del parent durante il dialogo modale

- Ripresa da `5a9d66c`, senza riaprire i permessi: baseline hosted test-only `ce76a62`, run `38949548176`, riproduce il progress Sync Center visibile/indeterminato mentre Recovery attende l'utente. Tutti gli assert originali e gli undici gruppi eseguiti; dieci gruppi PASS, primo gruppo FAIL soltanto sulla nuova regressione, processo 53.252,673 ms, cleanup confermato. Sync controllato success/fault/cancel e guard doppio click PASS.
- Separato `_syncRunning`, usato solo per il progress, dal lock `_operationRunning` conservato durante autorizzazione e dialogo. Nessuna modifica a shadow, renderer, setup, dataset, assertion o limite 60.000 ms. Build WPF/harness Release x86 senza warning/errori e 50/50 gate locali PASS. Confronto hosted corretto, CI completa, review e integrazione richiedono le ricevute del candidato esatto; il difetto UI riprodotto non spiega automaticamente tutte le latenze di paging storiche.
- `f03d877`, confronto hosted `38950121389`: 11/11 gruppi PASS in 45.940,996 ms; progress parent spento e comandi bloccati. CPU osservata 106,984 s contro 130,344 s baseline su VM diverse; review mantiene HOLD sulla causa del timeout storico. Nel controllo dei soli nomi delle ricevute, trovato export del profilo trust sintetico: filtro JSON limitato a `sync-backup-load.json`, regressione sintetica prima FAIL/dopo PASS, nessuna lettura del profilo e nessuna rimozione di dati storici.

## 2026-10-15 – QA automatizzata e diagnosi Recovery residua

- Ripresa da PR128 `cafe6f2`: riprodotto e corretto `TIMEOUT` sovrascritto da `FAIL` quando falliva `Copy-FunctionalEvidence`; nuovo `evidenceExportError` separato, primo errore ed exit code preservati. Otto combinazioni esito/export nel test esistente, senza eseguire WPF.
- Rimossa la seconda `WaitForExit()` illimitata del runner functional: su Windows la redirection passa file handle al figlio, senza reader asincroni sul `Process` restituito. Gate locale 50/50 PASS, incluso test runtime sintetico con sentinelle finali stdout/stderr e cleanup. I 60.000 ms e tutti i gruppi/assert Recovery sono invariati; il timeout storico precede questa attesa e non è risolto dalla patch. Validazione hosted del nuovo HEAD e integrazione restano da verificare nelle ricevute esterne.
- Baseline main `3892f37`, pulita; preservati stash, altri worktree e i tre WIP nominati. Nessuna modifica al prodotto Core/Data/WPF o alle dipendenze.
- Conservati FAIL CI `38670961016` e TIMEOUT diagnostico `38775697755`. Tre coppie alternate `38776939297`: tutti PASS, entrypoint sperimentale senza beneficio, ipotesi JIT respinta e codice sperimentale rimosso. Causa Recovery UNKNOWN; timeout 60.000 ms e undici gruppi invariati.
- Runner funzionale con selezione mirata, report per tutti i 19 scenari più visual, conservazione primo errore e prosecuzione degli scenari indipendenti. Diagnostica monotona/gruppo/fase e campioni memoria/working set/handle/finestre WPF; nessuna qualifica risorse Win7 dedotta.
- Il candidato `eac24ed` conserva TIMEOUT/PASS/PASS nelle tre ripetizioni prestabilite; intervallo creazione→corpo functional osservato di 22,4 s, non il solo startup WPF, causa nativa UNKNOWN. Nuovi marker limitati distinguono Main, Startup, ingresso async, risultato e uscita. Suite precedente raccolta senza fermarsi al Recovery, compresi i FAIL.
- Dimostrato separatamente un difetto del nuovo runner: TEMP annidata nel report supera i limiti nativi SQLite/Framework. Stesso test e binario: FAIL con TEMP di 125 caratteri, PASS con 77; il runner corretto passa anche con report lungo/Unicode. Dati temporanei brevi e univoci separati dai report; nessun cambiamento al timeout o ai carichi WPF.
- Orchestratore Local/Windows invocabile senza credenziali e riusato dalla CI; staging delegato a vault/readiness canonici. Test sintetici coprono timeout, cancellazione, identità immagine, PID riusato, path, zero test, report e prosecuzione dopo errori. Review indipendente con tre difetti corretti e relative regressioni.
- Asus: SDK 10.0.301 già presente fuori PATH, build net48/x86 PASS e 179 Core/Data Recovery PASS. Avvio WPF locale rifiutato dal controllo automatico (`blocked by policy`), senza riproporlo tramite wrapper. WPF misurato solo su build indipendente hosted.
- Profilo QA esistente valido; readiness richiesta assente (404 sul main Admin corrente), nessun test live. Win7 differito dall'utente; mobile e hardware senza executor/dispositivi assegnati. Stato finale suite/PR/main/pacchetto nelle ricevute esterne della corsa, senza attribuire prove headless o hosted a Win7.

## 2026-10-01 – PR112 software closeout with owner-deferred external QA

- Owner decision: close PR112 as software-qualified and merge normally after exact-final-HEAD CI/Security PASS; verify the exact post-merge Release Pack. ASUS-THIRD-SCAN is `OWNER_ACCEPTED_DEFERRED_EXTERNAL_QA`, with root cause still unproven. Asus Integrated/Final remain `NOT_EXECUTED / OWNER_DEFERRED`; `productionCertified=false`.
- Initial main `bedcf17a97d814a3b098387cc2720ff2b11abbbf`, initial PR head `4a4af8a9afaa7d2d3858e7323b63fc22655b59f9`, clean 0/0. Compared with qualified source `aea65314b9de6f18a46a5ba9aefbeb2de363012d`, src/tests/scripts/.github are unchanged. Reuse 1,033 Core/Data tests, nine WPF scenarios, and hosted run `36510437821`: 738.848s/660 scans plus 3601.349s/3220 scans, both M/E/S=true. No repeat of the long qualification or claim that hosted evidence certifies Asus/Win7.
- Separate deferred QA: Asus physical Integrated/Final, Windows 7 SP1, Xprinter/spooler, barcode scanner, installer lifecycle, staging/image recovery. No product changes, paid signing, license change, unsigned retry, SAC change or threshold/budget relaxation. Preserve all historical failures, 55 unrelated worktrees and the image checkpoint. Software closeout completed; software blockers=0 in the owner-approved scope.

- Final PR head `272d5f1dd18b2c6a8b587bd4624b80b40367cf4a`: CI `36908943636` and Security `36908943627` PASS. Delta audit PASS: 32 product/harness/script/workflow files reviewed, no secret, unexpected binary/temporary path or security finding. Closeout additions changed only these three documentation files.
- PR112 merged normally as `c3af7f2177c4df8aba8407baa06dfe0f74871044`; parents are initial main and final PR head. Exact-merge CI `36911424442` and Security `36911424816` PASS. Release Pack `36911424362` PASS, canonical downloaded verification exit 0: manifest/hash/three GitHub archive digests, 41-file ZIP/payload/dist, matching Setup, SLSA/in-toto provenance, attestation/SBOM binding and Release x86/net48. Setup SHA256 `cdd0f1a222559e0a7a898524ebcc6e5c2ff0c6f21dafd0b2cb86c771f8c33e39`; payload ZIP SHA256 `f45351a91a1bea6ba8df40f36ec43b9ca88e4c675d29c68d2870af18e1a63b7d`. Stage `development-unsigned`, Setup `NotSigned`, installation `NOT_EXECUTED`. Final documentation publication and clean 0/0 are attested in the existing external closeout receipt; the verified delivery artifact remains bound to the merge SHA.

## 2026-09-26 – Post-PR111 root cause optimization (ASUS-W7POS-018, in corso)

- Baseline `bedcf17a97d8`, 56 worktree inizialmente puliti; candidati e checkpoint esterni preservati.
- Ablazione/reintroduzione: 500 card Grid anche nascoste causano gli stalli di realizzazione; 501 indicatori indeterminati invisibili causano render ricorrenti e ritardano callback che trattengono editor/VM chiusi. Patch mirate a virtualizzazione multicolonna, visibilità degli indicatori e coalescenza del solo focus/scroll dell'app.
- Corretto il harness: composizione PosView reale, identità persistenti, alternanza ordine, attesa visuale inclusa, osservatore limitato e flag separati. Ventotto vettori del validatore PASS, incluso rifiuto del soak storico, dei budget allargati, di fixture errate e identificativi numericamente duplicati.
- Matrice finale equivalente 20k/100k × 50/100/500 con identico osservatore; a 100k/500 Rows p95 reset-stress da 3394 a 187ms e container Grid da 500 a 11. Conservato il precedente 20k p95 259ms; il pannello definitivo misura 192ms, senza attribuire la variabilità solo alle ultime correzioni né certificare latenza monitor.
- Regressione canonica ha trovato/corretto inizializzazione lazy del generatore con carrello già popolato. PASS: 49 gate, 1.033 Core/Data, CLI, build x86, profilo immagini, 50 catture, otto scenari preesistenti, autorizzazione/logging/paging. Il nuovo scenario strutturale arriva al focus e resta FAIL per host non attivo.
- Controllo diagnostico 614,903s/26 cicli: heap idle 14,34–17,16MiB, coda 0–3, nessun overflow o root osservato verso finestre chiuse; budget congelati prima della qualifica. Desktop non corrispondente/non accessibile e host non attivo: ENVIRONMENT_VALID=false e STABILITY_PASS=false. Focus corrente da riverificare in sessione disponibile.
- Traccia WPR negata da Windows (0x80070005), nessun aggiramento; prove interne CPU/allocazioni e catene di riferimenti conservate privatamente. Qualifica integrata/finale, merge e hardware non dichiarati completati.
- Ripresa dopo sblocco: CI/security dell'head `0ed0a06` e tutti e nove gli scenari funzionali su Asus PASS. Il run Integrated si interrompe alla terza scansione Rows con `qualification_visual_scan_timeout`, ambiente osservato valido; stesso esito nella breve ablazione senza osservatore. Raccolta post-fallimento v16: 1.177 UpdatePeer pendenti e callback focus/testo/render, messaggio nativo dispatcher senza indicazione tastiera/mouse. Causa del residuo interattivo ancora aperta.
- Harness diagnostico v17 bloccato prima dell'avvio da Smart App Control, evento 3077, policy VerifiedAndReputableDesktop; sorgenti, hash e evento 3118 conservati privatamente. Nessun tentativo alternativo o modifica policy. Aggiunta diagnostica solo dopo timeout e tracing opzionale escluso dalla qualifica; nuovo percorso tracing compilato ma non verificato a runtime. Budget congelati invariati, run incompleti tutti non positivi.
- Report unico: [root cause optimization](reports/2026-09-26_POST_PR111_ROOT_CAUSE_OPTIMIZATION.md).
- Ripresa autorizzata PR112: canale B disponibile sul runner WPF GitHub esistente, build indipendente dai sorgenti senza trasferire il file negato. Run `36289638541` e `36290265761` completano cinque scan con/senza trace; il secondo confronta anche dispatch Input. Tutti M/E=true, S=false; CPU/96DPI e provenienza separati da Asus. Nessuna riproduzione del timeout sul runner.
- Watchdog thread-pool bounded e timeline eseguita verificati, zero drop; contesto reale del driver Send, confronto Input interamente incluso nei tempi. Differenza osservata senza attribuirle il timeout Asus; protocollo di qualifica invariato. Rafforzati completamento IsBusy/quantità, binding SHA Integrated, sei preflight runner e regressioni quantità/altezze/immagine dopo recycling; build x86 e 49 gate locali PASS. Suite runtime consolidata e stato CI esatto nell'attestazione esterna.
- Unica scheda di attivazione nel report: accettazione/firma Asus, readiness/cleanup staging e VM/hardware. Checkpoint e worktree preservati. PR112 resta draft; nessun merge, nuovo tentativo unsigned locale, modifica policy o qualifica finale dichiarata.
- Review finale: riprodotto un falso positivo del validatore per attesa visuale di un singolo scan sopra budget ma UI p95 ancora accettabile. Aggiunto controllo della colonna già misurata, rifiuto di valori mancanti/non finiti e tetto visuale fisso 250ms; quattro nuovi negativi, totale 32 vettori più sei preflight runner. Nessuna modifica al budget congelato o attribuzione al residuo Asus.
- Ripresa 2026-09-28: esteso il workflow QA manuale con Integrated 12 minuti e soak Integrated 60 condizionato al PASS, stesso host/payload e nessuna cancellazione per push documentali. Release Pack `36454068992`, CI `36454075173` e Security `36454075193` PASS sul codice `cb43fd4`; payload canonico di 41 file verificato e sovrapposto al harness tramite modulo esistente. Sette nuovi casi di aggregazione conservano FAIL e ricevute mancanti/non positive.
- Eseguito hosted `36456455827`: 733,084s, 33 cicli, 660 scan, 1.353 campioni ambiente validi; M/E=true, S=false per sei età ready sopra 250ms (primo ciclo 3, Background FireTick 280,275ms; massimo 309,130ms). Rows/Grid UI p95 68,496/61,105ms, private/managed picchi 87,020/16,326MiB, zero root/drop e 12 container Grid. Ricalcolo locale conferma esito; nessuna esclusione callback, patch causale non dimostrata o modifica budget. Soak 60 saltato, Asus/Final distinti. Unica scheda di firma/accettazione locale pronta, nessun nuovo avvio Asus, worktree/checkpoint preservati.

- Indagine FireTick 28–29 settembre: identificato InputManager.ValidateInputDevices (Background, 125ms), vittima del rendering ricorrente dell'indicatore nascosto ImageUploadProgress nell'editor. Candidato catalogo smentito e revertito; H6 rimozione/reintroduzione sullo stesso host conferma causa APP (0/800 attese >250ms con fix, 97/723 ripristinando il vecchio valore). Solo questa proprietà cambia nel prodotto rispetto al candidato cb43; regressione del lifecycle editor e CI/Security a69b275 PASS.
- Osservatore v2 risolve l'hook PriorityChanged pre-pubblicazione senza sottrarre due volte Inactive; test a clock controllato, tracce bounded con identità deboli, handler reali e tempi monotoni. Corretto anche il prerelease SemVer per prefissi SHA tutti numerici. H7/H8 dimostrano separatamente che il setup avviava scan con zero righe realizzate: v4 attende fixture renderizzata, registra 217–248ms di setup, mantiene i 250ms e tutte le scansioni. Sei prove accoppiate completate, ritardo UI iniziale da 128–137 a 0,2–2,0ms; timeout storico a freddo non attribuito automaticamente. Review corregge una race nel flush finale della sola traccia con regressione deterministica. Sorgente consolidato aea6531; nessun nuovo avvio Asus o merge.
- Verifica 2026-10-01 della qualifica cloud completata il 29 settembre UTC: CI 36507133349, Security 36507133252 e Release Pack 36508924895 PASS sul sorgente aea6531 (49 gate, 1.033 Core/Data senza skip, nove scenari WPF). Run 36510437821: Integrated 738,848s/33 cicli/660 scan e soak premerge 3601,349s/161 cicli/3220 scan, entrambi M/E/S=true; ricalcolo indipendente concorde con ogni metrica. Budget v1 invariato, protocollo v4, massimo ready 9,262/19,229ms, zero root/drop; binding di 41 file verificato. Pack development-unsigned, installer non eseguito. HOSTED-TIMER-AGE risolto sul runner; Asus/Final distinti e non qualificati, unica azione immediata nel report: attivare il canale ufficiale di firma/accettazione del harness identificato. PR112 resta draft, nessun merge o nuovo tentativo unsigned; prove storiche/worktree/checkpoint preservati.

## 2026-09-25 – Post-PR109 residual closeout (ASUS-W7POS-017)

- Baseline `be370ba3`, main locale/remota 0/0, 55 altri checkout puliti preservati; ASUS-W7POS-016 resta chiusa.
- Riprodotta omissione di `Win7POS-build-report.md` dal ReleasePack scaricato: il manifest lo richiede, il validatore canonico fallisce. Aggiunto il report alla lista upload.
- Esteso il solo harness prestazioni con tempi delle fasi renderer e soak finito senza GC forzato; runner riproducibile con hash prima/dopo, directory nuove e timeout.
- Prosecuzione 26 settembre: soak interrotto da Modern Standby (coperchio) dopo 58 minuti di campioni, conservato senza PASS. La ripetizione sul pacchetto #110 riproduce callback tardivo del product editor su CTS disposto; quattro righe di guardia e regressione runtime prima FAIL/dopo PASS, ottavo scenario CI, dialog 35/35.
- Readiness tipizzata: un positivo e 38 negativi, inclusi array scambiati per stringhe; ricontrollo della readiness Admin prima del lancio. Admin main aggiornata a `db5bb83`, senza nuova readiness Win7POS e senza modifiche Admin da questa task.
- Il runner articoli ora richiede il Release Pack dell'esatto SHA: download verificato, overlay completo e hash prima/dopo prepare e restart. Helper verificato con un positivo/sei negativi e con tutti i 41 file del pacchetto reale; 48 gate PASS dopo la modifica.
- Suite seriale: 1.033/1.033 Core/Data, 48 gate, build e smoke CI locali PASS. Prove package-aware e lunga durata documentate nel report di prosecuzione.
- Soak finale sul pacchetto corretto: 3.617,240 secondi, 115 cicli, 2.530 campioni, hash dei 41 file invariati e nessuna sospensione. Esecuzione PASS; stabilità FAIL nelle condizioni registrate: recupero spontaneo ma crescita ricorrente della coda, scansione con UI righe p95 1.735,806 ms. Dati/analisi/grafico pubblici, senza rimuovere outlier o forzare GC; nessun nuovo claim di leak permanente.
- Readiness Admin storica ritirata, deployment corrente non attestabile con l'autorità locale disponibile, checkpoint immagini preservato dopo denial storico. Nessun bypass, nuovo READY o installazione su macchina non autorizzata.
- Report: [post-PR109 residual closeout](reports/2026-09-25_POST_PR109_RESIDUAL_CLOSEOUT.md). SHA/run/hash finali nell'attestazione esterna; GitHub, staging e hardware hanno stati separati.

## 2026-09-25 – Functional and performance completion

- Partenza da main pulita `10e0b06fc84887c54308ea87b314e714fcb86cae`; altri worktree preservati.
- Correzioni F01–F07: export/gate, economia CLP e limiti, batch/off-dispatcher, LIKE letterale, sospesi durevoli/scoped, richieste UI obsolete e impostazioni atomiche.
- Difetti aggiuntivi riprodotti: preview/ricevuta e subscription, snapshot/chiavi obsoleti, modifica carrello dopo risposta persa al commit, footer login in errore catalogo.
- Regressioni Core/Data e WPF isolate collegate alla CI; 1.033 test, 48 gate, suite autorizzazione/immagini/log/paging e matrice visiva. Misure prima/dopo con 20k/100k prodotti e 200k/1m prezzi storici.
- Report: [functional/performance completion](reports/2026-09-25_FUNCTIONAL_PERFORMANCE_COMPLETION.md). CI, manifest e attestazione di consegna identificano lo SHA esatto; nessuna qualifica hardware/produzione implicita.

## 2026-07-30 – Article sync final acceptance e closeout

- Validato Win7POS dall'exact main post-PR68 senza modificare il checkout
  primario dirty.
- Integrate PR #69 (namespace isolato post-PR68) e PR #70 (report Gitleaks
  zero-findings non vuoto), entrambe con CI, CodeQL e supply-chain PASS.
- Acceptance staging finale PASS: login/trust/restart, catalogo autorevole da
  676 pagine, matrice article mutation completa, conflitto risolto, zero echo,
  outbox zero, vendite e hardware zero.
- Core/Data 746/746; WPF/harness net48/x86 0 warning/error; dialog 34/34;
  required gates 45/45; soluzione 0 warning/error; Gitleaks 0.
- Manifest cleanup consolidato a sette gruppi validato SELECT-only e non
  eseguito; handoff cross-repo `READY_FOR_MAC_FINAL_CLEANUP`.
- Chiusura: `DONE`, `USER_CONFIRMED_CLOSURE`, P0/P1/P2/P3=0/0/0/0;
  Windows 7 fisico resta `EXTERNAL_PENDING`.

## 2026-03-17 – Pulizia doc repo
- Spostati report storici in `docs/reports/` (DIALOG_REVERT_REPORT, ANALISI_MIGLIORAMENTI, CANDIDATI_PULIZIA).
- Archiviato piano eseguito in `docs/plans/_archive/dialog-positioning-standardization-plan.md`.
- Creato `docs/AI_WORKLOG.md` (questo file).

## 2026-03 – Dialog standardization (completata)
- Tutti i 25 dialog: `ShowHeader="False"`, footer/title con shared resources (`ModernStyles.xaml`).
- Positioning via `SourceInitialized`; niente `Left`/`Top` custom; clamp in `DialogShellWindow`.
- Owner policy: `DialogOwnerHelper.GetSafeOwner()` ovunque; nested dialog via `OwnerWindow ?? GetSafeOwner()`.
- Script audit: `scripts/check-dialog-standards.ps1`.

## 2026-03 – Fix tecnici core
- N+1 query in `ImportDiffer` → batch `GetByBarcodesAsync`.
- `Thread.Sleep` → `await Task.Delay` in `WindowsSpoolerReceiptPrinter`.
- `FileLogger`: aggiunto `lock(_writeLock)` per scritture concorrenti.
- `DiscountDialog`: rimosso `GetAwaiter().GetResult()` (blocco UI) → callback async.
- SQL injection in `Program.cs` → query parametrizzata con `@saleId`.

## 2026-03-18 – Audit utenti/ruoli, fix P0/P1 + hardening verifiche
- Branch / commit di partenza: `main` @ `8fea4df` (`Bug 4 schermi`).
- Obiettivo: audit end-to-end utenti/ruoli/permessi/testi UI e fix minimi per `RolesManage`, login/lockout, PIN numerico, sconto oltre limite, compat DB.
- File modificati:
  - `src/Win7POS.Wpf/Infrastructure/Security/{IOperatorSession.cs,OperatorSession.cs}`
  - `src/Win7POS.Wpf/MainWindow.xaml.cs`
  - `src/Win7POS.Wpf/Pos/PosViewModel.cs`
  - `src/Win7POS.Wpf/Pos/Dialogs/{OperatorLoginDialog.xaml.cs,ChangePinDialog.xaml.cs,NewUserDialog.xaml.cs,FirstRunSetupDialog.xaml.cs,DiscountDialog.xaml.cs,DiscountViewModel.cs,UserManagementViewModel.cs,UserManagementDialog.xaml}`
  - `src/Win7POS.Wpf/Pos/UserManagementView.xaml`
  - `src/Win7POS.Data/DbInitializer.cs`
  - `scripts/check-dialog-standards.ps1`
- Fix applicati:
  - `RolesManage` enforced nei command/metodi ruolo, UI ruolo nascosta/read-only senza permesso, save coerente con `PermissionEditMode`.
  - invalidazione/recreazione sicura di `UserManagementViewModel` su cambio operatore per evitare VM/permessi stale.
  - `LoginAsync` ora distingue `Success/Failed/LockedOut`; UI login mostra lockout esplicito.
  - validazione PIN solo numerica in create/change/reset + testi PIN uniformati.
  - `MaxDiscountPercent` applicato davvero; override richiesto su `pos.discount_over_limit`; dialog sconto resta aperto se override negato.
  - `EnsureMigrations()` esteso alle colonne security mancanti in `users`.
  - fix allo script `check-dialog-standards.ps1`: matcher case-sensitive + copertura `SetCurrentValue(Window.LeftProperty/TopProperty, ...)`.
- Verifiche eseguite:
  - `pwsh -File scripts/check-dialog-standards.ps1` → `ALL PASS`.
  - `dotnet build src/Win7POS.Core/Win7POS.Core.csproj --no-restore` → PASS.
  - `dotnet build src/Win7POS.Data/Win7POS.Data.csproj --no-restore` → PASS.
  - grep/code pass mirato su call-site login, Users/Roles, discount, migrazioni DB.
- Verifiche NON eseguite perché bloccate dall'ambiente:
  - build WPF `net48`/x86 reale su Windows non eseguibile da host macOS.
  - smoke test manuali WPF `Users/Roles` e `Discount` non eseguibili senza UI Windows.
  - test con DB vecchio/backup reale in app Windows non eseguibile qui.
- Prossimi passi su Windows:
  - eseguire `dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86`
  - rieseguire `pwsh -File scripts/check-dialog-standards.ps1`
  - fare smoke test manuali: tab/dialog `Users/Roles`, cambio operatore, sconto oltre limite (override negato/concesso), DB vecchio/backup reale con login/lockout/max_discount_percent

## 2026-03-18 – Debug e standardizzazione dialog ruoli / accesso operatore
- Data: `2026-03-18`.
- Obiettivo: correggere i bug UX di `RoleEditDialog` e `OperatorLoginDialog`, ridurre il drift dallo standard dialog e chiudere i problemi su titolo, layout codice ruolo, collisioni in duplica ruolo e focus PIN.
- File modificati:
  - `src/Win7POS.Wpf/Pos/Dialogs/RoleEditDialog.xaml`
  - `src/Win7POS.Wpf/Pos/Dialogs/RoleEditDialog.xaml.cs`
  - `src/Win7POS.Wpf/Pos/Dialogs/UserManagementViewModel.cs`
  - `src/Win7POS.Wpf/Pos/Dialogs/OperatorLoginDialog.xaml`
  - `src/Win7POS.Wpf/Pos/Dialogs/OperatorLoginDialog.xaml.cs`
- Fix applicati:
  - `RoleEditDialog`: aggiunti `TitleText` dinamico e `Grid.ColumnSpan="2"` su `CodeRow`; `Title` fallback normalizzato a `Ruolo`.
  - `RoleEditDialog`: focus iniziale per `Nuovo ruolo` / `Duplica ruolo` / `Rinomina ruolo`; guard `_submitted` contro doppio submit; callback `ValidateCode` con ritorno focus al campo corretto dopo popup di validazione.
  - `UserManagementViewModel`: introdotto `GenerateUniqueRoleCodeAsync` con schema `_copia`, `_2.._99` e fallback raro `DateTime.Now.Ticks`; validazione sincrona del codice duplicato in `NewRoleAsync` e `DuplicateRoleAsync`.
  - `UserManagementViewModel`: sanitizzati gli errori `UNIQUE` per evitare messaggi SQLite grezzi nella status bar.
  - `OperatorLoginDialog`: `SelectionChanged` sulla combo operatore per spostare il focus su `PinBox`; con un solo operatore il dialog apre direttamente con focus sul PIN.
  - Decisioni UX esplicite del batch:
  - `SelectedRole` resta volutamente non preservato dopo `New` / `Duplicate` / `Rename`; nessuna auto-selezione introdotta in questo batch.
  - il nome ruolo viene `Trim()`mato prima del salvataggio; nome vuoto o solo spazi resta bloccato; nomi duplicati con codici diversi restano consentiti.
- Verifiche eseguite:
  - `pwsh -File scripts/check-dialog-standards.ps1` → `ALL PASS`.
  - review del diff sui file toccati e conferma wiring `Owner`, titolo dinamico, validazione codice e focus dialog.
  - spot-check di `src/Win7POS.Wpf/Products/DeleteProductConfirmDialog.xaml[.cs]` e call-site: nessuna patch necessaria in questo batch.
- Verifiche non concluse:
  - `dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86` avviato su host macOS, ma rimasto senza esito conclusivo in questo ambiente.
  - smoke test manuali WPF non eseguibili qui: aperture dialog, caret visibile reale, focus di ritorno dopo chiusura `ModernMessageDialog`, duplica ruolo 2a/3a volta, accesso operatore mouse/tastiera.
- Prossimi passi su Windows:
  - eseguire `dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86`
  - rieseguire `pwsh -File scripts/check-dialog-standards.ps1`
  - fare smoke test `RoleEditDialog`: `Nuovo`, `Duplica`, `Rinomina`, focus iniziale, caret visibile e ritorno focus reale dopo popup di validazione
  - verificare duplica ruolo ripetuta: `*_copia`, `*_copia_2`, `*_copia_3`, e fallback oltre `_99` solo come safety net rara
  - fare smoke test `OperatorLoginDialog`: focus su combo con N>1 operatori, focus diretto su `PinBox` con N=1, e refocus sul PIN dopo selezione o login fallito

## 2026-03-18 – Import/Export Prodotti: audit completo + patch minima
- Data: `2026-03-18`.
- Obiettivo: correggere i bug di normalizzazione supplier/category nell'import Excel multi-sheet, rafforzare diagnostica e summary, garantire round-trip export XLSX -> reimport, migliorare il feedback UX della schermata Prodotti e allineare la policy `PriceHistory` orphan/backup al workflow WPF reale.
- File modificati:
  - `src/Win7POS.Core/ImportDb/ProductDbWorkbook.cs`
  - `src/Win7POS.Core/ImportDb/ProductDbExcelReader.cs`
  - `src/Win7POS.Core/ImportDb/ProductDbAnalysis.cs`
  - `src/Win7POS.Data/Import/CategorySupplierResolver.cs`
  - `src/Win7POS.Data/ImportDb/ProductDbImporter.cs`
  - `src/Win7POS.Core/Import/ImportApplyResult.cs`
  - `src/Win7POS.Wpf/Import/ImportWorkflowService.cs`
  - `src/Win7POS.Wpf/Import/ImportViewModel.cs`
  - `src/Win7POS.Wpf/Import/ProductDbImportViewModel.cs`
  - `src/Win7POS.Wpf/Products/ProductsViewModel.cs`
  - `src/Win7POS.Wpf/Products/ExportDataDialog.xaml`
- Fix applicati:
  - reader Excel: introdotta normalizzazione `trim + collapse whitespace` per supplier/category e aggiunti alias header inglesi del writer (`Name2`, `PurchasePrice`, `RetailPrice`, `SupplierName`, `CategoryName`, `StockQty`) per rendere robusto il reimport XLSX anche senza fallback posizionale.
  - workbook analysis: aggiunti conteggi e warning classificati per duplicati supplier/category, righe inutilizzate/non risolte, `PriceHistory` orphan e cap a 10 warning dettagliati; tracciato anche se i fogli dedicati sono assenti o presenti ma funzionalmente vuoti/sporchi.
  - importer legacy DB: lookup supplier/category allineati a `CategorySupplierResolver.Normalize()` e `PriceHistorySkipped` non piu` silenzioso.
  - resolver/import apply: aggiunti contatori `fromSheet/fromDb/created` per supplier/category e propagati nel risultato apply.
  - workflow import WPF: `LoadParseResultAsync` ora wrappa la lettura Excel in `Task.Run`, conserva `PriceHistoryRows`, separa chiaramente analysis summary vs apply summary, importa `PriceHistory` con policy `keep with warning`, e rende il backup pre-apply best-effort invece che bloccante.
  - overwrite per ID storico: documentata la precedenza dei fogli dedicati e aggiunto summary apply per i casi in cui `INSERT OR REPLACE` sovrascrive un nome supplier/category esistente sullo stesso ID.
  - Products UI: aggiunti `StatusMessage` espliciti per apertura/export annullato/progresso/refresh post-import, errori export distinti per `IOException` e `UnauthorizedAccessException`, e reset di `SelectedProduct` durante `SearchAsync`.
  - dialog export: label CSV chiarita a `CSV (solo prodotti)` e footer/title riallineati agli shared dialog resources.
  - scope boundary esplicitato nel codice: deduplica/migrazione dei duplicati storici gia` presenti nel DB e retention automatica dei backup restano fuori scope di questo batch.
- Verifiche eseguite:
  - `dotnet build src/Win7POS.Core/Win7POS.Core.csproj -c Release` -> PASS.
  - `dotnet build src/Win7POS.Data/Win7POS.Data.csproj -c Release` -> PASS.
  - `pwsh -File scripts/check-dialog-standards.ps1` -> `ALL PASS`.
  - `git diff --check` -> nessun whitespace/error di patch.
  - review mirata dei diff e dei call-site del workflow import/apply/export prodotti.
- Verifiche non concluse:
  - `dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86` non concluso su host macOS/sandbox: il target `net48` WindowsDesktop resta bloccato senza output finale in questo ambiente.
  - smoke test manuali WPF non eseguibili qui: import XLSX con 4 fogli, fogli dedicati vuoti/sporchi, duplicati supplier/category, `PriceHistory` orphan, export XLSX -> reimport, export CSV solo prodotti.
  - verifica DB reale su Windows non eseguibile qui: summary apply, overwrite per stesso ID, backup creato/non creato, refresh catalogo e combo dopo import.
- Prossimi passi su Windows:
  - eseguire `dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86`.
  - rieseguire `pwsh -File scripts/check-dialog-standards.ps1`.
  - fare smoke test della schermata Prodotti: export annullato, export file aperto in Excel, export CSV/XLSX riuscito, refresh catalogo e reset `SelectedProduct`.
  - eseguire i test manuali del batch import: workbook 4 fogli completo, foglio `Suppliers`/`Categories` assente, foglio presente ma solo header o righe sporche, duplicati sporchi supplier/category, `PriceHistory` orphan mantenuto con warning, round-trip export XLSX -> reimport.
  - validare su DB Windows reale il summary apply: `Products(...)`, `Suppliers(fromSheet/fromDb/created)`, `Categories(fromSheet/fromDb/created)`, `PriceHistory(inserted/skipped)` e `Dedicated sheet overwrites(...)`.
  - follow-up fuori scope: valutare una migrazione separata per duplicati storici supplier/category nel DB e una retention automatica della cartella backup.

## 2026-07-01 – Win7POS full audit Mac + hardening CI/check
- Branch / commit di partenza: `audit/win7pos-full-hardening` @ `6a21f0a`; branch creata da `main` per non continuare su main con worktree sporco.
- Obiettivo: audit completo Mac per build, CI, script statici, sicurezza/log, SQLite/outbox/restore, online/Admin Web boundary, dialog WPF e handoff Windows.
- Nota worktree: esistevano gia modifiche non committate su README, online/bootstrap/sync/WPF e nuovi script prima delle patch di questa sessione; preservate senza revert.
- Fix applicati:
  - workflow GitHub `ci`, `wpf-build`, `release-pack`: SDK .NET aggiornato da `8.0.x` a `10.0.x` per allineare `Win7POS.Cli` target `net10.0`.
  - `PosSyncStatusReader`: outbox summary usa label `sync.blocked` (`Bloccate`) invece di `sync.blockedAttention`.
  - `PosLocalization`: copy IT carta over-balance aggiornata a `La carta non può superare il saldo da pagare...`.
  - `check-pos-sync-status-ux.ps1`, `check-pos-shop-data-readonly.ps1`, `check-pos-revenue-copy.ps1`: resi i18n-aware leggendo le traduzioni oltre a XAML/code-behind.
  - creati `docs/reports/2026-07-01_WIN7POS_FULL_AUDIT.md` e `docs/reports/2026-07-01_CODEX_ASUS_WINDOWS_QA_TASK.md`.
- Verifiche eseguite:
  - `git diff --check` -> PASS.
  - `dotnet restore src/Win7POS.Cli/Win7POS.Cli.csproj` -> PASS.
  - `dotnet build` Core/Data/CLI Release -> PASS.
  - `dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86` -> PASS su Mac SDK `10.0.301`.
  - CLI selftest con `WIN7POS_DATA_DIR=/tmp/win7pos-codex-selftest` -> `自检 PASS`.
  - script statici dialog, staging config, debug logging, online client/bootstrap, sale-safe UI, start-of-day, catalog pull, sales sync, restore guard, legacy DB migrations, no eager DB startup, startup Win7-safe, sync UX, shop readonly, revenue copy e product free-text -> ALL PASS.
  - `check-release-pack-completeness -ReleasePackSource dist/Win7POS` -> PASS su drop preesistente.
- Non eseguiti:
  - `systeminfo`, smoke WPF reale, Windows 7 runtime/hardware, stampante, DPI/multi-monitor e installer Inno Setup fresco: richiedono Windows/ASUS.
- Prossima fase: REVIEW + esecuzione `docs/reports/2026-07-01_CODEX_ASUS_WINDOWS_QA_TASK.md`; nessun commit effettuato.

## 2026-07-01 – Win7POS missing tasks closure Mac
- Branch / commit: `audit/win7pos-full-hardening` @ `6a21f0a`; worktree ancora non pulito e non ripulito.
- Obiettivo: chiudere i missing tasks del full audit senza dichiarare smoke Windows/hardware non eseguiti.
- Stato/diff:
  - `git status --short`, `git diff --stat`, `git diff --name-status` e `git diff --check` rieseguiti.
  - Diff review separata in `docs/reports/2026-07-01_WIN7POS_MISSING_TASKS_CLOSURE.md`: batch audit, preesistenti/non attribuiti, file toccati ora.
- Fix/documentazione applicati:
  - creato `docs/reports/2026-07-01_WIN7POS_MISSING_TASKS_CLOSURE.md`.
  - aggiornato `docs/reports/2026-07-01_CODEX_ASUS_WINDOWS_QA_TASK.md` con prompt operativo Windows/ASUS completo.
  - aggiornato `docs/reports/2026-07-01_WIN7POS_FULL_AUDIT.md` con addendum closure.
  - `.github/workflows/release-pack.yml`: release pack ora rimuove `*.pdb` dopo il copy in `dist/Win7POS`.
  - `scripts/win7pos/windows/build-release-x86.ps1`: drop Windows builder ora rimuove `*.pdb` dopo il copy output.
- Verifiche rieseguite:
  - `git diff --check` -> PASS.
  - `dotnet --info` -> SDK `10.0.301`.
  - `dotnet restore` CLI/WPF -> PASS.
  - `dotnet build` Core/Data/CLI Release -> PASS.
  - `dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86` -> PASS, 0 warning/errori.
  - `WIN7POS_DATA_DIR=/tmp/win7pos-codex-missing-selftest-current dotnet run --project src/Win7POS.Cli/Win7POS.Cli.csproj -c Release -- --selftest --keepdb` -> `自检 PASS`.
  - script statici dialog, public staging config, debug logging, online client/bootstrap, first-login sale-safe, start-of-day, catalog pull, sales sync, restore guard, legacy DB migrations, startup Win7-safe, product free text, sync UX, shop readonly, revenue copy -> ALL PASS.
  - PowerShell parse di `scripts/win7pos/windows/build-release-x86.ps1` -> PASS.
- Release pack fresco:
  - cartella: `/tmp/win7pos-missing-closure-current-20260701_135330/Win7POS`.
  - zip: `/tmp/win7pos-missing-closure-current-20260701_135330/Win7POS_missing_closure_current_20260701_135330.zip`.
  - `check-release-pack-completeness` su cartella e zip -> ALL PASS.
  - sweep manuale: rimossi `*.pdb`; nessun sorgente/script/config locale/dev file trovato; match residui solo copy PIN/password, `publicKeyToken` e istruzioni per tenere unset il flag HTTP LAN.
- Non eseguiti:
  - ASUS/Windows smoke reale: `ASUS_NOT_RUN`, task pronto.
  - Installer Inno Setup: `iscc` non disponibile su Mac; richiede Windows/ASUS o CI release workflow.
  - CI GitHub: non lanciata da questa sessione.
- Prossima fase consigliata: nuovo giro ASUS/Windows, poi review umana prima di staging/commit; nessun commit effettuato.

## 2026-07-01 - ASUS Windows printer/cash drawer hardening
- Branch / base: `qa/asus-printer-cashdrawer-hardening-20260701`, creata da `qa/asus-win7pos-result-20260701` @ `63cdaaa`; `main` non toccato.
- Ambiente: SDK .NET `10.0.301` da `C:\Dev\dotnet10`, `WIN7POS_DATA_DIR=C:\POSData\TestRun1`.
- Fix applicati:
  - aggiunta enumerazione Win7-safe delle stampanti Windows installate con rilevamento default e virtual/PDF/XPS probabile.
  - introdotte chiavi settings dedicate per ricevuta, auto-print, default Windows, stampanti virtuali e cassetto.
  - ampliato dialog impostazioni stampante con lista driver, selezione ricevuta, test print, configurazione cassetto e test drawer.
  - reso il payment flow sale-safe: vendita salvata prima di cassetto/stampa; errori di stampa/cassetto diventano warning non distruttivi.
  - rimosso fallback automatico alla stampante predefinita Windows nello spooler; `PrinterName` e' obbligatorio.
  - cassetto disabilitato di default e apertura consentita solo con configurazione esplicita non virtuale.
  - aggiunto `scripts/check-pos-printer-cashdrawer-safety.ps1`.
- Smoke/QA:
  - harness servizio: `Microsoft Print to PDF` rilevata come default+virtuale; `OneNote (Desktop)` rilevata virtuale.
  - cash sale senza stampante configurata: vendita salvata, auto-print bloccato con warning, DB count 1.
  - `Microsoft Print to PDF` configurata con virtuali disabilitate: vendita salvata, auto-print bloccato prima dello spooler.
  - card sale dummy salvata con `paid_cash=0`, `paid_card=2345`.
  - backup DB PASS e restore guard con outbox pending PASS.
- Verifiche automatiche:
  - build Core/Data/CLI/WPF Release x86 PASS; CLI selftest PASS.
  - script statici richiesti PASS, incluso nuovo controllo printer/cash drawer.
  - release pack Windows PASS, completeness PASS, avvio da `dist\Win7POS` PASS, installer Inno PASS.
- Limiti:
  - desktop Windows bloccato durante Computer Use: UI WPF reale non cliccabile in questa sessione.
  - stampante POS fisica e cassetto fisico non disponibili; test hardware marcati SKIP, non dichiarati PASS.
- Report: `docs/reports/2026-07-01_ASUS_PRINTER_CASHDRAWER_QA_RESULT.md`.

## 2026-07-01 - Mac final ASUS review and controlled main merge
- Branch integration: `integration/win7pos-asus-final-review-20260701`, creata da `main` aggiornato a `8c94275`.
- Branch ASUS revisionate:
  - `qa/asus-win7pos-result-20260701` @ `63cdaaa`.
  - `qa/asus-printer-cashdrawer-hardening-20260701` @ `8ba8a25`.
- Merge integration: `63501e0`; conflitto risolto in `src/Win7POS.Wpf/Localization/PosLocalization.cs` scegliendo catalogo traduzioni lazy ASUS.
- Fix Mac applicato: `scripts/check-pos-startup-win7-safe.ps1` ora accetta la forma lazy corretta di `PosLocalization`; il comportamento runtime non e' stato cambiato.
- Verifiche integration:
  - build Core/Data/CLI/WPF Release x86 PASS.
  - CLI selftest con `WIN7POS_DATA_DIR=/tmp/win7pos-final-merge-selftest` -> `自检 PASS`.
  - tutti gli script statici richiesti, incluso `check-pos-printer-cashdrawer-safety.ps1`, -> ALL PASS.
  - secret scan: solo falsi positivi documentali/checker; nessun secret reale.
  - artifact scan: nessun artifact generato tracciato.
- Evidenza ASUS usata: release pack PASS, installer Inno PASS, WPF smoke PASS con limiti hardware dichiarati, printer/cashdrawer software PASS.
- Report finale: `docs/reports/2026-07-01_MAC_FINAL_ASUS_REVIEW_AND_MAIN_MERGE.md`.
- Decisione Mac: `READY_FOR_MAIN_MERGE`; main merge/push da eseguire solo dopo commit report e check rapidi su main.

## 2026-07-01 - Win7POS ASUS QA final closure
- main @ `d4b6215`; `origin/main` verificato a `d4b62157a2b683a067ac24b6bcc5915aeda2c0c3`.
- Final checks su main: `git diff --check`, SDK `.NET 10.0.301`, PowerShell `7.6.3`, restore CLI, build Core/Data/CLI/WPF, CLI selftest, dialog standards, staging config, debug logging, online client/bootstrap, sales sync, restore guard, startup Win7-safe e printer/cashdrawer safety -> PASS.
- Security/artifact: secret scan finale solo falsi positivi documentali/checker; nessun artifact generato tracciato.
- Hardware limits: stampante fisica, cassetto fisico, Windows 7 fisico e multi-monitor/DPI hardware non completati; non dichiarati PASS.
- Next steps: smoke hardware Win7 reale, decisione su payment method `other`, cleanup branch temporanee dopo conferma.
- Report: `docs/reports/2026-07-01_WIN7POS_TASK_CLOSURE_FINAL.md`.
- Decisione: `TASK_CLOSED_MAIN_PUSHED`.

## 2026-07-01 - Win7POS branch cleanup post-merge
- main/origin main verificati a `7ba92bb8981dbb7f6f9cee246a879100e2ed2583` prima del cleanup.
- Branch remote cancellate dopo ancestry `origin/main` exit 0: `origin/handoff/win7pos-asus-qa-20260701`, `origin/qa/asus-win7pos-result-20260701`, `origin/qa/asus-printer-cashdrawer-hardening-20260701`.
- Branch locali cancellate con `git branch -d` dopo ancestry `main` exit 0: `audit/win7pos-full-hardening`, `handoff/win7pos-asus-qa-20260701`, `integration/win7pos-asus-final-review-20260701`.
- Branch mantenute: nessuna tra quelle richieste.
- Nessun force delete, nessun tag cancellato, nessun codice modificato.
- Report: `docs/reports/2026-07-01_WIN7POS_BRANCH_CLEANUP.md`.
- Decisione: `CLEANUP_DONE`.

## 2026-07-06 - Refactor architettura ideale Win7POS
- Branch / base: `refactor/ideal-architecture-win7pos` da `origin/main` @ `d25a377`.
- Refactor applicato:
  - spostati `PosAdminWebClient`, reader Excel/HTML fornitore e reader/writer Product DB da Core a Data.
  - Core resta `netstandard2.0` senza WPF, SQLite/Dapper, HTTP transport o package Excel concreti.
  - aggiunti servizi Data per apply import prodotti, proof smoke fornitore, clear SQLite pool e persistenza transazionale refund/void.
  - WPF non usa piu direttamente `Microsoft.Data.Sqlite` o Dapper; resta responsabile di UI, dialog, stampa e composizione.
  - rafforzato `scripts/check-architecture-boundaries.ps1` con gate Core/Data/WPF, Supabase marker, payload redaction e project references.
  - aggiunti test MSTest `ArchitectureBoundaryTests`.
- Docs aggiornati: `README.md` e `docs/ARCHITECTURE/POS_ADMIN_SUPABASE_SYNC_ARCHITECTURE.md`.
- Live gate non dichiarati completati: staging Supabase/Admin Web/Cloudflare e smoke Windows 7 fisico/stampante.

## 2026-07-07 - Consolidamento architettura 100
- Branch / base: `refactor/architecture-100-consolidation` da `main` @ `4b0c149` (`fix: localize DB restore owner error`).
- Audit eseguito:
  - project references e target verificati: Core senza reference, Data -> Core, WPF/CLI -> Core+Data; Core/Data `netstandard2.0`, WPF `net48`/x86/`Prefer32Bit`.
  - `rg` mirati su Core/Data/WPF per UI, SQLite/Dapper, HTTP/Excel concreti, Supabase marker, hard delete, TODO/NotImplemented e payload/token.
  - nessuna deviazione codice trovata che richiedesse spostamenti di layer o refactor WPF/Data.
- Consolidamento applicato:
  - `scripts/check-architecture-boundaries.ps1` rafforzato con controlli target/proprieta csproj.
  - `ArchitectureBoundaryTests` ampliato con test espliciti per target, reference shape, WPF no SQLite/Dapper diretto, Data no WPF/UI, marker Supabase e redazione payload.
  - `README.md` e `POS_ADMIN_SUPABASE_SYNC_ARCHITECTURE.md` riallineati al branch e ai gate correnti.
- Verifiche automatiche:
  - `pwsh -File scripts/check-dialog-standards.ps1` -> PASS.
  - `pwsh -File scripts/check-architecture-boundaries.ps1` -> PASS.
  - `dotnet build src/Win7POS.Core/Win7POS.Core.csproj -c Release` -> PASS.
  - `dotnet build src/Win7POS.Data/Win7POS.Data.csproj -c Release` -> PASS.
  - `C:\Dev\dotnet10\dotnet.exe test tests/Win7POS.Core.Tests/Win7POS.Core.Tests.csproj -c Release` -> PASS, 24 test.
  - script catalog/import/sync/restore/logging (`check-pos-catalog-import-outbox`, `check-pos-catalog-import-sync`, `check-pos-catalog-pull`, `check-pos-online-bootstrap`, `check-pos-online-client`, `check-supplier-excel-wizard`, `check-pos-debug-logging`, `check-win7pos-restore-guard`, `check-pos-sales-sync`, `check-pos-start-of-day-sync`) -> PASS.
  - `C:\Dev\dotnet10\dotnet.exe run --project src/Win7POS.Cli/Win7POS.Cli.csproj -c Release -- --selftest --keepdb` con DB temporaneo fuori repo -> `自检 PASS`.
  - `C:\Dev\dotnet10\dotnet.exe build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86` -> PASS.
- Non eseguiti:
  - smoke fisico Windows 7 SP1, stampante Xprinter/spooler reale, multi-monitor/DPI e rete instabile: richiedono hardware/VM reale.
  - staging/Admin Web/Supabase credentialed E2E, Cloudflare deploy/CI e release packaging completo: richiedono credenziali o pipeline owner.

## 2026-07-07 - Final architecture 100 merge readiness
- Branch / base: `final/win7pos-architecture-100-merge` da `main` @ `4b0c149`.
- Scope:
  - riportato il consolidamento architecture-100 sul branch finale richiesto.
  - verificati branch recenti (`refactor/*`, `fix/*`, `integration/*`, `pr/*`): nessun commit/diff utile rimasto fuori da `main`.
  - creato report `docs/reports/2026-07-07_WIN7POS_FINAL_100_MERGE_READINESS.md`.
- Gate locali/Windows build host:
  - `pwsh -File scripts/check-dialog-standards.ps1` -> PASS.
  - `pwsh -File scripts/check-architecture-boundaries.ps1` -> PASS.
  - `dotnet restore Win7POS.slnx` con SDK 9 sul PATH -> FAIL atteso `NETSDK1045` per `net10.0`; rieseguito con `C:\Dev\dotnet10\dotnet.exe restore Win7POS.slnx` -> PASS.
  - `C:\Dev\dotnet10\dotnet.exe build Win7POS.slnx -c Release --no-restore` -> PASS.
  - `C:\Dev\dotnet10\dotnet.exe test tests/Win7POS.Core.Tests/Win7POS.Core.Tests.csproj -c Release --no-build --no-restore` -> PASS, 24 test.
  - `C:\Dev\dotnet10\dotnet.exe run --project src/Win7POS.Cli/Win7POS.Cli.csproj -c Release --no-build --no-restore -- --selftest --keepdb` -> `自检 PASS`.
  - `C:\Dev\dotnet10\dotnet.exe build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release -p:Platform=x86 -p:PlatformTarget=x86` -> PASS.
  - tutti gli script `scripts/check*.ps1` compatibili e richiesti -> PASS; `scripts/check-pos-restore-guard.ps1` -> NOT FOUND, sostituito da `scripts/check-win7pos-restore-guard.ps1` -> PASS.
  - `scripts/win7pos/windows/build-release-x86.ps1 -BuildInstaller` -> PASS; release pack e installer Inno generati ma non versionati.
  - `check-release-pack-completeness -ReleasePackSource dist\Win7POS` e `check-win7-runtime-release-validation -ReleasePackSource dist\Win7POS` -> PASS.
- Decisione: `READY_FOR_WIN7_PHYSICAL_SMOKE`.
- Non eseguiti/non dichiarati PASS: Win7 SP1 fisico/VM, Xprinter reale, scanner barcode reale, staging/Admin Web/Supabase credentialed E2E e Cloudflare/CI owner-authenticated.

## 2026-07-14 - Shop-scoped sync finalizzato e handoff Asus
- Branch di integrazione: `integration/win7pos-mac-final-20260714` da `origin/main` @ `353b7f752db61bed77ae1eadd3207b841eb4c961`.
- Commit implementazione: `94bb9573544811ef97c45f74b4ccf3ac85dc10de` (`fix: finalize Win7POS shop-scoped sync architecture`).
- Architettura: outbox sales/catalog vincolate allo shop origine, payload e idempotenza immutabili, retry/lease fail-closed, barriera/epoch di transizione shop, full-refresh/tombstone, restore cross-shop/TOCTOU e reversal hardening.
- Gate Mac: 29/29 script `check-*.ps1` PASS; Core/Data/CLI Release e WPF `net48/x86` PASS con zero warning/errori; test Core 69/69; CLI selftest, sales sync, shop cache, catalog outbox/reconciliation/HTTP, SQLite integrity e restore guard PASS; tombstone 5/5 e race transizione 1/1 PASS.
- Release pack: completezza e runtime validator PASS su cartella e ZIP; PE x86 e `unzip -t` PASS; SHA-256 registrati nell'handoff.
- Secret/artifact review: nessun secret, path privato, DB/log/build output/PDB o artefatto Codex incluso.
- Handoff: `docs/HANDOFFS/WIN7POS-ASUS-RUNTIME-VALIDATION-2026-07-14.md`; 30 test restano `DEFERRED_TO_CODEX_ASUS`.
- Classificazione: `PUBLISHED_TO_MAIN`, `PASS_STATIC_ARCHITECTURE`, `PASS_AUTOMATED_TESTS`, `PASS_BUILD`, `EXTERNAL_TEST_PENDING_CODEX_ASUS`.

## 2026-07-15 - Lease autorizzazione offline e binding riga reversal
- Branch di integrazione: `integration/win7pos-mac-final-20260714` dall'ultima `origin/main` @ `8c39e13c0f7a001956023919dd6bda612288351f`.
- Commit implementazione: `138b3e64d82558e069bb04920bfda62e5d642b72` (`fix: finalize Win7POS shop-scoped sync architecture`).
- Sicurezza sessione: lease offline fail-closed di massimo 12 ore derivata da `serverTime` autenticato, ricezione locale, expiry e high-water di processo; guard unico su PIN locale, permessi, override, cambio operatore e commit vendita.
- Contratto reversal: `clientOriginalLineId` additivo su refund/void; payload legacy incompleto bloccato in preflight prima della rete senza cancellare outbox/catalog/mirror.
- Gate Mac: 30/30 script `check-*.ps1` PASS; nuovo scanner lease/reversal 22/22; Core/Data/CLI Release e WPF `net48/x86` PASS con zero warning/errori; Core test 82/82 e filtri mirati 27/27; tutti gli otto harness CLI PASS.
- Release pack esterno rigenerato dall'output WPF x86, completezza/runtime validator e `unzip -t` PASS; ZIP SHA-256 `b80cebc63954a423c6abb9b64e9aa02ed4b1c165b96e2a85bd963c4a8ecb4ede`.
- Handoff aggiornato in-place: `docs/HANDOFFS/WIN7POS-ASUS-RUNTIME-VALIDATION-2026-07-14.md`; nessun documento equivalente duplicato.
- Classificazione runtime invariata: `EXTERNAL_TEST_PENDING_CODEX_ASUS`; test Windows/UTM/hardware 1-30 restano `DEFERRED_TO_CODEX_ASUS`.

## 2026-07-15 - Parità economica refund/void con RPC Admin
- Branch di integrazione: `integration/win7pos-reversal-economics-20260715` da `origin/main` @ `0d6eaff0870014a93b29f184e868d6a619d67387`.
- Commit implementazione: `dc162aeff484b576ef21565338cf3d5d492285d4` (`fix: align reversal economics with Admin contract`).
- Contratto economico: gross derivato dalle sole righe item; discount/tax reversal allocati sul gross cumulativo con arrotondamento PostgreSQL `numeric` half-away; quota corrente calcolata come target cumulativo meno quota precedente effettiva; net `-(gross-discount+tax)`.
- Fail-closed: originale e reversal precedenti riletti dal payload outbox immutabile con verifica SHA256; storia gross-only/corrotta/bloccata rifiutata senza riscrivere payload/hash; invio di reversal successive serializzato sugli ACK precedenti dello stesso originale.
- Workflow: pseudo-righe `DISC:*`/`TAX:*` escluse da selezione e binding; preview, dialog, payment, totale locale e payload condividono la stessa policy; reversal inviate item-only con header `gross/discount/tax/net/paid` coerente.
- Gate Mac reali: Core/Data tests Release 95/95; policy mirata 6/6; integrazione discounted+taxed partial/full, successive rounding/ACK e legacy immutabile 3/3; WPF x86/net48 e Core/Data/CLI Release PASS zero warning/error; CLI selftest e TASK-081 sales harness PASS; 31/31 scanner `check-*.ps1` PASS, nuovo scanner economics 12/12; `git diff --check` PASS.
- Release pack esterno rigenerato dal commit implementazione: cartella e ZIP completezza/runtime/startup/linking PASS, `unzip -t` PASS, PE32/x86, nessun PDB/CLI/source/DB; ZIP SHA-256 `9e489fcbcc770159ea99f748b3feb38c05d28ec4f409719a382e054455d4cd84`.
- Handoff aggiornato in-place: `docs/HANDOFFS/WIN7POS-ASUS-RUNTIME-VALIDATION-2026-07-14.md`; nessun duplicato.
- Runtime invariato: `EXTERNAL_TEST_PENDING_CODEX_ASUS`; test Windows 7/UTM/staging autenticato/hardware 1-30 restano `DEFERRED_TO_CODEX_ASUS`, mai dichiarati PASS da Mac.

## 2026-07-17 - Incremental-first sync, fencing e Sync Center

- Branch: `feature/win7pos-incremental-sync-ui-v2-20260716-201301` da audit
  `b590335348937ca830c92289c84032523e267497`; `origin/main`
  `5160b7c1574313ac8be47fdf2e139bb715a37e7d` non modificata.
- Policy Core: incremental/resume per tutti i trigger normali; full limitato alle
  dieci ragioni evidence-driven del contratto (bootstrap, binding/cursor legacy non
  recuperabile, cursor/reset server, cambio shop, restore, exactness, repair
  amministratore o migrazione esplicitamente incompatibile).
- Safety: CAS sales preflight, guard `Success/Value/Ok`, caller cancellation
  preservata, barriera/epoch/shop/mode/cursor su apply/checkpoint/exactness/restore e
  late response bloccata.
- Coordinator: single-flight dirty-bit, massimo due run per drain, resume dopo 5 s,
  polling 24-36 s, backoff 5/15/30/60/120/300 s ±20%, auth stop e diagnostica
  `pos.catalog.sync.*`.
- UI: Sync Center moderno, full repair separata/permission-gated, diagnostica safe,
  focus scanner ripristinato, shared visual polish e copy IT/EN/ES/ZH.
- Verifica: 29/29 gate, 204/204 test, CLI selftest PASS, WPF net48/x86 PASS 0/0,
  policy 34/34 e trigger normali 100/100 incremental/resume.
- Performance sintetica: delta mediane 17.383/26.772/162.696 ms per 10/100/1000;
  full 19.762 mediana 3865.609 ms, exactness `Verified` 3/3, pending 0.
- Release: pack x86, completezza/runtime validator e installer Inno PASS.
- Visuale: first login isolato PASS sul solo host 1440×900/200%; matrice
  1024/1366 a 100/125 e superfici autenticate non eseguite senza credenziali.
- Restano esterni: staging autenticato, Win7 fisico/VM, Xprinter, scanner, cash
  drawer e x86 full-sync memory certification.

## 2026-07-17 - Final closure review (NOT_DONE)

- Provenance verificata dalla source cumulativa
  `codex/win7pos-sync-drain-closeout-20260717` @ `75be03853a95cbb1b38db249b2b332f3f3549a32`:
  22 commit sopra `origin/main` iniziale `5160b7c1574313ac8be47fdf2e139bb715a37e7d`,
  tutti gli antenati richiesti presenti, nessun lavoro di branch mancante dimostrato.
- Review read-only A-E completata. Tre P1 riproducibili corretti sulla review branch:
  retry endpoint-offline senza transizione NIC, inizializzazione customer display
  best-effort e dialog impostazioni adattivo all'area di lavoro. Zero P0/P1 aperti.
- Finale locale: 30/30 gate, 249/249 test senza skip, solution e WPF net48/x86
  0 warning/0 error, CLI selftest e UiSmokeHarness build PASS.
- Benchmark: 2.000 righe batch/legacy mediana 248,930/19.951,492 ms (`80,15x`);
  full sintetico 19.762 righe mediana 4.293,185 ms, `Verified` 3/3, pending 0.
- Release pack x86, validator runtime e installer Inno locali PASS; harness, DB,
  PDB, sorgenti e marker secret esclusi.
- Stato task: `NOT_DONE`. Staging autenticato non eseguito senza credenziali QA;
  host disponibile Windows 11 con un solo monitor e senza scanner, Xprinter o
  cash drawer, quindi Win7 fisico, dual monitor, hardware, DPI e matrice runtime
  restano `NOT_RUN`. Nessuna PR, merge o modifica a main.

## 2026-07-17 - Final closure publication and blocker recheck (NOT_DONE)

- Recuperata senza ricostruzione la review branch
  `release/win7pos-final-review-20260717-015659` a `1b11a6f`; ancestry source e
  main verificata, nuovo bundle completo creato e validato.
- Review branch pubblicata su `origin` senza force; nessuna PR creata e `main`
  invariata.
- Review cumulativa aggiornata: 22 commit source + 2 finali, 110 file, tre lane
  read-only sulle cinque aree richieste, zero nuovi P0/P1 e nove P2 invariati.
- Rerun locale: 30/30 gate, 249/249 test, skipped 0, solution/WPF x86/harness
  0 warning e 0 error, CLI selftest PASS.
- Release pack PE32/x86, completezza, validator Win7 e installer Inno PASS; nessun
  DB/WAL/SHM, PDB, harness, screenshot, config QA attiva, credenziale o path
  personale nel pack.
- Computer Use sul data root isolato ha confermato shell massimizzata, Restore
  disabilitato e login sullo staging online. Nessuna credenziale è stata inserita.
- Host verificato: ASUS Zenbook Windows 11, un monitor 1440×900, sole stampanti
  virtuali, nessuno scanner/Xprinter/cash drawer. Staging autenticato, Win7 reale,
  dual monitor, hardware e matrice DPI/lingue restano `NOT_RUN`.
- Classificazione invariata: `STOPPED_STAGING`; task `NOT_DONE`, nessuna PR/CI/merge.

## 2026-07-17 - Final software merge authorization

- Review branch: `release/win7pos-final-review-20260717-015659`; initial review
  HEAD `f24afcb5dde54d73fc95a53f8645f0f90a82893a`; validated software HEAD
  `4fe6f0a69e15fb38e77f01f6f6e61afb98e65d62`.
- Final review found one additional P1 in customer-display first-frame placement.
  The window is now placed before first visibility and closed on setup failure;
  checker, WPF x86 build and lifecycle harness passed. Cumulative P0 `0/0/0`,
  P1 `4/4/0`; nine P2 remain deferred.
- Final local software validation: canonical gates `30/30`; automated tests
  `249/249`, skipped `0`; solution and WPF net48/x86 `0 warnings / 0 errors`;
  CLI selftest `PASS`; UiSmokeHarness build and lifecycle `PASS`.
- Catalog regression: batch/legacy median ratio `56.12x`, pending prices `0`;
  paged-full exactness `Verified` in `3/3` iterations with pending prices `0`.
- Clean-tree local x86 Release Pack, completeness validator, Win7 runtime
  validator and Inno Setup installer: `PASS`; pack commit `4fe6f0a`, PE32/x86,
  net48, no harness/DB/log/PDB/source/secret or active QA configuration.
- External validation moved to
  `docs/QA/WIN7POS_EXTERNAL_VALIDATION_BACKLOG.md`: 16 items remain `OPEN` and
  `DEFERRED_EXTERNAL_VALIDATION`; no previous `NOT_RUN` is promoted to PASS.
- Task software status: `READY_FOR_MERGE`.
- Merge authorization: `APPROVED_BY_PROJECT_OWNER`.
- Production certification remains open: `NOT_YET_CERTIFIED`.

## 2026-07-17 - Software merge and post-merge verification (DONE_SOFTWARE_MERGED)

- PR `#4` head and merge commit:
  `f4f17f06d04c2d9ec16309317952cfd08420961e`; PR CI run `29590576245`
  completed successfully on that exact SHA.
- Merge method: direct fast-forward from `5160b7c` to `f4f17f0`; no force push.
  GitHub records PR `#4` as `MERGED`.
- Task software status: `DONE_SOFTWARE_MERGED`.
- Merge status: `MERGED_AND_VERIFIED`.
- External validation status: `DEFERRED_EXTERNAL_VALIDATION`.
- Production certification: `OPEN`.
- Final main SHA: `f4f17f06d04c2d9ec16309317952cfd08420961e` (software merge
  head before this documentation-only completion commit).
- Post-merge CI: `PASS`, run `29590958239`, push event on exact main SHA; all
  workflow steps passed, including upload of the TRX with 249/249 tests and zero
  skipped.
- Post-merge Release Pack: `PASS`, run `29590958386`, push event on exact main
  SHA; downloaded GitHub artifacts passed 33/33 source-and-release gates,
  VERSION/ref/tree-state checks and 41/41 manifest hashes.
- GitHub installer SHA-256:
  `7DB77AD2B3EEBDFC60C0009EC7A5F75AF7524C64AD845C0D6AAE8EFAC96FCBA7`;
  release ZIP SHA-256:
  `BE6627F89D771E965B6597DA243833A15A43EDF77F98A26B5C63AC0F2CC1EBE2`.
- The 16 entries in `docs/QA/WIN7POS_EXTERNAL_VALIDATION_BACKLOG.md` remain
  `OPEN` / `DEFERRED_EXTERNAL_VALIDATION`; staging, Win7 physical,
  dual-monitor, scanner, Xprinter, cash drawer and DPI/language runtime are not
  declared PASS.

## 2026-07-17 - Post-merge PR-A persistence foundation

- Source main verified at `f3e779bd537d62ed0f3ddb5333149e9213e2c13f`; PR `#4`
  already merged; final main CI `29591597390` and Release Pack `29591597131`
  both `completed/success` on the exact SHA.
- External certification remains `0/16`: items 1-8 `BLOCKED_CREDENTIALS`, 9-10
  `BLOCKED_WIN7`, 11-15 `BLOCKED_HARDWARE`, item 16 `NOT_RUN`; production
  certification stays `OPEN` and the authoritative backlog is unchanged.
- Selected only PR-A on branch
  `codex/pr-a-persistence-foundation-20260717-114614`; implementation commit
  `188d9cd`; GitHub PR `#5` is open, non-draft and must not be auto-merged.
- Added verified SQLite online backup, process-wide connection fencing,
  same-directory `File.Replace`, durable prepared/committed marker recovery,
  integrity/FK validation and startup recovery. No schema, WAL policy, dependency,
  payload/hash, catalog or economics change.
- Review fixed four P1 edge cases: rollback journal cleanup, committed-live
  validation before rollback disposal, cleanup failure after commit and fence
  recovery after an owner handle leak. P0/P1 open: `0/0`.
- Validation: 30/30 canonical gates, 257/257 tests with zero skipped, CLI PASS,
  solution and WPF net48/x86 0 warnings/errors. Same-host A/B medians changed
  +3.66% legacy, +4.91% batch and -2.29% full; ratio `38.92x`, exactness
  `Verified` 3/3 and pending prices 0.
- Structural status: PR-A `READY_FOR_REVIEW`; next incomplete item is PR-B
  versioned migrations. No merge was performed.

## 2026-07-17 - PR-A final review and merge

- Reverified base `f3e779b`, original PR head `09b0be3`, open/non-draft/mergeable
  state and successful historical CI `29596021792` before final review.
- Final read-only review found a P0 pre-fence restore/outbox TOCTOU and a P1 drain
  deadlock when an active flow needed a second SQLite connection. No schema, WAL,
  package, payload/hash/idempotency or refund/void economics change was introduced.
- Fixed both on commit `607e1f1`: authoritative shop/epoch/sales/catalog/candidate
  revalidation now runs after connection drain and before backup/swap; connection
  admission closes permanently at the first zero boundary, with a safe 30-second
  pre-action timeout and regression coverage.
- Independent re-review found P0/P1 open `0/0`. Local evidence: 30/30 canonical
  gates, 19/19 focused tests, 260/260 full tests with zero skipped, CLI PASS,
  solution and WPF net48/x86 builds with zero warnings/errors, Release Pack and
  installer PASS.
- Final PR CI `29600241291` passed on exact head `607e1f1`. GitHub rejected author
  self-approval by policy, so an approve-equivalent formal review comment recorded
  the evidence before integration.
- PR `#5` was integrated by fast-forward `f3e779b..607e1f1`; squash, rebase and
  force push were not used. Post-merge main CI `29600645459` and Release Pack
  `29600645440` both passed on exact `607e1f1` with 260/260 tests.
- Downloaded release provenance reports `CommitSHA=607e1f1...`, `Ref=main`,
  `TreeState=clean`; installer and ZIP hashes are recorded in the closeout report.
- Ledger status: PR-A `DONE_MERGED`, PR-B `NEXT`, external certification remains
  `OPEN 0/16`. PR-B was not started.

## 2026-07-17 - PR-B versioned SQLite migrations

- Started from main `ad431fe8b7cf4de1bf3bee744bab159b6a95e80c` on
  `codex/pr-b-versioned-migrations-20260717-143330`; backup branch and verified
  all-ref bundle were created before implementation.
- Added an immutable six-entry SHA-256 migration registry and
  `schema_migrations` ledger, exact legacy metadata detection, verified online
  backup before ledger bootstrap/upgrade, immediate per-migration transactions,
  fail-closed checksum/gap/future-ID handling and no-op reopen reconciliation.
- Added six sanitized SQL generations plus rollback, concurrent startup,
  tamper, backup failure, restore/re-upgrade and semantic fresh-schema parity
  coverage. No WAL, package, payload/hash/idempotency, sync-policy or reversal
  economics change was introduced.
- Local evidence: canonical gates `31/31`; migration/restore/architecture gates
  PASS; tests `290/290`, skipped `0`; WPF `net48/x86` 0 warnings/errors; CLI
  selftest PASS. Solution compilation has 0 errors and four external `NU1900`
  warnings because the NuGet vulnerability endpoint was unavailable.
- Catalog regression: 2,000-row batch median `262.188 ms` (`77.21x` versus
  legacy); 19,762-row paged-full exactness `Verified` in 3/3 iterations.
- Clean committed-head x86 Release Pack, completeness validator, Win7 runtime
  validator and Inno Setup 6.7.3 installer: `PASS`.
- Hardware/settings and sync/efficiency roadmaps record no open P0, six deferred
  repository-level P1 findings and current-server-compatible follow-up PRs.
  PR-B is to remain `READY_FOR_REVIEW`; no automatic merge is authorized.

## 2026-07-17 - Epson receipt alignment, POS footer and drawer pulse

- Enlarged the POS `Pay` touch target to the exact visible width/edges of the
  right tools panel while retaining the compact single-row footer.
- Added one shared receipt renderer used by payment preview, completed-sale
  printing/reprint and the non-persisted Printer Settings sample. Exact alignment
  is covered for cash, card and mixed payments at 32 and 42 columns, including
  lossless line/cart discount rows and EN/ES/IT/ZH labels. The shop snapshot is
  frozen across payment/commit and the fictitious test uses the sale barcode path.
- Matched the Printer Settings preview hierarchy to the payment preview and made
  the fictitious sample show both cash and card without creating a sale.
- Added focused geometry, renderer-parity and visual-capture harness coverage;
  Release x86 build and targeted checks pass.
- Fixed the payment localization event subscription so every completed/cancelled
  payment flow and every harness fixture releases its handler.
- Submitted one production-code pin-2 drawer command to `EPSON TM-T60 Receipt`
  in a fresh isolated evidence root, outside the authenticated settings UI. The
  call returned successfully; pre/post queue observations were `Normal`/0 jobs,
  one log entry was retained and no `pos.db` was created. The operator later
  explicitly confirmed exactly one physical opening; no retry or pin-5 pulse was
  issued.
- The external backlog is now `OPEN 3/18`: row 15 is the single manual pin-2
  drawer PASS and rows 17/18 are physical print PASS. Transactional cash/card,
  reprint/failure and Windows 7 rows remain open. The earlier `OPEN 0/16` entry
  is the pre-Epson historical snapshot.
- Implementation and automated coverage were committed as `7d1ef84` and pushed
  to `codex/hardware-epson-tm-t60-20260717-161122`. Draft PR `#7` targets
  `main`; no merge or auto-merge was requested.

## 2026-07-18 - Epson transactional matrix physical checkpoint

- Ran the Release x86 application against isolated QA root
  `C:\POSData\Win7POS-QA\Win7POS-Epson-Transactional-20260718-104809`.
- Cash sale `VMRQI73CRZQ6` printed and cut; the operator confirmed exactly one
  drawer opening. Card-only sale `VMRQIA8J5KE3` printed with the drawer closed.
- Reprinted the persisted cash sale once; counts remained unchanged and the
  operator confirmed no drawer opening.
- Paused `EPSON TM-T60 Receipt`, committed card-only sale `VMRQIK583IXD`, observed
  the explicit saved-sale/print-failed state, resumed the queue and used
  `Print last` once. The operator confirmed correct paper and no drawer opening.
- Final evidence: 10 sales, 12 lines, 11 stock movements, 10 outbox rows; no
  duplicate client sale IDs or movement keys; exactly one drawer log event;
  printer `Normal`, zero jobs, Spooler running/automatic.
- Final local regression before the receipt-surface addendum: required gates
  31/31, Core tests 260/260 with zero skipped, CLI selftest PASS, solution and
  WPF/harness x86 builds PASS, lifecycle 20 Printer Settings / 50 display /
  50 manager cycles PASS with zero residual windows/ViewModels.
- Merge remains blocked by the receipt-surface/daily-close addendum and physical
  Windows 7 validation; no extra cash transaction or drawer pulse is authorized.

## 2026-07-18 - Local recovery isolation and safe POS re-entry

- Diagnosed online sign-in denial as `shop_switch_blocked_unresolved_outbox`:
  the isolated Epson QA database belongs to the existing QA shop and still has
  queued/blocked sales, so credentials for another shop cannot rebind it.
- Kept the physical database and outbox unchanged. Read-only inspection confirmed
  that the local QA recovery users are active, local-only and not locked.
- Added an explicit local-recovery login path that preserves PIN verification and
  lockout while no longer requiring an online authorization lease for verified
  local-only users. Normal POS login continues to require the lease.
- Restricted recovery mode to the user's granted subset of catalog import/edit and
  database-maintenance permissions. Sales, payment, daily close, settings and
  security administration remain unavailable until a normal online session for
  the database's original shop is restored.
- Hardened recovery transitions: payment is cancelled, online schedulers stop,
  unsafe tabs are clamped, the existing POS/cart view is suspended and restored,
  and exiting recovery rechecks a valid normal authorization lease.
- Added clearer localized feedback for a blocked cross-shop sign-in and focused
  source/data/policy tests for local-user classification and scoped permissions.
- Local recovery can never promote itself to normal POS access. Normal offline
  access, operator switching and override authorization now resolve only the
  remote mirror bound to the trusted shop/staff IDs, normalized codes and exact
  credential version. First-run setup uses the dedicated recovery verifier.
- Final local validation: required gates 32/32, Core tests 291/291, Release
  solution plus WPF/harness x86 builds PASS. The canonical seeded lifecycle is
  PASS with 0 residual ViewModels, 0 open windows, stable subscriptions and all
  functional checks true. The run observed 10 retired `SalesRegisterDialog`
  window shells with no live ViewModel or open-window retention; these shells
  remain diagnostic-only and their trend is monitored by the harness.

## 2026-07-18 - Isolated offline sales and payment QA sandbox

- Added a non-shipping launcher that creates a unique, empty data root below the
  local fixed-drive `POSData\\Win7POS-QA` tree, rejects UNC/device paths and any
  existing reparse-point ancestor, then rechecks containment after creation.
- The harness seeds one shop-bound synthetic remote operator, 48 synthetic
  products, a sale-safe catalog, a 12-hour trusted lease and zero sales/outbox
  rows. It explicitly verifies that no local-recovery identity was created.
- The child Win7POS process runs with safe-start and a loopback-only Admin Web
  endpoint. Post-sale/manual sync, automatic and manual fiscal PDF output,
  automatic receipt printing and the cash drawer are disabled by default.
- The documented flow uses the normal offline-mirror sign-in; Local recovery
  remains a restricted catalog/database-maintenance surface and never opens
  sales or payments.
- Actual seed-only validation PASS at
  `C:\\POSData\\Win7POS-QA\\Offline-Sales-20260718-215001`; lease 12 hours and
  hardware disabled. Canonical gates 32/32, Core 291/291, Release builds and
  standard-fixture lifecycle all PASS on the final source state.

## 2026-07-18 - Offline sales sandbox interactive runtime validation

- Logged in through the normal offline mirror against
  `C:\POSData\Win7POS-QA\Offline-Sales-20260718-220237`; safe-start and the
  loopback-only Admin Web endpoint remained active throughout the run.
- Completed one manual-price cash sale for CLP 3,432, one `QA000002` card sale
  for CLP 550 and one `QA000003` mixed sale for CLP 575 (cash 300/card 275).
- Final read-only audit: 3 sales, 3 lines, 2 expected stock decrements and 3
  pristine pending outbox rows with zero attempts/errors/server IDs. Gross CLP
  4,557, cash CLP 3,732, card CLP 825, change zero; SQLite quick/FK checks PASS.
- Sales Register receipt previews and Daily Close matched the persisted tenders
  and totals. Reopening those read-only surfaces left sales, stock, outbox and
  fiscal flags unchanged; logs contained no print, PDF, spooler, drawer or
  online-sync attempt.
- Live UI review exposed the Sales Register operator filter rendering the CLR
  type name. The shared modern ComboBox template now forwards the WPF item
  template selector used by `DisplayMemberPath`, with a focused UX source gate.
- Post-fix validation: required gates 32/32, Core tests 291/291 with zero
  skipped, full Release solution plus WPF/harness x86 builds PASS with zero
  warnings/errors. The artifact capture passed POS footer, payment/printer
  previews, Sales Register, Daily Close and compact 1024x600 layouts.

## 2026-07-18 - Fail-closed offline sandbox resume

- Added an explicit `-ResumeExistingSandbox` launcher mode so the same current
  synthetic QA run can be reopened without reseeding, replacing sales data or
  renewing its authorization lease.
- Resume remains confined below the local fixed-drive `POSData\\Win7POS-QA`
  tree and now requires the original marker/database/session files, rejects
  reparse points and repeats the single-instance check immediately before launch.
- A database-query-only harness verifier requires disabled receipt/drawer flags,
  blank saved printer names, the exact synthetic shop identity, an explicit raw
  fiscal lock and a still-valid DPAPI-protected trusted-session lease. Its unique
  exit code `73` prevents a stale or incompatible harness from being accepted.
- Regressed fresh seed-only creation successfully, then resumed
  `C:\POSData\Win7POS-QA\Offline-Sales-20260718-220237` with the Release x86
  build, safe-start, loopback-only Admin Web endpoint and hardware disabled.
  Credentials remained manual and were not stored or automated.
- Final validation: required gates 32/32, Core tests 291/291 with zero skipped,
  full Release solution and WPF/harness Release x86 builds PASS with zero
  warnings/errors.

## 2026-07-18 - Direct boleta printing without automatic PDF files

- Removed the post-sale fiscal PDF writer, its delayed 15-second cleanup and the
  `PDFsharp-gdi` runtime dependency. The old flow could leave a PDF in `exports`
  after a spooler failure, application exit or cleanup error.
- The selected boleta preview and number now go directly to the configured
  Windows receipt spooler. Receipt and boleta jobs keep the configured physical
  copy count; no image, text or PDF archive is created by either path.
- Preserved cash-only auto-print policy, Safe Start blocking, sale commit order,
  boleta-number advancement after successful printing, synchronization/outbox
  payloads and the legacy `sales.pdf_printed` compatibility flag. That column is
  document status only and stores no path or file.
- Updated the payment UI and EN/ES/IT/ZH copy to describe direct boleta printing,
  print status and same-number physical copies instead of a local PDF workflow.
- Added a focused x86 harness check for cash, card-only and simulated printer
  failure plus filesystem snapshots. Result: PASS, zero generated PDF files and
  no export directory in the isolated QA root.
- Final validation on source state: required gates 32/32, Core tests 291/291,
  WPF net48/x86 and UI harness x86 isolated builds PASS with zero warnings/errors;
  both build outputs contained zero `PdfSharp` DLLs and zero PDFs. A physical
  post-relaunch boleta print remains the hardware confirmation gate.

## 2026-07-19 - PR #7 cumulative closure and physical receipt addendum

- Completed a cumulative review of every published and unpublished PR #7 change.
  Two security findings were corrected: a normal remote mirror can no longer
  receive lease-free recovery treatment, and cancelling an operator change
  cannot commit the candidate identity. Independent final review reports
  P0/P1/P2 open as zero.
- Hardened the physical boundary with a final Safe Start guard, per-printer
  single-flight rejection after an indeterminate timeout, strict one-to-three
  copy validation and explicit post-commit fiscal-print failure guidance that
  preserves the reserved boleta number without triggering a second print.
- Hardened release provenance, exact runtime inventory, strict UTF-8 no-BOM
  manifests, secret/privacy rejection, Win7 dependency closure and obsolete
  runtime cleanup. Five negative fixtures confirmed fail-closed behavior for a
  changed payload, UTF-16 BOM, secret marker, `.env.*` payload and nested root.
- Final local software evidence: Core `298/298`, canonical source-and-release
  gates `35/35`, dialog standards `34/34`, WPF and UI harness Release net48/x86
  builds with zero warnings/errors, and the 20-cycle lifecycle run with zero
  residual ViewModels or windows.
- The dedicated physical harness submitted one sequence of six one-copy jobs to
  the Epson TM-T60: fiscal 32/42, identical receipt original/reprint and daily
  close 32/42. The manifest records six submissions, zero drawer calls and no
  database artifacts; the operator confirmed all six slips, correct output,
  no duplicates and a closed drawer. The Windows 11 host result closes the PR #7
  receipt-surface gate; physical Windows 7 remains `NOT_RUN_WIN7_PHYSICAL`.

## 2026-07-19 - PR-B refresh after PR #7

- Preserved the published PR-B head with a backup branch and verified all-ref
  bundle, then normally merged PR #7 main `db623a5` without rebase, squash or
  force push.
- Kept migration IDs/checksums 0001–0006 immutable and appended
  `0007-receipt-shop-snapshot` with pinned checksum `a1d12cca...6462`, verified
  backup and a separate bounded post-PR7 ledgerless baseline.
- Added a seventh sanitized fixture and fail-closed regressions for false custom
  predecessor bootstrap, incomplete ownership evidence, malformed current
  schema, validation-before-reconciliation and restore rollback.
- Preserved PR #7 receipt/recovery/hardware/release behavior. No sync,
  payload/hash/idempotency, reversal-economics or WAL/journal-mode change was
  introduced, and the physical printer was not exercised again.
- Pre-publication evidence: gates `33/33`, focused migration/fixture/restore
  `38/38`, full tests `336/336`, CLI selftest PASS, Release solution and WPF
  net48/x86 builds with zero warnings/errors. Catalog benchmarks: 2,000-row
  batch median `609.510 ms` (`30.25x` vs legacy) and 19,762-row paged full
  `Verified` in 3/3 runs.
- Clean refresh merge commit `5377778` produced an exact-commit, clean-tree
  Release/x86 pack; completeness, Win7 runtime validation and the Inno Setup
  6.7.3 installer all passed before publication.

## 2026-07-19 - PR #6 merge and SYNC-1 correctness

- PR #6 exact head `a9fa6fa` passed fresh CI and was merged normally as
  `ea85d91`, without rebase, squash or force push. The seven immutable migration
  checksums, verified legacy fixtures, PR #7 receipt/recovery behavior and clean
  exact-head Release Pack remained intact.
- SYNC-1 was then developed from the resulting main and merged through PR #8 as
  `6d9a9e0`. It added the fail-closed ambiguous-pagination guard, optional
  heartbeat revision/change/poll hints, separate observed/committed revisions,
  typed sales and catalog-import drain results, durable bounded full-chain
  staging and expanded Sync Center diagnostics.
- SYNC-1 retained incremental-first behavior and the 24–36 second compatibility
  fallback, preserved prior sale-safe state on failed deltas and prevented a
  fresh database from becoming sale-safe after incomplete or contradictory full
  pagination. Its exact head passed CI, 33/33 gates, 389/389 tests, CLI, WPF
  net48/x86, Release Pack/runtime validation and installer generation.

## 2026-07-19 - SYNC-2 generation-scoped lane supervisor

- Replaced the catalog-oriented sequential scheduler with one trusted-session
  supervisor owning four independent lanes: heartbeat, sales outbox,
  catalog-import outbox and catalog delta. Same-lane work is single-flight, the
  shared request cap is two, heartbeat has queue priority and transient failures
  retain lane-local due/backoff state.
- Added immutable generation/fingerprint fencing and append-only migration
  `0008-online-sync-generation`. Generation-aware settings, catalog and outbox
  writes reject stale completions; the one-time legacy upgrade releases old
  `in_progress` claims without consuming another attempt.
- Hardened authenticated relink with full-predecessor SQLite CAS,
  authorization-epoch serialization and the DPAPI file as the final crash-safe
  commit marker. Legacy file-only trust is normalized before first-login I/O so
  an authoritative denial always has an exact durable tombstone/clear scope.
- Integrated the shared supervisor with start of day, foreground/network
  recovery and immediate local sale, refund and supplier-import signals.
  Authentication denial from every lane cancels all work once and is persisted
  even when it races ordinary shutdown.
- Restore now enters authorization maintenance, drains the supervisor before
  capture/swap, installs an inactive generation tombstone and never resumes
  after success. A failed resume keeps traffic blocked behind a distinct retry
  marker; retry, nesting and subsequent successful-restore suppression have
  executable fault-injection coverage.
- Offline authorization high-water state is scoped to both generation and
  authorization epoch and is revalidated after PIN verification before the
  operator session can be committed.
- Final dirty-tree source evidence before publication: required gates `33/33`,
  focused generation/supervisor/request-gate/maintenance-bus tests `27/27`, full
  Core/Data suite `417/417` with zero skipped, and WPF net48/x86 build with zero
  warnings/errors. Independent cumulative review reports no open P0/P1; a clean
  exact-commit Release Pack and PR CI remain mandatory publication gates.

## 2026-07-19 - PERF-1 bounded transport and catalog run context

- SYNC-2 was published as PR #9 from exact head `02e6462`, passed branch CI and
  Release Pack, and merged normally as `1be7017`. Main CI and Release Pack then
  passed on the merge commit before the separate PERF-1 branch was created.
- Replaced per-call HTTP transports with endpoint/configuration-scoped reuse,
  `ResponseHeadersRead`, direct data-contract request/response streaming, an
  8 MiB success limit, a 64 KiB error limit and a counting stream that also
  stops chunked/no-length bodies. Cancellation now interrupts a blocked body
  read; error fields are bounded/control-free and no response body is logged.
- Added one catalog apply context per sync run. It reuses one SQLite connection,
  eight prepared commands and small reference maps while retaining a separate
  immediate transaction and generation/cursor fence per page. Existing product
  identities, reference cleanup and pending stock are scoped to the current-page
  staging table; rollback cannot publish page-local maps.
- Added `HttpBoundedStreamingTests` and `CatalogRunContextPerformanceTests`, and
  expanded the scheduled/manual performance workflow with 10/100/1,000-row
  deltas plus a real net48/x86 19,763-row full-path harness.
- Final measured full-path medians: net10/x64 4,733.787 ms and net48/x86
  6,427.448 ms, both exactly 19,763 products/prices, zero pending, 20 pages and
  `Verified` in 3/3 runs. The x86 maxima were 78,008,320-byte working set,
  62,595,072-byte private memory and 24.528 ms dispatcher delay. Catalog scope
  queries fell from 120 to 42; prepared-statement creation from 120 to 8.
- Incremental deltas against 19,763 products completed in 35.645 ms (10),
  57.850 ms (100) and 215.794 ms (1,000), each in one logical request with no
  full reconciliation. A 100,000-row x64 scale probe completed in 29,456.767 ms
  with exact product/price counts and zero pending rows.
- Pre-publication validation: gates 33/33, solution and WPF net48/x86 builds
  zero warnings/errors, full suite 428/428 with zero skipped and CLI selftest
  PASS. At this original checkpoint the final exact-head and publication gates
  were still required; PR #10 subsequently satisfied them as recorded below.
  PERF-1 submitted no printer jobs.

## 2026-07-21 - Security follow-up and cumulative-diff closure

- Remediated all eight findings from security scan
  `0207326e-0be9-4257-9319-6dc8102ffa70`: catalog validation/exactness,
  two-phase offline authorization, trusted-generation fencing, canonical
  migration ledger validation, receipt amplification, supplier-import
  authorization, database-maintenance permissions and action-time reprint
  authorization.
- The final cumulative review additionally closed semantic timestamp/cursor
  validation, historical receipt reader contention, transaction ownership for
  direct sale-line inserts, durable-authority drift after a cancelled operator
  switch and unexpected grants on UI-read-only system roles.
- Definitive local evidence: required gates `33/33`, Core/Data `465/465`, focused
  migration/restore `40/40`, receipt `32/32`, Release solution plus WPF/harness
  `net48/x86` builds with zero warnings/errors, CLI selftest PASS, authorization
  smoke PASS, supplier `.xlsx`/`.xls` smokes PASS and lifecycle PASS with zero
  residual windows/ViewModels/handlers.
- Release Pack completeness, Win7 runtime validation and Inno Setup compilation
  passed. No physical job was sent in this follow-up; the prior Epson 6/6 result
  remains confirmed and Windows 7 physical remains `NOT_RUN_WIN7_PHYSICAL`.
- Full report:
  `docs/reports/2026-07-21_SECURITY_FOLLOWUP_REMEDIATION.md`.

## 2026-07-21 - PERF-1 integration after security main

- Security PR #11 merged normally as `908bb22`; its exact-main CI and Release
  Pack passed before PERF-1 was refreshed. PERF-1 then merged `origin/main`
  without rebase or force push. The only conflict was this worklog chronology;
  all source overlaps retained both validation and run-context behavior.
- Added an explicit reusable-context regression proving invalid remote catalog
  content cannot publish page/query/staging diagnostics or durable rows. Text-
  policy coverage now includes valid pairs plus malformed high/low surrogate and
  C1 cases while retaining universal sink validation.
- Combined evidence before publication: required gates `33/33`, focused
  catalog/security `75/75`, full Core/Data `477/477`, Release solution and WPF/
  harness net48/x86 builds with zero warnings/errors, and CLI selftest PASS.
- Post-security 19,763-row medians were 5,103.924 ms net10/x64 and 7,328.418 ms
  net48/x86, exact and `Verified` 3/3. X86 peak working/private memory was
  78,475,264 / 63,160,320 bytes and maximum dispatcher delay was 21.344 ms.
  The 100,000-row median was 34,306.050 ms (+16.46%); the explicit cause is the
  new O(n) direct-sink safety validation, while fresh-process memory remained
  +5.92%/+8.26%, below the 20% ceiling.
- No printer call was made. At this integration checkpoint a clean exact-commit
  Release Pack, fresh PR CI and manual performance workflow were still required;
  PR #10 subsequently satisfied them as recorded below.

## 2026-07-21 - PERF-1 publication and merge closeout

- GitHub PR #10 merged normally, without force push, as
  `0ad16bd2c40454c07d0ffb6e4908e23b89b4a5a1` from exact head
  `92e1c323d55ea7b00f3f8bfa35f32fe2fb28e391`.
- Exact-head CI run `29874926694` and Catalog Performance run `29874958148`
  completed successfully on `92e1c323`. Post-merge CI run `29875846523` and
  Release Pack run `29875846496` completed successfully on `0ad16bd2`.
- PERF-1 is `DONE_MERGED`. Admin backend/authenticated staging, physical Windows
  7, PERF-2, `P1-REL-01` and production/hardware certification remain open; no
  external backlog row was promoted.
- Current main already contains immutable migration
  `0008-online-sync-generation`. PERF-2 authoritative-ID staging must append the
  next immutable migration, expected `0009-catalog-authoritative-id-stage`, and
  must not modify `0007` or `0008`.

## 2026-07-22 - RELEASE1 and PERF2 residual completion

- CLOSEOUT-DOCS PR #12 was promoted from draft only after its exact head,
  four-file scope, CI `29879271908`, mergeability and zero unresolved review
  threads were reverified. It merged normally as `939ca843`; post-merge CI and
  Release Pack runs `29882440608` / `29882440561` passed.
- RELEASE1-A PR #13 merged normally as `1832dcca`. Its four workflows contained
  12/12 full-SHA Action pins and all three checkout uses disabled credential
  persistence. SDK `10.0.301`, seven NuGet locks, semantic version `1.0.0` and
  official Inno Setup `6.7.3` hash verification are fail-closed.
- RELEASE1-B PR #20 merged normally as `5313ff36` and extended the invariant to
  the final six workflows, 32/32 Action pins and 8/8 credential-safe checkouts.
  CycloneDX 1.6 (99 components), vulnerability/deprecation/license gates,
  Gitleaks history scan, CodeQL, two-clean-build comparison, checksums,
  provenance and unsigned attestation passed exact-head and post-merge
  workflows. Protected signing is wired and negatively tested, but real
  certificate/RFC3161 evidence is absent; `P1-REL-01` remains
  `PARTIAL_EXTERNAL_SIGNING`.
- PERF2-A PR #21 merged normally as `81acd479` and appended only
  `0009-catalog-authoritative-id-stage`, checksum
  `68d57cd65b2d56456d5b2ab5eee83237477aefc85f93aa2d81e5f64699fae659`.
  Durable shop/generation/full-run authoritative staging, transactional page
  fences, bounded cleanup and migration/rollback/restore coverage passed.
- PERF2-B PR #22 merged normally as `63152222`. Ordinary product navigation is
  keyset-based with bounded anchors and explicit arbitrary-jump OFFSET fallback.
  At 100,000 rows repository p95 was 40.587 ms keyset versus 86.186 ms OFFSET;
  raw SQL p95 was 0.302 versus 17.638 ms.
- PERF2-C PR #23 merged normally as `0c5052f3`. One process-wide bounded Core
  writer owns append/rotation; redaction precedes enqueue and INFO drops before
  reserved WARN/ERROR capacity. The x86 smoke recorded 708.60 us producer p95,
  6.243 ms maximum, high-water 256/256, 102,817 deliberate INFO drops and peak
  working/private memory 37,584,896 / 28,389,376 bytes.
- PERF2-C exact-head runs CI/Security/Release Pack/Catalog
  `29906780759` / `29906780732` / `29906806859` / `29906804341` and post-merge
  runs `29908121321` / `29908121286` / `29908121289` / `29908134313` all passed
  on their exact SHAs. Final local evidence was gates 37/37, Core/Data 534/534,
  CLI PASS, solution and WPF net48/x86 zero warnings/errors.
- `P1-PERF-02` is `DONE_MERGED`. Structural reassessment leaves Startup/PR-C
  `PARTIAL`, ProductRepository/PR-E `OPEN` and SaleRepository/PR-F `OPEN`; none
  is mislabeled `SUPERSEDED`. The nine historical P2 items are five
  `DONE_MERGED`, three `OPEN`, one `PARTIAL`.
- The Admin backend repository is accessible at
  `XNIW/merchandise-control-admin-web` main `9406da33`, but that SHA/catalog-v2
  migration is not deployed to the configured staging environment and no safe
  authenticated 19,763-row fixture/session run exists. Current host is Windows
  11, not physical Win7. External backlog remains exactly 10/25 PASS.
- No force push, squash/rebase merge, production data or physical effect was
  used. Full closeout evidence is in
  `docs/reports/2026-07-22_WIN7POS_ALL_RESIDUALS_CLOSEOUT.md`.

## 2026-07-23 - Repository-local residual closeout and external blocker record

- Normal merge closure completed for SQLite durability PR #25 (`3887262`),
  ARCH-005 PR #26 (`56b3803`), PR-C #27 (`d6751576`), ProductRepository/
  ARCH-003 PRs #28-#31 (`6556a85`, `f2175ab`, `bc52904`, `38cf284`) and
  SaleRepository/PR-F PRs #32-#38 (`0918437`, `0c95204`, `e2d8e24`,
  `93100e2`, `8bcbaea`, `f20030c`, `cc9f02c`). The linked PRs retain their
  individual exact-head and post-merge records. Post-merge CI on PR #33 merge
  `0c952047` failed in run `29985031154`; PR #34 remediated that failure and
  completed its post-merge workflows successfully.
- PERF-05 merged normally through PR #39 from exact head
  `777931aba66727779480fd6774d8c5c2548d5a3a` as
  `93a5e4afa819f0de14513ddb7603091433d917ba`. It adds page-local remote-price
  SQL command/statement diagnostics that are merged only after `tx.Commit()`.
  The fresh 3-price/2-page fixture proves 19 commands / 22 statements; rollback
  coverage proves failed pages publish no counters.
- PERF-05 local evidence: Core/Data `610/610`, required gates `44/44`, WPF and
  smoke-harness Release `net48/x86` builds zero warnings/errors, CLI self-test,
  Gitleaks worktree/history (424 commits), `batch-price-only 3 1 2` at 19/22 and
  19,763-row paged-full exactness `Verified` in 3/3 iterations. Local harness
  execution was blocked by host Application Control and was not bypassed; the
  exact-head and post-merge CI runners completed both WPF smoke checks.
- PR #39 exact-head CI/Security/Release Pack runs `30005059126` /
  `30005059146` / `30005117027` and post-merge runs `30005966795` /
  `30005966771` / `30005966770` all completed successfully on their exact SHAs.
- Structural and historical-P2 source-of-truth state is now nine
  `DONE_MERGED`, zero `OPEN`, zero `PARTIAL`, zero `SUPERSEDED`. This does not
  promote external work: `P1-REL-01=PARTIAL_EXTERNAL_SIGNING` and physical
  Windows 7 is `DEFERRED_BY_USER`.
- External staging remains blocked. Source migration inventory is 72, applied
  staging history is 79 and 71 IDs are common; no authoritative
  source-provenance/recovery reconciliation manifest and verified backup for the
  eight remote-only Supabase staging migrations was supplied. The remote-only
  applied IDs are `20260707183000`, `20260707200500`,
  `20260708003000`, `20260713010000`, `20260713020000`, `20260718120000`,
  `20260718235345`, `20260719090000`. Source-only `20260719170600` was observed.
  No migration was repaired, rebased, baselined, reverted or deployed.
- The required owner action is one concrete input: provide the authoritative
  source-provenance/recovery reconciliation manifest and verified backup for the
  eight remote-only Supabase staging migrations. No token, credential or
  production data was recorded.

## 2026-07-25 - TASK-139 final review closeout (DONE)

- Repository: `XNIW/Win7POS`, branch `main`.
- SHA codice verificata:
  `1b947f3776e8af71450418cfae66e407da92682b`; coincideva con `main`,
  `origin/main` e GitHub `main` al preflight ed è antenata del successivo
  commit documentale.
- Final review approvata esplicitamente dall'utente: stato TASK-139 `DONE`,
  P0/P1/P2 aperti `0/0/0`.
- Workflow esistenti riusati sull'esatta SHA:
  - CI `30179709588`: `SUCCESS`, inclusi required gates `44/44`, Release
    build, Core/Data, CLI selftest, WPF `net48/x86`, UI harness, authorization
    lease runtime, logging `100k` e paging `100k`;
  - targeted authorization smoke `30179377746`: `SUCCESS`, inclusi dynamic
    admission, restart, clock/capacity, `hardwareEffects=0` e assenza di
    sale/outbox non autorizzate;
  - Security Supply Chain `30179709582`: `SUCCESS`;
  - Release Pack `30179709584`: `SUCCESS`;
  - candidate CI `30179448464`: `SUCCESS`.
- Candidate/worktree TASK-139 revisionati: le lane consegnate sono antenate di
  `main`; l'esperimento locale `33c8e268` di catalog remote-failure è fuori
  dallo scope finale accettato offline-authorization lease/bridge, è stato
  superseduto e non è stato integrato. Nessuna modifica utile accettata resta
  fuori `main`.
- Windows 7 fisico, signing, staging autenticato, printer/scanner/camera e
  periferiche restano evidence esterna opzionale/non bloccante.
- Nessun nuovo scan Codex Security, rerun manuale CodeQL/Supply Chain, full
  suite, deploy o mutazione production è stato avviato nel closeout.
- Report: `docs/reports/2026-07-25_TASK-139_FINAL_REVIEW_CLOSEOUT.md`.

## 2026-07-26 - Runtime observability catalog failure (in delivery)

- Implementata diagnostica strutturata e sanitizzata per login/catalog pull,
  con stato fail-closed, correlation ID bounded e pannello tecnico WPF copiabile.
- Verifica locale completata: focused 16/16, Core/Data 642/642, gate 44/44,
  WPF e UiSmokeHarness `net48/x86` senza warning/errori; smoke loopback HTTP
  500/db_failure a 1024x768 PASS senza credenziali, staging o backend.
- Feature commit: `1c64baac673afbcfa4be21ffbd14df03742ce3c2`; PR #42 aperta verso
  `main`. Report: `docs/reports/2026-07-26_WIN7POS_RUNTIME_OBSERVABILITY.md`.
  CI e merge saranno verificati sulla testa finale della PR.

## 2026-07-27 - ASUS catalog_product_row_invalid offline automation (in delivery)

- Evidence congelata in `C:\Dev\_codex-evidence\win7pos-catalog-row-invalid-20260727`:
  due pagine full-response persistite, entrambe senza lane prodotti. La failure
  `catalog_product_row_invalid` avviene prima dello staging della risposta
  successiva; il payload/riga non e quindi ricostruibile offline. Ownership
  resta `UNKNOWN`, senza ipotesi di bug client o server non dimostrata.
- Aggiunti analyzer CLI offline/redatto, diagnostica bounded pre-stage per le
  prossime failure, soppressione retry deterministic per revision e mapper
  catalogo condiviso fra pull production/analyzer. Il POS rimane fail-closed.
- La patch dialog ASUS usa `SizeToContent=WidthAndHeight` e `MinHeight=650`
  (invece di 720): UiSmokeHarness loopback passa a 1024x768 con errore chiuso/
  aperto, footer raggiungibile e artefatti EN/ES/IT/ZH; nessuna chiamata staging.
- Aggiunto vault QA DPAPI CurrentUser con ACL current-user/SYSTEM, self-test
  sintetico, harness staging test-only e report/screenshot redatti. Il profilo
  reale non e stato creato o letto e l'acceptance staging non e stata eseguita.

## 2026-07-27 - ASUS catalog product server ownership confirmed

- Un singolo login manuale sulla build diagnostica ha identificato la prima
  riga non conforme: pagina 3, lane products, riga 56,
  `invalid_product_name_text`. Il nome ha lunghezza 38 (limite 512); la causa e
  quindi un carattere di controllo o Unicode malformato nel payload staging.
- DTO deserializzato, mapping non eseguito e pagina non staged: ownership
  server/data-contract confermata. Il POS resta fail-closed e non normalizza,
  salta o inventa il prodotto.
- Creato l'handoff redatto
  `docs/HANDOFFS/2026-07-27_WIN7POS_CATALOG_PRODUCT_ROW_INVALID.md` e il prompt
  Mac esterno nell'evidence root. Gate statici `44/44` e build WPF `net48/x86`
  PASS; test `net10.0` demandati alla CI per SDK 10 non installato localmente.

## 2026-07-27 - WIN7POS-CATALOG-TOLERANCE-001 (REVIEW)

- Creato worktree isolato `codex/asus-catalog-warning-tolerance-20260727` da
  `e1dcbe7`; il precedente worktree interrotto era pulito e la sua patch binaria
  esportata era vuota.
- Aggiunta recovery Core su copia del payload per soli display text catalogo,
  con warning aggregati redatti e mapper/staging che consumano esclusivamente il
  valore recuperato. Barcode, ID, item number, prezzi, manifest e exactness non
  sono normalizzati permissivamente.
- Vendorizzato byte per byte il fixture comune Admin/Android/iOS (Git blob
  `63e527a`, SHA-256 `1cec15e9…36ae9a6`) e aggiunto il fixture consumer
  Win7POS.
- PR #45 e' stata unita normalmente a `de29501` dopo CI, CodeQL e supply-chain
  verdi; la review indipendente finale ha P0/P1/P2/P3 aperti = 0. Build del
  main pulito e smoke warning sintetico passano. Acceptance reale resta
  in `REVIEW`: profilo DPAPI inizializzato e una sola acceptance staging eseguita,
  ma bootstrap `failure` prima del catalog pull. Il controllo redatto conferma
  che i valori profilo non sono nel log; non e' stato eseguito un secondo run.

## 2026-07-28 - POS article mutation sync v1 (in delivery)

- Congelati byte per byte i tre fixture Admin
  `pos-article-mutation-v1`/first-login e implementati contratto Core,
  canonical JSON/hash, limiti 25/256 KiB, validazione ACK/replay e base revision
  UTC a sei frazioni.
- Aggiunta la migration SQLite additiva
  `0010-article-mutation-outbox` con outbox/attempt ledger, dipendenze create
  pre-ACK, shadow remoto, stock manuale, atomicita' Save/outbox, recovery e
  restore/shop guard.
- Integrati tutti i Save manuali WPF con origine esplicita, lane supervisor
  background/fair, auth stop, pull protection/no-echo, Sync Center e toast
  conflitto EN/ES/IT/ZH. Supplier import e stock sales restano isolati.
- Acceptance locale corrente: Core/Data `726/726`, required gates `44/44`,
  dialog standard PASS, WPF/UiSmokeHarness/solution Release net48/x86 senza
  warning/errori, loopback completo e UI 1024x768 PASS, drain autorevole
  676 pagine/19.763 prodotti/41.228 prezzi `Verified`, supplier XLSX e DPAPI
  self-test PASS, Gitleaks working tree e storia completa senza finding.
- Preparato l'harness di staging esatto-main con profilo DPAPI
  `asus-staging`, data dir isolata
  `C:\POSData\Win7POSArticleMutationAcceptance`, run ID `ASUSART_*`, scenario
  mutazioni/replay/mismatch/conflitto completo, fail-fast senza retry cieco,
  evidenze redatte, cleanup manifest e prompt Mac generato. Nessuna chiamata
  staging e nessuna azione hardware sono state ancora eseguite in questa fase.

## 2026-08-09 - Physical Win7/hardware acceptance and final release

- Protetta integralmente la modifica locale preesistente nel checkout primario:
  file SHA-256 `82869ACF...987B6`, patch esterna SHA-256
  `47172902...38280`; nessun reset/checkout/stash/apply sul primario.
- Baseline `origin/main=26e1fd1`; worktree/branch dedicati creati. PR #88
  Storefront v1 è rimasta separata e non è stata integrata.
- Build-host acceptance: SDK 10.0.301, restore locked, required gates `47/47`,
  dialog `34/34`, solution e WPF net48/x86 senza warning/errori, CLI/backup/
  restore e smoke WPF sintetici PASS. La run UI canonica conserva 47 screenshot.
- Corretto il percorso evidence UI (`6709096`) e un N+1 catalogo dimostrato:
  ~19,763 righe da mediana 19.656 s a 2.067 s (-89,5%), con 123 command di
  contesto e 140/378 command/statement remote-price deterministici.
- Il focused security diff scan locale, sigillato senza attribuire successo al
  launcher MCP rifiutato, ha prodotto un finding P3/low sul lookup transitive e
  due difetti di correttezza. Tutti sono stati corretti in `b6a92b0` con test e
  gate; Gitleaks worktree 0 finding.
- L'host WDAC ha bloccato i nuovi assembly MSTest e l'EXE benchmark con
  `0x800711C7`; la policy non è stata disabilitata o aggirata. Build e gate
  locali passano; test/performance esatto-head devono essere certificati dalla
  CI pulita.
- Creato RC pulito `1.0.0-dev.b6a92b0d8f6d`, installer SHA-256
  `FC51AFE79ECE5045361D18BC8FD6D09099ABADE735EAA634BB7127B57A06A758`,
  `NotSigned`. Nessun certificato code-signing reale disponibile.
- Nessun dispositivo Win7 fisico, scanner, Xprinter, drawer, customer display,
  account/shop QA autenticato o production endpoint era disponibile. Nessun
  impulso drawer e nessuna mutazione staging/production. Backlog esterno
  invariato: 10/25 PASS storici, 15 open/partial, 0 nuove chiusure.
- Classificazione: `BLOCKED_EXTERNAL`. Report:
  `docs/QA/WIN7POS_PHYSICAL_WIN7_HARDWARE_ACCEPTANCE_2026-08-09.md`.

## 2026-10-03 - Functional, sync and operations closeout

- Corretti ordinamento revisioni/tombstone, stock frazionario e preservazione
  stock CSV nel gate/transaction; completati Hardware Center, profili/default/
  audit e display, riusando W7-F07 e OPERATIONS-1.
- PR #114/#115/#116 integrate dopo review distinta e CI/Security dell'HEAD
  esatto. Core/Data locale 1201/1201, zero skip; build net48/x86 senza warning/
  errori; regressioni WPF hosted PASS. Conservate prove causali FAIL.
  La successiva CI main37145182291 fallisce PERF Background-progress dopo
  dodici scenari PASS: prova distinta, da non cancellare col PASS della PR.
  Il runner ora stampa i file fase/stack sintetici già prodotti dal harness
  prima dello stesso throw: nessun cambio a timeout, asserzioni o comportamento.
- Catalogo sintetico e load integrity separati dal live. Diagnostic Asus
  ENVIRONMENT_VALID=false/STABILITY_PASS=false; Asus disponibile secondo
  l'utente, Win7/share TEST assenti. Il launcher Interactive/Limited risolve
  il desktop ma il probe visuale scan1 fallisce; conservato il callback WPF
  TextEditor 528.877ms osservato, senza causa sottostante inventata.
  Nessuna nuova deferizione o accettazione hardware/produzione.
- Package D, SHA finale, readiness e ReleasePack verificato sono tracciati da
  `reports/2026-10-03_FUNCTIONAL_SYNC_OPERATIONS_CLOSEOUT.md` e dal receipt
  machine-readable post-merge indicato nel report.

## 2026-10-08 - Post-PR121 functional and operational closeout

- U1: riprodotti sei salvataggi parziali nel vero WPF/SQLite; validazione di
  entrambi i prezzi prima delle scritture, draft/focus conservati e single-flight.
  Limiti manuali Int32, retail locale Int64 e import Admin distinti; 13 gruppi PASS.
- U2: riprodotti tre intenti ridotti a un solo delta, anche con risposta persa
  in loopback (nessun commit Admin live dimostrato). Recupero completo con originale immutabile, ricevuta/ritiro esplicito,
  correzioni CAS e contributori/catene durabili; backup verificato e autorizzazione
  transazionale. Omissioni originali e movimenti locali successivi preservati.
- Review indipendenti chiuse; Core/Data 1385/1385 senza skip, 49/49 gate e build
  net48/x86 senza warning/errori. U2 WPF 9/9 e 8 screenshot nuovi in quattro lingue;
  prima fixture UUID/ACK errata conservata e corretta senza allentare asserzioni.
- Sequenza funzionale locale 18/19: PERF esistente fallisce il drain Background
  nel restart dell'indicatore immagini; baseline PR121 isolata PASS, causa ancora
  non dimostrata. Avvio isolato candidato rifiutato dalla review automatica.
  CI dell'esatto SHA deve qualificare l'intera sequenza prima dell'integrazione.
- Misure recovery locali campionate, query/batch worker e backup raccolti;
  rilascio memoria assoluto NOT_QUALIFIED. Nessuna qualifica Win7 o periferiche.
- CI 37849987382: Core/Data 1385/1385 e 49 gate PASS, ma sequenza funzionale
  fermata dalla fixture U2 che equiparava overlay nativo e viewport 1024x768;
  un PASS, un FAIL, diciassette non eseguiti. Geometria hosted non osservata.
  Separati i controlli live owner/workarea dal viewport esatto e dal ripristino
  del layout; prova causale locale del fallback e 9 gruppi/10 PNG PASS, review
  distinta e build senza warning/errori. Nessuna modifica a dialog di produzione,
  budget o timeout; serve nuovo CI completo dell'esatto candidato.
- Admin PR132 integrata; due DDL U2/deploy restano separatamente autorizzabili.
  TASK150 read-only PASS non sostituisce scope QA/metriche/READY. Manifest TASK148
  originale recuperato; nessuna readiness o prova live inventata.
- Nuovo installer e integrazione finale nella ricevuta privata post-merge,
  evitando commit documentali per il proprio SHA futuro. Report:
  reports/2026-10-08_WIN7POS_POST_PR121_FUNCTIONAL_OPERATIONAL_CLOSEOUT.md.


## 2026-10-14 - Consolidamento aggiornamenti GitHub Actions

- Riprese le PR esistenti #125 e #101, con integrazione del main corrente
  `5cbd6e5d` nel candidato di #125. Nessun nuovo branch creato.
- Aggiornati i pin esatti delle assertion CodeQL e attest allo stesso SHA
  ufficiale gia proposto dai workflow: la verifica resta fail-closed e non
  accetta versioni arbitrarie. Il download artifact v8 conserva il runtime
  Node 24 e rende fail-closed il digest mismatch per impostazione predefinita.
- Il merge main attiva soltanto CI, supply-chain e artifact development-unsigned;
  Protected Release richiede un tag esplicito, non creato in questa attivita.
- Verifiche, review indipendente, CI sull'esatto candidato e destinazione della
  PR duplicata sono registrate nel report esterno di chiusura integrazione.
  Nessuna attestazione aggiuntiva di hardware Win7, Asus o staging autenticato.


## 2026-10-14 - Consolidamento dipendenze NuGet e grafi locked

- Ripresa la PR #127 esistente come candidato combinato delle PR #106,
  #107 e #108; i loro package finali sono valutati insieme agli aggiornamenti
  Actions di #125/#101. Il confronto indipendente degli otto blob package
  finali dimostra l'equivalenza di #106 e #127 sul main corrente.
- Rigenerati tutti i lock consumer con SDK esatto `10.0.301`, contesto restore
  Windows e `AutomaticallyUseReferenceAssemblyPackages=false` sul Mac:
  preservati RID `win-x86`, target `net48`/`netstandard2.0` e restore locked
  obbligatorio nei workflow. Nessun cambio al pin SDK o al target Win7/x86.
- Le undici versioni nuove conservano le stesse espressioni Apache-2.0/MIT
  attestate dai nuspec del feed ufficiale; la policy mantiene il confronto
  chiuso con il grafo completo (49 package/versioni), senza nuove eccezioni.
- L'analisi indipendente dei sorgenti System.Drawing.Common conferma che
  l'asset net462 inoltra i tipi al framework sia nella vecchia sia nella nuova
  versione. Build e regressioni restano distinte dall'accettazione hardware
  Windows7/Asus e dagli ambienti autenticati, non attraversati nel closeout Git.
- Comandi reali, relativi esiti, review e CI dell'esatto candidato sono
  registrati nel report esterno `MAIN_INTEGRATION_CLOSEOUT_RESULT`.

- La re-review ha rilevato che il callsite ZXing `Code128Renderer` non era
  attraversato dai gate esistenti. Aggiunta allo smoke immagini canonico una
  regressione `net48/x86` senza stampa: tre barcode generati dal renderer
  applicativo e decodificati con il binding aggiornato, controllo CODE_128 e
  roundtrip esatto, contratto null/empty. Il runner richiede il nuovo marker
  oltre a tutti quelli precedenti. Il candidato precedente viene sostituito e
  rivalidato; nessun risultato viene trasferito automaticamente al nuovo SHA.

## 2026-10-15 - PR #128, diagnosi del timeout Recovery

- Conservata la correzione verificata dei percorsi SQLite dell'orchestratore;
  nessuna modifica a prodotto, dipendenze, net48/x86 o budget Recovery 60000 ms.
- Aggiunta osservazione limitata di startup, CPU/thread e hard fault, con
  qualificazione separata, cleanup dei soli processi/sessioni posseduti e raw
  ETL escluso dagli artifact. Spostata dopo il test la lettura degli hash del
  runtime x86 per evitare di alterare la cache prima dell'osservazione.
- Coppia finale prestabilita `38812472152` su SHA `1b06ff7`: Legacy e Isolated
  entrambi TIMEOUT. Isolated ha una traccia completa senza perdita eventi e
  37,315 s di paging UI nei primi 60 s; Legacy completa tutti gli 11 gruppi ma
  non il processo entro il termine. Nessuna prova di correzione causale.
- L'entry sperimentale è rimossa dal candidato, conservata nella storia Git.
  Il report distingue il costo osservato dalle cause host ancora non provate;
  nessun merge, ulteriore tentativo identico o modifica del runtime.
- ASUS_WPF resta non eseguito per rifiuto automatico del comando di avvio;
  HOSTED_WPF è evidenza separata. Readiness Admin ancora esterna; Win7 reale
  differito dall'utente. Esiti, SHA e limiti nel report esterno
  `WIN7POS_RECOVERY_CAUSE_RESULT.md` e relativo JSON.

### 2026-11-04 — ASUS-W7POS-019 Execution A+B+C (in progress)

Explicit end-to-end Execution mandate supersedes historical Planning-only limits.
Preserved baseline and local documentation; independent private corpus audit and
product preview matched without source changes. Implementing isolated historical
ledger/receipts/undo, DB authority/AUX, mixed report/CSV and WPF consumers with
closed QA contracts. Intermediate Data/WPF/dialog checks and regression failures
are recorded in the [execution ledger](plans/ASUS-W7POS-019_GETNET_HISTORY/EXECUTION.md).
No real shop import, public release or physical Windows 7 qualification claimed.

### 2026-11-05 UTC — ASUS-W7POS-019 software qualification and delivery

Implemented A+B+C with bounded Getnet analysis, historical ledger/undo, database
authority and mixed reports/CSV with operator-scoped publication. Local Run03
retains its FAIL alongside Core1692/1692 PASS. Subsequent focused canonical
steps qualified Recovery, foundation5/5, smoke8/8 and the first measured50k load;
audit/ownership regression23/23 and final required gates50/50 PASS. Real pending
report reads exposed synchronous audit admission; LogEventAsync now awaits the
factory, with a regression under actual maintenance. CI now requests All.
Main commit, exact-SHA workflows and downloaded artifacts are recorded separately
in the [result](reports/ASUS_W7POS_019_EXECUTION_RESULT.md) as they become observed.
Private originals remain unmodified; real Shop/time and physical Win7 qualification
are pending external evidence, without blocking software delivery.

### 2026-11-05 UTC — ASUS-W7POS-019 first hosted feedback

First main candidate77427d1: Security/WPF/foreign-CWD QA PASS, CI41955182242 FAIL
on real short-alias availability and18.014s apply versus15s. Core, functional20/20
and historicalSmoke8/8 passed independently; superseded Release was cancelled.
Corrected the fixture with an owned real NTFS alias proof and reused typed SQLite
commands within the transaction. Targeted113/113, clean net48/x86 build and
canonical focused foundation5/5, Smoke8/8, measuredLoad PASS; local apply2.184s.
Budgets/permissions unchanged. A new exact-SHA delivery qualification follows;
first-candidate successes and failures retain their original attribution.

### 2026-11-05 UTC — ASUS-W7POS-019 software delivered; external qualification pending

Code/package5c3327bd320293ad48edc7d9cc01ad2466e591b4 on main. Exact-SHA CI41959769748
All15/15, Security41959769752, Release41959769763 and WPF41959770648 PASS.
Core1715/1715 zero skipped, gates50/50, foundation5/37, historicalSmoke8/44 and
measuredLoad1/11; hosted apply3.784s versus the previous18.014s, unchanged15s limit.
Canonical downloaded Release verifier PASS:41 payload/110 checksummed files,
setup/dist/ZIP/SBOM/provenance/attestation, development-unsigned (not installed).
Separate39-file net48/x86 QA verified and executed from foreign empty CWD;33 shared
product/QA files byte-identical. Final packaged reader privately matched7/7
originals/independent controls with hashes unchanged and NeedsEvidence=true;
no real apply. All earlier failures/cancelled package remain recorded honestly.
Final docs-only delta preserves the qualified code SHA. Real Shop/time evidence
and physical Win7/peripherals remain BLOCKED_EXTERNAL; no inherited hardware defer.
Exact identities, hashes, measurements and operator actions are in the
[result](reports/ASUS_W7POS_019_EXECUTION_RESULT.md) and
[owner card](reports/ASUS_W7POS_019_EXTERNAL_QUALIFICATION.md).


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
Dettagli e riferimenti: [report canonico](reports/ASUS_W7POS_019_RETRY_E2E_RESULT.md).
Le sezioni sotto sono checkpoint storici, non lo stato corrente. Nessun DONE.

</details>
