# Win7POS customer display QA matrix

Current review evidence: `release/win7pos-final-review-20260717-015659` at the
preserved runtime/documentation HEAD `1b11a6fdd7140e046ee1ef48f4e46828eadd209c`.

## Automated coverage

| Area | Evidence | Result |
| --- | --- | --- |
| Close policy | Idle, cart, payment, DB critical, full repair, incremental, programmatic, SessionEnding, minimize | PASS_AUTOMATED |
| Projection | empty, add/order, quantity, discount authoritative totals, manual/reserved barcode privacy, completed paid/change | PASS_AUTOMATED |
| Monitor policy | one/two/three screens, primary right, negative X/Y, portrait, duplicate bounds, missing manual selection, same screen, reconnect | PASS_AUTOMATED |
| Layout | 800×600, 1024×600, 1024×768, 1366×768, 1920×1080, portrait, ultrawide, system DPI input | PASS_AUTOMATED |
| Static safety | non-dialog, no-activate, taskbar hidden, Win7 APIs, no SQL/HTTP, privacy DTO, SystemEvents cleanup, settings/localization | PASS_GATE |
| WPF build | `net48`, Release, x86 | PASS_BUILD |
| Harness lifecycle | customer settings 20×, customer window 50×, manager/SystemEvents 50× | PENDING_FINAL_RUN |

## Required physical/runtime matrix

The current host exposes one independent screen: 1440×900, work area 1440×852,
32 bpp, landscape, primary, bounds origin 0,0. It cannot certify a real customer
screen, focus retention across two displays, hot-plug, negative physical placement
or portrait hardware.

| Profile | Result |
| --- | --- |
| Current single-screen host | PASS_SINGLE_MONITOR_DETECTION |
| Windows Extend with two independent monitors | NOT_RUN_SECOND_MONITOR |
| Disconnect/reconnect physical monitor | NOT_RUN_SECOND_MONITOR |
| Secondary right/left/above | NOT_RUN_SECOND_MONITOR |
| Physical portrait secondary | NOT_RUN_SECOND_MONITOR |
| 800×600 | PASS_POLICY; NOT_RUN_PHYSICAL |
| 1024×600 | PASS_POLICY; NOT_RUN_PHYSICAL |
| 1024×768 100%/125% | PASS_POLICY; NOT_RUN_PHYSICAL |
| 1366×768 100%/125% | PASS_POLICY; NOT_RUN_PHYSICAL |
| 1920×1080 | PASS_POLICY; NOT_RUN_PHYSICAL |
| IT/EN/ES/ZH static catalog | PASS_AUTOMATED |
| IT/EN/ES/ZH customer screen visual | NOT_RUN_SECOND_MONITOR |
| Windows 7 SP1 physical/VM | NOT_RUN_WIN7_PHYSICAL |

`PASS_MULTI_MONITOR` must not be claimed until Computer Use observes the real POS
on Windows Extend mode with two independent screens and completes the cart,
payment, focus, taskbar, minimize/restore, exit and hot-plug scenarios.

Screenshots, if later collected, must remain outside Git under the isolated QA
directory and must not include credentials, PINs, tokens or production data.

## Final certification — 2026-07-17

Final static safety, adaptive settings sizing, best-effort initialization, Core
policy tests and WPF net48/x86 build pass. Computer Use reconfirmed the isolated
real application entrypoint but could not open the authenticated settings flow.
Physical certification remains `NOT_RUN`: the available Windows 11 host has one
monitor and no attached scanner, Xprinter or cash drawer. A Windows 7 SP1 POS in
Extend mode with two independent monitors is still required for focus, taskbar,
cashier fallback, negative coordinates, portrait and hot-plug evidence. The
harness lifecycle row remains `PENDING_FINAL_RUN`; build success is not relabeled
as lifecycle or visual PASS.
