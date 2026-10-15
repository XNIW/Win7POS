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

Ogni avvio crea una directory nuova con timestamp e UUID sotto `OutputRoot`
(default: directory temporanea), fuori dal checkout e dai percorsi dati POS.
Rifiuta link/reparse point negli antenati e riuso dell'identità. Non elimina
directory: conserva dati e log sintetici per l'ispezione. Anche le fixture dei
runner canonici che usano `GetTempPath()` sono confinate mediante TEMP/TMP/TMPDIR
nell'ambiente figlio. Il processo chiamante non cambia directory o ambiente.
Le variabili WIN7POS ereditate sono rimosse nel figlio; Local/Windows usano
soltanto l'endpoint loopback chiuso `http://127.0.0.1:9`.

Un mutex per percorso checkout serializza restore/build/suite fra invocazioni
dell'orchestratore; non coordina comandi manuali o altri wrapper. L'operatore
deve mantenere un solo writer ed evitare test pesanti concorrenti. Ogni figlio
ha PID, creation time e percorso exe registrati. Su Windows un Job con chiusura
dei discendenti è assegnato prima di sbloccare il comando, evitando figli
incontrollati su timeout/cancellazione. La terminazione esplicita richiede
identità corrispondente; non ricerca né termina processi per nome.

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

Il timeout originale su main `3892f37` (CI `38670961016`) rimane un FAIL.
La riproduzione diagnostica `38775697755` conserva un secondo TIMEOUT. Il
confronto `38776939297` contiene tre coppie prestabilite con ordine alternato:
entrambi i percorsi passano, e l'entrypoint sperimentale non migliora quello
canonico. L'ipotesi JIT è quindi smentita come correzione; quel codice è rimosso.
La causa dei timeout rimane UNKNOWN, senza attribuzione a ValueTuple, deadlock
o prodotto. Un PASS successivo significa soltanto mancata riproduzione nelle
condizioni registrate. I 60.000 ms, gli undici gruppi e tutte le assertion restano
invariati. Il report misura anche cleanup ed esportazione, quindi l'elapsed
totale può superare il budget del solo processo; il timeout resta un TIMEOUT.
