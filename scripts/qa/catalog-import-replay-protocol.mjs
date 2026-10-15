// QA only. No database/provider, timers, background replay or product deadlines.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';

export const QA_REPLAY_POLICY = 'apply-single-first-response-terminal-idempotence-v1';
export const MAX_WIRE_BYTES = 524288;
export const hashBytes = bytes => 'sha256:' + createHash('sha256').update(bytes).digest('hex');
export const STABLE_ACK_KEYS = Object.freeze(['ok', 'shopId', 'shopDeviceId', 'payloadHash',
  'partCount', 'offset', 'limit', 'receiptEncoding', 'planId', 'partIndex', 'clientImportId',
  'idempotencyKey', 'canonicalPayloadHash', 'receiptSha256', 'totalItemCount', 'complete', 'receipt']);

function requiredEqual(left, right, keys, label) {
  for (const key of keys) {
    assert.ok(Object.hasOwn(left, key) && Object.hasOwn(right, key), label + ': missing ' + key);
    assert.deepEqual(left[key], right[key], label + ': ' + key);
  }
}

export function assertAcceptedPart(body, request, context) {
  assert.equal(body.ok, true);
  assert.equal(body.status, 'accepted');
  assert.equal(request.planId, context.planId);
  assert.equal(body.planId, request.planId);
  assert.ok(typeof request.shopDeviceId === 'string' && request.shopDeviceId.length > 0, 'Owned request device required');
  assert.equal(body.shopDeviceId, request.shopDeviceId);
  assert.ok(typeof body.shopId === 'string' && body.shopId.length > 0);
  if (context.shopId !== undefined) assert.equal(body.shopId, context.shopId);
  assert.ok(Number.isInteger(request.partIndex) && request.partIndex >= 0 && request.partIndex < context.partCount);
  assert.equal(body.partIndex, request.partIndex);
  assert.equal(body.partCount, context.partCount);
  assert.ok(Number.isInteger(body.totalItemCount) && body.totalItemCount >= 1 && body.totalItemCount <= 1000);
}

export function assertImmutableTerminalAck(first, terminal, request, context) {
  assertAcceptedPart(first, request, context);
  assertAcceptedPart(terminal, request, context);
  requiredEqual(first, terminal, STABLE_ACK_KEYS, 'persisted ACK JSON');
  assert.equal(first.parentStatus, request.partIndex === context.partCount - 1 ? 'complete' : 'partial');
  assert.equal(first.acceptedPartCount, request.partIndex + 1);
  assert.equal(terminal.parentStatus, 'complete');
  assert.equal(terminal.acceptedPartCount, context.partCount);
}

// The FIRST response already exists. Apply never calls replayRoute here.
// Other successful POST actions retain their blocking, byte-identical replay.
export async function acceptedPostReplay({ action, method, bytes, status, responseBytes, replayRoute }) {
  if (status !== 200 || method !== 'POST') return { responsePath: 'NO_ACCEPTED_POST_REPLAY', retryBytes: null };
  assert.ok(bytes.length <= MAX_WIRE_BYTES, 'Accepted entire UTF8 request exceeds wire cap');
  if (action === 'apply') return { responsePath: 'SINGLE_APPLY_FIRST_RESPONSE', retryBytes: null };
  const retry = await replayRoute(bytes);
  assert.equal(retry.status, status, 'Immutable retry HTTP status');
  const retryBytes = Buffer.from(retry.bytes);
  assert.deepEqual(retryBytes, responseBytes, 'Immutable retry durable response bytes');
  return { responsePath: 'BLOCKING_INBAND_OTHER_ACTION', retryBytes };
}

// Callbacks are supplied by the owned fixture controller, never by a provider.
// Capture complete economic snapshots on both sides of one awaited terminal run.
export function normalizedEconomicSnapshot(snapshot, context) {
  assert.equal(snapshot?.schemaVersion, 'maximum-uniform-db-postcheck-v2');
  assert.equal(snapshot.status, 'PASS_DB_SCOPE');
  for (const key of ['nonce', 'sourceCommit', 'sourceTree', 'logicalRows',
    'inputsBindingManifestSha256', 'fixtureManifestSha256']) {
    assert.notEqual(context[key], undefined, 'Economic context required: ' + key);
    assert.equal(snapshot[key], context[key], 'Economic binding: ' + key);
  }
  assert.ok(Number.isInteger(context.logicalRows) && context.logicalRows > 0);
  assert.match(snapshot.inputManifestSha256, /^[a-f0-9]{64}$/);
  assert.match(snapshot.csharpReadbackSha256, /^[a-f0-9]{64}$/);
  const count = (object, key, expected) => {
    assert.ok(Object.hasOwn(object, key) && (typeof object[key] === 'number' || typeof object[key] === 'string'), 'Required count: ' + key);
    assert.equal(Number(object[key]), expected, key);
  };
  const rows = context.logicalRows, parts = context.partCount;
  assert.ok(Number.isInteger(parts) && parts > 0);
  for (const key of ['products', 'exact_products', 'purchase', 'retail']) count(snapshot.counts, key, rows);
  count(snapshot.counts, 'prices', 2 * rows);
  for (const key of ['acks', 'batches']) count(snapshot.counts, key, parts);
  for (const key of ['price_duplicates', 'foreign_products', 'foreign_prices']) count(snapshot.counts, key, 0);
  for (const key of ['productRows', 'activeProductRows', 'distinctProductIds', 'distinctBarcodes',
    'purchaseRows', 'retailRows', 'acceptedBatchItemSum', 'productHistoryOccurrences',
    'distinctProductsInHistory', 'planDeclaredItems', 'planCoverageRows']) count(snapshot.membership, key, rows);
  for (const key of ['priceRows', 'distinctPriceIds', 'priceHistoryOccurrences', 'distinctPricesInHistory']) count(snapshot.membership, key, 2 * rows);
  for (const key of ['wrongProductOwner', 'wrongPriceOwner', 'wrongPriceSource', 'priceParentMismatch',
    'productsWithoutExactlyTwoPrices', 'duplicateProductTypeEffective', 'emptyBatchAck', 'stockMovementRows',
    'productEventMembershipMismatch', 'priceEventMembershipMismatch', 'unsafeSyncEventMembership',
    'unsafeSyncEventOperation', 'sharedHistoryRows']) count(snapshot.membership, key, 0);
  for (const key of ['batchRows', 'planPartRows', 'planAckRows', 'planReceiptHashes', 'planDeclaredParts']) count(snapshot.membership, key, parts);
  count(snapshot.membership, 'originalRetirementRows', 1);
  for (const key of ['stockMinimum', 'stockMaximum']) count(snapshot.membership, key, 1.25);
  count(snapshot.membership, 'stockTotal', rows * 1.25);
  assert.equal(snapshot.pages.length, 2);
  for (let j = 0; j < 2; j++) {
    const page = snapshot.pages[j], expected = j === 0 ? rows : 2 * rows;
    assert.equal(page.table, j === 0 ? 'inventory_products' : 'inventory_product_prices');
    assert.equal(page.rows, expected); assert.equal(page.pages, Math.ceil(expected / 1000) + 1);
    assert.equal(page.terminal, true); assert.equal(page.nextCursor, null);
    assert.match(page.lastCursor, /^[a-f0-9-]{36}$/); assert.match(page.contentSha256, /^[a-f0-9]{64}$/);
  }
  // Only the controller's phase label changes; all economic/source fields,
  // additional counters, page digests and limits remain part of equality.
  const { label, ...economic } = snapshot;
  return structuredClone(economic);
}

export async function withEconomicBarrier({ captureEconomics, replay, context }) {
  const frozenBefore = normalizedEconomicSnapshot(await captureEconomics('before'), context);
  const result = await replay();
  const after = normalizedEconomicSnapshot(await captureEconomics('after'), context);
  assert.deepEqual(after, frozenBefore, 'Terminal replay changed economic snapshot');
  return { result, before: frozenBefore, after, economicBarrierPassed: true };
}

export function validateTerminalReplayEvidence({ live, final, policy, terminal, retries, retryFiles,
  context, readArtifact, economicBefore, economicAfter }) {
  assert.ok(economicBefore && economicAfter, 'Economic-before and economic-after are mandatory');
  assert.deepEqual(normalizedEconomicSnapshot(economicAfter, context), normalizedEconomicSnapshot(economicBefore, context), 'Every economic page/count/digest must be unchanged');
  const n = context.partCount;
  assert.ok(Number.isInteger(n) && n > 0);
  for (const doc of [live, final]) {
    assert.equal(doc.schemaVersion, 'win7pos-phased-admin-schedule-v2');
    assert.equal(doc.qaReplayPolicy, QA_REPLAY_POLICY);
    requiredEqual(doc, context, ['nonce', 'sourceCommit', 'sourceTree', 'inputsBindingManifestSha256'], 'schedule binding');
    assert.ok(Array.isArray(doc.requests));
  }
  assert.equal(policy.schemaVersion, 'maximum-uniform-qa-replay-policy-v1');
  assert.equal(policy.nonce, context.nonce);
  assert.equal(policy.qaReplayPolicy, QA_REPLAY_POLICY);
  assert.equal(policy.workerScriptSha256, context.workerScriptSha256);
  assert.equal(policy.terminalReplayScriptSha256, context.terminalReplayScriptSha256);
  for (const key of ['otherAcceptedPostActionsInbandReplay', 'terminalReplayRequired', 'noConcurrentOrBackgroundReplay']) assert.equal(policy[key], true, key);
  for (const key of ['applyInbandReplay', 'clientDeadlineChanged']) assert.equal(policy[key], false, key);
  const firstApply = live.requests.filter(frame => frame.action === 'apply');
  const allApply = final.requests.filter(frame => frame.action === 'apply');
  assert.equal(firstApply.length, n);
  assert.equal(allApply.length, 2 * n);
  assert.equal(final.requests.length, live.requests.length + n);
  assert.deepEqual(final.requests.slice(0, live.requests.length), live.requests, 'Immutable original-live prefix');
  const frames = new Map();
  for (const frame of final.requests) {
    assert.ok(Number.isSafeInteger(frame.index) && frame.index >= 0 && !frames.has(frame.index), 'Unique request index');
    const prefix = String(frame.index).padStart(5, '0');
    assert.equal(frame.requestFile, prefix + '.request.json');
    assert.equal(frame.responseFile, prefix + '.response.json');
    const request = readArtifact(frame.requestFile), response = readArtifact(frame.responseFile);
    assert.equal(hashBytes(request), frame.requestSha256);
    assert.equal(hashBytes(response), frame.responseSha256);
    assert.equal(request.length, frame.requestByteLength);
    assert.equal(response.length, frame.responseByteLength);
    assert.ok(request.length <= MAX_WIRE_BYTES);
    assert.equal(frame.httpStatus, 200);
    assert.equal(frame.qaReplayPolicy, QA_REPLAY_POLICY);
    frames.set(frame.index, frame);
  }
  const nonapply = live.requests.filter(frame => frame.responsePath === 'BLOCKING_INBAND_OTHER_ACTION');
  assert.equal(retries.length, nonapply.length, 'K nonapply witnesses');
  assert.equal(retryFiles.length, nonapply.length, 'No hidden apply retry or orphan file');
  const retryIndexes = new Set();
  for (const retry of retries) {
    assert.ok(!retryIndexes.has(retry.index) && frames.has(retry.index));
    retryIndexes.add(retry.index);
    const frame = frames.get(retry.index);
    assert.notEqual(frame.action, 'apply');
    assert.equal(frame.responsePath, 'BLOCKING_INBAND_OTHER_ACTION');
    assert.equal(retry.action, frame.action);
    assert.equal(retry.requestSha256, frame.requestSha256);
    const name = String(frame.index).padStart(5, '0') + '.retry.response.json';
    assert.ok(retryFiles.includes(name));
    const bytes = readArtifact(name);
    assert.equal(hashBytes(bytes), retry.retryResponseSha256);
    assert.equal(retry.firstResponseSha256, frame.responseSha256);
    assert.equal(retry.retryResponseSha256, frame.responseSha256);
    assert.equal(bytes.length, frame.responseByteLength);
    assert.equal(retry.requestBytesUnchanged, true);
    assert.equal(retry.responseBytesEqual, true);
    assert.ok(Number.isFinite(frame.inbandReplayMs) && frame.inbandReplayMs >= 0);
  }
  for (const frame of nonapply) assert.ok(retryIndexes.has(frame.index));
  assert.equal(terminal.schemaVersion, 'maximum-uniform-terminal-replay-v1');
  assert.equal(terminal.nonce, context.nonce);
  assert.equal(terminal.status, 'PASS_REPLAY_SCOPE');
  assert.equal(terminal.parts, n);
  assert.equal(terminal.requests.length, n);
  const hashes = new Set();
  for (let j = 0; j < n; j++) {
    const first = firstApply[j], again = final.requests[live.requests.length + j];
    assert.equal(again.action, 'apply');
    for (const frame of [first, again]) {
      assert.equal(frame.responsePath, 'SINGLE_APPLY_FIRST_RESPONSE');
      assert.equal(frame.inbandReplayMs, null);
      assert.ok(!retryFiles.includes(String(frame.index).padStart(5, '0') + '.retry.response.json'));
    }
    assert.equal(first.requestSha256, again.requestSha256);
    assert.equal(first.requestByteLength, again.requestByteLength);
    assert.ok(!hashes.has(first.requestSha256)); hashes.add(first.requestSha256);
    // Decode only the current bounded apply pair, not large original upload bodies.
    const request = JSON.parse(readArtifact(first.requestFile));
    assert.deepEqual(JSON.parse(readArtifact(again.requestFile)), request);
    assert.equal(request.partIndex, j);
    const firstAck = JSON.parse(readArtifact(first.responseFile));
    const lastAck = JSON.parse(readArtifact(again.responseFile));
    assertImmutableTerminalAck(firstAck, lastAck, request, context);
    const witness = terminal.requests[j];
    assert.equal(witness.partIndex, j);
    assert.equal(witness.requestSha256, first.requestSha256);
    assert.equal(witness.wireByteLength, first.requestByteLength);
    assert.equal(witness.immutableIdentity, true);
    assert.equal(witness.immutableReceipt, true);
    assert.equal(witness.parentStatus, 'complete');
  }
  return { qaReplayPolicy: QA_REPLAY_POLICY, liveApplyCount: n, terminalApplyCount: n,
    externalApplyFrameCount: 2 * n, nonapplyInbandReplayCount: nonapply.length,
    economicBarrierPassed: true, deadlineChanged: false };
}
