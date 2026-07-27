# WIN7POS-CATALOG-TOLERANCE-001 evidence

## Cross-platform fixture provenance

- Admin source: `XNIW/merchandise-control-admin-web`, commit
  `96e9dc52e4c558099762d70e93357b33ec17c20c`, path
  `tests/fixtures/catalog-text-policy-v1.json`.
- Android source: `XNIW/MerchandiseControlSplitView`, path
  `app/src/test/resources/fixtures/catalog-text-policy-v1.json`.
- iOS source: `XNIW/iOSMerchandiseControl`, path
  `iOSMerchandiseControlTests/Fixtures/CATALOG-TEXT-001/catalog-text-policy-v1.json`.
- All three resolve to Git blob `63e527a9259ca778fafdf49bb15979996d17c55b`.
- Source SHA-256: `1cec15e9c623fb78ce7cfc27225e135fe5afea78be3b9ff1653369a0366ae9a6`.
- Vendored SHA-256: `1cec15e9c623fb78ce7cfc27225e135fe5afea78be3b9ff1653369a0366ae9a6`.

The common fixture remains unchanged. The Win7POS-specific consumer fixture
documents additional client-only recovery warnings without changing the common
strict identity policy.

## Privacy boundary

Only counters, correction categories and catalog revision are persisted. No
catalog name, barcode, remote ID, token, credential or response body is written
to the warning summary or UI diagnostics.
