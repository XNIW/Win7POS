# Importazione dello storico carta

Guida del candidato ASUS-W7POS-019 in qualifica. L'installato attestato resta
248e689/v6 finché non sono completati gate e consegna del successore. Codice,
risultati del candidato e comportamento installato sono prove distinte.

La funzione importa i movimenti Getnet nello storico separato, locale o condiviso
secondo l'autorità verificata del negozio. Non crea vendite
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

Dopo accesso normale valido e inizializzazione locale, si apre direttamente la
vera postazione POS con carrello e barcode, anche se il catalogo è vuoto o ancora
in download. Login, lease, shop e integrità devono essere validi. Catalogo, outbox
e ordini si aggiornano automaticamente in background; Sync Center resta una
sezione diagnostica volontaria. Nessun popup storico obbligatorio al login.

Un primo download pendente dà un riscontro specifico sui codici non disponibili;
un codice realmente sconosciuto riusa la creazione rapida con CatalogEdit o
supervisore. Manuali CLP e articoli locali con provenienza verificata seguono i
permessi e i controlli economici fino al commit. Mostrare il carrello non concede
vendite su vecchi prodotti diagnostici: dopo empty autorevole il catalogo remoto
richiede una nuova sostituzione completa qualificata. Un tentativo lento/fallito
conserva l'ultimo snapshot operativo valido e non distrugge input, selezione,
carrello o report. Prezzi e disponibilità vengono rivalidati al pagamento, senza
cambiare silenziosamente i totali dell'operatore.

Menu → Riepilogo/registro giornaliero → Cronologia → Importa storico è il percorso
volontario. Close, X ed Escape chiudono la secondaria e ritornano allo stesso
contesto. Permessi storici e policy temporale del report nativo sono indipendenti
dal catalogo e dalla presenza del carrello. Autorizzazione ordinaria POSv6 e PIN
Minimondo non hanno scadenza ordinaria; cambio manuale e revoca nota restano
verificati. Le ammissioni privilegiate rimangono brevi e legate all'operazione.
Questo percorso non sostituisce prove esercente, tempo e overlap.

Dal riepilogo aprire **Importazione storico**. Il permesso
`register.import_history` abilita l'importazione; `register.cancel_history` abilita
l'annullamento. La consultazione dei lotti richiede anche la visibilità di tutti
gli operatori. I nuovi permessi non vengono assegnati automaticamente a cassieri
o responsabili.

1. **Scegli file**: selezionare gli originali. Intestazioni e contenuto determinano
   il formato; il nome non certifica periodo, negozio o valuta.
2. **Verifica dati**: controllare vendite, annullamenti e pagamenti rifiutati dal
   circuito. Questi ultimi sono esclusi dagli incassi e distinti dagli errori
   di lettura. CLP esplicito nel moderno deriva dal file. Il netto sconosciuto
   rimane sconosciuto. Requisiti e rimedio precedono i dettagli espandibili.
3. **Verifica importazione**: leggere nuovi, già presenti, esclusi, conflitti e
   totali delle sole righe nuove. Il POS rilegge le qualificazioni applicabili;
   nello storico condiviso la review autorevole è quella del server. Una
   preparazione pendente ha un esito esplicito: ripremere la verifica consulta
   la stessa operazione, senza confermarla.
4. **Conferma importazione → Risultato**: confermare una sola volta il piano
   corrente e conservare la ricevuta. Un file già registrato e ancora attivo
   dà un no-op senza seconda scrittura. La sola ricevuta o lo SHA non provano
   che un proprietario annullato sia ancora attivo.

Valuta legacy, esercente, tempo moderno e confine d'avvio POS sono qualificazioni
documentate nella console autorizzata. Testo libero e checkbox non conferiscono
questa autorità. Le prove valgono solo per shop/esercente, file/hash, formato,
parser/versione, intervallo e stato non revocato. I parser `getnet-ooxml-v1`
locale e `getnet-ooxml-web-v1` server sono distinti: non si deduce la validità
per uno dalla prova dell'altro.

Un Master effettivo o delegato esplicito registra il documento in **Console →
Pagamenti storici → Qualificazioni**. Un badge o il codice1001 non assegnano
privilegi tecnici. Origini, migrazione, freeze, cutover e recovery sono nella
stessa gestione autorizzata; l'import ordinario rimane disponibile dal POS.

`NeedsEvidence` consente la preview e impedisce l'applicazione. La presenza di `Z`
e di un offset nell'export non costituisce da sola l'approvazione della semantica
temporale del provider. Non scegliere una policy per far scomparire il blocco.

**Verifica dati** legge gli originali anche con prove mancanti. **Verifica
importazione** prepara il piano o consulta la preparazione server. **Conferma**
richiede review e autorità correnti: cambio di file, policy, documento, sessione
o autorità di scrittura invalida il piano. Un vecchio callback non pubblica sotto
la nuova selezione. Le schede mantengono stati indipendenti e terminali.

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
Nel percorso condiviso intento e chiave di commit sono persistiti prima della
richiesta. Dopo un ACK perso la verifica cerca la ricevuta della stessa
operazione; un nuovo accesso dello stesso principale non cambia quella chiave.
La ricevuta esistente non richiede un altro backup o una rilettura dell'originale.
Le nuove scritture richiedono invece fonti verificate e conferma corrente.
Se la verifica cambia mentre la conferma è aperta, chiudere e verificare di nuovo
il piano; non confermare un riepilogo precedente.

**Annulla lotto** disattiva soltanto i movimenti posseduti da quel lotto. Mantiene
l'audit e le ricevute. Un lotto che ha soltanto osservato duplicati non li rende
nuovamente visibili dopo l'annullamento del loro proprietario. Per ripristinare
uno storico annullato usare una nuova analisi/importazione.

## Riepilogo, registro ed esportazione

La vista ordinaria compone le componenti disponibili senza selettore tecnico
Origin. Se manca la policy temporale POS, mostra lo storico carta e dichiara
indisponibili scontrini e contanti POS. I dati storici senza
operatore originale richiedono `register.view_all`. Il riepilogo richiede
`daily_close.view`, il registro `register.view`; senza `register.view_all`
giorno, storico, lista, dettaglio e CSV sono limitati all'operatore corrente.
La media scontrino riguarda esclusivamente gli scontrini POS positivi. Storni e
annullamenti mantengono il loro segno anche nel grafico. La copertura esplicita
distingue un sottoinsieme verificato da un totale completo o da dati indisponibili.

Per includere i nativi serve una policy temporale canonica approvata per il negozio.
La configurazione tecnica è amministrata separatamente; il POS non propone
l'editor del fuso nel riepilogo ordinario. Nessuna zona del PC è usata come
fallback. Un esempio sintetico non approva la zona di un negozio.

La tabella contiene giornate con movimenti registrati. Gli altri giorni sono
copertura sconosciuta, non zeri certificati. I conteggi descrivono il filtro:
2577movimenti dal12giugno e25947nell'intero lotto non dimostrano una perdita.
Saldo prima delle commissioni e netto di regolamento Getnet rimangono distinti.
Nessuno scontrino, contante o articolo storico viene inventato per completare
le componenti mancanti.

Lista, dettaglio, aggregati e CSV si riferiscono allo stesso snapshot. Se cambia
il database, lo shop o l'autorizzazione, aggiornare il report. Il CSV contiene un
record di copertura separato dai movimenti; importare le colonne identificative
come testo in Excel per preservare zeri iniziali e identificativi lunghi.
L'esportazione protegge i campi testuali da formule e conserva i numeri firmati.
La sostituzione di un file esistente richiede consenso esplicito sul percorso.

## Qualificazione e limiti correnti

Al14novembre il successore resta in Execution: resolver63/63, delta portable
226/226 e primo giro guidato esclusivo61check con smoke8/52 PASS. Ulteriore
client condiviso, gate del candidato congelato e installazione sono ancora da
consolidare. La baseline usa lo stesso archivio originale verificato sul server:
un timeout di preparazione non è una ricevuta di pubblicazione. Ultima copia e
ledger sono conservati; ACK e selezione della replica richiedono convergenza
canonica. Continuità del servizio e successo della sincronizzazione sono distinti.

<details>
<summary>Checkpoint9novembre conservato come storia</summary>

Checkpoint9novembre2026: codice/pacchetto a8286c6 qualificato e installato,
versione1.0.0-dev.a8286c669a7c. La conferma mantiene il piano stabile, impedisce
una seconda attivazione e richiede nuova review se i dati cambiano mentre il
popup è aperto. La prima failure postcommit resta conservata: una schermata
d'errore non prova che la transazione sia stata annullata. Prima di ripetere
un'importazione consultare ricevuta e lotto; non annullare un lotto valido
come cleanup. Il collegamento Start Menu usa il profilo Minimondo conservato
NEW_SHOP_019 tramite configurazione utente già verificata.

Gli originali sette XLSX sono stati analizzati nella UI; soltanto i quattro
legacy con prove A/B/CLP valide sono committati e riconciliati. I tre moderni
rimangono esclusi dall'Apply finché manca C pertinente: il solo suffisso Z non
risolve la semantica dell'export. La copertura descrive record osservati, con
giugno parziale. Il report solo storico non richiede la policy POS nativa dei
report misti. LIVE_REOPEN_PENDING sul successore; qualifica Win7 fisica e
periferiche NOT_RUN/BLOCKED_TARGET, CSV ingresso BLOCKED_INPUT.

</details>

Lo stato effettivo della task e dei gate è nel
[registro di esecuzione](plans/ASUS-W7POS-019_GETNET_HISTORY/EXECUTION.md).
Input reali, prove temporali, binding del nuovo negozio e Windows 7 fisico sono
qualifiche distinte. Una build o una prova sintetica non le sostituisce. La
[scheda esterna](reports/ASUS_W7POS_019_EXTERNAL_QUALIFICATION.md) contiene la
procedura da eseguire sul pacchetto del candidato effettivamente consegnato.
