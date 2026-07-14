# Codex Asus Task Index

This file tracks Asus Windows build-machine tasks. Detailed evidence belongs in
dated reports under `docs/reports/`.

## Recent Tasks

| Task ID | Status | Title | Commit | Evidence |
| --- | --- | --- | --- | --- |
| ASUS-W7POS-001 | Done | Unified POS access login | Present by later unified-login commits | Single POS access startup, offline fallback, no double initial OperatorLogin, covered by later gates. |
| ASUS-W7POS-002 | Done | Install .NET 10 and complete QA | Environment task | `C:\Dev\dotnet10\dotnet.exe` used for restore/build/test; net10 tests/CLI and net48 WPF x86 build pass. |
| ASUS-W7POS-003 | Done | Wi-Fi badge and sync checklist UI polish | Present by `a4a9559` | Header/dialog network badge, sync checklist, WPF x86 build pass. |
| ASUS-W7POS-004 | Done | Compact POS access dialog layout | `a4a9559` | POS access first screen compacted, empty gap removed, UI smoke pass. |
| ASUS-W7POS-005 | Done | Harden POS access login logging | `cb525e8`, reinforced in ASUS-W7POS-011 | `category=pos.access`, attempt id, stage/result/duration; final closeout secret scan pass. |
| ASUS-W7POS-006 | Done | Fix CI startup validator and align task numbering | `9878eb4` | Startup validator aligned with POS access flow; local validator pass. |
| ASUS-W7POS-007 | Done | Quick operator switch and permission-denied elevation UX | `c91d17a` | `Change / Lock` opens `OperatorSwitchDialog`; permission denied offers switch; logs omit PIN/password. |
| ASUS-W7POS-008 | Done | Audit POS role permissions and improve denial diagnostics | `6812a0b` | Role hierarchy documented; denial diagnostics include current role and missing permission; manager remains non-admin. |
| ASUS-W7POS-009 | Done | Support POS Admin staff role mapping | `9bf33c9` | `pos_admin`, `staff_admin`, and `shop_owner_staff` map to local admin; manager remains manager; Core tests pass. |
| ASUS-W7POS-010 | Done | E2E review of POS Admin staff role | Validated at `9bf33c9` and revalidated in ASUS-W7POS-011 | Redacted POS Admin staff online/offline smoke pass; admin areas open; switch negative case not run because the smoke data dir had only one local POS Admin operator. |
| ASUS-W7POS-011 | Done | Final review and closeout | This final closeout commit | Tasks 001-010 closed, final report created, gates/release drop/smoke/log scan complete, logging redaction bug fixed. |
| ASUS-W7POS-012 | Pending external runtime | Shop-scoped sync runtime validation, 30-test matrix | `94bb9573544811ef97c45f74b4ccf3ac85dc10de` | `docs/HANDOFFS/WIN7POS-ASUS-RUNTIME-VALIDATION-2026-07-14.md`; Mac static/build/test gates pass, runtime remains `EXTERNAL_TEST_PENDING_CODEX_ASUS`. |

## External Hardware Pending

- Windows 7 SP1 physical machine smoke.
- Real Xprinter/spooler test.
- Real barcode scanner test.
- Shop-scoped sync 30-test runtime/staging matrix from ASUS-W7POS-012.

These items require hardware and are not software blockers for the Asus closeout.
