# Win7POS customer display architecture

## Scope and invariants

The customer display is a local workstation feature. It does not change full or
incremental synchronization, sales outbox payloads, refund/void economics or the
Admin Web contract. Its settings use the `pos.customer_display.*` prefix in the
local `app_settings` table and are never included in sync payloads.

The cashier shell remains `WindowState=Maximized` and `ResizeMode=CanMinimize`.
`WindowState.Normal` is rejected. User close requests are evaluated by the pure
`MainShellClosePolicy`; internal startup/auth exits use `CloseWithoutUserPrompt`,
and Windows logoff/shutdown uses the `SessionEnding` bypass without a modal prompt.

## Data flow

1. `PosWorkflowService` remains the authoritative source for line economics,
   subtotal and total.
2. `PosViewModel.ApplySnapshot` finishes updating the entire cashier snapshot and
   then publishes one immutable `CustomerDisplaySnapshot`.
3. `CustomerDisplayProjection` copies the authoritative subtotal/total and derives
   only the already-defined discount presentation (`Subtotal - Total`). It never
   recomputes the sale total from customer lines.
4. `CustomerDisplayManager` receives the immutable snapshot, validates topology
   and updates one `CustomerDisplayWindow` instance. It performs no DB or HTTP work
   during cart changes.
5. After a successful local sale commit, the safe paid/change values are published
   in a `Completed` snapshot. A configurable timer returns the display to `Idle`.

The customer DTO is whitelist-only: name, optional barcode, quantity, unit price,
line total and customer-facing adjustment kind. It contains no product/database
ID, stock, cost, margin, supplier, operator identity, remote ID, token, outbox or
technical error text.

## Monitor and window lifecycle

`WindowsDisplayTopologyProvider` enumerates `System.Windows.Forms.Screen.AllScreens`.
The pure monitor policy de-duplicates identical bounds, selects a non-cashier
screen deterministically, supports negative X/Y and portrait bounds, and never
falls back to the cashier screen. Duplicate/mirror topology is rejected with the
instruction to use Windows Extend mode.

`CustomerDisplayWindow` is not a dialog and has no owner. It is borderless,
non-resizable, hidden from the taskbar and non-activating. On `SourceInitialized`,
Win7-safe `user32` styles `WS_EX_NOACTIVATE` and `WS_EX_TOOLWINDOW` are applied.
Physical `Screen.Bounds` or `WorkingArea` coordinates are passed to `SetWindowPos`;
no per-monitor DPI API introduced after Windows 7 is used.

`CustomerDisplayManager` owns the sole `SystemEvents.DisplaySettingsChanged`
subscription, debounces it by 750 ms on the WPF Dispatcher and always unsubscribes
in idempotent `Dispose`. When the selected monitor disappears, only the customer
window closes. Reconnect reopens it only when configured. Failures are redacted,
non-blocking and cannot interrupt scanning, payment or the local sale commit.

## Settings and permissions

Configuration reuses `PermissionCodes.SettingsPrinter`; no role or seed permission
was widened. `CustomerDisplaySettingsRepository` loads typed fail-safe defaults and
saves all keys in one SQLite transaction. The dialog validates same-screen,
missing-screen and duplicate-topology conditions before save. A failed live apply
restores the previous persisted and runtime settings. Cancel performs no write.

Customer language lookup is side-effect-free. Selecting IT, EN, ES or ZH for the
customer display does not change the application language or cashier culture.

## Win7 compatibility boundary

- WPF/.NET Framework 4.8, x86, C# 8.
- `Screen.AllScreens`, `SetWindowPos`, `GetWindowLong`, `SetWindowLong` only.
- No `GetDpiForWindow`, `GetDpiForMonitor`, WebView2, WinUI, Mica, Acrylic or blur.
- No hardcoded target resolution; layout policy covers compact, standard, large
  and portrait profiles without a whole-window `Viewbox`.
