# Win7POS CLI JSON Contract (v1)

## Versioning

- `schemaVersion` starts from `1`.
- Future breaking changes MUST bump `schemaVersion`.
- Non-breaking additions keep current `schemaVersion`.

## Backward Compatibility Rules

- Existing fields MUST NOT be renamed.
- Existing fields MUST NOT be removed.
- Existing field types MUST NOT change.
- Field order is part of the protocol and MUST remain stable.
- New fields can only be appended at the end of objects and MUST be optional.

## Top-Level Schema (Order Fixed)

Top-level keys MUST appear in this exact order:

1. `schemaVersion`
2. `timestampUtc`
3. `command`
4. `dbPath`
5. `input`
6. `analysis`
7. `diff`
8. `apply`
9. `errors`

## Per-Command Payload

- `command = "diff"`
  - `input` includes: `csvPath`, `maxItems`
  - `analysis` is object
  - `diff` is object (`summary`, `items`)
  - `apply` is `null`
- `command = "apply"`
  - `input` includes: `csvPath`, `maxItems`
  - `analysis` is object
  - `diff` is object (`summary`, `items`)
  - `apply` is object (`options`, `result`, `preview`)
- `command = "analyze"`
  - `input` includes: `csvPath`
  - `analysis` is object
  - `diff` is `null`
  - `apply` is `null`
- `command = "daily"`
  - `input` includes: `date`
  - `analysis` is `null`
  - `diff` is `null`
  - `apply` is object with `daily` summary and placeholder result/preview
- `command = "selftest"`
  - `input` includes: `keepDb`
  - `analysis` is `null`
  - `diff` is object (empty summary + empty items)
  - `apply` is object with `selftest.passed`

## Diff Items Schema

Each `diff.items[]` object uses fixed keys:

- `kind`
- `barcode`
- `oldName`
- `oldPriceMinor`
- `newName`
- `newPriceMinor`

## Apply Result Schema

`apply.result` fixed keys:

- `appliedInserted`
- `appliedUpdated`
- `noChange`
- `skipped`
- `errorsCount`
- `rolledBack`

`apply.preview` fixed keys:

- `maxItems`
- `items`

## Error Object

Each `errors[]` object:

- `code`: machine-readable error type
- `line`: line number in source input, nullable (`null` for runtime-level errors)
- `message`: human-readable error details

