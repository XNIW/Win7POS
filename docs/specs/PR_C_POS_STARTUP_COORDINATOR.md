# PR-C — POS startup coordinator extraction

## Problem

`MainWindow` currently owns both WPF shell presentation and the non-visual POS
startup lifecycle: database initialization, factory/session construction,
authenticated-access state, catalog-safety decision, recovery transition, and
the online supervisor lifecycle. That coupling makes the startup sequence
difficult to reason about and gives a single UI class too many infrastructure
responsibilities.

## Scope

Introduce a WPF-internal `PosStartupCoordinator` as the authoritative
non-visual startup/runtime boundary. It owns default database initialization,
the shared factory and operator session, access/recovery state, trusted-host
attachment, catalog-safety/shell-mode decisions, recovery validation and the
online supervisor lifecycle. `MainWindow` remains the UI renderer: rendered
shell wait, dialog creation/ownership, start-of-day modal presentation,
localization/status controls, navigation, recovery visuals and exception
presentation.

## Invariants

- No new `Loaded` event, position/size code, or dialog ownership change.
- The visible-shell wait stays before the first login dialog; start-of-day
  remains before POS view creation.
- The coordinator owns exactly one default factory/session/host tuple and the
  `PosOnlineSyncSignalBus` registration; it disposes registration before host.
- `MainWindow` does not create the default factory, initialize the database,
  construct the operator session, or store authoritative access/recovery state.
- Maintenance stop/resume evaluates coordinator-owned current safe-start,
  recovery, and access mode rather than a construction-time snapshot.
- Recovery stops the host but does not dispose it; shell cleanup is the only
  disposal point. Its UI projection remains in the shell.
- Access acceptance returns a UI-neutral shell decision. Recovery exit is
  validated and completed by the coordinator around the UI-owned start-of-day
  dialog so the POS view cannot open after an access or catalog-safety change.
- Adaptive triggers keep the existing lane mapping, cancellation propagation,
  full-repair tracking, and fail-closed authorization behavior.

## Acceptance criteria

1. `MainWindow` no longer owns default DB/session/access/recovery/host state,
   signal registration, or online lane scheduling implementation.
2. The coordinator exposes the existing host only for already UI-owned dialogs
   and recovery presentation, without changing their ordering or owner.
3. Source gates reject regression of DB/session/recovery/host lifecycle into
   the shell, verify the coordinator as authoritative, and continue to verify
   shell ordering.
4. Existing Core/Data tests, WPF x86 build/smokes, required gates, and CLI
   selftest remain green.

## Files

- `src/Win7POS.Wpf/Pos/Online/PosStartupCoordinator.cs`
- `src/Win7POS.Core/Online/PosStartupCoordinatorPolicy.cs`
- `src/Win7POS.Wpf/MainWindow.xaml.cs`
- startup/online source gates under `scripts/`
- `tests/Win7POS.Core.Tests/Online/PosStartupCoordinatorPolicyTests.cs`
  it

## Risks and negative cases

The dangerous regressions are starting sync in safe/recovery mode, accepting a
login without re-evaluating catalog safety, resuming after a state change,
disposing a host on recovery, starting a lane before a trusted generation is
attached, and moving modal UI before the shell is rendered. Preserve retry and
authorization outcomes and verify that cancellation reaches lane triggers.

## Benchmark and validation

This is a lifecycle refactor, not a performance rewrite.  It must not add a
connection, transaction, or dispatcher block.  Validate focused coverage,
full Core/Data tests, locked restore, solution/WPF x86 builds, WPF logging and
dispatcher smokes, CLI selftest, required gates, and `git diff --check`.
