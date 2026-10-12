import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { extractSourceIndexFixture, assertSavedCatalogBaseline, assertSourceIndexInstallation,
  assertFullIndexCatalogUnchanged, SOURCE_INDEXES } from './catalog-source-index-fixture.mjs';

const source = readFileSync(new URL('./fixtures/catalog-source-indexes/source-indexes.synthetic.sql', import.meta.url), 'utf8').replaceAll('\r\n', '\n');
const toy = JSON.parse(readFileSync(new URL('./fixtures/catalog-source-indexes/catalog.synthetic.json', import.meta.url), 'utf8'));
const fixture = extractSourceIndexFixture(source);
function state() {
  const before = toy.groups.flatMap(group => Array.from({ length: group.count }, (_, ordinal) => {
    const row = structuredClone(group.row);
    row.indexname = row.indexname.replace('{n}', String(ordinal));
    row.indexdef = row.indexdef.replace('{n}', String(ordinal));
    return { ...row, indexOid: group.oidStart + ordinal };
  }));
  const saved = before.map(({ schemaname, indexname, indexdef }) => ({ schemaname, indexname, indexdef }));
  return { before, saved, after: before.concat(structuredClone(toy.added)), tables: structuredClone(toy.targetTables) };
}
const installation = s => assertSourceIndexInstallation(s.before, s.after, s.tables, fixture);

test('source fixture is the exact two canonical LF statements; CRLF input has the same artifact', () => {
  assert.equal(fixture.sql, source);
  assert.equal(fixture.sqlSha256, '588c33e99d251fbbc1f604d56692644eb94e06ce617c0943cd281c06758a6d09');
  assert.deepEqual(extractSourceIndexFixture(source.replaceAll('\n', '\r\n')), fixture);
});
test('comments and quoted function bodies cannot supply phantom executable source indexes', () => {
  assert.throws(() => extractSourceIndexFixture(`/* nested /* comment */ ${source} */`), /Exactly one executable/);
  assert.throws(() => extractSourceIndexFixture(`DO $body$ BEGIN ${source} END $body$;`), /Exactly one executable/);
  assert.throws(() => extractSourceIndexFixture(`select '${source.replaceAll("'", "''")}';`), /Exactly one executable/);
  assert.deepEqual(extractSourceIndexFixture(`-- 商品 🧾\n/* ${source} */\nDO $body$ BEGIN ${source} END $body$;\n${source}`), fixture);
});
for (const index of SOURCE_INDEXES) {
  test(`source extraction rejects missing ${index.name}`, () => {
    const missing = source.replace(new RegExp(`create index ${index.name}\\n[^;]+;\\n`), '');
    assert.throws(() => extractSourceIndexFixture(missing), /Exactly one executable/);
  });
  test(`count-preserving catalog replacement cannot hide missing ${index.name}`, () => {
    const s = state(); const row = s.after.find(row => row.indexname === index.name);
    row.indexname = 'unrelated_replacement_idx'; row.indexdef = 'CREATE INDEX unrelated_replacement_idx ON public.qa_products USING btree (id)';
    assert.equal(s.after.length, 76);
    assert.throws(() => installation(s), /Missing source index or unexpected replacement/);
  });
}
test('source definition changes, duplicates and incomplete SQL fail closed', () => {
  for (const wrong of [source.replace('lower(id::text)', 'id::text'), source.replace('public.inventory_products ', 'app_private.inventory_products '),
    source.replace('create index inventory_products', 'create unique index inventory_products'), source + source,
    source.replace('((lower(id::text)));', '((lower(id::text))) where id is not null;')]) assert.throws(() => extractSourceIndexFixture(wrong));
  assert.throws(() => extractSourceIndexFixture("/* unterminated"), /Unterminated/);
});
test('saved74 definition-only baseline, full74 before, exact two additions and complete76 replay', () => {
  const s = state(); const original = structuredClone(s);
  assert.equal(s.before.length, 74); assert.equal(s.before.filter(row => row.schemaname === 'public').length, 51);
  assert.deepEqual(assertSavedCatalogBaseline(s.saved, s.before), { indexCount: 74, savedPropertiesInvented: false });
  assert.deepEqual(installation(s), { beforeIndexCount: 74, afterIndexCount: 76, addedSourceIndexCount: 2,
    allExistingPropertiesUnchanged: true, targetPropertiesVerified: true });
  assert.equal(s.after.filter(row => row.schemaname === 'public').length, 53);
  assert.deepEqual(assertFullIndexCatalogUnchanged(s.after, s.after.slice().reverse()), { indexCount: 76, completePropertiesUnchanged: true });
  assert.deepEqual(s, original, 'Comparator must not mutate any captured array or property');
});
test('74 full-catalog query cannot be checked against the old qualified16 subset', () => {
  const s = state(); assert.throws(() => assertSavedCatalogBaseline(s.saved.slice(0, 16), s.before), /Complete saved baseline identity set differs/);
});
test('saved fields are compared but absent historical flags cannot be invented', () => {
  const s = state(); s.saved[0].valid = false;
  assert.throws(() => assertSavedCatalogBaseline(s.saved, s.before), /Saved baseline property drift/);
  const definitionsOnly = s.before.map(({ schemaname, indexname, indexdef }) => ({ schemaname, indexname, indexdef }));
  assert.throws(() => assertSavedCatalogBaseline(definitionsOnly, definitionsOnly), /Missing\/invalid indexOid/);
});
test('saved query envelopes require one result set with an honest rowCount', () => {
  const s = state();
  assertSavedCatalogBaseline({ resultSets: [{ rowCount: 74, rows: s.saved }] }, { resultSets: [{ rowCount: 74, rows: s.before }] });
  assertSourceIndexInstallation(s.before, s.after, { resultSets: [{ rowCount: 2, rows: s.tables }] }, fixture);
  assert.throws(() => assertSavedCatalogBaseline({ resultSets: [{ rowCount: 16, rows: s.saved }] }, s.before));
  assert.throws(() => assertSavedCatalogBaseline({ resultSets: [{ rows: s.saved }, { rows: [] }] }, s.before));
});
for (const [field, value] of Object.entries({ valid: false, ready: false, live: false, unique: true, primary: true, method: 'hash',
  expression: 'id::text', predicate: 'id IS NOT NULL', ownerOid: 99, ownerName: 'foreign_owner', tableOid: 999, schemaOid: 999,
  tableName: 'another_table', indexdef: 'CREATE INDEX unrelated ON public.inventory_products USING btree (id)' })) {
  test(`new source index rejects wrong ${field}, although name and76-count still match`, () => {
    const s = state(); s.after[74][field] = value;
    assert.throws(() => installation(s));
  });
}
for (const [field, value] of Object.entries({ valid: false, ready: false, live: false, unique: true, primary: true, method: 'hash',
  expression: 'id::text', predicate: 'id IS NOT NULL', ownerOid: 99, ownerName: 'foreign_owner', tableOid: 999, schemaOid: 999,
  tableName: 'another_table', indexOid: 999, indexdef: 'CREATE INDEX toy_public_0_idx ON public.qa_products USING btree (other)' })) {
  test(`all74 existing properties preserve ${field} across installation`, () => {
    const s = state(); s.after[0] = { ...s.after[0], [field]: value };
    assert.throws(() => installation(s), /Existing full index properties changed/);
  });
}
test('baseline removal, extra index, duplicate identity/OID and foreign schema are rejected', () => {
  const s = state();
  assert.throws(() => assertSavedCatalogBaseline(s.saved.slice(1), s.before));
  assert.throws(() => assertSourceIndexInstallation(s.before, s.after.concat({ ...s.after[0], indexname: 'extra_idx', indexOid: 99999 }), s.tables, fixture));
  assert.throws(() => assertSourceIndexInstallation(s.before, s.after.concat(s.after[0]), s.tables, fixture), /Duplicate catalog index/);
  s.after[74].indexOid = s.before[0].indexOid; assert.throws(() => installation(s), /Duplicate index OID/);
  const foreign = state(); foreign.after[0].schemaname = 'outside_scope'; assert.throws(() => installation(foreign), /Unexpected schema/);
});
test('pre-DDL target contexts cannot guess owners, schema/table identities or duplicate one table', () => {
  const s = state(); delete s.tables[0].ownerOid; assert.throws(() => installation(s), /Missing pre-DDL table ownerOid/);
  const duplicate = state(); duplicate.tables[1] = duplicate.tables[0]; assert.throws(() => installation(duplicate), /Unique target table context/);
});
test('replay preserves all76 metadata, including new OIDs and additional captured properties', () => {
  const s = state(); const changed = structuredClone(s.after); changed[74].indexOid = 9000;
  assert.throws(() => assertFullIndexCatalogUnchanged(s.after, changed), /Complete index catalog properties changed/);
  const owner = structuredClone(s.after); owner[0].ownerName = 'foreign_owner'; assert.throws(() => assertFullIndexCatalogUnchanged(s.after, owner));
  s.after[0].additionalCatalogProperty = { preserved: 'typed value' };
  const missing = structuredClone(s.after); delete missing[0].additionalCatalogProperty;
  assert.throws(() => assertFullIndexCatalogUnchanged(s.after, missing));
});
test('fixture metadata/hash drift or extra DDL cannot broaden the allowed two statements', () => {
  const s = state(); const changed = structuredClone(fixture); changed.indexes[0].indexdef += ' WHERE id IS NOT NULL';
  assert.throws(() => assertSourceIndexInstallation(s.before, s.after, s.tables, changed), /Fixture metadata differs/);
  const extra = extractSourceIndexFixture(source); extra.sql += 'DROP TABLE public.qa_products;\n';
  assert.throws(() => assertSourceIndexInstallation(s.before, s.after, s.tables, extra));
});
