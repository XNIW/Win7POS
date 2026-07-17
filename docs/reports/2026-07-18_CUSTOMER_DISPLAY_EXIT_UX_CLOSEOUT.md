# Customer display and safe-exit UX closeout — 2026-07-18

## Scope

Implementation started from remote QA SHA
`32dec1e4ec0f6c0bbfcb9e7abcb1838792f2ebbc` on a new feature branch. `main` was
not modified, checked out for mutation or merged. The full/incremental sync policy,
outbox protocol and reversal economics were not changed. No PR 1–5 architecture
refactor was performed.

## Delivered

- Maximize-only cashier shell retained; minimize remains available.
- X and Alt+F4 share a guarded three-action confirmation; a non-empty cart shows
  authoritative item count and total.
- Login cancel, startup failure, start-of-day block and authorization failure use
  a programmatic close bypass.
- Windows SessionEnding performs best-effort cleanup and never opens the exit dialog.
- Local typed customer-display settings are persisted atomically in `app_settings`.
- Win7-safe display enumeration, duplicate-bounds rejection, deterministic automatic
  selection, negative coordinates, portrait bounds and no cashier fallback.
- Dedicated non-activating WPF customer window with responsive compact/standard/
  large/portrait layouts and no whole-window Viewbox.
- Atomic customer cart projection, payment/completed/locked states and safe paid/change.
- Settings Hub card, permission reuse, screen identification, preview and live apply.
- IT/EN/ES/ZH customer-display and settings copy with side-effect-free language lookup.
- Core policy tests, WPF lifecycle coverage and canonical safety gate.

## Validation status

Baseline before edits passed 29/29 canonical gates, solution Release build, 205
Core/Data tests, CLI selftest and WPF net48/x86 build. Feature validation is
recorded in the final task result after all gates, release pack and installer runs.

The host has only one independent monitor. Real dual-screen Computer Use, physical
hot-plug, Win7 hardware and the requested physical DPI profiles are therefore
`NOT_RUN`, never PASS. Fake topology and WPF harness coverage do not replace that
external evidence.

## Merge recommendation

`NOT_READY_FOR_MERGE` until the final automated/release validation is green and a
separate two-monitor Windows Extend session completes the physical runtime matrix.

## Final certification — 2026-07-17

Final local automated/release validation is green. Review fixes make customer
display initialization explicitly best-effort and keep the settings dialog within
the active work area/owner cap. The remaining merge blocker is external runtime
evidence: no Windows 7 SP1 machine, second monitor, scanner, Xprinter or cash drawer
is attached to this host. Dual-display focus/hot-plug, physical DPI and installer
smoke therefore remain `NOT_RUN`; recommendation remains `NOT_READY_FOR_MERGE`.
