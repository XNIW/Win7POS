# Importazione locale dello storico carta

La funzione importa i movimenti Getnet in un registro separato. Non crea vendite
con articoli, movimenti di magazzino, cassa, rimborsi, documenti fiscali o invii
commerciali al server. Conservare gli originali Excel: l'analisi li legge e
l'applicazione ne verifica nuovamente l'integrità.

## Preparazione e preview

L'ingresso supportato legge soltanto `.xlsx`. Selezionare gli originali senza
convertirli: il CSV dei report è un'uscita, non un formato d'importazione.
CSV_INPUT rimane BLOCKED_INPUT finché non esiste un export Getnet CSV reale
rappresentativo da qualificare; non rinominare un CSV in Excel.

Per l'avvio quotidiano il prodotto usa `WIN7POS_DATA_DIR`, se configurata,
altrimenti la directory predefinita ProgramData. Il collegamento ordinario deve
puntare all'EXE installato e il suo processo deve ereditare il profilo concordato.
Una variabile impostata soltanto nella PowerShell di una prova non qualifica il
collegamento Start Menu. Dopo una configurazione per utente, chiudere/riaprire
dal collegamento esistente e verificare negozio/profilo prima del PIN. Non
spostare DB/trust o usare una seconda istanza per correggere una selezione errata.

Per un nuovo negozio senza catalogo operativo, il primo accesso online può
mostrare **Storico disponibile, vendite non pronte** e il pulsante di accesso
allo storico. Serve una risposta catalogo completa con totale zero, riferita
allo shop e alla generation correnti: errori, timeout e risposte parziali non
abilitano questo percorso. Il login, la lease e i permessi dedicati devono
essere validi. Il workbench consente import e riepilogo/grafico/CSV, senza
aprire cassa, pagamento o stampa. Non è la modalità recovery locale.

La prova di catalogo vuoto non rende il catalogo sale-safe. I dati precedenti
sono conservati e le vendite rimangono bloccate finché una successiva
sincronizzazione completa non qualifica un catalogo operativo. Una nuova
richiesta invalida la precedente prova empty; un errore non riattiva le vendite.
La scadenza o revoca dell'autorizzazione invalida lo storico e i suoi dati visibili.
Il percorso non sostituisce le prove esercente, tempo e overlap richieste sotto.

Dal riepilogo aprire **Importazione storico**. Il permesso
`register.import_history` abilita l'importazione; `register.cancel_history` abilita
l'annullamento. La consultazione dei lotti richiede anche la visibilità di tutti
gli operatori. I nuovi permessi non vengono assegnati automaticamente a cassieri
o responsabili.

1. Selezionare uno o più export originali. Il riconoscimento usa le intestazioni,
   non il nome del file. Confermare esplicitamente CLP per il formato legacy.
2. Analizzare. Vendite, annullamenti e richieste respinte sono distinti; le respinte
   non contribuiscono ai ricavi. Il netto sconosciuto rimane sconosciuto.
3. Controllare la copertura osservata di ciascun file. Giorni mancanti non sono
   certificati come giorni senza vendite. Per le date moderne ancora irrisolte,
   esaminare entrambe le interpretazioni e il riferimento della prova.
4. Assegnare le identità esportate a gruppi esercente documentati. Un gruppo comune
   significa che le prove identificano lo stesso esercente. Il negozio proviene
   dalla sessione autorizzata; non viene dedotto dal nome dell'esercente.
5. Inserire data d'avvio POS verificata e riferimento della verifica di overlap.
   Lo storico deve precedere tale data. Il controllo non sostituisce le prove.
6. Eseguire **Verifica importazione** e leggere nuovi movimenti, duplicati,
   conflitti e importi delle sole righe nuove. Una correzione di una versione già
   annullata richiede una revisione esplicita delle differenze e un'approvazione
   riferita a quella precisa versione. Gli attivi in conflitto non sono sostituiti.

`NeedsEvidence` consente la preview e impedisce l'applicazione. La presenza di `Z`
e di un offset nell'export non costituisce da sola l'approvazione della semantica
temporale del provider. Non scegliere una policy per far scomparire il blocco.

**Analizza** è distinto da **Verifica importazione**: la prima fase legge gli
originali e mostra anche dati con prove mancanti; la seconda prepara un piano
solo con analisi applicabile, gruppi esercente, data/confine d'inizio POS e
riferimenti delle prove validi. **Applica** richiede quel piano ancora corrente
e una conferma. Cambiare file, opzioni, mapping o autorizzazione invalida il piano.

Valutare le prove per il formato selezionato. Gli orari commerciali locali del
legacy restano distinti dall'interpretazione Z/offset dei moderni: una prova
mancante sui moderni non approva né invalida automaticamente un lotto separato
legacy. Per un negozio mai usato prima, conservare l'attestazione tracciata e
usare il confine verificato della nuova attivazione; non inventare una data di
prima vendita storica. Il DB isolato vuoto non sostituisce l'attestazione.

## Applicazione, ricevute e annullamento

La conferma esegue un backup verificato prima della transazione. Durante la
manutenzione il carrello è conservato e le nuove operazioni sul carrello vengono
rifiutate. Attendere la conclusione o l'annullamento prima di riprendere la vendita.
La cancellazione cooperativa può attendere il completamento di una chiamata SQLite
già iniziata; non chiudere forzatamente il programma per simulare un annullamento.

Conservare il riferimento della ricevuta. Dopo un'interruzione consultare i lotti
e le ricevute prima di riprovare. La ricevuta originaria è immutabile e viene
mostrata separatamente dallo stato corrente del lotto. Un reimport identico non
acquisisce la proprietà dei movimenti già presenti.

Premere la conferma una sola volta e attendere la ricevuta. Un messaggio d'errore
successivo alla conferma non dimostra che la transazione sia stata annullata:
prima di riprovare aprire **Lotti importati**, aggiornare e consultare la ricevuta
originaria. Se il lotto valido è già presente, conservarlo; non usare **Annulla**
come pulizia. Dopo la chiusura e riapertura, un eventuale reimport identico deve
mostrare zero nuovi movimenti e duplicati coerenti, con totali invariati.
Durante l'analisi successiva, il prodotto ricarica i binding esercente già
registrati: conservarli se le prove non sono cambiate, evitando di creare un
nuovo gruppo per il solo scopo di ripetere gli stessi file.
Se la verifica cambia mentre la conferma è aperta, chiudere e verificare di nuovo
il piano; non confermare un riepilogo precedente.

**Annulla lotto** disattiva soltanto i movimenti posseduti da quel lotto. Mantiene
l'audit e le ricevute. Un lotto che ha soltanto osservato duplicati non li rende
nuovamente visibili dopo l'annullamento del loro proprietario. Per ripristinare
uno storico annullato usare una nuova analisi/importazione.

## Riepilogo, registro ed esportazione

Usare il filtro di origine per POS, storico o entrambi. I dati storici senza
operatore originale richiedono `register.view_all`. Il riepilogo richiede
`daily_close.view`, il registro `register.view`; senza `register.view_all`
giorno, storico, lista, dettaglio e CSV sono limitati all'operatore corrente.
La media scontrino riguarda esclusivamente gli scontrini POS positivi. Storni e
annullamenti mantengono il loro segno anche nel grafico. La copertura esplicita
distingue un sottoinsieme verificato da un totale completo o da dati indisponibili.

Per includere i nativi serve una policy temporale canonica approvata per il negozio.
Il pulsante **Fuso report** legge un file JSON con hash, versione, provenienza e
segmenti UTC; mostra l'anteprima e richiede evidenza e autorizzazione prima di
attivarlo con backup e audit. Richiede `register.import_history`. Il formato è quello di
`SalesReportTimePolicyRepository.SerializeApprovedFile`. Nessuna zona del PC viene
usata come fallback. Un file di esempio sintetico non approva la zona di un negozio.

Lista, dettaglio, aggregati e CSV si riferiscono allo stesso snapshot. Se cambia
il database, lo shop o l'autorizzazione, aggiornare il report. Il CSV contiene un
record di copertura separato dai movimenti; importare le colonne identificative
come testo in Excel per preservare zeri iniziali e identificativi lunghi.
L'esportazione protegge i campi testuali da formule e conserva i numeri firmati.
La sostituzione di un file esistente richiede consenso esplicito sul percorso.

## Qualificazione e limiti correnti

Lo stato effettivo della task e dei gate è nel
[registro di esecuzione](plans/ASUS-W7POS-019_GETNET_HISTORY/EXECUTION.md).
Input reali, prove temporali, binding del nuovo negozio e Windows 7 fisico sono
qualifiche distinte. Una build o una prova sintetica non le sostituisce. La
[scheda esterna](reports/ASUS_W7POS_019_EXTERNAL_QUALIFICATION.md) contiene la
procedura da eseguire sul pacchetto del candidato effettivamente consegnato.
