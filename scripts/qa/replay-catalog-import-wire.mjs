// Replays unedited C# HTTP bodies through the actual Admin foundation-test loader.
// RPC/lease fixtures are those of the official foundation runner. This is parser/
// handler evidence, not an assertion that a shared or deployed database was changed.
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';
import { resolve } from 'node:path';
import { Script, createContext } from 'node:vm';

const [adminRoot, corpusRoot, dependencyRoot, expectation = 'baseline-rejection'] = process.argv.slice(2);
if (!adminRoot || !corpusRoot || !dependencyRoot || !['baseline-rejection', 'accept'].includes(expectation))
  throw new Error('Usage: node replay-catalog-import-wire.mjs ADMIN_ROOT CORPUS_ROOT DEPENDENCY_ROOT [baseline-rejection|accept]');
const require = createRequire(resolve(dependencyRoot, 'package.json'));
const ts = require('typescript');
const manifest = JSON.parse(readFileSync(resolve(corpusRoot, 'manifest.json')));
for (const item of manifest.files) {
  const bytes = readFileSync(resolve(corpusRoot, item.path));
  assert.equal(bytes.length, item.bytes, item.path);
  assert.equal(createHash('sha256').update(bytes).digest('hex'), item.sha256, item.path);
}
const officialPath = resolve(adminRoot, 'tests/foundation/task-094-pos-catalog-import-receipt-runtime.test.mjs');
const officialSource = readFileSync(officialPath, 'utf8');
let loader = officialSource.slice(0, officialSource.indexOf("test('receipt recalculates"));
assert.ok(loader.includes('function load('), 'Official foundation loader not found; review runner changes.');
loader = loader.replace("import.meta.url", JSON.stringify(new URL('file:///' + officialPath.replaceAll('\\', '/')).href))
  .replace("'contracts/pos-catalog-import-receipt-v1/lookup.request.json'", JSON.stringify(resolve(corpusRoot, 'lookup.request.json')))
  .replace("return {service:module(resolve('src/server/pos-auth/catalog-import-receipt.ts'))", "return {syncService:module(resolve('src/server/pos-auth/catalog-import-sync.ts')),service:module(resolve('src/server/pos-auth/catalog-import-receipt.ts'))");
loader += '\nmodule.exports = {load};';
const output = ts.transpileModule(loader, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS, esModuleInterop: true } }).outputText;
const loaded = { exports: {} };
const previous = process.cwd();
process.chdir(adminRoot);
const foundationRequire = id => id === 'typescript' ? ts : require(id);
new Script(output, { filename: officialPath }).runInContext(createContext({ exports: loaded.exports, module: loaded,
  require: foundationRequire, Date, Map, Set, Buffer, process, console }));
const reports = [];
try {
  for (const file of manifest.files.filter(item => item.path.endsWith('.request.json'))) {
    const bytes = readFileSync(resolve(corpusRoot, file.path));
    const input = JSON.parse(bytes.toString('utf8'));
    const state = loaded.exports.load({ result: args => ({ ok: true,
      shopId: '10000000-0000-4000-8000-000000000094', shopDeviceId: input.shopDeviceId,
      clientImportId: args.p_client_import_id, idempotencyKey: args.p_idempotency_key, payloadHash: args.p_payload_hash,
      status: input.schemaVersion.includes('retirement') ? 'retired' : input.schemaVersion.includes('correction') ? 'conflict' : 'not_found',
      oldIdentityBlocked: true, retiredAt: '2026-10-08T20:00:00.000000Z', reason: 'revision_conflict' }) });
    if (file.path === 'ordinary.request.json') {
      const parsed = state.syncService.parseCatalogImportInput(input);
      assert.ok(parsed, 'Control: real ordinary HTTP request must parse.');
      reports.push({ path: file.path, sha256: file.sha256, parserAccepted: true, canonicalHash: parsed.payloadHash, ordinaryAttemptCount: input.batch.attemptCount });
      continue;
    }
    const correction = input.schemaVersion === 'pos-catalog-import-correction-v1';
    const retirement = input.schemaVersion === 'pos-catalog-import-retirement-v1';
    const parsed = correction ? state.correctionService.parsePosCatalogImportCorrection(input)
      : state.service.parsePosCatalogImportReceipt(input, retirement);
    const result = correction ? await state.correctionService.handlePosCatalogImportCorrection(input)
      : await state.service.handlePosCatalogImportReceipt(input, {}, retirement);
    reports.push({ path: file.path, sha256: file.sha256, parserAccepted: Boolean(parsed), httpStatus: result.status,
      response: result.body, calls: state.calls.map(call => call.name), canonicalHash: parsed?.payloadHash ?? parsed?.canonicalPayloadHash });
    if (expectation === 'baseline-rejection') {
      assert.equal(parsed, null, file.path);
      assert.equal(result.status, 400, file.path);
      assert.equal(result.body.code, 'validation_failed', file.path);
      assert.equal(state.calls.length, 0, 'Baseline rejects before authorization/storage.');
    } else {
      assert.ok(parsed, file.path);
      assert.equal(result.status, 200, file.path);
    }
  }
} finally {
  process.chdir(previous);
  writeFileSync(resolve(corpusRoot, 'admin-handler-replay.' + expectation + '.json'), JSON.stringify({
    expectation, evidenceScope: 'actual Admin parser and handler; official foundation lease/RPC fixtures; no database mutation',
    adminRoot, officialLoaderSha256: createHash('sha256').update(officialSource).digest('hex'), reports
  }, null, 2));
}
console.log(JSON.stringify({ expectation, requests: reports.length, actualHandlerRequests: reports.filter(item => item.httpStatus).length,
  acceptedParsers: reports.filter(item => item.parserAccepted).length, rejected400: reports.filter(item => item.httpStatus === 400).length }));
