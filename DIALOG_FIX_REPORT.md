# Report: Fix funzionale dialog UI (regressioni usabilità)

## Inventario dialog modificati nel refactor UI

Tutti i dialog che estendono `DialogShellWindow` (chrome custom):

| Dialog | ShowHeader | UseModalOverlay | Layout fix |
|--------|------------|-----------------|------------|
| AboutSupportDialog | True | No | Sì: ScrollViewer + pulsanti fuori |
| ApplyConfirmDialog | True | Sì | — |
| BoletaNumberDialog | True | Sì | — |
| ChangePinDialog | True | Sì | — |
| ChangeQuantityDialog | True | Sì | — |
| DailyReportDialog | True | No | — |
| DbMaintenanceDialog | True | No | Sì: ScrollViewer + pulsanti fuori |
| DiscountDialog | True | Sì | — |
| ExportDataDialog | True | Sì | — |
| FirstRunSetupDialog | True | Sì | — |
| HeldCartsDialog | True | Sì | — |
| ImportDataDialog | True | Sì | — |
| ModernMessageDialog | True | Sì | — |
| NewUserDialog | True | Sì | — |
| OperatorLoginDialog | True | No | — |
| OverrideAuthorizationDialog | True | Sì | — |
| PrinterSettingsDialog | True | Sì | Sì: ScrollViewer + pulsanti fuori |
| ProductEditDialog | True | No | — |
| ProductPriceHistoryDialog | True | No | — |
| RefundDialog | True | No | — |
| RoleEditDialog | True | Sì | — |
| SalesRegisterDialog | True | Sì | — |
| ShopSettingsDialog | True | Sì | Sì: ScrollViewer + pulsanti fuori |
| UserManagementDialog | True | No | — |
| DeleteProductConfirmDialog | True | Sì | — |

## Problemi identificati e correzioni

### 1. Tasto X non funzionante
**Causa:** Tutti i dialog avevano `ShowHeader="False"` → nessun pulsante X in header.

**Correzione shared:** `ShowHeader="True"` per tutti i 25 dialog. L’header con il pulsante X è ora presente ovunque.

### 2. Contenuto tagliato / pulsanti fuori vista
**Causa:** Pulsanti inseriti dentro `ScrollViewer` → con overflow sparivano insieme al contenuto.

**Correzione:**
- **ShopSettingsDialog:** Row 2 con pulsanti Salva/Chiudi fuori da `ScrollViewer`.
- **PrinterSettingsDialog:** Row 2 con pulsanti Conferma/Annulla fuori da `ScrollViewer`.
- **AboutSupportDialog:** pulsanti Apri cartella dati/log, Copia info, Chiudi fuori da `ScrollViewer`.
- **DbMaintenanceDialog:** pulsante Chiudi fuori da `ScrollViewer`.

### 3. Layout sfasato / dimensioni insufficienti
**Correzione dimensioni:**
- ShopSettingsDialog: 560×620 (era 540×520)
- PrinterSettingsDialog: 600×680 (era 560×520), MaxWidth 720, MaxHeight 900
- AboutSupportDialog: 640×600, ResizeMode CanResize, Min 560×520
- DbMaintenanceDialog: 680×520 fissi
- SalesRegisterDialog: 1000×750, Min 900×640
- ImportDataDialog: 920×750
- ModernMessageDialog, ApplyConfirmDialog, DeleteProductConfirmDialog: MinHeight 240–300

### 4. ApplyAdaptiveDialogSizing in conflitto con UseModalOverlay
**Correzione shared:** In `WindowSizingHelper.ApplyAdaptiveDialogSizing` viene eseguito un early-return se il dialog usa `UseModalOverlay`, per evitare che le dimensioni dell’overlay vengano sovrascritte.

### 5. Pulsanti Chiudi senza handler
- **AboutSupportDialog:** aggiunto `Click="Close_Click"` al pulsante Chiudi.
- **ShopSettingsDialog:** aggiunto `Click="Close_Click"` al pulsante Chiudi.

## File modificati

### XAML
- `Pos/Dialogs/AboutSupportDialog.xaml`
- `Pos/Dialogs/ApplyConfirmDialog.xaml`
- `Pos/Dialogs/BoletaNumberDialog.xaml`
- `Pos/Dialogs/ChangePinDialog.xaml`
- `Pos/Dialogs/ChangeQuantityDialog.xaml`
- `Pos/Dialogs/DailyReportDialog.xaml`
- `Pos/Dialogs/DbMaintenanceDialog.xaml`
- `Pos/Dialogs/DiscountDialog.xaml`
- `Pos/Dialogs/FirstRunSetupDialog.xaml`
- `Pos/Dialogs/HeldCartsDialog.xaml`
- `Pos/Dialogs/NewUserDialog.xaml`
- `Pos/Dialogs/OverrideAuthorizationDialog.xaml`
- `Pos/Dialogs/PrinterSettingsDialog.xaml`
- `Pos/Dialogs/RefundDialog.xaml`
- `Pos/Dialogs/RoleEditDialog.xaml`
- `Pos/Dialogs/SalesRegisterDialog.xaml`
- `Pos/Dialogs/ShopSettingsDialog.xaml`
- `Pos/Dialogs/UserManagementDialog.xaml`
- `Import/ImportDataDialog.xaml`
- `Import/ModernMessageDialog.xaml`
- `Import/ApplyConfirmDialog.xaml`
- `Products/DeleteProductConfirmDialog.xaml`
- `Products/ExportDataDialog.xaml`
- `Products/ProductEditDialog.xaml`
- `Products/ProductPriceHistoryDialog.xaml`

### Code-behind
- `Pos/Dialogs/AboutSupportDialog.xaml.cs` – handler `Close_Click`
- `Pos/Dialogs/ShopSettingsDialog.xaml.cs` – rimosso `ApplyAdaptiveDialogSizing`, aggiunto `Close_Click`
- `Products/DeleteProductConfirmDialog.xaml.cs` – rimosso `ApplyAdaptiveDialogSizing`

### Shared
- `Infrastructure/WindowSizingHelper.cs` – early-return per `UseModalOverlay` in `ApplyAdaptiveDialogSizing`

## Verifica manuale consigliata su Win7

1. ShopSettingsDialog – X, contenuto completo, Salva/Chiudi visibili
2. PrinterSettingsDialog – X, contenuto completo, Conferma/Annulla visibili
3. AboutSupportDialog – X, pulsanti nella parte bassa visibili
4. DbMaintenanceDialog – X, Chiudi visibile
5. ImportDataDialog – X, contenuto ImportView visibile
6. SalesRegisterDialog – X, Chiudi e pulsanti laterali visibili
7. ModernMessageDialog – X, OK visibile
8. ApplyConfirmDialog – X, Annulla/Conferma visibili
9. DeleteProductConfirmDialog – X, Annulla/Elimina visibili
10. HeldCartsDialog – X, Chiudi visibile
11. DiscountDialog – X, Annulla/Conferma visibili
12. ExportDataDialog – X, pulsanti esportazione visibili
13. ProductEditDialog – X, Salva/Annulla visibili
14. DailyReportDialog – layout completo
15. UserManagementDialog – layout completo
16. RefundDialog – layout completo
17. Primo avvio (FirstRunSetupDialog)
18. Login operatore (OperatorLoginDialog)
19. Tutti gli altri dialog minori (Boleta, ChangeQty, ChangePin, NewUser, RoleEdit, OverrideAuth)

## Build

```bash
dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release /p:Platform=x86
```

Build target: net48 x86.
