# QA automatizzata Win7POS

L'orchestratore `scripts/qa/Invoke-Win7PosAutonomousTests.ps1` richiede PowerShell
7.4+ sull'host di sviluppo/CI e l'SDK di `global.json` disponibile nel PATH.
Non aggiunge dipendenze al prodotto net48/x86 né richiede `pwsh` sul target Win7.
Se l'SDK verificato è installato fuori PATH, aggiungerne temporaneamente la
directory soltanto alla sessione che avvia il comando; non cambiare `global.json`.

Invocabile direttamente da terminale, CI o da una chat Codex autorizzata a eseguire
questi test. `codex-cli 0.142.4` è stato verificato con `codex --version` e
`codex exec --help`; il runner non richiede opzioni Codex speciali né modifica
configurazione, sandbox o approvazioni. Per l'esecuzione non interattiva fare
riferimento alla [documentazione ufficiale](https://developers.openai.com/codex/cli/reference).

```powershell
pwsh -NoProfile -File scripts/qa/Invoke-Win7PosAutonomousTests.ps1 -Suite Local
pwsh -NoProfile -File scripts/qa/Invoke-Win7PosAutonomousTests.ps1 -Suite Windows -OutputRoot C:\Dev\_codex-evidence
pwsh -NoProfile -File tests/qa/Test-Win7PosAutonomousTests.ps1
pwsh -NoProfile -File tests/qa/Test-Win7PosAutonomousTests.ps1 -Runtime
```

Local esegue restore locked dei progetti Core.Tests e CLI, gate QA del catalogo,
build, tutti i test Core/Data (SQLite temporaneo e mock/loopback) e selftest CLI.
Windows esegue la sequenza canonica: restore locked soluzione, required gates,
build soluzione, Core/Data, selftest, build WPF/harness x86 e i cinque runner
immagini/serializzazione, functional, authorization lease, logging e paging
Dispatcher. Functional conserva il budget di 60.000 ms per processo scenario;
deve produrre esattamente visual + 19 risultati distinti. Un suo FAIL non impedisce
gli smoke successivi. Un prerequisito fallito blocca soltanto i propri dipendenti.
Nessuna suite vuota o TRX con zero test può produrre PASS.

Il test senza `-Runtime` usa AST, fixture sintetiche e mock. `-Runtime` aggiunge
solo processi PowerShell sintetici con uscita zero/nonzero, timeout e percorso
eseguibile mancante: non esegue WPF, build, Git o servizi esterni. Questi test
devono essere eseguiti anche in CI; un blocco della piattaforma va riportato e
non aggirato con un diverso launcher.

Il runner functional conserva l'esito primario anche se la copia dei diagnostici
fallisce: `TIMEOUT`, `FAIL` e `NOT_RUN` rimangono tali, mentre un precedente `PASS`
diventa `FAIL`. Il campo `evidenceExportError` registra separatamente l'errore
secondario; `error`, `firstError` ed exit code originali non vengono sovrascritti.
La regressione esegue il vero blocco di esportazione con copy riuscita/fallita
per i quattro esiti. Su Windows, `Start-Process` redirige stdout/stderr tramite
handle di file ereditati: dopo l'attesa limitata non serve un'altra `WaitForExit()`
illimitata. Il test `-Runtime` verifica entrambe le scritture finali con un figlio
PowerShell sintetico e cleanup finito. Queste correzioni del supervisore non
dimostrano la soluzione del timeout Recovery, che avviene nell'attesa precedente.

Ogni avvio crea una directory nuova con timestamp e UUID sotto `OutputRoot`
(default: directory temporanea), fuori dal checkout e dai percorsi dati POS.
Rifiuta link/reparse point negli antenati e riuso dell'identità. Non elimina
directory: conserva dati e log sintetici per l'ispezione. I dati dei figli sono
separati dall'output: ogni processo usa una root breve e nuova
`<TEMP-base>/W7Q.<UUID-base64url>/`, verificata fuori da checkout/dati POS e senza
link. Il nome breve conserva tutti i 128 bit dell'UUID. TEMP/TMP/TMPDIR del solo
figlio puntano a questa root, WIN7POS_DATA_DIR alla sottodirectory `data`;
il chiamante non cambia directory o ambiente. Su Windows il preflight rifiuta
TEMP oltre 60 caratteri: il suffisso del logo display misurato richiede 194
caratteri aggiuntivi, e le fixture backup devono anche rispettare il contratto
esistente `destinazione.Length + 104 < 240`. I limiti prodotto non cambiano.
Se la base temporanea è già troppo lunga,
la suite fallisce prima di avviare il figlio: serve una base QA più breve nella
sessione autorizzata. La profondità di OutputRoot non allunga più i dati SQLite.
Un riferimento interno WIN7POS_QA_TEMP_BASE, rivalidato, conserva la stessa base
breve nei gate annidati e non modifica l'ambiente del chiamante.
Nei job CI e Release Pack la base è esplicitamente `runner.temp`, rivalidata
dal runner: la TEMP del profilo hosted può essere troppo lunga per queste fixture.
Ogni riga JSON registra workDirectory, temporaryDirectory e dataDirectory;
`qa-work.json` nella root breve registra il percorso inverso evidenceDirectory.
Il runner functional mantiene a sua volta dati WPF in una directory breve nuova,
separata da OutputDirectory: riporta la mappatura e copia nell'evidenza soltanto
diagnostici/screenshot, senza database. Tutti i dati brevi sono conservati;
nessuna pulizia ricorsiva è aggiunta. Questa correzione riguarda i percorsi QA e
non dimostra la causa del precedente timeout Recovery.
Gli altri smoke esportano soltanto ricevute e diagnostici testuali con nomi
esplicitamente consentiti. I runner immagini e authorization usano `-KeepData`
per conservare la loro fixture fino all'esportazione; i database e i profili di
trust non sono pubblicati negli artifact.
Le variabili WIN7POS ereditate sono rimosse nel figlio; Local/Windows usano
soltanto l'endpoint loopback chiuso `http://127.0.0.1:9`.

Un mutex per percorso checkout serializza restore/build/suite fra invocazioni
dell'orchestratore; non coordina comandi manuali o altri wrapper. L'operatore
deve mantenere un solo writer ed evitare test pesanti concorrenti. Ogni figlio
ha PID, creation time e percorso exe registrati. Su Windows un Job con chiusura
dei discendenti è assegnato prima di sbloccare il comando, evitando figli
incontrollati su timeout/cancellazione. La terminazione esplicita richiede
identità corrispondente; non ricerca né termina processi per nome.
Il cleanup interroga la Job posseduta, ne termina i soli membri e verifica zero
processi attivi entro cinque secondi prima di chiuderla. Un cleanup non confermato
blocca i passi successivi come NOT_RUN e conserva il primo errore. Le ricevute
registrano conteggio iniziale/finale e risultato del cleanup.

`-CancellationFile C:\percorso\stop.flag` permette la cancellazione finita:
creare quel file interrompe il passo posseduto e impedisce i successivi. Ogni
avvio controlla il flag prima di creare il processo e prima di aprire il gate,
anche nel percorso Staging. Ogni passo ha timeout finito e non prevede retry
automatici. Core/Data ha un limite di 3.600 secondi sulla base dei 35m56s
osservati nella CI originale; i 60.000 ms degli scenari WPF restano invariati.
I report
`qa-results.json` e `qa-summary.txt` riportano source SHA, host, profilo, hash
dei binari WPF compilati con successo nella suite Windows e risultati. Local
lascia vuoto l'elenco WPF, anche se trova vecchi eseguibili nel checkout.
La source SHA è accompagnata da hash del diff tracked e dei sorgenti untracked:
un checkout dirty non viene attribuito al solo commit. Il fingerprint è
ricontrollato a fine corsa; una variazione causa FAIL. I contenuti dei diff non
sono salvati nei report. `packageSha256=null` significa pacchetto
non qualificato da questa suite; la prova Release Pack resta separata. Il primo
errore rimane disponibile anche se l'esportazione finale fallisce. Il primo
errore e i risultati già conclusi sono salvati progressivamente; un checkpoint
parziale conserva stato FAIL. Un errore nella directory di un passo viene
registrato e permette di raccogliere i passi indipendenti successivi. L'exit code
è diverso da zero per FAIL, TIMEOUT, NOT_RUN o BLOCKED_EXTERNAL e per errori di
scrittura dei report. Una directory report non scrivibile blocca l'esecuzione
prima del restore o di azioni live.

## Staging e autorizzazione operativa

Un profilo registra l'ambito autorizzato e non crea una nuova autorizzazione.
Il wrapper non crea account, secret, readiness, sessioni live o scope QA. Per
Staging servono tutti questi prerequisiti reali:

- Profilo DPAPI già autorizzato e valido, con hostname staging consentito,
  shop/staff/device designati e binding verificato.
- Readiness corrente emessa dal maintainer per quello scope, client SHA,
  deployment e run ID; i vecchi manifest READY non sono riutilizzabili.
- Release Pack riuscito sullo stesso SHA, checkout pulito e HEAD=origin/main,
  come impone il runner staging canonico.

```powershell
pwsh -NoProfile -File scripts/qa/Invoke-Win7PosAutonomousTests.ps1 -Suite Staging -Profile <profilo-dpapi-autorizzato> -ReadinessRunId <nuovo-id-riservato> -ReleasePackRunId <run-release-stesso-sha>
```

Questi placeholder sono inerti. I valori del secret restano esclusivamente
nel vault esistente; non passarli in JSON, argomenti, ambiente o log. L'unico
endpoint consentito resta quello verificato dal vault e dal client canonici,
incluso il rifiuto dei redirect verso host diversi. Nessun URL alternativo è
accettato dall'orchestratore. La suite delega a
`Invoke-Win7PosStagingAcceptance.ps1`, che controlla readiness prima delle
mutazioni e mantiene lock/singolo consumo/payload e redazione esistenti. Dati
ed evidenze staging rimangono nei percorsi fissi del runner canonico descritti
in [WIN7POS_STAGING_QA_CREDENTIAL_VAULT.md](QA/WIN7POS_STAGING_QA_CREDENTIAL_VAULT.md).
Il PASS richiede anche la ricevuta canonica con run ID corrispondente, una sola
esecuzione logica e nessun orfano. Se mancano profilo, readiness o Release Pack,
il risultato è BLOCKED_EXTERNAL senza interazione con il servizio.

La riusabilità riguarda i test locali e i soli scenari QA già designati,
con creazione e gestione dei propri dati sintetici. Il run ID live consumato
non autorizza un secondo tentativo. Nessun pagamento reale, invio fiscale,
reset outbox, modifica produzione o azione su periferiche è inclusa.

La CI Local/Windows non usa secret. Una futura invocazione Staging in CI deve
essere collegata a un ambiente QA protetto e codice fidato; non eseguire secret
su codice di PR/fork. L'orchestratore non aggiunge job schedulati né modifica
permessi, sandbox o approvazioni.

## Win7 e hardware

`-Suite Win7` e `-Suite Hardware` restituiscono BLOCKED_EXTERNAL: non esiste un
adapter automatico per host/dispositivi reali autorizzati. Per riprendere servono
host Win7 SP1 identificato, pacchetto x86 con SHA256, dati QA e scope periferiche
effettivamente disponibili. Usare la checklist
[WIN7_PHYSICAL_SMOKE_REQUEST.md](QA/WIN7_PHYSICAL_SMOKE_REQUEST.md) e gli strumenti
compatibili già presenti sul target. Un PASS Windows moderno o una simulazione
non qualifica Win7, stampante, scanner o cassetto fisici.

## Interpretazione delle evidenze Recovery

La ripresa da `3026ce5` riusa anche il prefisso `native-kernel` dello SHA
`32ddfbd` (43,085 s del processo): a differenza dell'export Legacy successivo,
contiene stack dei worker. La mappatura degli indirizzi agli intervalli
`Image/Load` trova nel thread di rendering 6216 9.846 stack con `wpfgfx`,
8.580 con `d3d9` e 8.365 con `d3d10warp`; altri sette thread con CPU osservata
contengono chiamate WARP. Sono stack di scheduling, non campioni CPU pesati;
non assegnare a una funzione i secondi CPU del thread. Il prefisso rimane
incompleto e il relativo TIMEOUT rimane tale.

Il `DialogShellWindow` di quel sorgente e di `3026ce5` è identico: applica
`DropShadowEffect` al bordo che contiene tutti i controlli. Le modifiche della
griglia e del progress animato invalidano quindi l'input dell'effetto esteso
all'intera card. Il meccanismo è descritto anche nella
[guida Microsoft WPF](https://devblogs.microsoft.com/visualstudio/wpf-in-visual-studio-2010-part-2-performance-tuning/).
La correzione separa un bordo decorativo vuoto nel medesimo `mainGrid`, con
gli stessi colori, raggi, spessore, blur, opacità e bounds. Il bordo esterno
continua a governare sizing, ownership e hit-test; nessun cambio di renderer,
cache, runtime o monitor. La regressione osserva sette stati reali di Sync
Center/Recovery: l'ombra deve conservare la sagoma e nessun controllo dinamico
deve avere un effetto nei propri antenati. Il verdetto viene emesso dopo gli
assert originali del primo gruppo; gli altri dieci gruppi restano eseguiti.
Questa separazione non promette che WPF non rasterizzi mai più l'ombra:
elimina i controlli dinamici dal suo input. La qualifica del timeout richiede
ancora i risultati del candidato esatto, oltre alla regressione causale.

Baseline test-only `b81f5ab`, run `38975083907`: TIMEOUT a 60.085,986 ms
(budget processo 60.000), cleanup confermato. I sette punti osservati hanno
`isolated=False`, sagoma conservata e input dell'effetto esteso ai controlli.
Il primo gruppo conclude tutti gli assert originali e fallisce soltanto la
nuova regressione; i successivi sei gruppi passano, l'ottavo resta incompleto.
Le quattordici catture sono conservate; gli ultimi gruppi non sono dichiarati
PASS. L'ingresso nel corpo functional è a 18.331,295 ms dalla creazione:
la baseline comprende anche un costo iniziale, che questa patch non elimina.

La regressione aggiunta dopo `5a9d66c` distingue il blocco dei comandi dal
sync effettivo: il Sync Center non deve animare il proprio progress mentre
attende il dialogo Recovery modale. Il test controlla anche progress attivo
durante un sync pendente, doppio click e ripristino dopo successo, errore e
cancellazione. Tutti gli assert originali del primo gruppo precedono il nuovo
verdetto, così la baseline rossa non abbrevia il flusso originale.
La baseline test-only `ce76a62`, run `38949548176`, completa tutti gli undici
gruppi in 53.252,673 ms: dieci PASS, un FAIL esclusivamente per il progress
parent visibile e indeterminato senza sync. I comandi restano bloccati e i tre
terminali sync passano. Il risultato finale deve verificare la correzione
`_syncRunning` sullo SHA esatto, con il percorso hosted mirato e la CI completa;
il fallimento conservato e un confronto di tempi fra VM diverse non bastano
da soli a qualificare la chiusura di tutti i timeout storici.

L'export funzionale ammette JSON soltanto per la ricevuta di misurazione
`sync-backup-load.json`: i profili `pos-trusted-device.json` e tutti i JSON
sconosciuti restano nella directory dati posseduta, insieme a DB e dump.
La regressione usa esclusivamente file sintetici: riproduce l'export indebito
prima della correzione e verifica dopo sia l'insieme esatto dei diagnostici
copiati sia la conservazione dei dati originali. Il contenuto dei profili non
viene letto. Le ricevute storiche non sono cancellate o riscritte.

Il timeout originale su main `3892f37` (CI `38670961016`) rimane un FAIL.
La riproduzione diagnostica `38775697755` conserva un secondo TIMEOUT. Il
confronto `38776939297` contiene tre coppie prestabilite con ordine alternato
sulla stessa VM: solo il primo processo era freddo. Quei PASS non dimostrano
un beneficio causale dell'entrypoint né escludono il costo di caricamento delle
dipendenze. Un PASS successivo significa soltanto mancata riproduzione nelle
condizioni registrate. I 60.000 ms, gli undici gruppi e tutte le assertion restano
invariati. `elapsedMs` comprende la supervisione e il cleanup del processo,
escludendo la successiva copia dei diagnostici; può superare il budget del solo
processo. Il timeout resta un TIMEOUT.

Il vecchio campo `process_creation_to_startup_wall_ms` è acquisito nel corpo di
`FunctionalCompletionSmoke.RunAsync`, dopo `Application.Startup` e
`PosDbOptions.Default()`: non misura il solo startup WPF. La timeline aggiunta
registra al massimo dodici punti: primo probe raggiungibile di Main, directory,
costruzione WPF, callback Startup, chiamata/body async functional, risultato e
ritorno da Application.Run. Distingue questi intervalli senza attribuire il
tempo prima di Main a JIT/antivirus/host. Tutta l'osservazione e il lavoro richiesto
rimangono nel budget del processo. Il workflow conserva immagine host e snapshot
limitati dei processi toolchain/harness pertinenti prima/dopo ogni prova.

L'indagine nativa del 15 ottobre 2026 distingue intervalli JIT da tempo CPU:
la traccia `38810033508` attribuisce 28,288 s dei primi 43,085 s a hard fault
del thread UI, soprattutto su immagini native .NET/WPF. È un prefisso, perché
l'export ha raggiunto il limite finito. Il collector precedente leggeva inoltre
gli hash CLR/DAC/SOS prima del target: questo preleggeva pagine del runtime e
limita la qualificazione di avvio freddo. Ora la provenienza x86 viene acquisita
solo dopo l'uscita del target e lo stop della traccia.

La coppia prefissata `38812472152`, sullo SHA
`1b06ff7` e su due VM distinte della stessa immagine `20261007.264.1`, mantiene
lo stesso binario, runtime net48/x86, ingresso funzionale, gruppi e budget:

- Legacy: TIMEOUT, 11/11 gruppi PASS, risultato scritto a 59.910,884 ms e
  ritorno da `Application.Run` a 59.962,363 ms. Questo non prova l'uscita del
  processo entro il termine. Export kernel limitato ai primi 54,624 s;
  CPU cumulativa del processo 151,3 s fra tutti i thread.
- Isolated: TIMEOUT, 6/11 gruppi completati, export completo fino all'uscita
  forzata, zero eventi persi. Nei primi 60 s dalla creazione del processo,
  l'unione degli hard fault UI è 37,315 s. Esclusi dall'attribuzione gli eventi
  precedenti alla creazione o appartenenti ad altri processi.

L'ingresso isolato non è una correzione; l'esperimento resta nello storico e
non viene adottato dal percorso canonico. Il paging spiega un costo concreto
della corsa Isolated, ma non identifica l'origine delle latenze dell'host né
spiega tutti i timeout. Non sono dimostrati difetti di WOF, storage, antivirus,
ValueTuple o prodotto. La qualificazione su fixture sintetica non misura
l'overhead della traccia kernel sul carico WPF. La coppia termina con questi
due risultati; non va ripetuta senza una nuova domanda diagnostica.

I modi diagnostici `native-startup` e `native-waits` usano strumenti Windows e
un reader Microsoft firmato già installato. Qualificazione e target girano su
VM distinte; nessun runtime switch, warmup o setup funzionale fuori budget.
Il raw ETL rimane fuori dagli artifact. Il decoder conserva limiti finiti e
fallisce su perdita eventi o export incompleto; un prefisso non qualifica una
traccia completa. Le ricevute funzionali conservano TIMEOUT e cleanup anche
quando l'export fallisce. La PR non è integrabile finché manca una correzione
causale verificata e tutte le verifiche richieste sul candidato finale.

La readiness mancante è un file GitHub Admin
`docs/HANDOFFS/WIN7POS_ARTICLE_ACCEPTANCE_READY.json`, non un endpoint live in errore.
Il consumer fu introdotto con l'attestazione ancora assente; non è stato trovato
un successore canonico. L'Admin maintainer deve pubblicare una nuova attestazione
revisionata dopo verifiche reali del deployment e dello scope, con il contratto
di `Win7PosArticleReadiness.psm1`. Il suo scope `qa-articles-zero-sales` impone
`salesAllowed=false`: non autorizza test economici remoti.
