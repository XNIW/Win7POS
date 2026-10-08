# WIN7POS_POST_PR120_RESIDUAL_CLOSEOUT

Continuazione della [PR120](2026-10-08_WIN7POS_TARGETED_COMPLETION_RESULT.md).
Baseline main/origin/main verificata e pulita:
`0c13d8d10dc250650f775d71027156c198d4f01a`. Worktree precedente pulito riusato
con un nuovo branch; gli altri checkout e le evidenze precedenti sono preservati.
WPF net48/x86, Core/Data netstandard2.0, SDK10.0.301 e target Win7 SP1 invariati.

## R1: import, storico e confini monetari

Riproduzione reale .NET/Data prima della patch: import SQLite, prodotto,
`product_price_history`, lettore DTO e outbox configurata. Dodici casi, otto
fallimenti: `2147483648` diventa `-2147483648` nello storico, `4294967296`
diventa0, Int64.MaxValue diventa-1. Anche il prezzo precedente alto si corrompe
quando il nuovo prezzo è piccolo. Prodotto e JSON restavano esatti. Il test
baseline e i TRX sono conservati nel bundle privato `r1/`.

I binding e DTO dello storico diventano long/long?, senza migrazione: SQLite
INTEGER conserva già Int64. Comando retail singolo e doppio, writer locali,
lettori Dapper, riconciliazione, visualizzazione e workbook mantengono la
precisione. Il workbook scrive prezzi a64bit come testo, evita il limite
numerico Excel e conserva old_price NULL. Le colonne storiche opzionali del
formato canonico non vengono confuse con supplier/category; fallback legacy
G/H invariato. Review indipendente ha trovato questo problema prima della
suite finale e la regressione include supplier/category non vuoti.

| Confine effettivo | Dominio e comportamento |
| --- | --- |
| Storage/import locale retail | 0..9223372036854775807, verifica prima del ToEven CLP esistente. |
| Storage purchase | 0..2147483647, invariato. |
| Import sincronizzato Admin v1 | 0..999999999 per entrambi i prezzi. Fonte: `nonNegativeNumber` in Admin `catalog-import-sync.ts`, SHA02ea44b95d4a05baddbf46f24251f0d5f0dea294. Valori maggiori diventavano null nel receiver. |
| Ingresso manuale article esistente | Retail Int32, invariato. Edit purchase-only con retail esistente alto rifiutato esplicitamente, senza wrap o perdita del draft. |

Preview WPF, builder outbox e validator dei payload pendenti applicano il
limite Admin. Errore localizzato con riga/campo/limite prima di backup o
scritture; payload già persistiti incompatibili rimangono bloccati localmente,
senza invio o modifica del contenuto. Nessuna estensione API. Il dominio locale
Int64 resta supportato; non viene presentato come interoperabile con Admin.

Prove:0,999999999,Int32.MaxValue,+1,4294967296,Int64.MaxValue e primo valore
fuori dominio, insert/update con old_price alto. Uguaglianza prodotto/storico/
DTO e, nel dominio Admin, stringa canonica outbox. Input rifiutati conservano
draft e zero modifiche. Smoke net48 legge lo storico nella UI e verifica
l'edit manuale rifiutato; round trip XLSX con entrambi i lettori e reimport
SQLite. Rollback/fault/retry riusano le regressioni esistenti PR120.
Nessuna copia business autorizzata è stata fornita per un audit storico;
nessun prezzo storico è stato ricostruito o riparato.

## R2: intento di apertura del display

Prima della patch, la vera sequenza MainWindow `InitializeAsync→Attach`
apriva una Window WPF con AutoOpen=false. Riproduzione con PosViewModel,
publisher pubblico, Window e focus TextBox reali; topologia sintetica.
AutoOpen=true e i precedenti scenari W5/W6 passavano già. È un residuo
preesistente; nessuna attribuzione causale nuova alla PR120.

Un solo stato aggiuntivo registra l'intento runtime di apertura. AutoOpen
iniziale, OpenDisplay e applicazione esplicita impostazioni lo attivano;
snapshot/topologia/debounce/lock/unlock aggiornano il contenuto memorizzato
senza inventare un'apertura. Preview temporanea non attiva il display runtime.
Manual close, minimize, monitor mancante e reconnect bloccato restano distinti.

Dopo:9/9 gruppi completion PASS, inclusi AutoOpen on/off sul percorso completo,
snapshot iniziale e successivo, manual open, contenuto aggiornato, minimize/
restore, topology/lock prima dell'intento, preview/expiry/apply esplicito,
disable e chiusura nativa. W5/W6 con ReopenWhenMonitorReturns=false e polish
storico PASS. Review indipendente senza finding aperti. Hotplug fisico e Win7
non dedotti dalla topologia di test.

## Validazione e integrazione

La ricevuta post-merge esterna registra il candidato validato, i risultati
canonici locali, PR/head/merge, CI/Security/ReleasePack esatti e l'unico download
verificato. Non viene aggiunto un commit solo per inserire il proprio SHA.
Le misure Asus PR120 restano Win11/.NET4.8.1; non attestano picco memoria o
prestazioni Win7 del pacchetto corretto. Nessun nuovo soak Final60 richiesto.

## Preparazione operativa

Profilo `asus-staging` DPAPI CurrentUser v2 valido, ACL/decrypt PASS, non scaduto;
binding0f190ed7e5dfc3ee222b18c047a9854da520fc219feeefe14a9b4cf8d07a5596.
Tre contratti Admin/POS byte-identici; SDK e toolchain canonica del runner
preparati. Host staging merchandise-control-admin-web-staging.merchandise-control-admin-web.workers.dev,
scope qa-articles-zero-sales, salesAllowed=false. Nessuna credenziale nel report.

La chat Mac **Completa integrazione e collaudi** è stata incaricata delle
letture TEST ordinarie attuali di deployment/runtime, schema applicato,
contatori e scope. READY assente sul sorgente non è diagnosi backend. Nessun
runId riservato prematuramente e nessuna readiness sintetica pubblicata.
Bundle e comandi preparati: `readiness/prepared-inputs.json` e
`readiness/WIN7_AND_LIVE_HANDOFF.md` nelle evidenze Asus. La ricevuta finale
aggiunge l'esito Mac attuale e il binding effettivo del pacchetto.

Ricevuta Mac corrente8ottobre18:04:25UTC, SHA256
db57529ab21d672170fc47093732ab1d60a67ba4ba43e2431e90229babf68662:
deployment f726de06-fb79-46f5-a1b3-1d35fdc9de69, versione
22107a6f-f515-44c4-8392-a8e5653ff0b8 al100%; binding TEST, shop/membership
attivi e GET pubblico200. Schema applicato osservato: registry155,
latest20261002180757, digest identity corrispondenti. Le tre fixture sono
prove sorgente, separate dal commit runtime realmente servito, ancora non
attestato. Mancano HTTP503/CPU/memoria, manifest/preflight articoli,
activeQaRuns e qaScopeClean. Registry immagini con5cleaned/1provisioned senza
lease valide non dimostra clean articoli. Otto riferimenti originali verificati
dal maintainer. Query Analytics esistente e azione su dashboard autenticata
preparate; nessuna estrazione OAuth custom, cleanup o READY eseguiti.

POS↔Admin può partire appena le attestazioni mancanti sono attuali e la vera
readiness è pubblicata con autorizzazione. Mobile non è un prerequisito. C08
richiede scope economico TEST separato; C12 richiede scope restore e dati TEST
recuperabili. Per mobile: modifica→propagazione→riapertura→seconda modifica→
propagazione con identità/revision/opId e latenza; nessun aumento polling.
Android corrente MerchandiseControlSplitView/Room22 e iOS sono riferimenti
verificati in lettura; nessuna modifica a cashregistersystem.

Host disponibile osservato: Win11, un monitor, coda Canon GX6100 e stampanti
virtuali. Win7 remoto, barcode scanner, Xprinter e hotplug fisico non attestati.
Il pacchetto corretto, la ricevuta e la checklist
[Win7/hardware](../QA/WIN7POS_PHYSICAL_WIN7_HARDWARE_ACCEPTANCE_2026-08-09.md)
formano la consegna al tecnico. Il payload include check-win7-prereqs.ps1;
non serve SDK.NET10 sul PC di cassa. Installazione/upgrade preservando dati,
offline, scanner/focus, TEST sale/refund/reprint, import, display, restart/
reconnect e backup/restore TEST richiedono osservazione sul target.
Accepted dal canale stampa non attesta carta prodotta.
