import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { QA_REPLAY_POLICY, MAX_WIRE_BYTES, hashBytes, acceptedPostReplay,
  withEconomicBarrier, validateTerminalReplayEvidence } from '../../scripts/qa/catalog-import-replay-protocol.mjs';
import { executeTerminalReplay } from '../../scripts/qa/catalog-import-terminal-replay.mjs';

const jsonBytes = value => Buffer.from(JSON.stringify(value));
test('worker parses without executing its config, dependencies or provider bootstrap', () => {
  execFileSync(process.execPath, ['--check', fileURLToPath(new URL('../../scripts/qa/catalog-import-qa-worker.mjs', import.meta.url))], { windowsHide: true });
});
function fixture() {
  const qaScriptBindings = Object.fromEntries([['workerSha256', 'catalog-import-qa-worker.mjs'],
    ['terminalSha256', 'catalog-import-terminal-replay.mjs'], ['protocolSha256', 'catalog-import-replay-protocol.mjs']]
    .map(([key, file]) => [key, hashBytes(readFileSync(new URL('../../scripts/qa/' + file, import.meta.url))).slice(7)]));
  const context = { nonce: 'owned-toy-run', sourceCommit: 'c'.repeat(40), sourceTree: 'd'.repeat(40),
    inputsBindingManifestSha256: 'e'.repeat(64), fixtureManifestSha256: 'f'.repeat(64),
    workerScriptSha256: 'sha256:' + qaScriptBindings.workerSha256, terminalReplayScriptSha256: qaScriptBindings.terminalSha256,
    partCount: 2, planId: 'owned-plan', logicalRows: 3 };
  const raw = new Map();
  const request = j => ({ planId: context.planId, partIndex: j, shopDeviceId: 'owned-device', child: 'immutable-' + j });
  const ack = (j, terminal) => ({ ok: true, status: 'accepted', shopId: 'owned-shop', shopDeviceId: 'owned-device',
    payloadHash: 'payload', partCount: 2, offset: 0, limit: 1000, receiptEncoding: 'json',
    planId: context.planId, partIndex: j, clientImportId: 'child-' + j, idempotencyKey: 'identity-' + j,
    canonicalPayloadHash: 'canonical-' + j, receiptSha256: 'receipt-' + j, totalItemCount: j === 0 ? 2 : 1,
    complete: true, receipt: { summary: { stock: 1.25 }, ids: ['economic-id-' + j] },
    parentStatus: terminal || j === 1 ? 'complete' : 'partial', acceptedPartCount: terminal ? 2 : j + 1 });
  const frame = (index, action, req, res) => {
    const prefix = String(index).padStart(5, '0'), requestBytes = jsonBytes(req), responseBytes = jsonBytes(res);
    raw.set(prefix + '.request.json', requestBytes); raw.set(prefix + '.response.json', responseBytes);
    return { index, action, requestFile: prefix + '.request.json', responseFile: prefix + '.response.json',
      requestSha256: hashBytes(requestBytes), responseSha256: hashBytes(responseBytes),
      requestByteLength: requestBytes.length, responseByteLength: responseBytes.length,
      httpStatus: 200, qaReplayPolicy: QA_REPLAY_POLICY,
      responsePath: action === 'apply' ? 'SINGLE_APPLY_FIRST_RESPONSE' : 'BLOCKING_INBAND_OTHER_ACTION',
      inbandReplayMs: action === 'apply' ? null : 2 };
  };
  const other = frame(0, 'read', { cursor: 0 }, { ok: true });
  raw.set('00000.retry.response.json', raw.get(other.responseFile));
  const live = { schemaVersion: 'win7pos-phased-admin-schedule-v2', qaReplayPolicy: QA_REPLAY_POLICY,
    ...context, requests: [other, frame(1, 'apply', request(0), ack(0, false)), frame(2, 'apply', request(1), ack(1, false))] };
  const final = { ...structuredClone(live), requests: [...structuredClone(live.requests),
    frame(3, 'apply', request(0), ack(0, true)), frame(4, 'apply', request(1), ack(1, true))] };
  const policy = { schemaVersion: 'maximum-uniform-qa-replay-policy-v1', nonce: context.nonce,
    qaReplayPolicy: QA_REPLAY_POLICY, workerScriptSha256: context.workerScriptSha256,
    terminalReplayScriptSha256: context.terminalReplayScriptSha256, applyInbandReplay: false,
    otherAcceptedPostActionsInbandReplay: true, terminalReplayRequired: true,
    noConcurrentOrBackgroundReplay: true, clientDeadlineChanged: false };
  const terminal = { schemaVersion: 'maximum-uniform-terminal-replay-v1', nonce: context.nonce,
    status: 'PASS_REPLAY_SCOPE', parts: 2, requests: [0, 1].map(j => ({ partIndex: j,
      requestSha256: live.requests[j + 1].requestSha256, wireByteLength: live.requests[j + 1].requestByteLength,
      immutableIdentity: true, immutableReceipt: true, parentStatus: 'complete' })) };
  const retries = [{ index: 0, action: other.action, requestSha256: other.requestSha256,
    firstResponseSha256: other.responseSha256, retryResponseSha256: other.responseSha256,
    requestBytesUnchanged: true, responseBytesEqual: true }];
  const counts = { products: 3, exact_products: 3, prices: 6, purchase: 3, retail: 3,
    batches: 2, acks: 2, price_duplicates: 0, foreign_products: 0, foreign_prices: 0 };
  const membership = {};
  for (const key of ['productRows','activeProductRows','distinctProductIds','distinctBarcodes',
    'purchaseRows','retailRows','acceptedBatchItemSum','productHistoryOccurrences',
    'distinctProductsInHistory','planDeclaredItems','planCoverageRows']) membership[key] = 3;
  for (const key of ['priceRows','distinctPriceIds','priceHistoryOccurrences','distinctPricesInHistory']) membership[key] = 6;
  for (const key of ['wrongProductOwner','wrongPriceOwner','wrongPriceSource','priceParentMismatch',
    'productsWithoutExactlyTwoPrices','duplicateProductTypeEffective','emptyBatchAck','stockMovementRows',
    'productEventMembershipMismatch','priceEventMembershipMismatch','unsafeSyncEventMembership',
    'unsafeSyncEventOperation','sharedHistoryRows']) membership[key] = 0;
  for (const key of ['batchRows','planPartRows','planAckRows','planReceiptHashes','planDeclaredParts']) membership[key] = 2;
  Object.assign(membership, { originalRetirementRows: 1, stockMinimum: 1.25, stockMaximum: 1.25, stockTotal: 3.75 });
  const economicBefore = { schemaVersion: 'maximum-uniform-db-postcheck-v2', status: 'PASS_DB_SCOPE',
    label: 'before', nonce: context.nonce, sourceCommit: context.sourceCommit, sourceTree: context.sourceTree,
    inputsBindingManifestSha256: context.inputsBindingManifestSha256, fixtureManifestSha256: context.fixtureManifestSha256,
    logicalRows: 3, inputManifestSha256: '1'.repeat(64), csharpReadbackSha256: '2'.repeat(64), counts, membership,
    pages: ['inventory_products', 'inventory_product_prices'].map((table, j) => ({ table, pages: 2, rows: j === 0 ? 3 : 6,
      lastCursor: '00000000-0000-4000-8000-000000000001', nextCursor: null, terminal: true, contentSha256: String(j + 3).repeat(64) })) };
  const economicAfter = { ...structuredClone(economicBefore), label: 'after' };
  return { context, qaScriptBindings, raw, live, final, policy, terminal, retries, retryFiles: ['00000.retry.response.json'],
    economicBefore, economicAfter, readArtifact: name => { assert.ok(raw.has(name)); return raw.get(name); } };
}

test('FIRST apply does not invoke route replay; other POST keeps blocking immutable bytes', async () => {
  const bytes = jsonBytes({ partIndex: 0 }), responseBytes = jsonBytes({ ok: true });
  let calls = 0;
  const replayRoute = async value => { calls++; assert.equal(value, bytes); return { status: 200, bytes: responseBytes }; };
  assert.equal((await acceptedPostReplay({ action: 'apply', method: 'POST', bytes, status: 200, responseBytes, replayRoute })).responsePath, 'SINGLE_APPLY_FIRST_RESPONSE');
  assert.equal(calls, 0);
  assert.equal((await acceptedPostReplay({ action: 'read', method: 'POST', bytes, status: 200, responseBytes, replayRoute })).responsePath, 'BLOCKING_INBAND_OTHER_ACTION');
  assert.equal(calls, 1);
  await assert.rejects(acceptedPostReplay({ action: 'read', method: 'POST', bytes, status: 200, responseBytes,
    replayRoute: async () => ({ status: 200, bytes: jsonBytes({ changed: true }) }) }));
  await assert.rejects(acceptedPostReplay({ action: 'apply', method: 'POST', bytes: Buffer.alloc(MAX_WIRE_BYTES + 1), status: 200, responseBytes, replayRoute }));
});

test('schedule2 N+N/2N, full receipt/actor/progress and complete economics pass', () => {
  const result = validateTerminalReplayEvidence(fixture());
  assert.deepEqual(result, { qaReplayPolicy: QA_REPLAY_POLICY, liveApplyCount: 2, terminalApplyCount: 2,
    externalApplyFrameCount: 4, nonapplyInbandReplayCount: 1, economicBarrierPassed: true, deadlineChanged: false });
});

const defects = {
  'worker pin': f => { f.policy.workerScriptSha256 += 'bad'; },
  'terminal pin': f => { f.policy.terminalReplayScriptSha256 += 'bad'; },
  'source tuple': f => { f.final.sourceTree = '0'.repeat(40); },
  'original prefix': f => { f.final.requests[0].inbandReplayMs++; },
  'missing terminal part': f => { f.final.requests.pop(); },
  'duplicate request index': f => { f.final.requests[4].index = 3; },
  'escaped path': f => { f.final.requests[4].requestFile = '../00004.request.json'; },
  'raw mutation': f => { f.raw.set('00004.request.json', jsonBytes({ changed: true })); },
  'hidden apply inband file': f => { f.retryFiles.push('00001.retry.response.json'); },
  'missing K witness': f => { f.retries.length = 0; },
  'deadline change': f => { f.policy.clientDeadlineChanged = true; },
  'apply inband policy': f => { f.policy.applyInbandReplay = true; },
  'actor changed': f => changeAck(f, '00004.response.json', body => { body.shopDeviceId = 'other'; }),
  'nested receipt changed': f => changeAck(f, '00004.response.json', body => { body.receipt.ids[0] = 'different-id'; }),
  'missing stable key': f => changeAck(f, '00004.response.json', body => { delete body.receiptSha256; }),
  'terminal incomplete': f => changeAck(f, '00004.response.json', body => { body.parentStatus = 'partial'; }),
  'terminal progress wrong': f => changeAck(f, '00004.response.json', body => { body.acceptedPartCount = 1; }),
  'item count wrong': f => changeAck(f, '00004.response.json', body => { body.totalItemCount = 1001; }),
  'economic extra counter changed': f => { f.economicAfter.membership.extra = 1; },
  'economic page digest changed': f => { f.economicAfter.pages[0].contentSha256 = '9'.repeat(64); },
  'economic missing table': f => { f.economicAfter.pages.pop(); },
  'invalid unchanged economic counts': f => { f.economicBefore.counts.prices = f.economicAfter.counts.prices = 7; },
  'missing economic count': f => { delete f.economicBefore.membership.unsafeSyncEventMembership; delete f.economicAfter.membership.unsafeSyncEventMembership; },
  'wrong unchanged stock': f => { f.economicBefore.membership.stockTotal = f.economicAfter.membership.stockTotal = 0; }
};
function changeAck(f, name, change) {
  const body = JSON.parse(f.raw.get(name)); change(body); const bytes = jsonBytes(body); f.raw.set(name, bytes);
  const frame = f.final.requests.find(value => value.responseFile === name);
  frame.responseSha256 = hashBytes(bytes); frame.responseByteLength = bytes.length;
}
for (const [label, change] of Object.entries(defects)) test('rejects ' + label, () => {
  const f = fixture(); change(f); assert.throws(() => validateTerminalReplayEvidence(f));
});

test('economic capture is strictly before -> awaited terminal -> after; changes fail', async () => {
  const f = fixture(), order = [];
  const result = await withEconomicBarrier({ context: f.context,
    captureEconomics: async stage => { order.push(stage); return stage === 'before' ? f.economicBefore : f.economicAfter; },
    replay: async () => { order.push('terminal'); return 'done'; } });
  assert.deepEqual(order, ['before', 'terminal', 'after']); assert.equal(result.economicBarrierPassed, true);
  f.economicAfter.pages[1].contentSha256 = '9'.repeat(64);
  await assert.rejects(withEconomicBarrier({ context: f.context,
    captureEconomics: async stage => stage === 'before' ? f.economicBefore : f.economicAfter, replay: async () => 'done' }));
});

test('terminal runner sends exactly N original bodies and writes PASS only after barrier/validation', async () => {
  const f = fixture(), root = mkdtempSync(join(tmpdir(), 'win7pos-replay-toy-'));
  try {
    for (const [name, bytes] of f.raw) if (!/^0000[34]\./.test(name)) writeFileSync(join(root, name), bytes);
    writeFileSync(join(root, 'schedule.json'), jsonBytes(f.live));
    writeFileSync(join(root, 'qa-replay-policy.json'), jsonBytes(f.policy));
    writeFileSync(join(root, 'immutable-retries.jsonl'), JSON.stringify(f.retries[0]) + '\n');
    const calls = [], order = [];
    const config = { ...f.context, schemaVersion: 'maximum-uniform-worker-config-v2', runRoot: root,
      hostWindowReleased: true, httpHost: '127.0.0.1', httpPort: 23456, replayEachRequest: false,
      qaReplayPolicy: QA_REPLAY_POLICY, finalPosInputBinding: { planId: f.context.planId, partCount: 2 },
      qaScriptBindings: f.qaScriptBindings };
    const result = await executeTerminalReplay({ config,
      captureEconomics: async stage => { order.push(stage); return stage === 'before' ? f.economicBefore : f.economicAfter; },
      postApply: async bytes => {
        order.push('apply'); const j = calls.length; calls.push(bytes);
        assert.deepEqual(bytes, f.raw.get('0000' + (j + 1) + '.request.json'));
        const index = j + 3;
        for (const suffix of ['request.json', 'response.json']) writeFileSync(join(root, '0000' + index + '.' + suffix), f.raw.get('0000' + index + '.' + suffix));
        writeFileSync(join(root, 'schedule.json'), jsonBytes({ ...f.live, requests: f.final.requests.slice(0, index + 1) }));
        return { status: 200, bytes: f.raw.get('0000' + index + '.response.json') };
      } });
    assert.deepEqual(order, ['before', 'apply', 'apply', 'after']); assert.equal(calls.length, 2);
    assert.equal(result.validation.externalApplyFrameCount, 4);
    assert.equal(JSON.parse(readFileSync(join(root, 'qa-replay-validation.json'))).economicBarrierPassed, true);
    await assert.rejects(executeTerminalReplay({ config, postApply: async () => { throw Error('must not rerun'); }, captureEconomics: async () => f.economicBefore }));
  } finally { rmSync(root, { recursive: true, force: true }); }
});
