# ASUS019 — continuazione client cloud, Execution isolata

<!-- ASUS019_CURRENT_START -->
## Convergenza corrente — stessa Execution ASUS-W7POS-019

Due consegne: cassa quotidiana e storico bilaterale, senza nuove funzioni.
Stato ACTIVE; nessun DONE, merge main, deploy, installazione o nuova mutazione reale.
L'utente autorizza tutti i file necessari ai residui; soglie e workload invariati.

| Scenario | SHA / binding | Comando e ambiente | Esito | Evidence | Prossimo gate |
|---|---|---|---|---|---|
| Cassa PR136 | d1b2bc318c8a0aecce9543dc6a96f9a1aaf7cfc3 | CI42967023331; Security42967023327; WPF42967096688; Release42967017718 | PASS build/gate eIntegrated/Final ASUS ehosted | U19ReleaseD1b201:41file/110checksum, installer a07430ce e payload6b44ffe3 | backup/consegna installata e coppia bilaterale restano distinti |
| Focus originario | prodotto149c978 | Integrated12 +soak60, run42897760117, budget250ms | Integrated PASS; soak FAIL conservato | artifact13807390665, cicli116/118:258.573/258.382ms | nessuna attribuzione retroattiva senza fasi originali |
| Diagnosi focus | prodotto149, harness6533c71 | Diagnostic60, run42924975778,162cicli | completa; non qualifica | U19Focus149Hosted01, artifact13816767268 | callback focus superfluo già focalizzato, in coda dietro Send; nessun blocco scanner/ASUS presunto |
| Qualifica cassa b762 | pacchetto Release42934061207; 41/41 file corrispondenti | hosted42937991969, Integrated12 +soak60 | PASS entrambi, ambiente valido | U19CartB762Qual01, artifact13823212154, digest550a502d009879483d9d7913e505dfd81518e2aa87cb2a179cfeda9d62eda138; focus max28.354/14.428ms,32/159cicli | non attesta ASUS, Win7 o coppia finale |
| Ritorno menu Cassa | da6539c | AUTHENTICATED_SHELL_UX, net48/x86,200% | PASS73/73; rosso precedente conservato | U19MenuFocusRed01:29PASS poi focusFAIL; Green01 PASS | chiusura menu ripristina scanner solo quando Cassa selezionata, carrello/sessione conservati |
| Windows-All locale | 76146a42f6f395bd07fb7f2af47d9959ec049a5e, clean | Windows -HistoricalSalesMode All, root nuove | PASS16/16, Core1892/1892 | U19CloudAll05; fingerprintb3ec50f130bacc46051f9916c06f6bd3b7190c711413f003c77490419f1380d1 prima/dopo; Load301.0258ms | coppia finale compatibile ancora da congelare |
| Windows remoto successore | 76146a42f6f395bd07fb7f2af47d9959ec049a5e | CI42963370668; Security42963370701 | PASS16/16, Load11/11, ambiente valido | U19Win761Remote01, artifact13832115585, digest96728b32c6a7b60dcb63bbe268cc905820fec810557d0374bb499900f8bbae3c;8file verificati; querymax481.4968ms | nuova qualifica finale dopo integrazione completa |
| Windows remoto b976 | stesso b976 | CI42954896078; Security42954896096 | CI FAIL Load; Security PASS | U19WinB976Remote01, artifact13828441507, digestbaa623481ec22dffc879b6946adfe42599c649c9e412d020b9030598b58484c7 | tre query calde572–574.2707ms oltre500; altri15 passaggi PASS, ambiente Load valido |
| Correzione lettura mista | 76146a4 | una aggregazione per giorni/totale, stessi filtri/TX/token | 74 mirati eWindows-All locale/remoto PASS | U19QueryRollup01, U19CloudAll05, U19Win761Remote01 | origine/filtri/storni/vuoto/Int64 verificati, soglia500ms invariata |
| Precedenti Windows | f4ea8e2/136088b | localeAll03; CI42931948198 e42939260104 | f4 locale PASS/remoto Smoke FAIL;136 remoto PASS16/16 | U19CloudAll03, U19WinF4Remote01, U19Win136Remote02 | non cancellare FAIL storici, non sommare rerun |
| Recovery | fixture9a/f4, prodotto invariato | root dump +ritiro dopo2100ms, stessa regola controllo/Recovery | mirato eAll04 PASS | U19RecoveryHeap03/04, U19RecoveryRetirement04, U19CloudAll04 | root TextBoxView throttle timer2s; non semplice cambio5/3 |
| Decisioni client v2 | codice176eaa8 integrato b976 | Core ledger05, Smoke03, CloudSync01 | PASS97 Core; Smoke8gruppi/52verifiche; Cloud1/1 | U19DecisionsLedger05/Smoke03/Cloud01; Smoke31.299s, Cloud3.128s | esiti locale/canonico separati; nessun PASS E2E trasferito |
| Baseline v2 | client136, Adminfe6/830/815 | backup verificato, exportCLR, lettore Node, replaySQL isolato | componenti PASS | U19BaselineExport02, U19BaselineSqlContext03(82/4827), U19BaselinePipeline01(83/4859) | collegare baseline automatica e WPF senza ricaduta v1 |
| Admin qualificato | 85103361bc88c761386032db12e003234c05c906; merge43dbb4ab, tree455afe9a | CI42979142638 eCloudflare42979142701 | PASS; SQL88/5052, foundation1502PASS/14SKIP; deploy saltati | U19Admin85101:58file/lock/config/tree verificati; Worker13836547664 digest155da9a52f36f81183341d6f7d7e381fb1da26670fba32777afeadba02758e4a | bundle distinto dal servizio storico operativo |
| Contratti Node | 0fad8496a616c7b04827a9edee53f262b8a7c301 locale | typecheck e foundation integrati01 | PASS1559/7SKIP su1566, zeroFAIL | review-apply-integrated-typecheck01/foundation01; mirati corpus03 PASS3 | CI del candidato integrato; API v2 ancora chiuse |
| Proiezione locale server | 82dec507 integrato6a8918e/6c | replay isolato42952263404 | PASS84file/4892assert,33 nuove prove | U19ProjectionSql01, artifact13825732659, digest74d12f78e92680be80d876842de3ab2a617f97222754f650f339d9bfc6eb282a | importi dalle fonti immutabili; duplicato non acquisisce ownership |
| Indice review server | 254d2f6 integrato in7b25d76 | replay isolato42955496895 eCI integrata42964674538 | PASS85file/4929assert,37nuoveprove | U19ReviewIndexSql01 eU19Admin7b01 | indice di sole coordinate; nessuna copia economica |
| Corpus CLR→Node | client eab54ae; Node118f858+715b75f, integrato0fad849 | 9draft/receipt,37record,1correzione; trasporto SQL byte-esatto | PASS1CLR +3Node mirati distinti | U19PublicationCorpus02, U19PublicationCorpusTests02, publication-node03 | non E2E distribuito; test Unicode protegge gli hash dei byte originali |
| Review Apply server | ee8f2952506c4650713415ac29cb8f4887a5c3d2; migrationSHA8e0ddbfacbc41613db0b8bb4c13f2b3033af561a0fe362a16bdeaec6a44e1bbf | replay isolato42966932726 | PASS86file/4959assert,30nuoveprove | U19ReviewApplySql03, artifact13831916416, digest3a4f89551203865be4b3a86b2d242adfaf2d50da7dabcbaaf19cf658cc9766e8 | decisioni locale/canonica distinte, replay e revoca/revisione; nessun grant/ledger finanziario |
| Catena bilaterale | reviewer Apply/Undo egrant privati qualificati; journal client qualificato come componente | grant/delivery/journal/reconciliation/WPF | INCOMPLETE; E2E autenticato NOT_RUN | U19JournalTarget03 PASS292/292; Smoke/CloudSync successori NOT_RUN | concorrenza SQL, delivery/ricevuta, riconciliazione, baseline/WPF/lettura canonica eE2E completo |
| Cleanup TASK094 | fixtureee788ef4-591b-45a4-86f3-90d28a77e12d, codice/data esatti | query scoped read-only staging | PASS_SCOPED_READONLY, tutti gli attivi0 | task094-readonly-cleanup-20261110.private.json | FAIL42896389034 e cleanup originaleNOT_ATTESTED conservati |
| Installato / dati reali | 2ad42ca9ac5601d7da9555996c1642263252b6e0 | verifica EXE e profilo conservato | invariato; nessun nuovo Apply/Undo | EXE33a73a2c51e6fbe3082081ed787d6421da6641359f6222faf7f566538593eb0f | consegna finale distinta da build/CI |
| Mobile / Win7 / DPI / GetnetC | handoffTASK146 esistente | target Win7 assente; 200%2880×1800 | Win7BLOCKED_TARGET;100/125USER_DEFERRED; moderniC MISSING | commento6722613449, prove private originali | nessun PASS mobile/Win7 trasferito da Windows11 |
| Cassa d1b2 ASUS disponibile | pacchetto eQA d1b2, budget63970e30 invariato | run-cart-performance Integrated12+Final60 | PASS entrambi; tre flag true eprocessi terminati normalmente | U19D1b2AsusI01/F01:31/151cicli, focus-scroll max102.510/97.826ms; complete.private.json | Windows11 disponibile; non attesta Win7 fisico/periferiche/coppia storica |
| Cassa d1b2 hosted | stesso Release42967017718,41file corrispondenti | run42970975541, Integrated12+soak60 | PASS entrambi, ambiente/misurazione/stabilità validi | U19D1b2Hosted01:33/166cicli, focus-scroll max138.340/56.051ms; artifact13837190884 digestd100083f39920a95f54b7132f6c9ed4622ce7ea3bde00991c26ae23295106a47 | hosted distinto da ASUS/Win7/mergeFinal |
| Undo bounded | 0b8ea660e38e5ff5e6c86604b7be56daa9cc4723 | replay42970545278,39 nuove asserzioni | PASS87file/4998assert | U19UndoReviewSql01, artifact13832838738, digest1857e5b2d870641e0ee90ff2f1f3a649cd423da0cc3d17ecb7c78ef36468e4a2 | baseline eApply originali; caso201righe in due pagine, duplicato canonico senza ownership |
| Grant content privato | 2b45e1ff1fe6a1e2f78dd474d1773bc44ca6ea4c | replay42976723698,54 nuove asserzioni | PASS88file/5052assert | U19ContentGrantSql01, artifact13835169803, digest883fd373ba4145d0ab68039a5bf9d6e449753ef3cd80c09993aa0ddc1d032406 | esiti locale/canonico separati, replay senza rinnovo,TTL non è assenza; concorrenza multisessione ancora NOT_RUN |
| Journal pre-autorizzazione | A19Journal01, nuova migration0018; pin precedenti invariati | restore locked,292 test mirati, buildnet48/x86 e dialog gate | Core PASS292/292,8nuovi casi; build0warning/0error; standard PASS | U19JournalTarget03 fingerprint ebd4bec1b7d597658165234751305bd05e2d6a41a673aa413eae985af9c17014; U19JournalWpf01 | Smoke/CloudSync; tombstone locale non è ancora riconciliazione server |

Il FAIL Load remoto b976 resta conservato. Successore76146: locale301.0258ms
e remoto481.4968ms a caldo, entrambi entro500ms; Cold remoto517.7835ms e
ownership7082.3414ms rispettano i rispettivi budget. Query con aggregazione
giorni/totale unica, nessuna soglia/workload modificata. I risultati delle VM
diverse non sono una misura causale assoluta del guadagno.

Sono conservati i FAIL originali149, f4 remoto, All01/02, Smoke01/02 e il primo
corpusCLR con percorso export non valido. Fixture corretti senza cambiare le
guardie prodotto. Le vecchie run non diventano PASS e i test sovrapposti non si sommano.

Review Apply/Undo e grant privato hanno superato i rispettivi replay isolati.
Concorrenza multisessione, delivery atomica, ricevuta canonica, journal prima
di authorize e riconciliazione dei grant ignoti restano gate critici prima
dell'API e della coppia finale. Le prove
SQL/Node/loopback e il Worker impacchettato non attestano il servizio operativo.
Journal: primo mirato01 conserva FAIL1/291 per fixture articoli ferma a17,
successore02 PASS291/291. Test rosso di adozione riproduceva il confronto
errato dei literal SQL; corretti soltanto quelli del nuovo schema18, stato
letterale preservato. Successore03 PASS292/292; non sommare queste run.
Baseline da backup completo verificato locale; soltanto export storico selettivo
online. Profilo Minimondo, ledger originale e sette XLSX restano conservati.

Release menu da6539c42964888047 conserva FAIL del solo processo sintetico di
output:512WriteLine per flusso,447/448righe completate al limite15s. Successore
d1b2bc3 cattura byte-stream con buffer, stessi dati/15s e60s per caso WPF;
locale287ms con514/513righe/sentinelle esatte,22scenari/50gate PASS. Nuovo
Release e qualifica Integrated/Final ASUS ehosted sono PASS; installato ancora2ad42ca.

Review SQL: primi replay42964609617/42965662580 FAIL page digest conservati.
La lettura Python predefinita Windows aveva ricodificato un carattere UTF8 del
fixture in due caratteri; originaliCLR integri. Solo literalSQL ripristinato con
lettura UTF8 esplicita/escape Unicode;9pagine confrontate byte per byte, guardia
SHA prodotto invariata. Successore42966932726 PASS.

Checkpoint UTC 20261111T074733Z. I dettagli dei checkpoint precedenti rimangono nella
cronologia Git e nelle evidence private originali; nessuna approvazione operativa implicita.
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

## Addendum sincronizzazione bidirezionale — Execution 10 novembre

Il chiarimento successivo estende la verifica a tutti i domini commerciali
condivisi, conservando questa implementazione e la qualifica PR136. Nessuna
nuova task, scheduler globale o coda duplicata. La matrice seguente descrive
codice letto, non attribuisce E2E o distribuzione. Gate autenticati bilaterali
nuovi: NOT_RUN per tutte le righe finché accompagnati da ricevuta pertinente.

Checkpoint successivo: PR136/149c978 ha CI42884098486 e Security42884098503
SUCCESS, bundle verificato15/15 e functional22/22; Release42889654511 e payload
WPF42889661815 SUCCESS,41/40file verificati e34comuni identici. Il delta seguente rimane WIP su fbdde199 nel client135,
non è incluso nel candidato136 né nell'installato2ad42ca.

| Dominio / origini consentite | Autorità e transazione/intento POS | Endpoint / ACK | Feed/apply e conflitto | Cancellazione e vista / gap |
|---|---|---|---|---|
| Prodotti: Save/duplicate/edit POS, Admin | `LocalArticleMutationWriter` + `article_mutation_outbox`, sequenza/hash/baseRevision | `/api/pos/catalog/article-mutations`, ACK fenced | `/api/pos/catalog/pull`, `RemoteCatalogProductWriter`, shadow remoto conserva intento locale | deactivate/activate e tombstone distinti; griglia/editor/barcode; pubblicazione viste da verificare |
| Prezzi e storia: Save POS, import, Admin | stessa TX articolo/prezzo o import; ID evento immutabile | ACK articolo/import e remote price ID | catalog delta, prezzo pendente se manca prodotto; pull prima di ACK riconcilia ID esatto | storia immutabile, nessun secondo evento/ritocco silenzioso carrello; E2E nuovo NOT_RUN |
| Categorie/fornitori | POS seleziona riferimenti; creazione tramite import catalogo, nessun CRUD locale autonomo trovato in `CategoryRepository`/`SupplierRepository` | outbox import per le operazioni esposte; nessuna nuova coda generica | catalog delta + upsert/tombstone transazionali dei due repository | editor seleziona riferimenti remoti; completare verifica dei chiamanti import e superficie Admin |
| Import catalogo POS/Admin | `SupplierExcelImportApplier` + `catalog_import_outbox` nella stessa TX | `/api/pos/catalog/import-sync`, clientImportId/hash/attempt/receipt | ACK segnala delta, `PosCatalogPullService`, resume; niente full per ACK | review/correzioni conservano intenti; griglia/catalogo; E2E nuovo NOT_RUN |
| Immagini POS/Admin | `ProductImageSyncService` + outbox immagine esistente | intent/finalize/read-urls/remove sotto `/api/pos/catalog/product-images`, fence/receipt | revisioni catalogo e cache immagini scoped | rimozione dedicata, nessun echo; galleria/editor; E2E nuovo NOT_RUN |
| Giacenze | vendita/reso/void nella TX nativa; aggiustamento manuale nell'intento articolo; import nel suo dominio | sales sync oppure article mutation/import, mai entrambe per lo stesso effetto | catalog stock/base più movimenti locali pendenti protetti | nessun doppio delta di ritorno; stock/catalogo; E2E nuovo NOT_RUN |
| Vendite/reso/void | `SaleTransactionWriter` + `SalesSyncOutboxRepository`, economia/stock/outbox atomici | `/api/pos/sales/sync`, client sale ID/hash/attempt | ACK non prova download dei registri di altre casse: reader/replica di consultazione Windows da completare sezione per sezione | vendite ricevute mai rieseguite; report/Register: GAP lettura inter-postazione da verificare sul contratto server |
| Ordini | inbox + ACK durevole, claim server e lease | `/api/pos/orders/claim`, `/api/pos/orders/ack` | lane `CustomerOrders`, inbox scoped, niente doppio handoff | annullamento stato dominio; viste ordine; verificare origini/modifiche esposte, E2E nuovo NOT_RUN |
| Storico Getnet | local/frozen/web; ledger separato, nuova outbox0017 implementata come componente, non consegna cloud | `/api/pos/payment-history`, grant content-bound e ricevute recuperabili da completare | replica snapshot/delta separata; stesso supervisor, nessun gate cassa durante rete | Undo/correzione/provenance; riepilogo volontario; GAP centrale invio automatico/E2E/worker operativo |
| Configurazioni condivise | solo contratti shop/policy/permessi effettivi, da inventariare nei DTO/chiamanti | auth/heartbeat/policy esistenti, non copia di app_settings | aggiornamento fenced per generazione; revoca efficace | segreti, PIN, config periferiche/dispositivo e backup esclusi dal dataset condiviso |

Percorsi effettivi: `src/Win7POS.Core/Online/CatalogSyncPolicy.cs`,
`OnlineSyncSupervisorContracts.cs`; `src/Win7POS.Data/Online/OnlineSyncSupervisor.cs`,
`PriorityOnlineRequestGate.cs`, `PosCatalogPullService.cs`,
`PosCustomerOrderSyncService.cs`, `ProductImageSyncService.cs`;
`src/Win7POS.Data/Repositories/RemoteCatalogProductWriter.cs`,
`LocalArticleMutationWriter.cs`, `SalesSyncOutboxRepository.cs`,
`CategoryRepository.cs`, `SupplierRepository.cs`;
`src/Win7POS.Wpf/Pos/Online/PosOnlineSyncSupervisorHost.cs` e i file storici
indicati sopra. La policy catalogo corregge ora il vecchio ordine seriale;
l'architettura corregge il conflitto remoto secondo la protezione shadow/intent.

Budget ricavati dal codice: concorrenza HTTP2, priorità3/2/1/0; heartbeat24–36s
salvo intervallo server; retry5/15/30/60/120/300s ±20%; pagine catalogo parziali
resume5s; ordini25–35s; code con lavoro immediato1–5s. Non sono misure di
convergenza. Storico:2consegne/8pagine per turno,25–35s a riposo e1–5s con altro lavoro;
misurare ancora convergenza reale e concorrenza scanner/SQLite con Input250ms invariato.

Primo delta storico locale: `HistoricalReplicaSynchronizer` estrae rete/staging
fuori da manutenzione/gate commerciale, massimo8pagine/turno con resume; store
verifica la generazione durevole nella TX. Core mirato12/12 PASS11s
(`C:\Dev\U19CloudSync01\core\cloud-replica01.trx`), build WPF net48/x86
PASS0warning0error7.11s. Prova di componente, non E2E autenticato/rollout.

Ulteriore checkpoint locale, non consegna: lane `HistoricalPayments` nello stesso
supervisor, deadline esterna15s e timeout client HTTP10s invariato, turni8pagine;
successo/poll25–35s, parziale1–5s,
backoff comune. Il rifiuto del solo permesso storico non revoca il login cassa.
Nessun timer di schermata. La pubblicazione controlla la generazione durevole
nella stessa TX; aggiornamento delle viste e prove concorrenti reali restano aperti.

Migrazione additiva0017 ancora non pubblicata (checksum corrente
64162e83598480a4a15f864acdf48b39b83f97b281e82ba4a371704ec068661b):
outbox storica, pagine immutabili, stato consegna/ricevuta; niente schema0015/16
riscritto. Apply/Undo accodano nella TX della ricevuta; Undo dipende dall'Apply
originale. Draft streaming ricostruibile e digest delle pagine prima del grant;
nessun token/path originale incluso. Prova server tipizzata vincola op/shop/device,
dataset/restore e writer epoch, request fingerprint e content digest. In assenza
di prova il commit conserva `needs_attention`, non è inviabile automaticamente.
La proof v2 non è ancora acquisita dal percorso WPF né servita dal backend.
Trasporto/ingestione/ACK/backfill/cutover del nuovo protocollo sono IN PROGRESS.

Core mirato55/55 PASS27s (`U19CloudSync03`), build net48/x86 PASS3.62s senza
warning. Primo gruppo migrazioni114:109 PASS/5 FAIL conservato (`U19CloudSync04`):
quattro aspettative ancora16 e fixture schema manuale senza0017. Corretti i pin
aggiuntivi e la fixture, senza cambiare i16checksum precedenti. Secondo gruppo
141/141 PASS1m39 (`U19CloudSync05`), include restore candidato/live/review negato
quando una pubblicazione è pendente o richiede attenzione. Backup e ledger reali
non sono stati aperti in scrittura. Core/pagine sono prove isolate, non dimostrano
API, worker o convergenza end-to-end.

Admin: contratto strutturale incrementale v2 aggiunto separatamente dal formato
archivio;13 prove foundation mirate PASS525.965ms. Non è ancora collegato alla
route device. Il file migrazione20261110181221 è stato generato dal CLI e va
completato/qualificato prima di qualsiasi pin/replay/deploy. Nessun runtime server
distribuito, nessuna nuova qualifica completa attribuita.

### Checkpoint componenti, 10 novembre19:50Z

La lineage UUID persistita è distinta dall'epoch operativo catalogo: riparazione
catalogo/restart la conservano, il reset restore la ruota nella stessa TX.
Spool posseduto:256MiB massimi,256KiB/pagina,200record/pagina e16KiB/record;
nessun monitor attraversa yield/HTTP. Il checksum f192 precedente era WIP non
pubblicato. Lineage01:28PASS/69FAIL per seed app_settings eseguito dal validatore
DDL in memoria; seed separato, lineage02:97/97PASS52s. Checksum0001–0016 invariati.

Consegna durevole: claim/attempt CAS, uncertain prima dell'HTTP, lettura della
ricevuta originale senza nuovi grant. Controllo op/grant/shop/device/dataset/
lineage/epoch/digest/hash ricevuta locale; ACK obsoleti non sovrascrivono tentativi
successivi. Conflitto/503 su una riga non impediscono la successiva indipendente;
Undo aspetta solo il proprio Apply. Massimo2operazioni/turno, poi ricezione nello
stesso supervisor. Restore e preparazione freeze rifiutano pending/uncertain/
needs_attention. HTTP grant e freeze_ack fuori da manutenzione/gate commerciale.

Il coordinatore report pubblica revisione locale+cache cloud, con evento dopo
il rilascio del gate breve. Report/form aperti ricaricano in modo coalescente,
preservando selezione/filtri anche durante reload o copertura temporaneamente
indisponibile. Nessun nuovo timer di rete per schermata; restano i timer di
sicurezza e la revoca. Segnali storico: avvio/sessione, foreground/reconnect,
postcommit e manuale diagnostico sulla stessa lane, non necessario al sync ordinario.

| Livello / evidence locale | Esito |
|---|---|
| Report/replica/ledger U19CloudSync09 |49/49PASS34s, notifica fuori gate e delta invariato senza loop |
| Delivery01 U19CloudSync10 |46PASS/10FAIL: device/session scambiati nella nuova fixture; conservato |
| Delivery02/03 U19CloudSync11/12 |12/12 e17/17PASS, Int64 wire,503 e riga indipendente |
| Drain/report/export U19CloudSync13 |29/29PASS19s |
| WPF build09 net48/x86 |PASS,0warning/0error |
| CloudSync02 U19CloudView02 |FAIL: pubblicazione durante reload perdeva copia/selezione; cleanup verificato |
| CloudSync03 U19CloudView03 |PASS2506.2765ms; convergenza343ms, selezioni/invarianti conservati |
| Smoke01 U19CloudView01 |FAIL/deadline60s,7scenari completi/50assert PASS; nuova componente poi separata |
| Smoke02 U19CloudSmoke02 |8/52PASS55175.3963ms, budget60s invariato |
| Admin contratto03 e typecheck01 |16/16PASS509.9597ms e typecheckPASS; SQL/API nuovi non qualificati |

CloudSync03 e Smoke02 hanno fingerprint
748582df0c68465d26396c164d4dccf9908ca207c3f0cd98bf21cdae8b7bfec4,
su HEADfbdde199 con diff locale, non attribuiti al solo commit.343ms è convergenza
di componente, non Input250ms né polling/rete reale. Il runner Windows/All futuro
aggiunge historical-sales-import-cloudsync, indipendente, body60s/supervisore90s;
Smoke8/52 e functional22 invariati. Selezione: -HistoricalSalesMode CloudSync.
Driver privato ripetibile: Invoke-CloudComponent.private.ps1 -Mode CloudSync nella
root evidence registrata; contesto/harness/fixture soltanto sintetici posseduti.

Stato critico ancora aperto: WPF deve acquisire grant v2 e passare draft/proof
alla TX; il solo v1 non produce pubblicazione automatica valida. Mancano ammissione/
ingest SQL+API, contesto da backup verificato, backfill del singolo batch originario,
reader canonico locale/replica in fase local, stati business d'invio e UI Admin a
movimenti/aggregati globali, reader nativo inter-postazione, E2E autenticato e
worker effettivamente operativo. SQL Admin incompleto/unpinned/non applicato.
Nessun rollout o qualifica completa dedotti dai componenti; nessun nuovo Apply/
Undo/vendita reali. Handoff TASK146 aggiornato nella nota6722613449 esistente,
storia conservata byte per byte. Restano200%,100/125 USER_DEFERRED, Win7 fisico
BLOCKED_TARGET e semantica Getnet moderna C MISSING.

Baseline verificata: PR135 fbdde199, PR136 f87dd7c (qualifica/consegna separata),
Admin151 43f668df con precedente codice qualificato 6c2bd414; main Windows
0378e17. Installato verificato sul dispositivo: 2ad42ca, POS chiuso. La CI
fbdde199 è FAIL (42863342032), Security PASS; nessun PASS trasferito al delta.

Gap confermato nei chiamanti: Apply/Undo committano ledger e ricevuta SQLite,
ma non un intento di pubblicazione. Export/freeze_ack attestano un archivio;
non consegnano i movimenti. Snapshot/delta sono soltanto server→POS. Il grant
corrente è temporaneo, non persiste una prova server vincolata al contenuto.
SynchronizeHistoricalCloudAsync mantiene inoltre manutenzione e gate cassa
durante HTTP paginato: questo va separato prima dell'automazione.

| Autorità | Nuove mutazioni POS | Nuove mutazioni Admin | Trasferimento |
|---|---|---|---|
| local | grant vigente e prova vincolata | nessuna modifica economica concorrente | pubblica commit locali, consulta |
| frozen | bloccate | bloccate | solo drain delimitato e migrazione riconciliata |
| web | bloccate | permessi server | replica automatica ai POS |

Decisione condivisa con il piano Admin della stessa ASUS019: outbox storica
dedicata, scritta nella transazione del ledger, separata dalle vendite native.
Identità operazione/dataset/device/restore e writer epoch, digest immutabile e
sequenza sono parte del protocollo versionato. Prima della mutazione il server
registra un'autorizzazione vincolata all'operazione e al contenuto: la prova
durevole permette consegna tardiva del solo fatto autorizzato, non nuovi import
offline. Replay acquisito restituisce la ricevuta originale dopo autenticazione
corrente; prima consegna senza prova ammissibile richiede riconciliazione.

Sequenza: contratto e prove negative; schema additivo/outbox atomica; ingestione
idempotente e ricevute; rete/staging fuori dal gate commerciale; integrazione
nel supervisor esistente con budget/cancellazione; consultazione canonica e
stati IT/ES/EN/ZH; harness autenticato POS→API→DB→Admin→seconda replica. Backfill
da backup verificato del solo inventario approvato (quattro file, un lotto),
mai ri-Apply degli originali. Provenienza legacy_attested resta tale. Il cutover
deve riconciliare anche outbox e risultati incerti; vecchi device assenti bloccano.

Stato del nuovo delta: IMPLEMENTATION IN PROGRESS; gate nuovi NOT_RUN. Nessun
deploy/schema live/cutover/provisioning o nuovo import reale autorizzato qui.
Worker/API/bucket/gateway effettivamente serviti ed E2E autenticato sono gate
critici distinti dalla build. Originali XLSX e backup completo non fanno parte
dell'archivio manifest/NDJSON. Win7 fisico BLOCKED_TARGET, DPI100/125 differiti,
scala200% e prova moderna C mancante conservati. Nessun DONE.

## Follow-up del consolidamento quotidiano

Il client corrente congela 0c9809047024751b130e41d141780763f94949b0 dopo il delta
SQLite: letture differite e lookup fuori Dispatcher. La patch comune di follow-up
(src/scripts/tests più sola impronta storica gitleaks) è applicata qui a tre vie
senza conflitti; SHA256 66e76334a4b1327c7e6c8ee02839c99924abb067addb59b96981b7d3375ec6db.
Include le fixture sale-safe/visuale ora autorizzate, il negativo esplicito senza
guard e il probe pubblico durante lock esclusivo. Nessun nuovo endpoint cloud.

Le due CI 9cfe193 (42858379704 e 42858313174) sono FAIL e restano conservate;
Security 42858388684/42858313637 FAIL sul solo falso positivo storico del worklog
cc6128f visibile nella storia completa. Dir scan 9cfe193 senza finding; history
scan riproduce la sola impronta esatta ora registrata, nessuna esclusione ampia.
CodeQL 42858388684 PASS. Non si attribuisce qui una causa ulteriore alla CI prima
della lettura dei relativi diagnostici. Nuova qualifica propria richiesta sul
successore; niente retry dello stesso SHA e nessun PASS Windows trasferito.

## Stato corrente — allineamento UX comune, qualifica del successore aperta

La consegna corrente resta PR136, congelata su cc6128f; installato 2ad42ca.
Questo ramo futuro PR135 deriva da b0307d4 e integra soltanto la patch comune
src/scripts/tests del nuovo carrello, scanner, bootstrap e guard economici.
Patch SHA256: 6b469de9becd74a343c6be5cc2405c35cbd92e00b05cbed8863030bd828f1d8a.
Un conflitto nel server loopback è risolto mantenendo entrambi gli endpoint:
heartbeat automatico e payment-history autenticato già presente nel cloud.
Export, replica, grant e retention cloud restano separati dalla consegna ordinaria.

I due FAIL b0307d4 nel budget storico di 60 secondi (42826371935, 42832581283)
restano conservati. Nessun terzo retry di quel SHA. La nuova fixture separa
trasferimento di autenticazione e preparazione catalogo QA; la prova pubblica
attraversa invece MainWindow e supervisor automatico, senza scorciatoie manuali.
Il successore richiede la propria CI e qualifica; nessun PASS di cc6128f trasferito.
Admin151/6c2bd41 resta qualificato e non distribuito. Nessuna installazione del
client cloud, migrazione SQL live, merge o DONE implicito.

Il mandato dell'utente del9novembre abilita le implementazioni B/C senza
operatore. Questa continuazione usa un ramo isolato da0378e17 e non modifica
il candidato5d8e704, il pacchetto qualificato, l'installato a8286c6 o i profili.
Consegna/UAC/login/DPI restano per la sessione con l'utente. Stessa ASUS019,
nessun DONE o riapertura delle task storiche.

Admin B/C0: PR149. Ledger, worker e workbench: PR150, head1bc98c7 al checkpoint.
Worker distinto dal processo HTTP; nessuna applicazione SQL, grant o deploy live.
Il piano canonico Admin ASUS019 definisce C3 export/mapping/provenance, C4 replica
storico dedicata e C5 unica writer authority. Non usare outbox vendite o prodotti
artificiali. Prove V1 locali migrate come legacy_attested, mai provider_verified.

Client in implementazione: export da backup prodotto verificato con manifest
e checksum; mapping locale/globale esplicito; replica separata e consultabile
offline con watermark/stale; divieto import locale quando l'autorità non è
verificabile. Cutover soltanto con inventario completo, freeze, drain, reconcile
e convergenza di tutti i device, inclusi annullamenti. Vecchi client offline o
non disponibili impediscono l'abilitazione web; non possono ricevere divieti
remoti. Il Win7 fisico già indisponibile resta BLOCKED_TARGET, non viene richiesto
di nuovo. C provider moderno MISSING e CSV BLOCKED_INPUT restano distinti.

Nessuna nuova prova runtime è attribuita finché non eseguita. Export/migrazione
e replica vengono provati su fixture sintetiche, mai sul registro reale importato.
Le ricevute originali e gli importi restano intatti; nessun Apply/undo degli originali.

## Checkpoint implementazione10novembre, futuro client separato

Export da backup verificato e recupero dell'archivio esatto, ack di freeze
device autenticato, cache dedicata SQLite0016 con staging e pubblicazione atomica,
replica snapshot/delta con cursori/checksum, annullamenti e ripresa. I report
scelgono una sola fonte storica e non raddoppiano locale+cloud; la copia offline
è esplicitamente l'ultima completa. Ricevute locali originali restano consultabili
come provenienza. Nessuna attribuzione al cassiere corrente. Grant server fresco,
limitato nel tempo e controllato con orologio monotono per ogni nuovo Apply/Undo
locale; offline/authority frozen o web non consentono scritture locali. Replay
della ricevuta già committata resta una lettura. Il rollback web rimane congelato.

UI nel dialog esistente: prepara/recupera export, sincronizza storico, IT/ES/EN/ZH;
owner/footer/centering invariati. Build source identity fornita dalla toolchain
Release Pack, mai dedotta dal codice staff o inventata da un client locale.
Registro/prepared export/cache restano separati da vendite native, stock e outbox.
La verifica native-outbox è nel livello Data, nessuna query WPF.

15 prove mirate export/recupero/replica/report PASS, WPF net48/x86 build PASS e
dialog standards39/39 PASS. Prima suite Core1757:1735 PASS/22 FAIL, ricevuta
conservata; cause includono fixture ferme a0015, recognizer della nuova baseline,
prerequisito CLI e boundary WPF/Dapper. Corretti senza modificare checksum0001–15
o allentare guard. Nuovo gruppo116:115 PASS/1 FAIL, ultima asserzione del numero
di migrazioni ripristinate aggiornata15→16. Suite Local finale da eseguire.

Admin PR151 head35f3312: SQL72file/4587 assertion PASS,59 nuove;
CI42770536504 e Cloudflare42770536548 SUCCESS. API/UI e follow-up dei totali
review/nome postazioni sono una candidata successiva e non ereditano quei PASS.
Nessun deployment, SQL live, UAC, avvio POS, login o operazione sul registro reale.
Nuovo client non impacchettato/installato; qualificazione WPF completa, DPI e
hardware Win7 restano NOT_RUN/BLOCKED_TARGET. Nessun DONE.

## Verifica del client futuro e raccordo fixture

Local sul commit1ccc5b23b65fd1924889e5cd097ca578d18618df PASS:7/7 passi,
1760 Core/Data PASS,0 FAIL, restore/build/selftest CLI PASS. Sorgente pulita
prima/dopo. Build WPF net48/x86 PASS,0 warning, dialog standards39/39 PASS.
Nessuna finestra nativa aperta sul desktop dell'utente.

Primo CI42777264224 FAIL conservato, artifact13745727577: Core/Data PASS,
gate release-foundation FAIL (causa remota non determinata; eseguito localmente
sullo stesso codice: PASS), functional21/22 con geometria FAIL, storico Smoke
HIST02/HIST07/HIST08 FAIL, load NOT_RUN per dipendenza. Il rosso non è
PASS_WITH_NOTES. Il nuovo grant HTTP era assente dal server loopback della
geometria e la fixture storica usava ID non UUID; queste fixture non rispecchiavano
il contratto nuovo. Nessun guard prodotto è stato allentato.

Fixture aggiornata: identità UUID, ruolo pos_admin della sola fixture storica,
risposte authority/grant legate all'actor attuale; server loopback verifica i
veri token e prova la serializzazione HTTP net48. HIST02 conserva la conferma
unica e aggiunge i rifiuti frozen/web/offline/operation mismatch/grant scaduto;
la conferma positiva deve consumare esattamente un grant. Restano8case/52assertion
e il budget originale, senza nuovi skip o soglie più larghe. Due build del harness
PASS senza warning. Esecuzione nativa delle fixture corrette affidata alla CI
isolata, non ancora qualificata da queste sole build.

Diagnosi successiva: la proiezione pubblica iniziale indicava release-foundation,
ma il replay locale completo49/50 identifica il guard legacy-db-migrations fermo
alla baseline0015. Release-foundation eseguito realmente PASS; la causa del suo
identificativo diagnostico errato resta distinta. Guard aggiornato alla sequenza
append-only0016, nuovo checksum pin e recognizer cloud; i15 checksum precedenti e
le prove negative restano invariati. Nessun allentamento del controllo schema.

Ulteriore controllo della fixture: pos_admin viene mappato al ruolo admin locale,
che intenzionalmente include tutti i permessi. La fixture storica è quindi un
manager delegato con permessi espliciti: la rimozione di un singolo permesso deve
continuare a negare l'operazione. Il percorso HTTP di geometria rimane pos_admin.
Nessuna modifica ai ruoli o ai permessi del prodotto, nessun negativo eliminato.

## Review10novembre: conservazione limitata della replica

Stessa Execution ASUS019 ACTIVE, draft135. La copia pubblicata resta intatta
fino al compare-and-swap. Sono ammessi al massimo tre trasferimenti pendenti:
quelli compatibili conservano cursore e pagine per55minuti; quelli scaduti,
di altro epoch/dataset o basati su una pubblicazione superata sono rimossi
insieme alle loro righe. Nessuna modifica allo schema0016 o ai suoi checksum.

Un delta senza righe riusa la generazione pubblicata. Un delta con modifiche
materializza la copia solo alla prima pagina non vuota, una volta per tentativo;
il costo di quel caso resta proporzionale alle dimensioni della replica.
La pubblicazione verifica checksum/count/identità e impedisce regressioni di
revisione anche quando due delta vuoti condividono la stessa generazione.
La pulizia transazionale libera pagine SQLite riutilizzabili, senza VACUUM
automatico o promessa di riduzione immediata del file fisico.

HistoricalReplicaTests10/10 PASS (review-replica02, TRX privata):40 sincronizzazioni
vuote con pagine DB limitate, delta multipagina interrotto/ripreso, quota/expiry,
cambio epoch, revisione concorrente, checksum errato e revoca prima del commit.
Qualifica CI del nuovo commit ancora da acquisire; i PASS9d47f28 restano storici.
Pacchetto5d8e704/installato a828 invariati. Nessuna finestra POS, installazione,
Apply/Undo reale o modifica del lotto legacy. Operatore e targetWin7 restano aperti.

## Dipendenza dal correttivo shell emerso nella prova reale5d

La qualifica247d6f4 è conclusa: CI42819603512/Security42819603578 SUCCESS,
15/15 passi, smoke8/52 e load11 PASS, artefatto13768027872 scaricato/verificato.
La prova reale del prodotto5d installato ha però rilevato la lettura della prova
empty durante il refresh periodico. Il correttivo di produzione4righe del
draft136 viene incluso anche qui, con la stessa regressione controllata del
reader. Nessuna modifica ulteriore alla replica, al protocollo o al grant locale.
La nuova qualifica del successore è PENDING, senza trasferire i PASS247d6f4.
Nessun nuovo installer di questo client cloud. Il candidato prodotto2ad42ca
rimane separato dal ramo futuro; installato5d e ricevuta legacy conservati.

## Checkpoint21:00Z — gate condiviso, stati visibili e preparazione server

Nuovi percorsi: `src/Win7POS.Data/Online/IOnlineSyncCredentialedRequestExecutor.cs`,
`OnlineSyncSupervisor.ExecuteCredentialedRequestAsync`, host e SignalBus esistenti.
Nessuna nuova coda: l'operatore usa lo stesso PriorityOnlineRequestGate, con token
correnti dopo l'attesa; cancellare il form non cancella altre lane. Stop/relink
cancellano e attendono anche le richieste operatore. Test33PASS219ms; primo build
Core FAIL per dipendenza Host WPF nel bridge linkato, risolto con interfaccia Data,
non rimuovendo il bridge dai test. Nessuna dipendenza Win10+.

`HistoricalPublicationOutbox.ReadSummaryAsync` distingue ricevute locali,
pending/uncertain, confermate, attenzione e originali senza intento di trasporto.
`HistoricalDeliveryChanged` sul coordinatore per-database già esistente notifica
soltanto dopo commit SQL, senza invalidare il report economico. Il form aperto
ricarica con il coalescer posseduto: nessun timer rete, selezione preservata.
Una coda vuota con originali non registrati non diventa sincronizzazione riuscita.
UI stato TX e copia ricevuta separati, Int64 esatti e ultima revisione confermata.
Le nuove prove componenti non attestano un permesso server vero o una consegna
reale: CloudView04 usa DTO sintetico esplicito, senza HTTP o profilo Minimondo.

Server: `src/server/payment-history/device-boundary.ts` distingue auth_denied,
permesso storico scoped e503 DB. SQL42501 rilegge la lease corrente e i token;
36boundary PASS,17contract PASS, typecheck03 e foundation1484PASS/7SKIP.
Foundation02 1482PASS/2FAIL conservata: guard CI testuale, successivo mirato12PASS;
foundation03 completa verde. Modalità manuale database_replay_only esclude Verify
non richiesto e TASK094 staging; PR/main/default conservano i gate precedenti.

Migration20261110181221 SHA256
`ebfa26747b33a683a90864d2a4c33eb1acd4d17d839b1a806fec545d3daeda21`:
replay0162205/42896389034 75file/4664assert PASS. I run22c9cbc42892520753 e
42895021975 FAIL statement20 sono conservati; CASE delimitato realmente nel
corpo PLpgSQL,5funzioni parse PASS prima del replay. Il vecchio job staging
TASK094 sul0162205 è FAIL auth_denied (Accepted import proof failed); cleanup
finally nel runner, nessuna attestazione zero residui o retry live. Non è E2E v2.

Migration CLI20261110203449 SHA256
`253eec9b4dc565083d6fab31aba67d8a7878bc57bd1552694362cec76d92212a`:
preparazione privata contesto/snapshot verificato, identità/root, header, pagine,
ordine/resume/ACK perso, capacità e hash; nessun grant o effetto economico.
Replay isolato984d6e4/42897836745 76file/4694assert PASS, Verify/staging SKIP per
selezione dichiarata. Primitivi EXECUTE revocati, RLS delle tabelle conservata.
API v2 non ancora aperta: autorizzazione semantica e ingestione sono gate critici,
non dettagli da chiudere dopo rollout. Baseline/lineage va attestata da backup,
non dal semplice campo trasmesso dal POS. Originale reale: sempre un Apply/un lotto.

RequiredGates03 50/50PASS; il precedente48/50FAIL e i2corretti mirati restano
conservati. Publication01 18PASS/1FAIL e02 19/19PASS conservati: verifica della
copia in TX lettura WAL, breve CAS e cleanup separato dopo commit con postcondizione
esistente mantenuta. Build13 e nuove WPF prove hanno fingerprint14b78199 sopra;
Smoke03 8/52PASS57380.1686ms, CloudView04 PASS3045.1679ms,253ms stato/386ms copia.
Nessuna di queste durate modifica il budget Input250ms. Windows-All finale,
convergenza HTTP/DB/worker/Admin/duePOS e tutti i domini restano NOT_RUN.
