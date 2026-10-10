// Replays unedited C# HTTP bodies through the actual Admin foundation-test loader.
// RPC/lease fixtures are those of the official foundation runner. This is parser/
// handler evidence, not an assertion that a shared or deployed database was changed.
import assert from 'node:assert/strict';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';
import { resolve } from 'node:path';
import { Script, createContext } from 'node:vm';

const [adminRoot, corpusRoot, dependencyRoot, expectation = 'accept'] = process.argv.slice(2);
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
const trustFixture = existsSync(resolve(corpusRoot, 'lookup.request.json')) ? 'lookup.request.json'
  : manifest.files.find(item => item.path.endsWith('.request.json') || item.path.endsWith('.candidate.json'))?.path;
assert.ok(trustFixture, 'Corpus must contain at least one real HTTP request.');
let loader = officialSource.slice(0, officialSource.indexOf("test('receipt recalculates"));
assert.ok(loader.includes('function load('), 'Official foundation loader not found; review runner changes.');
loader = loader.replace("import.meta.url", JSON.stringify(new URL('file:///' + officialPath.replaceAll('\\', '/')).href))
  .replace("'contracts/pos-catalog-import-receipt-v1/lookup.request.json'", JSON.stringify(resolve(corpusRoot, trustFixture)))
  .replace('Date,Map,Set,Buffer,process,console', 'Date,Map,Set,Buffer,process,console,Request,Response,TextDecoder,Uint8Array')
  .replace("return {service:module(resolve('src/server/pos-auth/catalog-import-receipt.ts'))", "return {bodyReader:module(resolve('src/app/api/pos/_shared/pos-route-security.ts')),syncService:module(resolve('src/server/pos-auth/catalog-import-sync.ts')),service:module(resolve('src/server/pos-auth/catalog-import-receipt.ts'))");
loader += '\nmodule.exports = {load};';
const output = ts.transpileModule(loader, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS, esModuleInterop: true } }).outputText;
const loaded = { exports: {} };
const previous = process.cwd();
process.chdir(adminRoot);
const foundationRequire = id => id === 'typescript' ? ts : require(id);
new Script(output, { filename: officialPath }).runInContext(createContext({ exports: loaded.exports, module: loaded,
  require: foundationRequire, Date, Map, Set, Buffer, process, console, Request, Response, TextDecoder, Uint8Array }));
const reports = [];
const checks = [];
try {
  for (const file of manifest.files.filter(item => item.path.endsWith('.request.json') || item.path.endsWith('.candidate.json'))) {
    const bytes = readFileSync(resolve(corpusRoot, file.path));
    const input = JSON.parse(bytes.toString('utf8'));
    const state = loaded.exports.load({ result: args => ({ ok: true,
      shopId: '10000000-0000-4000-8000-000000000094', shopDeviceId: input.shopDeviceId,
      clientImportId: args.p_client_import_id, idempotencyKey: args.p_idempotency_key, payloadHash: args.p_payload_hash,
      status: input.schemaVersion.includes('retirement') ? 'retired' : input.schemaVersion.includes('correction') ? 'conflict' : 'not_found',
      oldIdentityBlocked: true, retiredAt: '2026-10-08T20:00:00.000000Z', reason: 'revision_conflict' }) });
    for (const hasLength of [false, true]) {
      const headers = { 'content-type': 'application/json; charset=utf-8' };
      if (hasLength) headers['content-length'] = String(bytes.length);
      const request = new Request('https://fixture.example.invalid/api/pos/catalog/import-sync', { method: 'POST', headers, body: bytes });
      const bounded = await state.bodyReader.readPosJsonBody(request, { maxBytes: state.syncService.MAX_POS_CATALOG_IMPORT_JSON_BODY_BYTES });
      assert.equal(Boolean(bounded), bytes.length <= 512 * 1024, 'Actual Admin bounded reader must enforce the entire UTF-8 body.');
      if (bounded) assert.equal(JSON.stringify(bounded), JSON.stringify(input));
      checks.push({ path: file.path, check: 'actual-bounded-body-reader', hasLength, bytes: bytes.length, accepted: Boolean(bounded) });
    }
    if (bytes.length > 512 * 1024) {
      const response = await state.syncService.handlePosCatalogImportSync(null);
      assert.equal(response.status, 400);
      assert.equal(state.calls.length, 0);
      reports.push({ path: file.path, sha256: file.sha256, bytes: bytes.length, blockedByBodyReader: true,
        httpStatus: response.status, response: response.body, actuallySentByClient: false });
      continue;
    }
    if (input.schemaVersion === 'pos-catalog-import-v1') {
      const parsed = state.syncService.parseCatalogImportInput(input);
      assert.ok(parsed, 'Control: real ordinary HTTP request must parse.');
      reports.push({ path: file.path, sha256: file.sha256, parserAccepted: true, canonicalHash: parsed.payloadHash, ordinaryAttemptCount: input.batch.attemptCount });
      for (const attempt of [undefined, 0, 1, 17]) {
        const variant = structuredClone(input);
        if (attempt === undefined) delete variant.batch.attemptCount;
        else variant.batch.attemptCount = attempt;
        const result = state.syncService.parseCatalogImportInput(variant);
        assert.equal(Boolean(result), (attempt ?? 0) > 0, 'Ordinary import must retain its positive attempt requirement.');
        if (result) assert.equal(result.payloadHash, parsed.payloadHash, 'Attempt metadata must not alter canonical business hash.');
        checks.push({ path: file.path, check: 'ordinary-attempt', attempt: attempt ?? 'omitted', parserAccepted: Boolean(result) });
      }
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
    assert.deepEqual(JSON.parse(bytes.toString('utf8')), input, 'Parser/handler must not mutate the original input.');
    let canonicalHash;
    for (const attempt of [undefined, 0, 1, 17]) {
      const variant = structuredClone(input);
      const original = correction ? variant.recoveryOf.originalRequest
        : variant.originalRequest.schemaVersion === 'pos-catalog-import-correction-v1'
          ? variant.originalRequest.recoveryOf.originalRequest : variant.originalRequest;
      if (attempt === undefined) delete original.batch.attemptCount;
      else original.batch.attemptCount = attempt;
      const value = correction ? state.correctionService.parsePosCatalogImportCorrection(variant)
        : state.service.parsePosCatalogImportReceipt(variant, retirement);
      const shouldAccept = expectation === 'accept' || (attempt ?? 0) > 0;
      assert.equal(Boolean(value), shouldAccept, `${file.path}: original attempt=${attempt}`);
      if (value) {
        const hash = correction ? value.canonicalPayloadHash : value.payloadHash;
        canonicalHash ??= hash;
        assert.equal(hash, canonicalHash, 'Only attempt metadata changed; canonical hash must stay stable.');
      }
      checks.push({ path: file.path, check: 'forensic-original-attempt', attempt: attempt ?? 'omitted', parserAccepted: Boolean(value) });
    }
    if (expectation === 'accept') {
      for (const defect of ['payloadHash', 'clientImportId', 'idempotencyKey', 'original-shop', 'original-device']) {
        const variant = structuredClone(input);
        const binding = correction ? variant.recoveryOf : variant;
        const original = correction ? variant.recoveryOf.originalRequest : variant.originalRequest;
        if (defect === 'original-shop') original.shopCode = 'WRONG';
        else if (defect === 'original-device') original.shopDeviceId = '30000000-0000-4000-8000-000000000095';
        else binding[defect] += '-wrong';
        const value = correction ? state.correctionService.parsePosCatalogImportCorrection(variant)
          : state.service.parsePosCatalogImportReceipt(variant, retirement);
        assert.equal(value, null, `${file.path}: ${defect} mismatch must fail closed.`);
        checks.push({ path: file.path, check: defect, parserAccepted: false });
      }
      for (const field of ['sessionToken', 'deviceToken', 'shopCode', 'shopDeviceId', 'posSessionId']) {
        const variant = structuredClone(input);
        variant[field] = field.endsWith('Id') ? '30000000-0000-4000-8000-000000000099' : 'wrong-current';
        // The official loader's lease fixture intentionally ignores requested IDs.
        // ID authorization belongs to that boundary; exercise its denied result and
        // verify the real handler forwarded the changed ID, rather than pretending
        // this loader executes the actual database lease/authentication check.
        const current = field.endsWith('Id') ? loaded.exports.load({ denied: true }) : state;
        const value = correction ? await current.correctionService.handlePosCatalogImportCorrection(variant)
          : await current.service.handlePosCatalogImportReceipt(variant, {}, retirement);
        assert.notEqual(value.status, 200, `${file.path}: wrong current ${field} cannot authorize.`);
        if (field.endsWith('Id') && value.status === 401)
          assert.equal(current.calls.find(call => call.name === 'lease').args[field], variant[field]);
        checks.push({ path: file.path, check: 'current-' + field, httpStatus: value.status,
          authorizationBoundary: field.endsWith('Id') ? 'official lease fixture explicitly denies; forwarded ID verified' : 'actual handler token/shop comparison' });
      }
    }
  }
} finally {
  process.chdir(previous);
  writeFileSync(resolve(corpusRoot, 'admin-handler-replay.' + expectation + '.json'), JSON.stringify({
    expectation, evidenceScope: 'actual Admin parser and handler; official foundation lease/RPC fixtures; no database mutation',
    adminRoot, officialLoaderSha256: createHash('sha256').update(officialSource).digest('hex'), reports, checks
  }, null, 2));
}
console.log(JSON.stringify({ expectation, requests: reports.length, actualHandlerRequests: reports.filter(item => item.httpStatus).length,
  acceptedParsers: reports.filter(item => item.parserAccepted).length, rejected400: reports.filter(item => item.httpStatus === 400).length }));
