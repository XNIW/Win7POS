// Read-only canonicalization bridge for generated supersession evidence.
// The official Admin foundation loader supplies its real parser. No captured
// HTTP bytes are edited: only the parser's separate transport projection gets
// the current synthetic trust, as the multipart handler does for a child.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';
import { execFileSync } from 'node:child_process';
import { resolve } from 'node:path';
import { Script, createContext } from 'node:vm';

const [adminRoot, dependencyRoot] = process.argv.slice(2);
if (!adminRoot || !dependencyRoot) throw new Error('Usage: node catalog-import-canonical-hash.mjs ADMIN_ROOT DEPENDENCY_ROOT < typed-input.json');
const input = JSON.parse(readFileSync(0, 'utf8'));
const require = createRequire(resolve(dependencyRoot, 'package.json'));
const ts = require('typescript');
const officialPath = resolve(adminRoot, 'tests/foundation/task-094-pos-catalog-import-receipt-runtime.test.mjs');
const officialSource = readFileSync(officialPath, 'utf8');
let loader = officialSource.slice(0, officialSource.indexOf("test('receipt recalculates"));
assert.ok(loader.includes('function load('));
loader = loader.replace('import.meta.url', JSON.stringify(new URL('file:///' + officialPath.replaceAll('\\', '/')).href));
loader += '\nmodule.exports = {load};';
const output = ts.transpileModule(loader, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS, esModuleInterop: true } }).outputText;
process.chdir(adminRoot);
const loaded = { exports: {} };
new Script(output, { filename: officialPath }).runInContext(createContext({ exports: loaded.exports, module: loaded,
  require: id => id === 'typescript' ? ts : require(id), Date, Map, Set, Buffer, process, console }));
const original = JSON.parse(input.childJson);
assert.equal(original.schemaVersion, 'pos-catalog-import-v1');
const projected = { ...original, ...input.trust };
const parsed = loaded.exports.load().ordinaryService.parseForensicCatalogImportInput(projected);
assert.ok(parsed, 'The actual Admin forensic parser must accept the typed child projection.');
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
process.stdout.write(JSON.stringify({ canonicalPayloadHash: parsed.payloadHash, childJsonSha256: hash(input.childJson),
  adminSourceCommit: execFileSync('git', ['-C', adminRoot, 'rev-parse', 'HEAD'], { encoding: 'utf8', windowsHide: true }).trim(),
  parserPath: 'src/server/pos-auth/catalog-import-sync.ts',
  parserSha256: hash(readFileSync(resolve(adminRoot, 'src/server/pos-auth/catalog-import-sync.ts'))),
  foundationLoaderSha256: hash(officialSource), scope: 'actual-parser-only; synthetic outer trust; no handler or SQL execution' }));
