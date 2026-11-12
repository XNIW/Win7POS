# ASUS019 — handoff al coordinatore mobile TASK-146

Aggiornamento10novembre: il chiarimento bilaterale e lo storico incrementale sono
stati trasmessi nella stessa nota TASK146/PR29, commento6722613449; parte storica
conservata byte per byte e aggiornamento riletto. Contratto v2 ancora WIP locale,
non distribuito né consumabile come API stabile. Matrice e gap canonici in
CLOUD-CONTINUATION.md: shadow/intenti e origini articolo esistenti riusati,
local/frozen/web, replay ricevuta distinto da prima consegna tardiva, nessuna
replica di consultazione riesegue stock/pagamento/fiscale/stampa/cassetto.
Reader nativo inter-postazione e convergenza autentica per dominio restano aperti.
Nessun test Windows qualifica iOS/Android o avvia un'Execution mobile concorrente.
Cart136/149c978 è separato: CI/Security/WPF/Release verdi,41/40/34file verificati;
Integrated12min+soak60min42897760117 in corso;
installato2ad42ca. Cloud135/151 ancora in implementazione, nessun rollout/DONE.
Riferimento: https://github.com/XNIW/iOSMerchandiseControl/pull/29#issuecomment-6722613449

Questo è un raccordo della stessa Execution Windows; non apre una execution mobile
concorrente e non certifica iOS/Android. Coordinatore: iOS TASK-146/PR29.

Contesto realmente letto il10 novembre2026:

- iOS PR29 head08294ec2d124469640b1489176193aa2d62225d8: task canonica
  `docs/TASKS/TASK-146-mobile-consolidation-domain-readiness-pos.md`,
  `ContentView.swift`(blobeb80b91c), `LocalRootPresentationState.swift`(309d1959),
  `POSRead/POSReadViewModel.swift`(e01eebab). La descrizione PR riporta candidato
  a8cde1a2 con B2/Root9 in corso; non attribuire a quel candidato questa lettura.
- Android main, task144(blob73d2fcf3), assorbita senza DONE nella TASK146 iOS;
  `ui/navigation/NavGraph.kt`(8aa7b754). Checkout locale4b74ed5 storico: non assunto
  quale stato remoto attuale. Nessuna compilazione/test mobile qui eseguita.

Riscontro statico: iOS mantiene tabContent fuori da AppSyncRootHost, il publisher
verifica presentationID, LocalRootPresentationState limita ricevute al manifest
corrente e POSRead richiede autorità specifica prima di esporre snapshot. Android
costruisce i consumer DB solo con LocalStoreAvailability.Ready, conserva una shell
con identità stabile e nasconde input/dati della route precedente a cambio scope.
Non inferire da questi punti che tutti i domini o i percorsi offline siano provati.

Principio da mantenere nel contesto mobile: disponibilità locale qualificata,
postazione/navigazione, ultima copia pubblicata e validità della singola azione
sono decisioni diverse. Un retry ordinario non deve rimontare schermate, cancellare
input o rendere obbligatoria una pagina sync. Errori e stato si riferiscono al
dominio interessato; revoca/cambio owner-shop, corruzione e dati non qualificati
restano bloccanti dove richiesto. Conservare motore unico, ownership/cancellazione,
cache/outbox durevole, cursore+dati atomici e guardie di pubblicazione esistenti.
POSRead mobile è consultazione remota: non dedurre un permesso di vendita o una
cache offline autorizzata dalla sola possibilità di usare inventario locale.

Matrice da qualificare dal coordinatore sul proprio candidato: cold/warm ingresso,
richiesta sync sospesa, dati locali disponibili e remoto503/offline, creazione
locale autorizzata, editor/report aperti durante pubblicazione, preservazione di
input/scroll/selezione, logout/shop/revoca durante risposta, riapertura e rilascio
risorse. Riutilizzare budget e fixture canonici TASK146, senza trasferire PASS
Windows, allargare deadline o sovrapporre owner/esecutori.

Windows: vero carrello/scanner con remoto vuoto/pending implementato;69 assert
pubbliche con supervisor reale, commit locale/manuale e Core175 mirati PASS di
sviluppo. Qualifica congelata/pacchetto/installazione nuovi ancora aperti. Draft
cloud135/Admin151 distinti, nessun merge/deploy implicito. Riferimenti aggiornati
in PLAN/EXECUTION/report e nella PR136.

Checkpoint21:00Z: la stessa nota6722613449 è aggiornata con gate condiviso33test,
stati/ledger67test, CloudView04 e smoke8/52/required50PASS, SQL isolato76file/4694
assert e foundation1484PASS/7SKIP. E2E TASK094 precedente FAIL auth_denied distinto
dai gate SQL; nuovo API v2 non aperto. Storia del commento conservata byte per byte.
Non è autorizzazione a consumare un endpoint futuro o prova di app mobile.
