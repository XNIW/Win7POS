# Win7POS UI runtime matrix — 2026-07-17

## Certification scope

- Source branch: `qa/win7pos-ui-architecture-runtime-20260716-215939`.
- Feature base: `74780e98d74a53bcbc449eeed3558049026a5480`.
- Public staging only: `https://merchandise-control-admin-web-staging.merchandise-control-admin-web.workers.dev`.
- Isolated synthetic data root; no production database, token, cookie or credential was used.
- Host display: 2880×1800 physical, 1440×900 logical, 200%, one monitor.
- `PASS_COMPUTER_USE` means the surface was actually rendered and inspected through Computer Use.
- `PASS_AUTOMATED` means real WPF XAML was exercised by the QA-only harness; it is not a visual PASS.
- `BLOCKED_AUTHENTICATED_STAGING` means the public server was reachable, but no valid authorized staging account was available.

## Dynamic surface inventory

The current repository contains 39 interactive surfaces: one `Window`, 32
`DialogShellWindow` dialogs and six reachable `UserControl` views. `DailyReportDialog`
and `UserManagementDialog` are legacy dialog classes whose commands are not bound by
the normal shell, so they require the QA harness. All other surfaces have a normal
application call site.

| ID | Surface / XAML | Type | Entrypoint and owner | Permission / data | States required | Normal | Harness required | Runtime result |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| UI-001 | `MainWindow.xaml` | Window | App startup; desktop work area | none; startup settings | first frame, maximize/minimize/close | YES | NO | PASS_COMPUTER_USE; maximize-only PASS_AUTOMATED |
| UI-002 | `Pos/PosView.xaml` | UserControl | sale-safe shell tab; MainWindow | authenticated operator; catalog/cart | empty, populated, status, scanner focus | YES | NO | BLOCKED_AUTHENTICATED_STAGING |
| UI-003 | `Pos/PaymentView.xaml` | UserControl | checkout; MainWindow | operator; payable cart | cash/card/error/disabled | YES | NO | BLOCKED_AUTHENTICATED_STAGING |
| UI-004 | `Products/ProductsView.xaml` | UserControl | Products menu; MainWindow | product permission; catalog | empty, populated, filters, busy | YES | NO | BLOCKED_AUTHENTICATED_STAGING |
| UI-005 | `Pos/DailyReportView.xaml` | UserControl | Daily report menu; MainWindow | report permission; sales | empty, populated, history/export | YES | NO | BLOCKED_AUTHENTICATED_STAGING |
| UI-006 | `Pos/UserManagementView.xaml` | UserControl | Users/Roles menu; MainWindow | administrator; users/roles | list, selection, validation | YES | NO | BLOCKED_AUTHENTICATED_STAGING |
| UI-007 | `Pos/Dialogs/SettingsHubDialog.xaml` | DialogShellWindow | Settings command; MainWindow | settings permission | cards, scroll, keyboard | YES | NO | PASS_AUTOMATED lifecycle 20×; visual BLOCKED_AUTHENTICATED_STAGING |
| UI-008 | `Pos/Dialogs/LanguageSettingsDialog.xaml` | DialogShellWindow | Settings Hub; hub owner | settings permission | four languages, validation | YES | NO | BLOCKED_AUTHENTICATED_STAGING |
| UI-009 | `Pos/Dialogs/SyncCenterDialog.xaml` | DialogShellWindow | sync pill; MainWindow | trusted session; sync state | idle/offline/running/retry/blocked/repair | YES | NO | PASS_AUTOMATED lifecycle 20×; visual BLOCKED_AUTHENTICATED_STAGING |
| UI-010 | `Pos/Dialogs/ShopSettingsDialog.xaml` | DialogShellWindow | Settings Hub; hub owner | administrator; shop snapshot | populated/error/save | YES | NO | BLOCKED_AUTHENTICATED_STAGING |
| UI-011 | `Pos/Dialogs/PrinterSettingsDialog.xaml` | DialogShellWindow | Settings Hub; hub owner | printer settings | empty/populated/test/error | YES | NO | BLOCKED_AUTHENTICATED_STAGING |
| UI-012 | `Pos/Dialogs/DbMaintenanceDialog.xaml` | DialogShellWindow | Settings Hub; hub owner | administrator; local DB | idle/busy/error/confirmation | YES | NO | BLOCKED_AUTHENTICATED_STAGING |
| UI-013 | `Pos/Dialogs/AboutSupportDialog.xaml` | DialogShellWindow | Settings Hub; hub owner | none | static/support details | YES | NO | BLOCKED_AUTHENTICATED_STAGING |
| UI-014 | `Pos/Dialogs/PosOnlineFirstLoginDialog.xaml` | DialogShellWindow | startup gate; MainWindow | public staging reachable | empty, focus, online, auth denial | YES | NO | PASS_COMPUTER_USE on public staging |
| UI-015 | `Pos/Dialogs/FirstRunSetupDialog.xaml` | DialogShellWindow | local recovery setup; login owner | recovery/new local DB | empty, validation, create/cancel | YES | NO | NOT_RUN under final public-staging constraint |
| UI-016 | `Pos/Dialogs/ChangePinDialog.xaml` | DialogShellWindow | operator/security flow; safe owner | authenticated operator | validation/error/success | YES | NO | BLOCKED_AUTHENTICATED_STAGING |
| UI-017 | `Pos/Dialogs/PosStartOfDaySyncDialog.xaml` | DialogShellWindow | post-login startup; MainWindow | trusted session/catalog states | progress/offline/retry/blocked | YES | NO | PASS_AUTOMATED lifecycle 20×; visual BLOCKED_AUTHENTICATED_STAGING |
| UI-018 | `Pos/Dialogs/OperatorSwitchDialog.xaml` | DialogShellWindow | Change/Lock; MainWindow | local staff mirror | empty/auth error/success | YES | NO | BLOCKED_AUTHENTICATED_STAGING |
| UI-019 | `Pos/Dialogs/PermissionDeniedDialog.xaml` | DialogShellWindow | denied command; safe owner | insufficient permission | message/override/cancel | YES | NO | BLOCKED_AUTHENTICATED_STAGING |
| UI-020 | `Pos/Dialogs/OverrideAuthorizationDialog.xaml` | DialogShellWindow | protected action; denied-dialog owner | manager/admin credentials | empty/error/success | YES | NO | BLOCKED_AUTHENTICATED_STAGING |
| UI-021 | `Pos/Dialogs/SalesRegisterDialog.xaml` | DialogShellWindow | Sales register menu; MainWindow | report permission; sales | empty/populated/detail | YES | NO | BLOCKED_AUTHENTICATED_STAGING |
| UI-022 | `Pos/Dialogs/RefundDialog.xaml` | DialogShellWindow | register/receipt action; register owner | refund permission; sale | partial/full/validation/confirm | YES | NO | BLOCKED_AUTHENTICATED_STAGING |
| UI-023 | `Pos/Dialogs/HeldCartsDialog.xaml` | DialogShellWindow | POS held carts; MainWindow | operator; held carts | empty/populated/recover/delete | YES | NO | BLOCKED_AUTHENTICATED_STAGING |
| UI-024 | `Pos/Dialogs/DiscountDialog.xaml` | DialogShellWindow | POS discount; MainWindow | discount permission; cart line | validation/limit/override | YES | NO | BLOCKED_AUTHENTICATED_STAGING |
| UI-025 | `Pos/Dialogs/ChangeQuantityDialog.xaml` | DialogShellWindow | POS line action; MainWindow | operator; selected line | keypad/validation | YES | NO | BLOCKED_AUTHENTICATED_STAGING |
| UI-026 | `Pos/Dialogs/BoletaNumberDialog.xaml` | DialogShellWindow | receipt workflow; MainWindow | operator; sale | empty/validation/confirm | YES | NO | BLOCKED_AUTHENTICATED_STAGING |
| UI-027 | `Pos/Dialogs/DailyReportDialog.xaml` | DialogShellWindow | legacy command unbound; harness owner | synthetic sales | populated/history/keyboard/footer | NO | YES | PASS_COMPUTER_USE via harness; PASS_AUTOMATED lifecycle 20× |
| UI-028 | `Pos/Dialogs/UserManagementDialog.xaml` | DialogShellWindow | legacy command unbound; harness owner | synthetic users/roles | list/selection/permissions/footer | NO | YES | PASS_COMPUTER_USE via harness; PASS_AUTOMATED lifecycle 20× |
| UI-029 | `Pos/Dialogs/NewUserDialog.xaml` | DialogShellWindow | User Management; management owner | administrator; roles | empty/validation/error/success | YES | NO | BLOCKED_AUTHENTICATED_STAGING |
| UI-030 | `Pos/Dialogs/RoleEditDialog.xaml` | DialogShellWindow | User Management; management owner | administrator; permissions | create/edit/validation | YES | NO | BLOCKED_AUTHENTICATED_STAGING |
| UI-031 | `Products/ProductEditDialog.xaml` | DialogShellWindow | Products add/edit; MainWindow | product permission; suppliers/categories | create/edit/validation | YES | NO | PASS_AUTOMATED lifecycle 20×; visual BLOCKED_AUTHENTICATED_STAGING |
| UI-032 | `Products/DeleteProductConfirmDialog.xaml` | DialogShellWindow | Products delete; Products owner | product permission; selected product | warning/cancel/confirm | YES | NO | BLOCKED_AUTHENTICATED_STAGING |
| UI-033 | `Products/ProductPriceHistoryDialog.xaml` | DialogShellWindow | Products price history; Products owner | product permission; history | empty/populated/scroll | YES | NO | BLOCKED_AUTHENTICATED_STAGING |
| UI-034 | `Products/ExportDataDialog.xaml` | DialogShellWindow | Products export; Products owner | export permission; catalog | options/busy/error/success | YES | NO | BLOCKED_AUTHENTICATED_STAGING |
| UI-035 | `Import/ImportDataDialog.xaml` | DialogShellWindow | Import command; safe owner | import permission; file | select/preview/busy/error | YES | NO | BLOCKED_AUTHENTICATED_STAGING |
| UI-036 | `Import/ImportView.xaml` | UserControl | ImportDataDialog content | import fixture | empty/preview/result | YES | NO | BLOCKED_AUTHENTICATED_STAGING |
| UI-037 | `Import/SupplierExcelImportDialog.xaml` | DialogShellWindow | supplier import command; safe owner | import permission; XLS/XLSX | select/map/preview/busy/result | YES | NO | BLOCKED_AUTHENTICATED_STAGING |
| UI-038 | `Import/ApplyConfirmDialog.xaml` | DialogShellWindow | import confirmation; import owner | staged import | summary/warning/cancel/apply | YES | NO | BLOCKED_AUTHENTICATED_STAGING |
| UI-039 | `Import/ModernMessageDialog.xaml` | DialogShellWindow | import result/error; import owner | import outcome | info/warning/error | YES | NO | BLOCKED_AUTHENTICATED_STAGING |

## Runtime totals

- Discovered: 39.
- Opened through valid real entrypoint on public staging: 2 (`UI-001`, `UI-014`).
- Opened through QA harness: 6 distinct types (`UI-007`, `UI-009`, `UI-017`, `UI-027`, `UI-028`, `UI-031`).
- Visual PASS through Computer Use: 4 (`UI-001`, `UI-014`, `UI-027`, `UI-028`).
- Visual FAIL: 0.
- Authenticated visual flows blocked/not run: 35.
- Lifecycle: PASS, 20 cycles across six types; no open windows, language handlers 0→0, non-monotonic handle/private-byte samples.

## Display, language and hardware matrix

| Profile | Result | Reason |
| --- | --- | --- |
| Current host, EN, 2880×1800 physical / 1440×900 logical, 200% | PASS_DISPLAY_PROFILE for four observed surfaces | Computer Use evidence |
| 1024×768 100% | NOT_RUN | true Windows display/DPI change not completed |
| 1024×768 125% | NOT_RUN | true Windows display/DPI change not completed |
| 1366×768 100% | NOT_RUN | true Windows display/DPI change not completed |
| 1366×768 125% | NOT_RUN | true Windows display/DPI change not completed |
| 1024×600 best effort | NOT_RUN | profile not available |
| multi-monitor best effort | NOT_RUN_MULTI_MONITOR | one monitor available |
| IT / ES / ZH runtime | NOT_RUN | authenticated critical matrix blocked |
| Windows 7 SP1 | NOT_RUN_WIN7_PHYSICAL | no Win7 machine/VM in session |
| Xprinter / scanner / cash drawer | NOT_RUN_HARDWARE | devices not attached |

Screenshots and CSV evidence are stored outside Git under
`C:\Dev\Win7POS-QA\20260716-215939\visual`.
