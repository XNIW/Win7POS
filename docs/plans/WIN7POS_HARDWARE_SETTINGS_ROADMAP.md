# Win7POS hardware and operations settings roadmap

## Scope and status

This roadmap is the read-only hardware/settings follow-up to PR-B. PR-B does not
change scanner input, printer/spooler behavior, cash-drawer output, customer
display runtime, or operations scheduling.

- Current-main reproducible P0: `0`.
- Current-main reproducible P1: `0`.
- Physical Windows 7 SP1, scanner, Xprinter, drawer, dual-monitor and DPI/language
  certification remains external and open.
- All proposed settings below are live-apply (`restart required = NO`).
- Hardware configuration never proves that hardware is connected. Health must
  distinguish `Healthy`, `Warning`, `Unknown`, `Disabled`, and `Needs test`.

## Current-state evidence

| Area | Current behavior | Evidence |
| --- | --- | --- |
| Settings | Generic string KV store; printer settings are saved through several independent writes. | `SettingsRepository.cs`, `PosWorkflowService.cs` |
| Scanner | Keyboard-wedge input is a focused text box submitted by Enter; there are no typed normalization keys or isolated scanner test. | `PosView.xaml`, `PosView.xaml.cs`, `PosViewModel.cs` |
| Printer | Installed queues are enumerated and basic settings validity is exposed; spooler jobs/queue/port health are not diagnosed. | `WindowsPrinterDiscovery.cs`, `InstalledPrinterInfo.cs` |
| Receipt | Legacy `pos.useReceipt42` selects 32/42-column formatting, while paper selection remains centered on 80 mm. | `PosWorkflowService.cs`, `ReceiptOptions.cs`, `WindowsSpoolerReceiptPrinter.cs` |
| Drawer | Disabled by default and protected from virtual targets, but custom raw bytes are permissive. | `PrinterSettingsDialog.xaml`, `WindowsSpoolerReceiptPrinter.cs` |
| Backup | Manual online backup is integrity/FK verified; no schedule, retention, or selectable destination exists. | `SqliteOnlineBackup.cs`, `DbMaintenanceRepository.cs` |
| Customer display | Typed atomic settings, Win7-safe topology, non-activating window, privacy projection and hot-plug handling already exist. | `CustomerDisplaySettings*.cs`, `CustomerDisplayManager.cs` |

## PR-H1 — Hardware Center

The Hardware Center uses the existing `settings.printer` permission for scanner,
printer, drawer, and display diagnostics. It must persist the complete typed
hardware model and its audit record in one transaction.

### Scanner keyboard-wedge options

| Key | Type | Default | Valid range | Permission | Restart | Win7 risk | Required test |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `pos.scanner.keyboard_wedge.terminator` | enum | `enter` | `enter`, `tab`, `enter_or_tab` | `settings.printer` | No | Tab can move focus; handle in `PreviewKeyDown`, without a global hook. | Enter/Tab/SystemKey; one submit; focus restored. |
| `pos.scanner.keyboard_wedge.prefix` | string | `""` | 0–16 printable characters; no controls | `settings.printer` | No | Keyboard layout/IME can transform characters. | Exact, absent and partial prefix; control/unsupported Unicode rejection. |
| `pos.scanner.keyboard_wedge.suffix` | string | `""` | 0–16 printable characters; terminator excluded | `settings.printer` | No | Same layout/IME risk as prefix. | Strip before lookup; mismatch and short input. |
| `pos.scanner.keyboard_wedge.trim_whitespace` | bool | `true` | Boolean | `settings.printer` | No | None specific. | CR/LF/outer spaces removed; internal barcode unchanged. |
| `pos.scanner.keyboard_wedge.min_length` | int | `1` | 1–128 | `settings.printer` | No | Aggressive minimum may reject legacy barcodes. | Boundaries 1/128 and non-destructive validation copy. |
| `pos.scanner.keyboard_wedge.max_length` | int | `128` | 1–256 and `>= min_length` | `settings.printer` | No | Bound input before lookup to protect x86 memory/UI. | 128/256/257 and min/max relation; no quick-create on invalid input. |

The scanner test is an explicitly armed, isolated surface. It shows normalized
value, recognized terminator and timing, but never modifies cart/catalog and never
logs or persists the scanned barcode. A keyboard wedge must not be represented as
a discoverable USB device.

### Printer and receipt options

| Key | Type | Default | Valid range | Permission | Restart | Win7 risk | Required test |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `pos.printer.receipt.enabled` | bool | `false` | Boolean | `settings.printer` | No | None specific. | Fail closed with no queue. |
| `pos.printer.receipt.name` | string | `""` | Installed queue, max 255; empty only when disabled or explicit default allowed | `settings.printer` | No | Unicode/remote queues and slow drivers. | Missing, renamed, offline, physical and virtual targets. |
| `pos.printer.receipt.auto_print_after_sale` | bool | `false` | Requires enabled and a valid non-interactive target | `settings.printer` | No | PDF/XPS may open UI. | Sale remains committed; no prompt; post-sale warning. |
| `pos.printer.receipt.copies` | int | `1` | 1–5 | `settings.printer` | No | Avoid unbounded jobs and the downstream `short` cast. | 0/1/5/6/`int.MaxValue`; exact fake-spooler count. |
| `pos.printer.receipt.allow_windows_default` | bool | `false` | Boolean | `settings.printer` | No | Windows default can change or become virtual. | Changed, empty and virtual default. |
| `pos.printer.receipt.allow_virtual_printers` | bool | `false` | Manual explicit print only; never auto-print | `settings.printer` | No | Virtual drivers can require dialogs. | Manual consent allowed; automatic path blocked. |
| `pos.printer.receipt.save_copy` | bool | `false` | Boolean | `settings.printer` | No | Disk/ACL failure. | Disk full/access denied cannot invalidate the sale. |
| `pos.printer.receipt.output_directory` | path | managed `data\receipts` | Absolute local directory; final path `<240` chars; no URI/credential | `settings.printer` | No | Win7 `MAX_PATH`, ACL, slow shares. | Invalid/long/read-only; path absent from logs/audit. |
| `pos.printer.receipt.profile` | enum | `thermal_80mm_42col` | `thermal_58mm_32col`, `thermal_80mm_42col` | `settings.printer` | No | Localized paper names and inconsistent drivers. | Pure paper selector, fallback, 32/42 formatting and matching test print. |

Compatibility: when `pos.printer.receipt.profile` is absent, map
`pos.useReceipt42=true` to `thermal_80mm_42col` and `false` to
`thermal_58mm_32col`. There must be one UI source of truth.

### Cash-drawer options

| Key | Type | Default | Valid range | Permission | Restart | Win7 risk | Required test |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `pos.cashdrawer.mode` | enum | `disabled` | `disabled`, `printer_kick` | `settings.printer` | No | RAW output is not supported by every driver. | Disabled no-op; explicit non-virtual target. |
| `pos.cashdrawer.printer_name` | string | `""` | Installed non-virtual queue; empty means receipt queue | `settings.printer` | No | Renamed/offline queue. | Receipt fallback, missing queue, virtual block. |
| `pos.cashdrawer.open_on_cash_sale` | bool | `true` | Boolean | `settings.printer` | No | None specific. | Cash opens; card/non-cash does not. |
| `pos.cashdrawer.preset` | enum | `escpos_pin2` | `escpos_pin2`, `escpos_pin5`, `custom` | `settings.printer` | No | Pulse wiring differs by device. | Exact pin2/pin5 bytes and round-trip. |
| `pos.cashdrawer.command` | string | `27,112,0,25,250` | Custom only; exact `ESC p m t1 t2`, `m` in `0,1,48,49`, times 0–255 | `settings.printer` | No | Invalid RAW bytes may print garbage. | Missing/extra/non-numeric/out-of-range bytes rejected. |

`pos.cashdrawer.enabled` remains a read-only compatibility alias when `mode` is
missing; it is not a second editable value.

### Hardware health summary

- Scanner: `Unknown` until the isolated test succeeds.
- Printer: use a Winspool adapter (`OpenPrinter`, `GetPrinter`, `EnumJobs`) on a
  background single-flight path with a UI timeout; display queue, status flags,
  job count and error. Do not expose purge/cancel actions.
- Drawer: only `Configured`, `Test passed at …`, or `Test failed`; reliable
  discovery is unavailable.
- Customer display: reuse the existing topology provider and explain
  single/extended/duplicate selection.

### PR-H1 definition of done

- Typed `HardwareSettingsRepository` with fail-safe load, validation, one
  transaction and redacted audit.
- Pure Core policies: `ScannerInputPolicy`, `ReceiptProfilePolicy`, and
  `CashDrawerPresetPolicy`.
- Scanner test cannot alter cart/catalog or open quick-create.
- Queue diagnostics cannot block UI or accumulate timed-out tasks.
- Extend `check-pos-printer-cashdrawer-safety.ps1`.
- Physical smoke: Win7 SP1 x86, Enter/Tab scanner, 58/80 mm, offline queue,
  Xprinter, and pin2/pin5 drawer.

## PR-H2 — Operations Settings

The Operations card is reachable with `db.backup`. Import/reset and sensitive
administration continue to require `db.maintenance`.

### Backup policy options

| Key | Type | Default | Valid range | Permission | Restart | Win7 risk | Required test |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `pos.operations.backup.schedule` | enum | `disabled` | `disabled`, `daily`, `weekly` | `db.backup` | No | In-process scheduler cannot run while the app is closed. | Fake clock, daily/weekly and no duplicate. |
| `pos.operations.backup.local_time` | string | `02:00` | invariant `HH:mm`, 00:00–23:59 | `db.backup` | No | DST/timezone/clock changes. | DST, clock rollback and timezone change. |
| `pos.operations.backup.weekly_day` | enum | `Sunday` | Seven invariant day names; ignored unless weekly | `db.backup` | No | Culture-specific day names. | Invariant serialization. |
| `pos.operations.backup.catch_up_on_startup` | bool | `true` | Boolean | `db.backup` | No | Startup must not wait synchronously. | One catch-up after recovery/migrations; closed-app gap. |
| `pos.operations.backup.retention.max_count` | int | `14` | 3–365 | `db.backup` | No | Locked files and safe deletion. | Keep newest three; delete managed names only. |
| `pos.operations.backup.retention.max_age_days` | int | `30` | 1–3650 | `db.backup` | No | Manipulated timestamps/clock anomalies. | Combined age/count policy. |
| `pos.operations.backup.destination.kind` | enum | `local` | `local`, `custom_local`, `network_share` | `db.backup` | No | Win7 SMB/ACL and offline shares. | Local/custom/UNC, denied and offline. |
| `pos.operations.backup.destination.path` | string | `""` | Absolute directory for custom/UNC; no credential; final file `<240` chars | `db.backup` | No | `MAX_PATH` and slow share. | Traversal, live-DB target, file-vs-directory, no silent fallback. |

Runtime rules:

- Reuse `SqliteOnlineBackup.CreateVerifiedAsync` after restore recovery and
  versioned migrations.
- Share one single-flight gate between manual and scheduled backup.
- An offline configured share produces a warning and later retry; it never
  silently falls back to local storage.
- Apply retention only after a verified successful backup and only to managed
  `pos_backup_*.db` names inside the resolved directory. Preserve restore,
  migration and user files.
- Audit basename, result code and counts only; never paths.

### Redacted settings profile

Use `*.win7pos-settings.json` with `schemaVersion=1`, application version, UTC,
an allowlisted canonical dictionary, an explicit redacted-key list, and SHA-256
for corruption detection (not authenticity).

Portable allowlist:

- scanner normalization;
- receipt profile, copies and safe printer booleans, excluding queue/path;
- drawer mode/preset/open-on-cash, excluding queue/raw command;
- backup schedule/retention, excluding destination;
- customer-display enum/bool visual/privacy settings, excluding device names,
  logo and custom text;
- `ui.language`.

Always exclude `pos.online.*`, `pos.catalog.*`, `pos.official_shop.*`,
`pos.restore.*`, session/login/trusted-device state, shop/fiscal identity,
queue/device names, raw commands, paths, free text and every unknown key.

| Action | Permission | Restart | Required test |
| --- | --- | --- | --- |
| Export redacted profile | `db.backup` | No | Secret/path/device canaries absent byte-for-byte. |
| Preview/import atomically | `db.maintenance` | No | Schema/checksum/duplicate/unknown/range validation and full rollback. |
| Restore defaults by scope | `db.maintenance` | No | Hardware fails closed; schedule off; identity/catalog/session untouched. |
| View settings audit | `db.maintenance` | No | Actor/result ordering and redaction. |

Audit each save/import/reset/policy update with actor, source, key names, count,
and before/after hashes, never values. Settings plus audit row commit in one
transaction. Proposed events: `HardwareSettingsUpdate`, `BackupPolicyUpdate`,
`SettingsProfileExport`, `SettingsProfileImport`, `SettingsDefaultsRestored`, and
`DbBackupScheduled`.

Internal non-exported key:

| Key | Type | Default | Valid range | Permission | Restart | Win7 risk | Required test |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `pos.operations.application.last_seen_version` | string | `""` | max 64 invariant chars; runtime-owned | internal | No | None | First observation, upgrade, no-op and downgrade audit; no network updater. |

## PR-H3 — Customer display polish

Preserve the current atomic repository, `settings.printer` permission, no-focus
window behavior, topology policy and existing keys.

| Key | Type | Default | Valid range | Permission | Restart | Win7 risk | Required test |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `pos.customer_display.branding.logo_file` | string | `""` | Managed basename; PNG/JPEG/BMP <=2 MiB and <=4 MP; no SVG/GIF/UNC | `settings.printer` | No | GDI decode/file lock/x86 memory. | Malformed/bomb/locked/missing; cloned decode and fallback. |
| `pos.customer_display.branding.logo_position` | enum | `left` | `left`, `center` | `settings.printer` | No | Small/portrait layout. | 800×600 through 1920×1080 and portrait. |
| `pos.customer_display.idle.mode` | enum | `welcome` | `welcome`, `custom_message`, `clock` | `settings.printer` | No | Dispatcher timer and local clock. | Idle transitions, language, tick and no leak. |
| `pos.customer_display.idle.message` | string | `""` | 0–120 chars; no control/markup | `settings.printer` | No | Font fallback/IME. | Max, CR/LF, IT/EN/ES/ZH; export redacted. |
| `pos.customer_display.privacy.barcode_mode` | enum | `hidden` | `hidden`, `last4`, `full` | `settings.printer` | No | Migration from legacy bool. | Legacy mapping; reserved/manual always hidden; masking. |
| `pos.customer_display.privacy.show_paid_amount` | bool | `false` | Boolean | `settings.printer` | No | More restrictive than current completed screen. | Cash/card/mixed with no leak. |
| `pos.customer_display.privacy.show_change_amount` | bool | `true` | Boolean | `settings.printer` | No | Change must remain readable. | Zero/positive cash change. |
| `pos.customer_display.test_pattern.duration_seconds` | int | `15` | 5–60 | `settings.printer` | No | Timer delay on slow machine. | Auto/manual close and prior projection restore. |

Compatibility: when `privacy.barcode_mode` is absent, map legacy
`show_barcode=true` to `full`, otherwise `hidden`. `DISC:`, `TAX:` and `MANUAL:`
barcodes always remain suppressed.

Additional behavior:

- Import a logo into managed storage and persist only its basename/hash.
- Test pattern contains no sale data; show grid, colors, monitor ordinal,
  work-area/bounds and timer.
- Preview/test uses unsaved dialog values but remains non-activating and does not
  steal scanner focus.
- Diagnostics expose primary/resolution/bounds/work area/bpp/orientation and the
  duplicate/extended decision; copied diagnostics hash raw device identifiers.
- No network content, HTML, WebView2, video or post-Win7 DPI APIs.

## Recommended order

1. `PR-H1`: typed hardware repository, scanner test, queue diagnostics, receipt
   profile, drawer presets and health summary.
2. `PR-H2`: schedule/retention/destination, portable redacted profile, defaults
   and atomic audit.
3. `PR-H3`: branding, idle content, privacy, test pattern and topology polish.

The current risks (unbounded copy count, permissive RAW drawer bytes, non-atomic
printer save, 80 mm bias, no backup schedule/retention and missing settings audit)
remain roadmap-level. They are not promoted artificially to P0/P1.
