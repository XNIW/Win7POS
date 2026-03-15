# Report: Revert ShowHeader + fix chiusura/layout

## 1. Dialog con ShowHeader="False" ripristinato

Ripristinato il look bianco/lavanda senza barra viola superiore su **tutti i 25 dialog**:

- AboutSupportDialog
- ApplyConfirmDialog
- BoletaNumberDialog
- ChangePinDialog
- ChangeQuantityDialog
- DailyReportDialog
- DbMaintenanceDialog
- DiscountDialog
- ExportDataDialog
- FirstRunSetupDialog
- HeldCartsDialog
- ImportDataDialog
- ModernMessageDialog
- NewUserDialog
- OperatorLoginDialog
- OverrideAuthorizationDialog
- PrinterSettingsDialog
- ProductEditDialog
- ProductPriceHistoryDialog
- RefundDialog
- RoleEditDialog
- SalesRegisterDialog
- ShopSettingsDialog
- UserManagementDialog
- DeleteProductConfirmDialog

**DialogShellWindow:** Default di ShowHeader impostato su `false` in `PropertyMetadata`.

---

## 2. Dialog con chiusura corretta (Close/ESC/IsCancel)

| Dialog | Modifica |
|--------|---------|
| OverrideAuthorizationDialog | `Click="Cancel_Click"` su Annulla |
| FirstRunSetupDialog | `Click="Cancel_Click"` su Annulla |
| OperatorLoginDialog | `Click="Cancel_Click"` su Annulla |
| RoleEditDialog | `Click="Cancel_Click"` su Annulla |
| NewUserDialog | `Click="Cancel_Click"` su Annulla |
| DbMaintenanceDialog | `Click="Close_Click"` su Chiudi |
| DailyReportDialog | `Click="Close_Click"` su Chiudi |
| ProductPriceHistoryDialog | `IsCancel="True"` su Chiudi (già Click) |
| HeldCartsDialog | `IsCancel="True"` su Chiudi (usa Command) |
| RefundDialog | `IsCancel="True"` su Annulla (usa Command) |
| PrinterSettingsDialog | `IsCancel="True"` su Annulla (usa Command) |

I seguenti avevano già tutto corretto:
- ShopSettingsDialog, AboutSupportDialog (Click già presente)
- SalesRegisterDialog (Click + IsCancel)
- ExportDataDialog, BoletaNumberDialog, ChangeQuantityDialog (Click + IsCancel)
- DeleteProductConfirmDialog, ApplyConfirmDialog (Click)
- ModernMessageDialog (OK con IsDefault + IsCancel)
- UserManagementDialog (OnCloseClick)
- ProductEditDialog (Command + IsCancel)

---

## 3. Dialog con layout/dimensioni (da sessione precedente)

- **ShopSettingsDialog:** ScrollViewer sul corpo, pulsanti in footer fisso, 560×620
- **PrinterSettingsDialog:** ScrollViewer sul corpo, pulsanti in footer fisso, 600×680
- **AboutSupportDialog:** ScrollViewer sul corpo, pulsanti in footer fisso, 640×600
- **DbMaintenanceDialog:** ScrollViewer sul corpo, pulsante Chiudi in footer fisso, 680×520

---

## 4. File modificati

### XAML (ShowHeader=False)
Tutti i 25 dialog.

### XAML (Close/IsCancel)
- OverrideAuthorizationDialog, FirstRunSetupDialog, OperatorLoginDialog
- RoleEditDialog, NewUserDialog, DbMaintenanceDialog, DailyReportDialog
- ProductPriceHistoryDialog, HeldCartsDialog, RefundDialog, PrinterSettingsDialog

### Code-behind (handler)
- OverrideAuthorizationDialog.xaml.cs – `Cancel_Click`
- FirstRunSetupDialog.xaml.cs – `Cancel_Click`
- OperatorLoginDialog.xaml.cs – `Cancel_Click`
- RoleEditDialog.xaml.cs – `Cancel_Click`
- NewUserDialog.xaml.cs – `Cancel_Click`
- DbMaintenanceDialog.xaml.cs – `Close_Click`
- DailyReportDialog.xaml.cs – `Close_Click`

---

## 5. Build

```bash
dotnet build src/Win7POS.Wpf/Win7POS.Wpf.csproj -c Release /p:Platform=x86
```
