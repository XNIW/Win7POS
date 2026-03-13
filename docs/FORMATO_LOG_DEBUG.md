# Formato log Win7POS – Guida al debug

## Path del file di log

- **Windows:** `C:\ProgramData\Win7POS\logs\app.log` (o path da variabile `WIN7POS_DATA_DIR`)
- **Override:** imposta `WIN7POS_DATA_DIR` per cambiare la cartella dati (log incluso)

## Formato riga

```
yyyy-MM-dd HH:mm:ss.fff [LEVEL][Source] Messaggio | Dettaglio eccezione (se presente)
```

### Esempi

```
2025-03-13 12:34:56.123 [INFO][PosWorkflowService] POS pay done: S001
2025-03-13 12:35:01.456 [ERROR][PosViewModel] POS VM pay failed | System.Data.SQLite.SQLiteException: database is locked at ...
2025-03-13 12:35:02.789 [WARN][App] EnsureIe11WebBrowser fallito (non critico): Access denied
```

### Livelli

| Livello | Significato |
|---------|-------------|
| `INFO` | Operazione completata con successo, stato utile |
| `WARN` | Avviso, problema non critico (es. fallback, configurazione mancante) |
| `ERROR` | Errore che richiede attenzione; include stack trace e inner exception |

### Source (componente)

| Source | Dove cercare |
|--------|--------------|
| `App` | Avvio applicazione, eccezioni globali (Dispatcher, AppDomain, Task) |
| `MainWindow` | Avvio finestra, FirstRun, Login, menu Utenti |
| `PosViewModel` | Carrello, pagamento, stampa, backup, dialogs POS |
| `PosWorkflowService` | Logica POS: add barcode, pay, refund, DB |
| `ProductsViewModel` | Catalogo prodotti, ricerca, export |
| `ProductsWorkflowService` | Export CSV/XLSX |
| `ImportWorkflowService` | Analisi e Apply import CSV/XLSX |
| `ProductPriceHistoryViewModel` | Storico prezzi, modifiche |
| `WindowsSpoolerReceiptPrinter` | Stampante, cassetto portamonete |
| `PaymentViewModel` | Schermata pagamento, PDF SII |
| `UiErrorHandler` | Errori generici da command/async |

## Come individuare la causa di un crash/errore

1. **Apri `app.log`** e vai alla fine (ultimi eventi).
2. **Cerca `[ERROR]`** – ogni errore ha contesto + stack trace.
3. **Identifica il Source** – es. `[PosViewModel]` → problema in carrello/pagamento.
4. **Leggi l’operazione** – es. `POS VM pay failed` → errore durante pagamento.
5. **Controlla lo stack trace** – indica file e riga del codice.

### Esempio di errore completo

```
2025-03-13 14:22:33.100 [ERROR][PosViewModel] POS VM pay failed | System.InvalidOperationException: Sale già completata
   at Win7POS.Core.Pos.PosWorkflowService.CompleteSaleAsync(...)
   at Win7POS.Wpf.Pos.PosViewModel.PayAsync(...)
 ---INNER--- System.Data.SQLite.SQLiteException: UNIQUE constraint failed: sales.code
   at ...
```

- **Componente:** PosViewModel
- **Operazione:** pay (pagamento)
- **Causa:** InvalidOperationException / SQLite UNIQUE constraint

### Eccezioni non gestite

- **`DispatcherUnhandledException`** – errore nel thread UI.
- **`AppDomain.UnhandledException`** – errore fuori da WPF (IsTerminating=true = chiusura app).
- **`TaskScheduler.UnobservedTaskException`** – eccezione in `Task` non awaitato.

Cercando questi testi nel log puoi capire dove è fallita l’applicazione.

## Ricerche utili (grep/text)

```bash
# Tutti gli errori
grep "\[ERROR\]" app.log

# Errori di un componente
grep "\[ERROR\]\[PosViewModel\]" app.log

# Ultimi 50 errori
grep "\[ERROR\]" app.log | tail -50

# Cercare per messaggio eccezione
grep "SqliteException" app.log
grep "FileNotFoundException" app.log
```

## Note

- Il log **non viene mai rimosso** automaticamente; ruota manualmente se diventa troppo grande.
- Il logging **non può provocare crash** – se scrivere nel log fallisce, l’errore viene ignorato.
- Per **test o ambienti diversi**, usa `WIN7POS_DATA_DIR` per avere un log separato.
