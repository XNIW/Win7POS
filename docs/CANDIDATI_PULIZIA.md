# Candidati a eliminazione / pulizia codice

Lista di file, cartelle e codice potenzialmente non usati o ridondanti, verificata il 2026-03-08. **Controllare prima di eliminare** (cercare riferimenti, fare build, test).

---

## 1. Cartella `_old/` (radice progetto)

| Elemento | Motivo |
|----------|--------|
| `_old/Pos.Tests/` | Progetto test non referenziato nella solution (`Win7POS.slnx` è vuoto / non include _old). |
| `_old/Pos.Core/` | Progetto "Pos.Core" sostituito da `Win7POS.Core`. |
| `_old/PosSolution.slnx` | Solution vecchia. |

**Azione suggerita:** Se non servi più per riferimento storico, puoi eliminare l’intera cartella `_old/`. Verificare che nessuno script o documentazione punti a `_old`.

---

## 2. Infrastructure – servizi non usati

| Elemento | Motivo |
|----------|--------|
| `src/Win7POS.Wpf/Infrastructure/INotificationService.cs` | Interfaccia mai referenziata (nessun `INotificationService` o `NotificationService` usato nel codice). |
| `src/Win7POS.Wpf/Infrastructure/NotificationService.cs` | Implementazione con `Current` e `SetDefault` mai chiamati. |

**Azione suggerita:** Se non prevedi notifiche toast/popup, puoi rimuovere entrambi i file.

---

## 3. ViewModels condivisi – classi orfane

| Elemento | Motivo |
|----------|--------|
| `src/Win7POS.Wpf/ViewModels/CartLineVm.cs` | Classe mai usata (nessun riferimento in tutto il repo). |
| `src/Win7POS.Wpf/ViewModels/ObservableObject.cs` | Usato **solo** da `CartLineVm`. Se elimini `CartLineVm`, anche `ObservableObject` diventa inutile. |

**Nota:** `ViewModels/RelayCommand.cs` **è usato** da `ShopSettingsViewModel` (signature `Action`, `Func<bool>`). Non eliminare.

**Azione suggerita:** Eliminare `CartLineVm.cs`. Se dopo la rimozione non resta nessun uso di `ObservableObject`, eliminare anche `ObservableObject.cs`.

---

## 4. PaymentViewModel – proprietà/comandi senza UI (opzionale)

Nella schermata Pagamento (`PaymentView`) non ci sono più il WebBrowser SII né il pulsante "Stampa PDF". Nel ViewModel restano:

| Elemento | Uso attuale |
|----------|-------------|
| `ShowSiiWeb` | Proprietà mai bindata in XAML. |
| `OpenSiiCommand` / `OpenSii()` | Comando mai bindato (prima apriva SII nel browser). |
| `GeneratePdfCommand` | Comando mai bindato (nessun pulsante "Genera PDF"). |
| `PrintPdfCommand` | **Usato** internamente da `StampaPdfAsync()` (es. conferma pagamento / stampa automatica). **Non rimuovere.** |

**Azione suggerita:** Solo pulizia opzionale: puoi commentare o rimuovere `ShowSiiWeb`, `OpenSiiCommand` e `OpenSii()` se non prevedi di riattivare l’area SII Web. `GeneratePdfCommand` puoi lasciarlo se in futuro aggiungi di nuovo un pulsante; altrimenti puoi rimuoverlo. **Non toccare** `PrintPdfCommand` / `StampaPdfAsync`.

---

## 5. Già rimosso (solo riferimento)

| Elemento | Stato |
|----------|--------|
| `PaymentDialog.xaml` / `PaymentDialog.xaml.cs` | **Già eliminati.** Pagamento usa solo `PaymentView` in `MainWindow`. |

---

## 6. Dialog e View – tutti usati

Verificati e **in uso** (nessun candidato a rimozione):

- ProductEditDialog, DeleteProductConfirmDialog, ProductPriceHistoryDialog  
- ExportDataDialog, BoletaNumberDialog, PinPromptDialog  
- DiscountDialog, ChangeQuantityDialog, RefundDialog  
- DailyReportDialog, DbMaintenanceDialog, AboutSupportDialog  
- PrinterSettingsDialog, ShopSettingsDialog  
- SalesRegisterDialog, HeldCartsDialog  
- PosView, PaymentView, ProductsView, ImportView  

---

## Riepilogo azioni consigliate

| Priorità | Azione |
|----------|--------|
| Alta | Eliminare cartella `_old/` se non serve più. |
| Alta | Eliminare `INotificationService.cs` e `NotificationService.cs` se non usi notifiche. |
| Media | Eliminare `CartLineVm.cs`; poi, se inutile, `ObservableObject.cs`. |
| Bassa | (Opzionale) In `PaymentViewModel`: rimuovere o commentare `ShowSiiWeb`, `OpenSiiCommand` / `OpenSii()`, eventualmente `GeneratePdfCommand` se non prevedi pulsante. |

Dopo ogni modifica: **build completo** e **test funzionali** (POS, Pagamento, Prodotti, Menu).
