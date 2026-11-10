# ASUS019 — continuazione client cloud, Execution isolata

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
