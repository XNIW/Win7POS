# Phased recovery input handoff

These inputs came from the official C# test runner and current production builder,
SQLite applier and recovery planner. They are inputs for an isolated Admin probe,
not evidence of Admin acceptance or of a shared TEST deployment.

`dense-5000` contains 5,000 data rows; `dense-59999` contains 59,999 data rows plus
one worksheet header (60,000 worksheet rows). Each name contains 240 CJK UTF-16
characters. Original and recovery operation timestamps are fixed in each manifest.
The legacy uncertain dispatch state was explicitly seeded after the one real local
application. No receipt, retirement or recovery commit was fabricated to export.

Each directory provides:

- `original.persisted.json`: the exact SQLite outbox JSON, with attempt omitted.
- `plan.document.json`: complete replacement coverage and immutable bounded children,
  built before a recovery commit. Its root proof ID derives solely from scope and
  the immutable original's raw SHA. No server canonical hash is assumed.
- `trust.synthetic.json`: synthetic shop/device/session credentials for fixture auth.
- `inputs.manifest.json`: identities, raw hashes, byte counts, local state and file hashes.

The full Asus bundle additionally retains `original.sqlite` and `children.saved.json`.
They are intentionally excluded from the compact handoff ZIP. Their hashes remain
in the manifest as local provenance. Do not treat their absence from this ZIP as
an instruction to substitute or reconstruct them.

## Isolated Admin probe

Use the published source candidate's real parser, handler and PostgreSQL RPCs with
an isolated fixture scope. Verify raw hashes from files before starting. Register
and seal descriptor pages (maximum 256), upload 256KiB chunks, then perform original
prepare/normalize/complete using only the cursor returned by each actual response.
Lookup the original, retire it authoritatively, then register/upload/prepare/normalize/
complete the exact plan. Normalization page sizes depend on projected bytes; do not
assume 1,000 rows per page. Child application and receipt validation can follow in
the same isolated run if included explicitly in its scope.

Capture all public response bodies and HTTP statuses into `schedule.json`:

```json
{
  "schemaVersion": "win7pos-phased-admin-schedule-v1",
  "sourceCommit": "actual full source commit",
  "requests": [
    {
      "action": "upload",
      "selector": {"phase":"manifest","uploadId":"actual UUID","offset":0},
      "httpStatus": 200,
      "responseFile": "00000.response.json",
      "responseSha256": "sha256:actual hash of these response bytes"
    }
  ]
}
```

Use local basename response filenames. Record selectors that bind phase, raw hash,
cursor/stage, offset or chunk index as applicable. Include provenance and database
qualification details beside the schedule. `RecordedScheduleHandler` in
`CatalogImportRecoveryPhasedInteropEvidenceTests.cs` validates these selectors and
response hashes, feeds the exact response bytes to the real C# client, and captures
its actual HTTP request bytes. This lets the C# client follow the observed cursor
schedule without manually editing its requests. Replay the resulting capture against
a fresh equivalent isolated database; a previous probe's mutated state is not an
equivalent initial condition.

No shared TEST DDL, Worker deployment, READY publication or physical-device acceptance
is included in this handoff.
