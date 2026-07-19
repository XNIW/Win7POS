# PR #7 receipt, recovery and release closeout — 2026-07-19

## Decision

- Branch: `codex/hardware-epson-tm-t60-20260717-161122`.
- Initial published PR head: `3536ef28aa095ea5d9593092d36f6d51777e85b5`.
- Base reviewed: `origin/main` at
  `ad431fe8b7cf4de1bf3bee744bab159b6a95e80c`.
- Cumulative open findings: P0 `0`, P1 `0`, P2 `0`.
- Receipt-surface physical merge gate: `PASS_PHYSICAL_2026-07-19`.
- Physical Windows 7 SP1: `NOT_RUN_WIN7_PHYSICAL`.
- Merge recommendation: `READY_AFTER_EXACT_HEAD_CI`.

The final implementation head is the commit containing this report. Published
commits were not rewritten; no rebase or force push was used.

## Correctness and security closure

The cumulative audit corrected the following material findings:

1. A normal remote mirror could enter the recovery shell and receive lease-free
   recovery treatment. Recovery now accepts only the explicit local maintenance
   identity, while normal offline access requires the exact
   shop/staff/version-bound mirror and trusted lease.
2. Cancelling a candidate operator change could leave the candidate identity
   committed. Identity mutation now occurs only after successful authorization;
   cancellation preserves the current operator.
3. Safe Start blocking did not exist at every last-mile hardware/network
   boundary. Direct spooler effects, discovery, hardware-armed settings and
   non-loopback Admin Web bootstrap/retry now fail closed.
4. A print request queued behind an indeterminate timed-out request could execute
   after its caller had already observed failure. Printer effects are now
   per-printer single-flight and reject a second effect before task creation.
5. Invalid or corrupted copy counts could reach the driver. The shared policy is
   exactly one through three copies; invalid writes and physical requests fail
   before submission, while a corrupted persisted value reads safely as one.
6. A committed cash sale could hide a fiscal print failure. The exception now
   reaches the POS surface exactly once with the reserved boleta number and
   explicit queue/paper guidance; persistence failure after a successful print
   warns the operator not to reprint.

Receipt, history, reprint, refund/void and daily-close rendering retain their
persisted-value semantics. No automatic receipt archive, fiscal PDF or temporary
PDF residue remains, and the legacy fiscal status column stores no path.

## Physical Epson receipt addendum

The dedicated harness used a new absent root and the real queue
`EPSON TM-T60 Receipt`, driver `EPSON TM-T60 Receipt5`, port `ESDPRT001`.
It submitted exactly one awaited sequence, with no retry:

| Job | Surface | Columns | Copies | Result |
| ---: | --- | ---: | ---: | --- |
| 1 | fiscal QA | 32 | 1 | PASS_PHYSICAL |
| 2 | fiscal QA | 42 | 1 | PASS_PHYSICAL |
| 3 | receipt original | 42 | 1 | PASS_PHYSICAL |
| 4 | exact same receipt request | 42 | 1 | PASS_PHYSICAL_IDENTICAL |
| 5 | daily close QA | 32 | 1 | PASS_PHYSICAL |
| 6 | daily close QA | 42 | 1 | PASS_PHYSICAL |

All jobs contained the visible warnings `QA - PRINTER TEST`, `NON FISCAL`,
`NO SALE SAVED` and `NO DRAWER`. The manifest records `SUBMITTED_JOBS=6`, one
copy per job, identical job 3/4 request hashes, `DRAWER_CALLS=0` and
`DATABASE_ARTIFACTS=ABSENT`. The operator confirmed exactly six legible and cut
slips, correct widths/codes, identical jobs 3/4, no extras and no drawer opening.
The queue returned `Normal` with zero pending jobs.

Manifest retained outside Git:
`C:\Dev\Win7POS-QA\hardware\Epson-TM-T60\20260719-pr7-final-01\physical-printer-qa.txt`
(SHA-256
`F635BB9DC6FC45A8DD5A881CDA1EB0E1AB3CF5288542569A43395279699FE1DA`).

The host was Windows 11 Home Single Language `10.0.26200`; this evidence must
not be relabeled as a physical Windows 7 result.

## Final local evidence

| Validation | Result |
| --- | --- |
| Independent cumulative review | PASS; P0/P1/P2 open 0/0/0 |
| `git diff --check` | PASS; line-ending notices only |
| Dialog standard | PASS, 34/34 |
| Core tests | PASS, 298/298, zero skipped |
| WPF Release net48/x86 | PASS, zero warnings/errors |
| UI harness Release net48/x86 | PASS, zero warnings/errors |
| Lifecycle, 20 cycles | PASS; zero residual ViewModels/windows |
| Required source-and-release gates | PASS, 35/35 |
| Release inventory/manifests/provenance | PASS |
| Negative release fixtures | PASS, 5/5 rejected as designed |
| Local Inno installer build | NOT_RUN_TOOL_UNAVAILABLE; required in CI |
| Physical Windows 7 SP1 | NOT_RUN_WIN7_PHYSICAL |

The exact final pushed head must pass fresh GitHub CI and Release Pack before the
PR is merged. Post-merge main CI and Release Pack remain mandatory.
