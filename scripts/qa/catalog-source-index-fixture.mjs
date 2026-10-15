// QA metadata only. This module never connects to PostgreSQL or executes SQL.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export const ADMIN_INDEX_MIGRATION = 'supabase/migrations/20261001220355_wechat_010_recovery_checkpoint_performance.sql';
export const INDEX_SCHEMAS = Object.freeze(['public', 'app_private', 'auth', 'extensions']);
export const SOURCE_INDEXES = Object.freeze([
  Object.freeze({ name: 'inventory_products_sync_event_id_text_idx', table: 'inventory_products' }),
  Object.freeze({ name: 'inventory_product_prices_sync_event_id_text_idx', table: 'inventory_product_prices' }),
]);
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const key = row => `${row.schemaname}\0${row.indexname}`;
const ordered = rows => rows.slice().sort((a, b) => key(a) < key(b) ? -1 : key(a) > key(b) ? 1 : 0);
const definition = index => `CREATE INDEX ${index.name} ON public.${index.table} USING btree (lower((id)::text))`;

// Mask comments and quoted bodies without changing offsets. A CREATE in a
// comment, string or PL/pgSQL definition is not executable source index DDL.
function visibleSql(source) {
  const chars = source.split(''); // UTF-16 offsets must match slice/indexOf.
  const mask = (start, end) => { for (let p = start; p < end; p++) if (chars[p] !== '\n' && chars[p] !== '\r') chars[p] = ' '; };
  for (let p = 0; p < source.length;) {
    const start = p;
    if (source.startsWith('--', p)) {
      p = source.indexOf('\n', p); if (p < 0) p = source.length;
    } else if (source.startsWith('/*', p)) {
      p += 2; let depth = 1;
      while (p < source.length && depth) {
        if (source.startsWith('/*', p)) { depth++; p += 2; }
        else if (source.startsWith('*/', p)) { depth--; p += 2; }
        else p++;
      }
      assert.equal(depth, 0, 'Unterminated SQL comment');
    } else if (source[p] === "'" || source[p] === '"') {
      const quote = source[p++]; const escaped = quote === "'" && /[eE]/.test(source[start - 1] ?? '');
      let closed = false;
      while (p < source.length) {
        if (escaped && source[p] === '\\') { p += 2; continue; }
        if (source[p++] === quote) {
          if (source[p] === quote) { p++; continue; }
          closed = true; break;
        }
      }
      assert.ok(closed, 'Unterminated SQL quote');
    } else {
      const delimiter = source.slice(p).match(/^\$(?:[A-Za-z_][A-Za-z_0-9]*)?\$/)?.[0];
      if (!delimiter) { p++; continue; }
      const end = source.indexOf(delimiter, p + delimiter.length);
      assert.ok(end >= 0, 'Unterminated dollar-quoted SQL body'); p = end + delimiter.length;
    }
    mask(start, p);
  }
  return chars.join('');
}

export function extractSourceIndexFixture(source) {
  assert.equal(typeof source, 'string');
  assert.ok(Buffer.byteLength(source, 'utf8') <= 1024 * 1024, 'Source migration exceeds QA parser bound');
  const normalized = source.replaceAll('\r\n', '\n');
  const statements = [...visibleSql(normalized).matchAll(/\bcreate\s+(?:unique\s+)?index\b[^;]*;/gi)];
  const sql = SOURCE_INDEXES.map(index => {
    const named = statements.filter(item => new RegExp(`^create\\s+(?:unique\\s+)?index\\s+${index.name}\\b`, 'i').test(item[0]));
    assert.equal(named.length, 1, `Exactly one executable source CREATE INDEX required: ${index.name}`);
    const expected = new RegExp(`^create\\s+index\\s+${index.name}\\s+on\\s+public\\.${index.table}\\s*\\(\\(\\s*lower\\(\\s*id\\s*::\\s*text\\s*\\)\\s*\\)\\s*\\)\\s*;$`, 'i');
    assert.match(named[0][0], expected, `Source index definition drift: ${index.name}`);
    return normalized.slice(named[0].index, named[0].index + named[0][0].length).trim();
  }).join('\n') + '\n';
  return { schemaVersion: 'pos-qa-source-index-fixture-v1', sql, sqlSha256: sha256(sql),
    indexes: SOURCE_INDEXES.map(index => ({ schemaname: 'public', indexname: index.name, indexdef: definition(index), tableName: index.table })) };
}

export function readCanonicalAdminFixture(adminRoot, adminCommit, migrationPath = ADMIN_INDEX_MIGRATION) {
  assert.match(adminCommit, /^[0-9a-f]{40}$/, 'An exact Admin commit SHA is required');
  assert.ok(migrationPath && !migrationPath.includes('\\') && !migrationPath.split('/').some(part => !part || part === '..' || part === '.'), 'Relative Git migration path required');
  const git = args => execFileSync('git', ['-C', resolve(adminRoot), ...args], { windowsHide: true, maxBuffer: 2 * 1024 * 1024 });
  assert.equal(git(['rev-parse', '--verify', `${adminCommit}^{commit}`]).toString('utf8').trim(), adminCommit);
  const source = git(['show', `${adminCommit}:${migrationPath}`]);
  const fixture = extractSourceIndexFixture(new TextDecoder('utf-8', { fatal: true }).decode(source));
  return { ...fixture, source: { adminCommit, adminTree: git(['rev-parse', `${adminCommit}^{tree}`]).toString('utf8').trim(),
    migrationPath, gitBlob: git(['rev-parse', `${adminCommit}:${migrationPath}`]).toString('utf8').trim(),
    byteLength: source.length, sha256: sha256(source), encoding: 'canonical Git UTF-8 bytes' } };
}

function capturedRows(capture) {
  let rows;
  if (Array.isArray(capture)) rows = capture;
  else if (Array.isArray(capture?.resultSets)) {
    assert.equal(capture.resultSets.length, 1, 'Exactly one index catalog result set required');
    rows = capture.resultSets[0].rows;
    if (capture.resultSets[0].rowCount !== undefined) assert.equal(capture.resultSets[0].rowCount, rows.length);
  } else rows = capture?.rows;
  assert.ok(Array.isArray(rows), 'Saved catalog rows required');
  return rows;
}

function catalogRows(capture) {
  const rows = capturedRows(capture);
  const keys = new Set();
  for (const row of rows) {
    assert.ok(row && !Array.isArray(row) && typeof row === 'object');
    for (const field of ['schemaname', 'indexname', 'indexdef']) assert.ok(typeof row[field] === 'string' && row[field] && !row[field].includes('\0'), `Missing catalog ${field}`);
    assert.ok(INDEX_SCHEMAS.includes(row.schemaname), `Unexpected schema: ${row.schemaname}`);
    assert.ok(!keys.has(key(row)), `Duplicate catalog index: ${row.schemaname}.${row.indexname}`); keys.add(key(row));
  }
  return ordered(rows);
}

function fullCatalog(capture) {
  const rows = catalogRows(capture); const oids = new Set();
  for (const row of rows) {
    for (const field of ['indexOid', 'schemaOid', 'tableOid', 'ownerOid']) assert.ok(Number.isInteger(row[field]) && row[field] > 0 && row[field] <= 0xffffffff, `Missing/invalid ${field}: ${row.indexname}`);
    for (const field of ['tableName', 'ownerName', 'method']) assert.ok(typeof row[field] === 'string' && row[field], `Missing ${field}: ${row.indexname}`);
    for (const field of ['valid', 'ready', 'live', 'unique', 'primary']) assert.equal(typeof row[field], 'boolean', `Missing ${field}: ${row.indexname}`);
    for (const field of ['expression', 'predicate']) assert.ok(row[field] === null || typeof row[field] === 'string', `Missing ${field}: ${row.indexname}`);
    assert.ok(!oids.has(row.indexOid), `Duplicate index OID: ${row.indexOid}`); oids.add(row.indexOid);
  }
  return rows;
}

// The historical capture may contain only three definition fields. Compare
// every field it actually saved; never manufacture absent flags/owners/OIDs.
export function assertSavedCatalogBaseline(saved, before) {
  const baseline = catalogRows(saved); const actual = fullCatalog(before);
  assert.deepEqual(actual.map(key), baseline.map(key), 'Complete saved baseline identity set differs');
  for (let p = 0; p < baseline.length; p++) {
    for (const [field, value] of Object.entries(baseline[p])) assert.deepEqual(actual[p][field], value, `Saved baseline property drift: ${key(baseline[p])}.${field}`);
  }
  return { indexCount: actual.length, savedPropertiesInvented: false };
}

export function assertSourceIndexInstallation(before, after, targetTables, fixture) {
  const derived = extractSourceIndexFixture(fixture.sql);
  assert.equal(fixture.schemaVersion, derived.schemaVersion); assert.equal(fixture.sqlSha256, derived.sqlSha256);
  assert.equal(fixture.sql, derived.sql, 'Fixture must contain only the two extracted source statements');
  assert.deepEqual(fixture.indexes, derived.indexes, 'Fixture metadata differs from its source DDL');
  const original = fullCatalog(before); const installed = fullCatalog(after);
  targetTables = capturedRows(targetTables);
  assert.equal(targetTables.length, 2, 'Two pre-DDL table contexts required');
  assert.equal(installed.length, original.length + 2, 'Only two source indexes may be added');
  const existing = new Map(original.map(row => [key(row), row]));
  for (const row of original) assert.ok(!SOURCE_INDEXES.some(index => row.indexname === index.name), 'Source indexes must be absent before installation');
  const added = installed.filter(row => !existing.has(key(row)));
  assert.deepEqual(added.map(key), ordered(derived.indexes).map(key), 'Missing source index or unexpected replacement/addition');
  for (const row of installed.filter(row => existing.has(key(row)))) assert.deepEqual(row, existing.get(key(row)), `Existing full index properties changed: ${key(row)}`);
  assert.equal(installed.filter(row => existing.has(key(row))).length, original.length, 'Existing index removed');
  for (const expected of derived.indexes) {
    const row = added.find(item => key(item) === key(expected));
    const tables = targetTables.filter(table => table.schemaname === 'public' && table.tableName === expected.tableName);
    assert.equal(tables.length, 1, `Unique target table context required: ${expected.tableName}`);
    const table = tables[0];
    for (const field of ['schemaOid', 'tableOid', 'ownerOid', 'ownerName']) {
      assert.ok(table[field] !== undefined, `Missing pre-DDL table ${field}`); assert.equal(row[field], table[field], `Source index target/owner drift: ${field}`);
    }
    assert.equal(row.indexdef, expected.indexdef); assert.equal(row.tableName, expected.tableName);
    assert.deepEqual({ valid: row.valid, ready: row.ready, live: row.live, unique: row.unique, primary: row.primary,
      method: row.method, expression: row.expression, predicate: row.predicate },
    { valid: true, ready: true, live: true, unique: false, primary: false, method: 'btree', expression: 'lower((id)::text)', predicate: null });
  }
  return { beforeIndexCount: original.length, afterIndexCount: installed.length, addedSourceIndexCount: 2,
    allExistingPropertiesUnchanged: true, targetPropertiesVerified: true };
}

export function assertFullIndexCatalogUnchanged(before, after) {
  const baseline = fullCatalog(before); assert.deepEqual(fullCatalog(after), baseline, 'Complete index catalog properties changed after replay');
  return { indexCount: baseline.length, completePropertiesUnchanged: true };
}

// These SELECTs are supplied to an already authorized owned runner. This
// module has no database client and cannot apply DDL or establish a connection.
export const FULL_INDEX_CATALOG_SQL = `select n.nspname as schemaname, i.relname as indexname, pg_get_indexdef(i.oid) as indexdef,
 i.oid as "indexOid", n.oid as "schemaOid", t.oid as "tableOid", t.relname as "tableName",
 i.relowner as "ownerOid", r.rolname as "ownerName", x.indisvalid as valid, x.indisready as ready,
 x.indislive as live, x.indisunique as "unique", x.indisprimary as "primary", a.amname as method,
 pg_get_expr(x.indexprs, x.indrelid) as expression, pg_get_expr(x.indpred, x.indrelid) as predicate
from pg_class i join pg_namespace n on n.oid=i.relnamespace join pg_index x on x.indexrelid=i.oid
 join pg_class t on t.oid=x.indrelid join pg_roles r on r.oid=i.relowner join pg_am a on a.oid=i.relam
where n.nspname in ('public','app_private','auth','extensions') order by n.nspname,i.relname;`;
export const TARGET_TABLE_CONTEXT_SQL = `select n.nspname as schemaname, n.oid as "schemaOid", t.oid as "tableOid",
 t.relname as "tableName", t.relowner as "ownerOid", r.rolname as "ownerName"
from pg_class t join pg_namespace n on n.oid=t.relnamespace join pg_roles r on r.oid=t.relowner
where n.nspname='public' and t.relname in ('inventory_products','inventory_product_prices') and t.relkind in ('r','p')
order by t.relname;`;

function cli(args) {
  const [command, ...rest] = args; const options = new Map();
  assert.equal(rest.length % 2, 0, 'Arguments must be --name value pairs');
  for (let p = 0; p < rest.length; p += 2) { assert.ok(rest[p].startsWith('--') && !options.has(rest[p]), 'Duplicate/invalid option'); options.set(rest[p], rest[p + 1]); }
  const allowed = command === 'source' ? ['--admin-root', '--admin-commit', '--migration-path', '--output']
    : ['--fixture', '--saved-baseline', '--before', '--after', '--after-replay', '--target-tables', '--output'];
  assert.ok(command === 'source' || command === 'verify', 'Command must be source or verify');
  for (const option of options.keys()) assert.ok(allowed.includes(option), `Unknown option: ${option}`);
  const required = name => { assert.ok(options.get(name), `Required option: ${name}`); return options.get(name); };
  const read = name => JSON.parse(readFileSync(required(name), 'utf8'));
  let result;
  if (command === 'source') result = readCanonicalAdminFixture(required('--admin-root'), required('--admin-commit'), options.get('--migration-path'));
  else {
    const before = read('--before'), after = read('--after');
    result = { schemaVersion: 'pos-qa-source-index-verification-v1', scope: 'saved catalog metadata only; no database/runtime/resource qualification',
      savedBaseline: assertSavedCatalogBaseline(read('--saved-baseline'), before),
      installation: assertSourceIndexInstallation(before, after, read('--target-tables'), read('--fixture')),
      afterReplay: assertFullIndexCatalogUnchanged(after, read('--after-replay')) };
  }
  const output = JSON.stringify(result, null, 2) + '\n';
  if (options.has('--output')) writeFileSync(options.get('--output'), output, { encoding: 'utf8', flag: 'wx' });
  else process.stdout.write(output);
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { cli(process.argv.slice(2)); } catch (error) { console.error(error.message); process.exitCode = 1; }
}
