// QA terminal replay. Importing this file never starts HTTP, SQL or a process.
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync, lstatSync, existsSync, readdirSync } from 'node:fs';
import { resolve, join, isAbsolute } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import { QA_REPLAY_POLICY, hashBytes, MAX_WIRE_BYTES, assertImmutableTerminalAck,
  withEconomicBarrier, validateTerminalReplayEvidence } from './catalog-import-replay-protocol.mjs';

export function ownedArtifactReader(root) {
  assert.ok(isAbsolute(root), 'Absolute owned runRoot required');
  const absolute = resolve(root);
  for (let cursor = absolute; ; cursor = resolve(cursor, '..')) {
    assert.ok(!lstatSync(cursor).isSymbolicLink(), 'Reparse ancestor refused');
    if (cursor === resolve(cursor, '..')) break;
  }
  assert.ok(lstatSync(absolute).isDirectory() && !lstatSync(absolute).isSymbolicLink());
  return name => {
    assert.match(name, /^[a-zA-Z0-9_.-]+$/, 'Exact basename required');
    const path = join(absolute, name);
    assert.ok(lstatSync(path).isFile() && !lstatSync(path).isSymbolicLink(), 'Regular owned artifact required');
    return readFileSync(path);
  };
}

export async function executeTerminalReplay({ config, postApply, captureEconomics }) {
  assert.equal(config.schemaVersion, 'maximum-uniform-worker-config-v2');
  assert.equal(config.hostWindowReleased, true);
  assert.equal(config.httpHost, '127.0.0.1');
  assert.equal(config.replayEachRequest, false);
  assert.equal(config.qaReplayPolicy, QA_REPLAY_POLICY);
  for (const [key, file] of [['workerSha256', './catalog-import-qa-worker.mjs'],
    ['terminalSha256', './catalog-import-terminal-replay.mjs'], ['protocolSha256', './catalog-import-replay-protocol.mjs']]) {
    assert.match(config.qaScriptBindings?.[key] ?? '', /^[a-f0-9]{64}$/);
    assert.equal(hashBytes(readFileSync(new URL(file, import.meta.url))), 'sha256:' + config.qaScriptBindings[key], 'Selected QA script binding: ' + key);
  }
  assert.ok(Number.isInteger(config.httpPort) && config.httpPort > 1024 && config.httpPort < 65536);
  const read = ownedArtifactReader(config.runRoot);
  const scheduleBytes = read('schedule.json'), schedule = JSON.parse(scheduleBytes);
  for (const key of ['nonce', 'sourceCommit', 'sourceTree', 'inputsBindingManifestSha256']) assert.equal(schedule[key], config[key]);
  assert.equal(schedule.schemaVersion, 'win7pos-phased-admin-schedule-v2');
  assert.equal(schedule.qaReplayPolicy, QA_REPLAY_POLICY);
  const records = schedule.requests.filter(record => record.action === 'apply');
  const context = { ...config, planId: config.finalPosInputBinding.planId,
    partCount: config.finalPosInputBinding.partCount,
    workerScriptSha256: 'sha256:' + config.qaScriptBindings.workerSha256,
    terminalReplayScriptSha256: config.qaScriptBindings.terminalSha256 };
  assert.equal(records.length, context.partCount);
  const policy = JSON.parse(read('qa-replay-policy.json'));
  assert.equal(policy.workerScriptSha256, context.workerScriptSha256);
  assert.equal(policy.terminalReplayScriptSha256, context.terminalReplayScriptSha256);
  writeFileSync(join(config.runRoot, 'original-live.schedule.json'), scheduleBytes, { flag: 'wx' });
  const barrier = await withEconomicBarrier({ captureEconomics, context, replay: async () => {
    const hashes = new Set(), result = [];
    for (let j = 0; j < records.length; j++) {
      const record = records[j], prefix = String(record.index).padStart(5, '0');
      assert.equal(record.requestFile, prefix + '.request.json');
      assert.equal(record.responseFile, prefix + '.response.json');
      const bytes = read(record.requestFile), firstBytes = read(record.responseFile);
      const hash = hashBytes(bytes);
      assert.equal(hash, record.requestSha256); assert.equal(bytes.length, record.requestByteLength);
      assert.equal(hashBytes(firstBytes), record.responseSha256); assert.equal(firstBytes.length, record.responseByteLength);
      assert.ok(bytes.length <= MAX_WIRE_BYTES && !hashes.has(hash)); hashes.add(hash);
      const request = JSON.parse(bytes), first = JSON.parse(firstBytes);
      assert.equal(request.partIndex, j);
      const response = await postApply(bytes);
      assert.equal(response.status, 200);
      const body = JSON.parse(response.bytes);
      assertImmutableTerminalAck(first, body, request, context);
      result.push({ partIndex: j, requestSha256: hash, wireByteLength: bytes.length,
        immutableIdentity: true, immutableReceipt: true, parentStatus: body.parentStatus });
    }
    return { schemaVersion: 'maximum-uniform-terminal-replay-v1', nonce: config.nonce,
      status: 'PASS_REPLAY_SCOPE', parts: result.length, requests: result };
  }});
  const final = JSON.parse(read('schedule.json'));
  const retries = readOptionalJsonLines(config.runRoot, 'immutable-retries.jsonl');
  const validation = validateTerminalReplayEvidence({ live: schedule, final, policy,
    terminal: barrier.result, retries, retryFiles: readdirSync(config.runRoot).filter(name => name.endsWith('.retry.response.json')),
    context, readArtifact: read, economicBefore: barrier.before, economicAfter: barrier.after });
  for (const [name, value] of [['terminal-ack-replay.json', barrier.result],
    ['terminal-economic-barrier.json', { before: barrier.before, after: barrier.after }],
    ['qa-replay-validation.json', validation]]) {
    writeFileSync(join(config.runRoot, name), JSON.stringify(value, null, 2), { flag: 'wx' });
  }
  return { terminal: barrier.result, validation };
}

function readOptionalJsonLines(root, name) {
  if (!existsSync(join(root, name))) return [];
  return ownedArtifactReader(root)(name).toString('utf8').split(/\r?\n/).filter(line => line.trim()).map(line => JSON.parse(line));
}

// The controller configures an already-reviewed economic reader; no SQL is
// embedded here. Its whole snapshot (all pages/counts/digests) must be JSON stdout.
async function main() {
  const index = process.argv.indexOf('--config');
  assert.ok(index >= 0 && process.argv[index + 1], '--config required');
  const config = JSON.parse(readFileSync(process.argv[index + 1], 'utf8').replace(/^\uFEFF/, ''));
  const command = config.economicSnapshotCommand;
  assert.ok(command && isAbsolute(command.scriptPath) && Array.isArray(command.arguments));
  assert.ok(command.arguments.every(value => typeof value === 'string'));
  assert.equal(hashBytes(readFileSync(command.scriptPath)), 'sha256:' + command.scriptSha256);
  const result = await executeTerminalReplay({ config,
    captureEconomics: stage => JSON.parse(execFileSync(process.execPath,
      [command.scriptPath, ...command.arguments, '--economic-stage', stage],
      { encoding: 'utf8', windowsHide: true, maxBuffer: 64 * 1024 * 1024 })),
    postApply: async bytes => {
      const response = await fetch('http://127.0.0.1:' + config.httpPort + '/api/pos/catalog/import-recovery/apply',
        { method: 'POST', headers: { 'content-type': 'application/json' }, body: bytes });
      return { status: response.status, bytes: Buffer.from(await response.arrayBuffer()) };
    } });
  console.log(JSON.stringify({ replayed: result.terminal.parts, economicBarrierPassed: true }));
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) await main();
