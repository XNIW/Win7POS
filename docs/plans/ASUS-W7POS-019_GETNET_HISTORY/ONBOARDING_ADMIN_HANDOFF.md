# ASUS-W7POS-019 — Handoff Admin / onboarding

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
| Catena bilaterale | reviewer Apply/Undo egrant privati qualificati; journal client qualificato come componente | grant/delivery/journal/reconciliation/WPF | INCOMPLETE; E2E autenticato NOT_RUN | U19JournalTarget03 PASS292/292; WPF Smoke52/52 eCloudSync1/1 PASS | concorrenza SQL, delivery/ricevuta, riconciliazione, baseline/WPF/lettura canonica eE2E completo |
| Cleanup TASK094 | fixtureee788ef4-591b-45a4-86f3-90d28a77e12d, codice/data esatti | query scoped read-only staging | PASS_SCOPED_READONLY, tutti gli attivi0 | task094-readonly-cleanup-20261110.private.json | FAIL42896389034 e cleanup originaleNOT_ATTESTED conservati |
| Installato / dati reali | 2ad42ca9ac5601d7da9555996c1642263252b6e0 | verifica EXE e profilo conservato | invariato; nessun nuovo Apply/Undo | EXE33a73a2c51e6fbe3082081ed787d6421da6641359f6222faf7f566538593eb0f | consegna finale distinta da build/CI |
| Mobile / Win7 / DPI / GetnetC | handoffTASK146 esistente | target Win7 assente; 200%2880×1800 | Win7BLOCKED_TARGET;100/125USER_DEFERRED; moderniC MISSING | commento6722613449, prove private originali | nessun PASS mobile/Win7 trasferito da Windows11 |
| Cassa d1b2 ASUS disponibile | pacchetto eQA d1b2, budget63970e30 invariato | run-cart-performance Integrated12+Final60 | PASS entrambi; tre flag true eprocessi terminati normalmente | U19D1b2AsusI01/F01:31/151cicli, focus-scroll max102.510/97.826ms; complete.private.json | Windows11 disponibile; non attesta Win7 fisico/periferiche/coppia storica |
| Cassa d1b2 hosted | stesso Release42967017718,41file corrispondenti | run42970975541, Integrated12+soak60 | PASS entrambi, ambiente/misurazione/stabilità validi | U19D1b2Hosted01:33/166cicli, focus-scroll max138.340/56.051ms; artifact13837190884 digestd100083f39920a95f54b7132f6c9ed4622ce7ea3bde00991c26ae23295106a47 | hosted distinto da ASUS/Win7/mergeFinal |
| Undo bounded | 0b8ea660e38e5ff5e6c86604b7be56daa9cc4723 | replay42970545278,39 nuove asserzioni | PASS87file/4998assert | U19UndoReviewSql01, artifact13832838738, digest1857e5b2d870641e0ee90ff2f1f3a649cd423da0cc3d17ecb7c78ef36468e4a2 | baseline eApply originali; caso201righe in due pagine, duplicato canonico senza ownership |
| Grant content privato | 2b45e1ff1fe6a1e2f78dd474d1773bc44ca6ea4c | replay42976723698,54 nuove asserzioni | PASS88file/5052assert | U19ContentGrantSql01, artifact13835169803, digest883fd373ba4145d0ab68039a5bf9d6e449753ef3cd80c09993aa0ddc1d032406 | esiti locale/canonico separati, replay senza rinnovo,TTL non è assenza; concorrenza multisessione ancora NOT_RUN |
| Journal pre-autorizzazione | prodotto dbeed59 efixture6bd062e, nuova migration0018; pin precedenti invariati | restore locked,292 test mirati, buildnet48/x86 e dialog gate | Core PASS292/292,8nuovi casi; WPFcomponenti PASS; build0warning/0error e standard PASS | U19JournalTarget03 fingerprint ebd4bec1b7d597658165234751305bd05e2d6a41a673aa413eae985af9c17014; U19JournalWpf01 | U19JournalSmoke02 PASS8gruppi/52assert32.683s; Cloud02 PASS1/1,3.312s; tombstone locale non è riconciliazione server |

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

Checkpoint UTC 20261111T075349Z. I dettagli dei checkpoint precedenti rimangono nella
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
