# Analisi progetto Win7POS – Punti critici e miglioramenti

## Fix implementati (ultimo aggiornamento)

### 1. Query N+1 ImportDiffer
- Aggiunto `GetByBarcodesAsync(IEnumerable<string>)` a `IProductSnapshotLookup`, `ProductRepository`, `ProductSnapshotLookupAdapter`.
- ImportDiffer ora esegue una sola query batch invece di N query.

### 2. Thread.Sleep → Task.Delay
- `WindowsSpoolerReceiptPrinter`: `TryPrintWithRetryAsync` usa `await Task.Delay(300)`.
- Aggiunte costanti `RetryDelayMs`, `ThermalPaper80mmMin/Max`.

### 3. FileLogger – lock per scritture concorrenti
- `lock (_writeLock)` su `File.AppendAllText`.

### 4. ImportApplier – catch con dettagli
- `result.Errors.Add($"{barcode}: {ex.Message}")` per diagnostica.
- Aggiunta proprietà `Errors` a `ImportApplyResult`.

### 5. ProductDbImporter – stack trace
- `ex.ToString()` al posto di `ex.Message` negli errori.

### 6. ConfigureAwait(false) in ProductRepository
- Aggiunto a tutti i metodi async.

### 7. PosViewModel duplicato rimosso
- Eliminato `ViewModels/PosViewModel.cs` (non usato).

### 8. INotificationService
- Nuova interfaccia e `NotificationService` per messaggi.
- Messaggio avvio App in italiano.

### 9. DiscountDialog – blocco UI eliminato (precedente)
- **Problema**: `GetAwaiter().GetResult()` bloccava il thread UI durante l’applicazione dello sconto.
- **Soluzione**: callback async (`Func<int,bool,string,Task>`) e `AsyncRelayCommand` con `await`.

### 10. SQL injection in Program.cs (precedente)
- **Problema**: `$"SELECT ... WHERE saleId = {saleId}"` concatenava parametri nella query.
- **Soluzione**: query parametrizzata con `@saleId` e `ScalarLongAsync` esteso per accettare parametri.

---

## Priorità alta

### async void e gestione errori
- **File**: `PosViewModel.cs`, `MainWindow.xaml.cs`, vari ViewModel.
- **Problema**: `async void Execute()` e `async void OnLoadedAsync` non permettono di propagare eccezioni.
- **Suggerimento**: try/catch nei command async o evento `CommandError` centralizzato per feedback utente.

### Query N+1 in ImportDiffer
- **File**: `src/Win7POS.Core/Import/ImportDiffer.cs:23-43`
- **Problema**: una query `GetByBarcodeAsync` per ogni riga CSV.
- **Suggerimento**: pre-caricare prodotti in batch o metodo `GetByBarcodesAsync(IEnumerable<string>)`.

---

## Priorità media

### Dependency injection
- **File**: `PosWorkflowService.cs`, `PosViewModel`, `ImportWorkflowService`
- **Problema**: `new` diretto di repository, printer, opzioni.
- **Suggerimento**: introdurre DI container (es. Microsoft.Extensions.DependencyInjection).

### PosViewModel duplicato
- **File**: `Pos/PosViewModel.cs` vs `ViewModels/PosViewModel.cs`
- **Suggerimento**: mantenere una sola implementazione e rimuovere l’altra.

### FileLogger – scritture concorrenti
- **File**: `Infrastructure/FileLogger.cs`
- **Problema**: `File.AppendAllText` senza lock.
- **Suggerimento**: lock per scritture o logger strutturato (es. Serilog).

### MessageBox vs notifiche
- **File**: `PosViewModel.cs` e altri
- **Problema**: molti `MessageBox.Show` per errori; nessun sistema di notifiche.
- **Suggerimento**: servizio di notifiche (snackbar/toast) per messaggi non critici.

---

## Priorità bassa

### ConfigureAwait(false) nei Data
- **File**: `ProductRepository`, `SaleRepository`, ecc.
- **Problema**: mancanza di `.ConfigureAwait(false)` nelle librerie.
- **Suggerimento**: aggiungere dove non serve tornare sul contesto di sync.

### Costanti per magic numbers
- **File**: `WindowSizingHelper`, `ProductsViewModel`, `WindowsSpoolerReceiptPrinter`
- **Suggerimento**: `const` o classi `AppSettingKeys`, `DialogSizes`, ecc.

### Thread.Sleep in printer
- **File**: `WindowsSpoolerReceiptPrinter.cs:43`
- **Suggerimento**: sostituire con `await Task.Delay(300)`.

---

## UI/UX

| Area | Suggerimento |
|------|--------------|
| Feedback | Verificare che tutte le operazioni lunghe usino `IsBusy` in modo coerente |
| Messaggi | Localizzazione coerente (evitare mix cinese/italiano) |
| StatusMessage | Eventuale auto-reset dopo alcuni secondi |
| Accessibilità | Aumentare contrasto, supportare ridimensionamento font |
