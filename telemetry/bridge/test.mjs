// node --test telemetry/bridge/test.mjs — differential parity with contract.py, reader, CLI, MCP, concurrency.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync, spawn } from 'node:child_process';
import { mkdirSync, mkdtempSync, writeFileSync, readFileSync, renameSync, rmSync, statSync, readdirSync, symlinkSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';
import { Client } from '@modelcontextprotocol/client';
import { StdioClientTransport } from '@modelcontextprotocol/client/stdio';
import { parseStrictJson, validateDocument, deriveOutput, deepEqual, instant, ageMs, ContractError, outputSchema } from './core.mjs';
import { TelemetryReader, loadFixture, ConfigError, getCurrentUserSid, extractSid, verifySddl, assertPrivateSecurity, aclVerificationCache } from './reader.mjs';

const here = fileURLToPath(new URL('.', import.meta.url));
const repo = join(here, '..', '..');
const fixtures = join(repo, 'telemetry', 'fixtures');
const work = join(repo, '.work', 'bridge-tests');
mkdirSync(work, { recursive: true });
const AS_OF = '2026-01-01T12:00:00Z';
const python = process.env.PYTHON || 'python';
const fixtureJson = (id, name) => readFileSync(join(fixtures, id, name), 'utf8');
const liveDoc = (id, name) => { const d = JSON.parse(fixtureJson(id, name)); d.mode = 'live'; return d; };
const tempDir = prefix => mkdtempSync(join(work, prefix));

function classify(kind, text, asOf, mode) {
  if (text === null) return ['missing', null];
  try {
    const doc = parseStrictJson(text);
    if (!doc || typeof doc !== 'object' || Array.isArray(doc)) return ['invalid_input', null];
    validateDocument(kind, doc, instant(asOf));
    if (doc.mode !== mode) return ['invalid_input', null];
    return ['ok', doc];
  } catch (error) { return [error instanceof ContractError ? error.code : 'invalid_input', null]; }
}

test('differential: bridge core equals contract.py derive_output on all fixture, edge and negative cases', { timeout: 60000 }, () => {
  const cases = JSON.parse(execFileSync(python, [join(here, 'differential.py')], { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 }));
  assert.ok(cases.length >= 120, `cases=${cases.length}`);
  const mismatches = [];
  for (const c of cases) {
    const [hc, hd] = classify('hardware-snapshot', c.hardware, c.as_of, c.mode);
    const [qc, qd] = classify('quotas-snapshot', c.quotas, c.as_of, c.mode);
    const out = deriveOutput(hd, qd, c.as_of, c.mode, c.hardware_read_status || hc, c.quotas_read_status || qc);
    const same = hc === c.expected_classes[0] && qc === c.expected_classes[1] && deepEqual(out, c.expected) && JSON.stringify(out) === JSON.stringify(c.expected);
    if (!same) mismatches.push({ name: c.name, node: [hc, qc], python: c.expected_classes });
  }
  writeFileSync(join(work, 'differential-report.json'), JSON.stringify({ cases: cases.length, mismatches, names: cases.map(c => [c.name, ...c.expected_classes]) }, null, 1));
  assert.deepEqual(mismatches, []);
});

test('stored fixture outputs validate and re-derive identically', () => {
  const manifest = JSON.parse(readFileSync(join(fixtures, 'manifest.json'), 'utf8'));
  assert.equal(manifest.scenarios.length, 10);
  for (const s of manifest.scenarios) {
    const out = parseStrictJson(fixtureJson(s.id, 'bridge-output.json'));
    validateDocument('bridge-output', out);
    const forged = structuredClone(out); forged.orchestration_hints.resource_pressure.cpu.level = 'high';
    assert.throws(() => validateDocument('bridge-output', forged));
  }
});

function liveDir(files = { hardware: liveDoc('normal', 'hardware-snapshot.json'), quotas: liveDoc('normal', 'quotas-snapshot.json') }) {
  const dir = tempDir('live-');
  if (process.platform === 'win32') {
    const sid = getCurrentUserSid();
    if (sid) {
      try {
        execFileSync('icacls.exe', [dir, '/inheritance:r', '/grant:r', `*${sid}:(OI)(CI)F`, '*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F']);
      } catch {}
    }
  }
  if (files.hardware) writeFileSync(join(dir, 'hardware-snapshot.json'), typeof files.hardware === 'string' ? files.hardware : JSON.stringify(files.hardware));
  if (files.quotas) writeFileSync(join(dir, 'quotas-snapshot.json'), typeof files.quotas === 'string' ? files.quotas : JSON.stringify(files.quotas));
  return dir;
}

test('live reader: ok, independent missing, cache fallback without rejuvenation, cold failures', () => {
  const dir = liveDir();
  const reader = new TelemetryReader({ directory: dir, clock: AS_OF });
  const ok = reader.read();
  assert.equal(ok.mode, 'live');
  assert.equal(ok.hardware_state.read_status, 'ok');
  assert.equal(ok.subscription_quotas.read_status, 'ok');
  const published = ok.hardware_state.source.published_at;
  const bad = {
    invalid_input: ['{', '', '{"mode":"live","mode":"fixture"}', JSON.stringify(liveDoc('normal', 'hardware-snapshot.json')).replace('"sample_interval_ms":1000', '"sample_interval_ms":9007199254740993'), ' '.repeat(1024 * 1024 + 1)],
    clock_invalid: [JSON.stringify({ ...liveDoc('normal', 'hardware-snapshot.json'), published_at: '2026-01-01T12:00:05Z' })],
  };
  for (const [status, contents] of Object.entries(bad)) {
    for (const content of contents) {
      writeFileSync(join(dir, 'hardware-snapshot.json'), content);
      const out = reader.read();
      assert.equal(out.hardware_state.read_status, status);
      assert.equal(out.hardware_state.status, 'error');
      assert.equal(out.hardware_state.source.published_at, published, 'cached source keeps original timestamps');
      assert.equal(out.subscription_quotas.read_status, 'ok', 'other source unaffected');
    }
  }
  rmSync(join(dir, 'hardware-snapshot.json'));
  const missing = reader.read();
  assert.equal(missing.hardware_state.read_status, 'missing');
  assert.equal(missing.hardware_state.status, 'error');
  const cold = new TelemetryReader({ directory: dir, clock: AS_OF }).read();
  assert.equal(cold.hardware_state.status, 'unavailable');
  assert.equal(cold.hardware_state.source, null);
  assert.equal(cold.hardware_state.liveness, 'unknown');
  writeFileSync(join(dir, 'hardware-snapshot.json'), '{');
  const coldBad = new TelemetryReader({ directory: dir, clock: AS_OF }).read();
  assert.deepEqual([coldBad.hardware_state.status, coldBad.hardware_state.read_status, coldBad.hardware_state.source], ['error', 'invalid_input', null]);
  validateDocument('bridge-output', coldBad);
});

test('mode isolation: live rejects fixture snapshots; --fixture refuses the live directory', () => {
  const dir = liveDir({ hardware: JSON.parse(fixtureJson('normal', 'hardware-snapshot.json')), quotas: liveDoc('normal', 'quotas-snapshot.json') });
  const out = new TelemetryReader({ directory: dir, clock: AS_OF }).read();
  assert.equal(out.hardware_state.read_status, 'invalid_input');
  assert.throws(() => loadFixture(dir, { env: { ...process.env, DESKTOP_WIDGETS_TELEMETRY_DIR: dir } }), e => e instanceof ConfigError && e.message === 'fixture_live_directory_forbidden');
  assert.doesNotThrow(() => loadFixture(join(fixtures, 'normal'), { asOf: AS_OF, env: { ...process.env, DESKTOP_WIDGETS_TELEMETRY_DIR: dir } }));
});

test('path security: relative, UNC, junction at startup and junction substitution after startup', () => {
  assert.throws(() => new TelemetryReader({ directory: 'relative\\dir' }), ConfigError);
  assert.throws(() => new TelemetryReader({ directory: '\\\\server\\share\\telemetry' }), ConfigError);
  const real = liveDir();
  const link = join(work, `junction-${process.pid}-${Date.now()}`);
  symlinkSync(real, link, 'junction');
  try {
    assert.throws(() => new TelemetryReader({ directory: link }), e => e.message === 'alias_or_reparse_in_telemetry_path');
    const moving = liveDir();
    const reader = new TelemetryReader({ directory: moving, clock: AS_OF });
    assert.equal(reader.read().hardware_state.read_status, 'ok');
    renameSync(moving, `${moving}-moved`);
    symlinkSync(real, moving, 'junction');
    const out = reader.read();
    assert.equal(out.hardware_state.read_status, 'access_denied');
    assert.equal(out.hardware_state.source.published_at, '2026-01-01T11:59:59Z', 'cache kept');
    rmSync(moving);
  } finally { rmSync(link, { force: true }); }
});

test('owner/DACL security: reader rejects snapshot or directory with untrusted owner or unsafe DACL', () => {
  if (process.platform !== 'win32') return;
  const userSid = getCurrentUserSid();

  // W2-5: Unit tests on verifySddl whitelist of ACE types and untrusted owner
  assert.equal(verifySddl('O:SYG:SYD:(A;;FA;;;SY)(A;;FA;;;BA)', userSid).ok, true, 'pure system/admin is ok');
  assert.equal(verifySddl(`O:${userSid}G:SYD:(A;;FA;;;${userSid})`, userSid).ok, true, 'pure current user is ok');

  // Allow types with untrusted trustee (WD = Everyone) must fail
  assert.equal(verifySddl(`O:${userSid}G:SYD:(OA;;FA;;;WD)`, userSid).ok, false, 'OA allow with WD must fail');
  assert.equal(verifySddl(`O:${userSid}G:SYD:(XA;;FA;;;WD;...)`, userSid).ok, false, 'XA allow with WD must fail');
  assert.equal(verifySddl(`O:${userSid}G:SYD:(ZA;;FA;;;WD)`, userSid).ok, false, 'ZA allow with WD must fail');
  assert.equal(verifySddl(`O:${userSid}G:SYD:(A;;FA;;;WD)`, userSid).ok, false, 'A allow with WD must fail');

  // Unknown ACE type must fail-closed
  assert.equal(verifySddl(`O:${userSid}G:SYD:(UNKNOWN;;FA;;;BA)`, userSid).ok, false, 'unknown ACE type must fail');

  // Untrusted owner must fail
  assert.equal(verifySddl('O:S-1-5-21-99999999-99999999-99999999-9999G:SYD:(A;;FA;;;SY)', userSid).ok, false, 'untrusted owner must fail');

  // Live reader verification
  const dir = liveDir();
  const reader = new TelemetryReader({ directory: dir, clock: AS_OF });
  const ok = reader.read();
  assert.equal(ok.hardware_state.read_status, 'ok');

  // 1. Grant Everyone access to hardware-snapshot.json -> access_denied
  const hwPath = join(dir, 'hardware-snapshot.json');
  execFileSync('icacls.exe', [hwPath, '/grant', '*S-1-1-0:(M)']);
  const badReader = new TelemetryReader({ directory: dir, clock: AS_OF });
  const out = badReader.read();
  assert.equal(out.hardware_state.read_status, 'access_denied');
  assert.equal(out.hardware_state.status, 'error');

  // 2. Unsafe DACL on the directory itself -> directory access_denied
  const dirBad = tempDir('dir-bad-acl');
  writeFileSync(join(dirBad, 'hardware-snapshot.json'), JSON.stringify(liveDoc('normal', 'hardware-snapshot.json')));
  writeFileSync(join(dirBad, 'quotas-snapshot.json'), JSON.stringify(liveDoc('normal', 'quotas-snapshot.json')));
  execFileSync('icacls.exe', [dirBad, '/grant', '*S-1-1-0:(OI)(CI)(M)']);
  try {
    const dirBadReader = new TelemetryReader({ directory: dirBad, clock: AS_OF });
    const dirOut = dirBadReader.read();
    assert.equal(dirOut.hardware_state.read_status, 'access_denied');
  } catch (err) {
    assert.equal(err.status || err.message, 'access_denied');
  }
});

test('concurrent atomic replacement: no partial JSON, bounded retries', { timeout: 30000 }, async () => {
  const dir = liveDir();
  const reader = new TelemetryReader({ directory: dir, clock: AS_OF });
  reader.read(); // warm up initial snapshot verification before spawn
  const text = JSON.stringify(liveDoc('normal', 'hardware-snapshot.json'));
  const writer = spawn(process.execPath, ['-e', `
    const fs=require('fs'),p=require('path');const dir=process.argv[1];const text=process.argv[2];let i=0;const end=Date.now()+3000;
    while(Date.now()<end){const t=p.join(dir,'.hw.'+(i++)+'.tmp');fs.writeFileSync(t,text,{flag:'wx'});try{fs.renameSync(t,p.join(dir,'hardware-snapshot.json'))}catch(e){fs.rmSync(t,{force:true})}}
    process.stdout.write(String(i));`, dir, text], { stdio: ['ignore', 'pipe', 'inherit'] });
  let published = '';
  writer.stdout.on('data', b => { published += b; });
  const done = new Promise(resolve => writer.once('close', resolve));
  const counts = {};
  let reads = 0;
  const end = Date.now() + 2500;
  while (Date.now() < end) {
    const s = reader.read().hardware_state.read_status;
    counts[s] = (counts[s] ?? 0) + 1; reads++;
    await new Promise(r => setImmediate(r));
  }
  await done;
  const report = { writer_replacements: Number(published), reads, read_status_counts: counts, stats: reader.stats };
  writeFileSync(join(work, 'concurrent-replacement-report.json'), JSON.stringify(report, null, 1));
  assert.equal(counts.invalid_input ?? 0, 0, 'no partial/corrupt JSON observed');
  assert.ok(reader.stats.max_attempts <= 4);
  assert.ok(reads > 100 && Number(published) > 100);
});

async function withClient(args, action) {
  const client = new Client({ name: 'bridge-test', version: '1.0.0' });
  const transport = new StdioClientTransport({ command: process.execPath, args: [join(here, 'server.mjs'), ...args], stderr: 'pipe' });
  await client.connect(transport);
  try { return await action(client, transport); } finally { await client.close(); }
}

test('MCP: tools/list, strict empty input, canonical outputSchema, fixture scenarios', { timeout: 30000 }, async () => {
  for (const id of ['normal', 'partial', 'quota-reset-pending']) {
    await withClient(['--fixture', join(fixtures, id), '--as-of', AS_OF], async client => {
      const { tools } = await client.listTools();
      assert.deepEqual(tools.map(t => t.name), ['get_telemetry_and_quotas']);
      assert.deepEqual(tools[0].outputSchema, outputSchema);
      assert.ok(!JSON.stringify(tools[0].outputSchema).includes('"$ref":"http'));
      assert.equal(tools[0].inputSchema.additionalProperties, false);
      const result = await client.callTool({ name: 'get_telemetry_and_quotas', arguments: {} });
      assert.deepEqual(result.structuredContent, JSON.parse(fixtureJson(id, 'bridge-output.json')));
      assert.deepEqual(JSON.parse(result.content[0].text), result.structuredContent);
      const invalid = await client.callTool({ name: 'get_telemetry_and_quotas', arguments: { path: 'C:\\x' } });
      assert.equal(invalid.isError, true);
    });
  }
});

test('10 concurrent MCP clients read passively: all valid, sources untouched', { timeout: 60000 }, async () => {
  const dir = liveDir();
  const snapshot = () => readdirSync(dir).sort().map(n => { const s = statSync(join(dir, n), { bigint: true }); return [n, String(s.ino), String(s.mtimeNs), String(s.size)]; });
  const before = snapshot();
  const results = await Promise.all(Array.from({ length: 10 }, () => withClient(['--telemetry-dir', dir, '--as-of', AS_OF], async client => {
    const outs = [];
    for (let i = 0; i < 20; i++) outs.push((await client.callTool({ name: 'get_telemetry_and_quotas', arguments: {} })).structuredContent);
    return outs;
  })));
  const flat = results.flat();
  assert.equal(flat.length, 200);
  for (const out of flat) { validateDocument('bridge-output', out); assert.equal(out.hardware_state.read_status, 'ok'); assert.equal(out.subscription_quotas.read_status, 'ok'); }
  assert.deepEqual(snapshot(), before, 'no writes, no new files, sources not replaced');
  writeFileSync(join(work, 'ten-clients-report.json'), JSON.stringify({ clients: 10, calls: flat.length, all_valid: true, directory_unchanged: true }, null, 1));
});

test('CLI: exit 0 with pure JSON, exit 2 on configuration errors', () => {
  const run = args => { try { return { code: 0, out: execFileSync(process.execPath, [join(here, 'cli.mjs'), ...args], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }) }; } catch (e) { return { code: e.status, out: e.stdout }; } };
  const ok = run(['--fixture', join(fixtures, 'normal'), '--as-of', AS_OF]);
  assert.equal(ok.code, 0);
  assert.deepEqual(JSON.parse(ok.out), JSON.parse(fixtureJson('normal', 'bridge-output.json')));
  assert.equal(run(['--telemetry-dir', 'relative']).code, 2);
  assert.equal(run(['--fixture', join(work, 'does-not-exist')]).code, 2);
  assert.equal(run(['--bogus']).code, 2);
  const missingLive = run(['--telemetry-dir', join(work, `absent-${Date.now()}`)]);
  assert.equal(missingLive.code, 0);
  assert.equal(JSON.parse(missingLive.out).hardware_state.status, 'unavailable');
});
// ---------- A1-API: api_limits extension (reader, node derivation, node invariants, ACL re-verification) ----------
const testPoolId = 'pool_0123456789abcdef0123';
const testAccId = 'acc_0123456789abcdef0123';
const testConn1 = 'conn_0123456789abcdef';
const testConn2 = 'conn_fedcba9876543210';
const poolB = 'pool_fedcba9876543210fedc';
const accB = 'acc_fedcba9876543210fedc';
const connB = 'conn_aaaaaaaaaaaaaaaa';
const T0 = '2026-01-01T11:59:00Z';
const ATTEMPT = '2026-01-01T11:59:50Z';
const NO_LIMITS = { rpm_limit: null, rpm_remaining: null, tpm_limit: null, tpm_remaining: null };

const ent = (o = {}) => ({ connection_id: testConn1, shared_pool_id: testPoolId, metric_kind: 'account_balance', currency: 'USD', credit_unit: null, amount: '25.50', scale: 2, minor_units: 2550, value_origin: 'measured', status: 'ok', reason: 'none', measured_at: T0, last_attempt_at: ATTEMPT, degradation_status: 'none', ...o });
const conn = (id = testConn1, pool = testPoolId, provider = 'deepseek') => ({ connection_id: id, shared_pool_id: pool, provider_id: provider, '1password_ref_masked': null, rate_limits: { ...NO_LIMITS }, status: 'ok', reason: 'none', http_status: null, degradation_status: 'none' });
const provider = (id = 'deepseek', pool = testPoolId, acc = testAccId) => ({ provider_id: id, account_id: acc, shared_pool_id: pool, status: 'ok', reason: 'none', degradation_status: 'none', measured_at: T0, last_attempt_at: ATTEMPT, age_ms: 50000, '1password_ref_masked': null, rate_limits: { ...NO_LIMITS }, currencies: ['USD'] });

function makeApiDoc(overrides = {}) {
  return {
    schemaVersion: '1.1.0',
    kind: 'api-balances-snapshot',
    source_id: 'api-widget',
    mode: 'live',
    measured_at: T0,
    published_at: ATTEMPT,
    last_attempt_at: ATTEMPT,
    status: 'ok',
    reason: 'none',
    poll_interval_ms: 60000,
    heartbeat_interval_ms: 10000,
    pools: [{ shared_pool_id: testPoolId, provider_id: 'deepseek', account_id: testAccId }],
    connections: [conn(testConn1), conn(testConn2)],
    entries: [
      ent(),
      ent({ metric_kind: 'period_spend', amount: '4.20', minor_units: 420 }),
      ent({ connection_id: testConn2, metric_kind: 'key_allowance', amount: '10.00', minor_units: 1000 }),
    ],
    providers: [provider()],
    ...overrides,
  };
}
// Second pool of another provider, used by the cross-pool and pool-without-connection cases.
const secondPool = { shared_pool_id: poolB, provider_id: 'openrouter', account_id: accB };

const clockApi = instant(AS_OF);
const apiRejected = (doc, message) => assert.throws(() => validateDocument('api-balances-snapshot', structuredClone(doc), clockApi), e => e instanceof ContractError && e.message === message, message);
const apiAccepted = doc => validateDocument('api-balances-snapshot', structuredClone(doc), clockApi);
const SCHEMA_FAILED = 'JSON Schema validation failed';

function liveApiDir(doc = makeApiDoc()) {
  const dir = liveDir();
  if (doc !== null) writeFileSync(join(dir, 'api-balances-snapshot.json'), typeof doc === 'string' ? doc : JSON.stringify(doc));
  return dir;
}
const readApi = dir => new TelemetryReader({ directory: dir, clock: AS_OF }).read();
const putApi = (dir, doc) => writeFileSync(join(dir, 'api-balances-snapshot.json'), typeof doc === 'string' ? doc : JSON.stringify(doc));

test('A1-API: reader, derivation, deduplication, monetary arithmetic and security of the producer file', () => {
  const dir = liveDir();

  // Missing snapshot -> backward compatible reserve
  const out0 = readApi(dir);
  assert.deepEqual(out0.api_limits, { status: 'not_available', reason: 'monitor_not_implemented' });
  validateDocument('bridge-output', out0);

  // Valid v1.1 live read and derivation
  putApi(dir, makeApiDoc());
  const out1 = readApi(dir);
  assert.deepEqual([out1.api_limits.schema_extension, out1.api_limits.status, out1.api_limits.reason, out1.api_limits.read_status, out1.api_limits.liveness, out1.api_limits.freshness],
    ['api-limits/1.1.0', 'ok', 'none', 'ok', 'alive', 'fresh']);
  assert.equal(out1.api_limits.pools.length, 1);
  assert.equal(out1.api_limits.pools[0].shared_pool_id, testPoolId);
  assert.deepEqual(out1.api_limits.pools[0].connection_ids, [testConn1, testConn2]);
  assert.equal(out1.api_limits.pools[0].entries.length, 3);
  validateDocument('bridge-output', out1);

  // Pool balance reported through two keys is one balance: deduplicated, never summed
  putApi(dir, makeApiDoc({ entries: [ent(), ent({ connection_id: testConn2 })] }));
  const out2 = readApi(dir);
  assert.equal(out2.api_limits.pools[0].entries.length, 1);
  assert.equal(out2.api_limits.pools[0].entries[0].amount, '25.50', 'not summed to 51.00');
  validateDocument('bridge-output', out2);

  // Conflicting pool balance at an identical measured_at -> rejected by the semantic rule (not by the schema)
  const conflict = makeApiDoc({ entries: [ent(), ent({ connection_id: testConn2, amount: '30.00', minor_units: 3000 })] });
  apiRejected(conflict, 'conflicting_shared_pool_balance');
  putApi(dir, conflict);
  const outConflict = readApi(dir);
  assert.deepEqual([outConflict.api_limits.status, outConflict.api_limits.reason, outConflict.api_limits.read_status], ['error', 'invalid_input', 'invalid_input']);
  validateDocument('bridge-output', outConflict);

  // Three records: one old and two conflicting new ones -> still isolated in api_limits, read() does not throw
  const threeRecords = makeApiDoc({ entries: [ent({ amount: '20.00', minor_units: 2000, measured_at: '2026-01-01T11:50:00Z', last_attempt_at: '2026-01-01T11:50:50Z' }), ent(), ent({ connection_id: testConn2, amount: '30.00', minor_units: 3000 })] });
  apiRejected(threeRecords, 'conflicting_shared_pool_balance');
  putApi(dir, threeRecords);
  const out3 = readApi(dir);
  assert.equal(out3.api_limits.status, 'error');
  assert.deepEqual([out3.hardware_state.status, out3.subscription_quotas.status], ['ok', 'ok']);
  validateDocument('bridge-output', out3);

  // Arithmetic: minor_units that does not match amount (valid by schema) -> semantic rule
  apiRejected(makeApiDoc({ entries: [ent({ minor_units: 2500 })] }), 'minor_units arithmetic mismatch');
  apiRejected(makeApiDoc({ entries: [ent({ scale: 3 })] }), 'scale mismatch');
  apiRejected(makeApiDoc({ entries: [ent({ currency: 'credits', credit_unit: null, amount: '5', scale: 0, minor_units: 5 })] }), SCHEMA_FAILED); // credits need credit_unit (schema if/then)
  apiRejected(makeApiDoc({ entries: [ent({ credit_unit: 'tokens' })] }), SCHEMA_FAILED); // fiat must not carry credit_unit
  apiAccepted(makeApiDoc({ entries: [ent({ currency: 'credits', credit_unit: 'tokens', amount: '5', scale: 0, minor_units: 5 })] }));
  // Float where a string is required; float for an integer metric (strict parser)
  const validJson = JSON.stringify(makeApiDoc());
  assert.throws(() => apiAccepted(parseStrictJson(validJson.replace('"amount":"25.50"', '"amount":25.50'))), e => e instanceof ContractError && e.message === SCHEMA_FAILED);
  assert.throws(() => parseStrictJson(validJson.replace('"minor_units":2550', '"minor_units":2550.0')), e => e instanceof ContractError && e.message === 'float_for_integer_metric');
  for (const [name, json] of [['float amount', validJson.replace('"amount":"25.50"', '"amount":25.50')], ['float minor_units', validJson.replace('"minor_units":2550', '"minor_units":2550.0')], ['truncated', '{"broken": true, "version":']]) {
    putApi(dir, json);
    const outBad = readApi(dir);
    assert.equal(outBad.api_limits.status, 'error', name);
    assert.deepEqual([outBad.hardware_state.status, outBad.subscription_quotas.status], ['ok', 'ok'], name);
    validateDocument('bridge-output', outBad);
  }

  // Negative amount for period_spend: with a connection and at pool level the violation is only in the amount.
  // The schema (if/then) rejects it first; the reader's own sign check is a second line behind it.
  for (const connection_id of [testConn1, null]) {
    apiRejected(makeApiDoc({ entries: [ent({ connection_id, metric_kind: 'period_spend', amount: '-1.00', minor_units: -100 })] }), SCHEMA_FAILED);
  }
  apiAccepted(makeApiDoc({ entries: [ent({ connection_id: null, metric_kind: 'account_balance', amount: '-1.00', minor_units: -100 })] })); // overdraft is a legal balance

  // Secrets: forms the schema patterns still let through must be caught by the content scan...
  const lowerProviderSecrets = ['sk-ant-api03-secretkey1234567890', 'ghp_0123456789abcdef0123456789'];
  for (const secret of lowerProviderSecrets) {
    const doc = JSON.parse(JSON.stringify(makeApiDoc()).replaceAll('"deepseek"', JSON.stringify(secret)));
    apiRejected(doc, 'secret_or_pii_detected');
    putApi(dir, doc);
    const outLeak = readApi(dir);
    assert.equal(outLeak.api_limits.status, 'error', `leaked value ${secret} rejected`);
    assert.ok(!JSON.stringify(outLeak).includes(secret), 'rejected value is never echoed');
    validateDocument('bridge-output', outLeak);
  }
  // ...and forms the patterns already forbid are rejected earlier, by the schema.
  for (const secret of ['op://vault/deepseek/api_key', 'admin@example.com', 'https://api.openai.com/v1/dashboard', 'E:\\data\\x']) {
    apiRejected(JSON.parse(JSON.stringify(makeApiDoc()).replaceAll('"deepseek"', JSON.stringify(secret))), SCHEMA_FAILED);
  }
});

test('A1-API: node status and freshness are aggregated from entries; stale and silent', () => {
  const dir = liveDir();
  // Stale entries with an alive publisher: the node is stale and partial
  const stale = makeApiDoc({
    measured_at: '2026-01-01T08:00:00Z', published_at: '2026-01-01T11:59:55Z', last_attempt_at: '2026-01-01T11:59:55Z',
    entries: [ent({ measured_at: '2026-01-01T08:00:00Z' })],
  });
  putApi(dir, stale);
  const outStale = readApi(dir);
  assert.deepEqual([outStale.api_limits.freshness, outStale.api_limits.liveness, outStale.api_limits.status, outStale.api_limits.reason], ['stale', 'alive', 'partial', 'source_stale']);
  assert.equal(outStale.api_limits.pools[0].entries[0].freshness, 'stale');
  validateDocument('bridge-output', outStale);

  // One fresh and one stale entry: the stale one is not hidden behind the fresh one
  putApi(dir, makeApiDoc({ entries: [ent(), ent({ metric_kind: 'period_spend', amount: '4.20', minor_units: 420, measured_at: '2026-01-01T08:00:00Z' })] }));
  const mixed = readApi(dir).api_limits;
  assert.deepEqual([mixed.freshness, mixed.status, mixed.reason], ['stale', 'partial', 'source_stale']);

  // A source measurement that is not the latest entry observation is not accepted
  apiRejected(makeApiDoc({ measured_at: '2026-01-01T08:00:00Z', entries: [ent()] }), 'source measurement is not latest original observation');
  apiRejected(makeApiDoc({ entries: [ent({ measured_at: '2026-01-01T11:59:30Z' })] }), 'source measurement is not latest original observation');
  // An ok source needs at least one ok entry
  const noValues = makeApiDoc({ entries: [ent({ status: 'error', reason: 'auth_failed', amount: null, scale: null, minor_units: null })] });
  apiRejected(noValues, 'ok snapshot without usable entries');
  putApi(dir, noValues);
  assert.equal(readApi(dir).api_limits.status, 'error');
  // An ok source with one failed entry is lowered to partial, not reported as ok
  putApi(dir, makeApiDoc({ entries: [ent(), ent({ metric_kind: 'period_spend', status: 'error', reason: 'timeout', amount: null, scale: null, minor_units: null })] }));
  const partial = readApi(dir).api_limits;
  assert.deepEqual([partial.status, partial.reason, partial.freshness], ['partial', 'source_partial', 'fresh']);

  // Silent publisher
  const silent = makeApiDoc({
    measured_at: '2026-01-01T11:57:00Z', published_at: '2026-01-01T11:58:00Z', last_attempt_at: '2026-01-01T11:58:00Z',
    entries: [ent({ measured_at: '2026-01-01T11:57:00Z', last_attempt_at: '2026-01-01T11:58:00Z' })],
  });
  putApi(dir, silent);
  const outSilent = readApi(dir);
  assert.deepEqual([outSilent.api_limits.status, outSilent.api_limits.reason, outSilent.api_limits.liveness], ['partial', 'publisher_silent', 'silent']);
  assert.equal(outSilent.api_limits.pools[0].entries[0].liveness, 'silent');
  validateDocument('bridge-output', outSilent);
});

test('A1-API: connection belongs to its pool; pool-level entries; time order of entries; pool without connection', () => {
  const twoPools = (extra = {}) => makeApiDoc({
    pools: [{ shared_pool_id: testPoolId, provider_id: 'deepseek', account_id: testAccId }, secondPool],
    connections: [conn(testConn1), conn(connB, poolB, 'openrouter')],
    providers: [provider(), provider('openrouter', poolB, accB)],
    ...extra,
  });
  apiAccepted(twoPools({ entries: [ent(), ent({ connection_id: connB, shared_pool_id: poolB, metric_kind: 'key_allowance', amount: '7.00', minor_units: 700 })] }));
  // key_allowance of the openrouter connection must not be filed under the deepseek pool
  apiRejected(twoPools({ entries: [ent(), ent({ connection_id: connB, shared_pool_id: testPoolId, metric_kind: 'key_allowance', amount: '7.00', minor_units: 700 })] }), 'entry connection belongs to another pool');
  // a connection must carry the provider of its pool
  apiRejected(makeApiDoc({ connections: [conn(testConn1, testPoolId, 'openrouter')] }), 'connection provider differs from its pool');
  apiRejected(makeApiDoc({ connections: [conn(testConn1), conn(testConn1)] }), 'duplicate connection_id');
  apiRejected(makeApiDoc({ connections: [conn(testConn1, poolB)] }), 'connection references unknown pool');
  apiRejected(makeApiDoc({ entries: [ent({ connection_id: 'conn_0000000000000000' })] }), 'entry references unknown connection');
  // pool-level entry: connection_id null is part of the producer schema and keeps the semantic rules
  apiAccepted(makeApiDoc({ entries: [ent({ connection_id: null })] }));
  apiRejected(makeApiDoc({ entries: [ent({ connection_id: null, minor_units: 2500 })] }), 'minor_units arithmetic mismatch');
  // a pool without any connection has no valid output form
  apiRejected(twoPools({ connections: [conn(testConn1)], providers: [provider()] }), 'pool without connection');

  // order of times inside entries
  apiRejected(makeApiDoc({ entries: [ent({ measured_at: '2026-01-01T11:59:58Z' })], measured_at: '2026-01-01T11:59:58Z', published_at: '2026-01-01T11:59:50Z' }), 'observation after last attempt'); // measured after publication
  apiRejected(makeApiDoc({ entries: [ent({ last_attempt_at: '2026-01-01T11:58:00Z' })] }), 'entry observation after its attempt');
  apiRejected(makeApiDoc({ entries: [ent({ last_attempt_at: '2026-01-01T11:59:55Z' })], published_at: '2026-01-01T11:59:56Z' }), 'entry attempt after source attempt');
  apiRejected(makeApiDoc({ entries: [ent({ last_attempt_at: null })] }), 'entry observation without attempt');
  apiRejected(makeApiDoc({ entries: [ent({ measured_at: '2026-01-01T12:00:05Z', last_attempt_at: '2026-01-01T12:00:05Z' })], measured_at: '2026-01-01T12:00:05Z', published_at: '2026-01-01T12:00:05Z', last_attempt_at: '2026-01-01T12:00:05Z' }), 'future_observation_publication_or_attempt');
});

test('A1-API: a pool without a connection stays isolated in api_limits (reader, CLI, MCP)', async () => {
  const doc = makeApiDoc({
    pools: [{ shared_pool_id: testPoolId, provider_id: 'deepseek', account_id: testAccId }, secondPool],
    providers: [provider(), provider('openrouter', poolB, accB)],
  });
  apiRejected(doc, 'pool without connection');
  const dir = liveApiDir(doc);
  const out = readApi(dir);
  assert.deepEqual([out.api_limits.status, out.api_limits.reason, out.api_limits.read_status, out.api_limits.pools.length], ['error', 'invalid_input', 'invalid_input', 0]);
  assert.deepEqual([out.hardware_state.status, out.subscription_quotas.status], ['ok', 'ok']);
  validateDocument('bridge-output', out);

  // CLI: exit 0 and an output that satisfies its own schema
  const cli = execFileSync(process.execPath, [join(here, 'cli.mjs'), '--telemetry-dir', dir, '--as-of', AS_OF], { encoding: 'utf8' });
  const cliOut = JSON.parse(cli);
  assert.equal(cliOut.api_limits.status, 'error');
  assert.equal(cliOut.hardware_state.status, 'ok');
  validateDocument('bridge-output', cliOut);

  // MCP: no isError, hardware and quotas are not lost
  await withClient(['--telemetry-dir', dir, '--as-of', AS_OF], async client => {
    const res = await client.callTool({ name: 'get_telemetry_and_quotas', arguments: {} });
    assert.equal(res.isError, undefined);
    assert.equal(res.structuredContent.api_limits.status, 'error');
    assert.equal(res.structuredContent.hardware_state.status, 'ok');
    assert.equal(res.structuredContent.subscription_quotas.status, 'ok');
    validateDocument('bridge-output', res.structuredContent);
  });
  // Broken snapshot over MCP, as before
  putApi(dir, '{"broken": true, "version":');
  await withClient(['--telemetry-dir', dir, '--as-of', AS_OF], async client => {
    const res = await client.callTool({ name: 'get_telemetry_and_quotas', arguments: {} });
    assert.equal(res.isError, undefined);
    assert.deepEqual([res.structuredContent.hardware_state.status, res.structuredContent.subscription_quotas.status, res.structuredContent.api_limits.status], ['ok', 'ok', 'error']);
  });

  // Second line of defence: even a source that skipped validation cannot make the output violate its schema
  const unsafe = JSON.parse(JSON.stringify(doc));
  const derived = deriveOutput(null, null, AS_OF, 'live', null, null, true, unsafe, 'ok');
  assert.deepEqual([derived.api_limits.status, derived.api_limits.reason, derived.api_limits.pools.length], ['error', 'invalid_input', 0]);
  validateDocument('bridge-output', derived);
});

test('A1-API: read error with a cached snapshot gives status error, like hardware and quotas (one reader)', () => {
  const dir = liveApiDir();
  const reader = new TelemetryReader({ directory: dir, clock: AS_OF });
  const first = reader.read();
  assert.deepEqual([first.api_limits.status, first.api_limits.read_status, first.api_limits.freshness], ['ok', 'ok', 'fresh']);
  const baseAge = first.api_limits.pools[0].entries[0].age_ms;
  const apiPath = join(dir, 'api-balances-snapshot.json');
  let tick = 0;
  const cases = [
    ['rejected: conflicting balance', () => putApi(dir, makeApiDoc({ entries: [ent(), ent({ connection_id: testConn2, amount: '30.00', minor_units: 3000 })] })), 'invalid_input', 'invalid_input'],
    ['rejected: secret in the file', () => putApi(dir, JSON.parse(JSON.stringify(makeApiDoc()).replaceAll('"deepseek"', '"sk-ant-api03-secretkey1234567890"'))), 'invalid_input', 'invalid_input'],
    ['rejected: broken JSON', () => putApi(dir, '{'), 'invalid_input', 'invalid_input'],
    ['rejected: clock in the future', () => putApi(dir, makeApiDoc({ published_at: '2026-01-01T13:00:00Z' })), 'clock_invalid', 'clock_invalid'],
    ['file deleted', () => rmSync(apiPath), 'missing', 'source_unavailable'],
  ];
  for (const [name, mutate, readStatus, reason] of cases) {
    mutate();
    reader.clock = `2026-01-01T12:00:${String(10 * ++tick).padStart(2, '0')}Z`;
    const out = reader.read();
    const node = out.api_limits;
    assert.deepEqual([node.status, node.reason, node.read_status], ['error', reason, readStatus], name);
    assert.equal(node.pools.length, 1, `${name}: cached pools stay`);
    assert.equal(node.pools[0].entries[0].measured_at, T0, `${name}: original time, not rejuvenated`);
    assert.ok(node.pools[0].entries[0].age_ms > baseAge, `${name}: age keeps growing`);
    assert.deepEqual([out.hardware_state.read_status, out.subscription_quotas.read_status], ['ok', 'ok'], `${name}: other nodes untouched`);
    validateDocument('bridge-output', out);
  }
  // recovery: a valid snapshot replaces the cache and the node is ok again
  putApi(dir, makeApiDoc({ measured_at: '2026-01-01T11:59:40Z', entries: [ent({ measured_at: '2026-01-01T11:59:40Z' })], published_at: '2026-01-01T11:59:50Z' }));
  reader.clock = AS_OF;
  const healed = reader.read().api_limits;
  assert.deepEqual([healed.status, healed.read_status], ['ok', 'ok']);
  // the cold cases are unchanged: no cache -> reserve for a missing file, isolated error for a broken one
  rmSync(apiPath);
  assert.deepEqual(readApi(dir).api_limits, { status: 'not_available', reason: 'monitor_not_implemented' });
  putApi(dir, '{');
  assert.deepEqual([readApi(dir).api_limits.status, readApi(dir).api_limits.read_status, readApi(dir).api_limits.pools.length], ['error', 'invalid_input', 0]);
});

test('A1-API: prebuilt bridge-output with the extension is checked for internal consistency', () => {
  const dir = liveApiDir();
  const good = readApi(dir);
  assert.equal(good.api_limits.status, 'ok');
  validateDocument('bridge-output', good);
  const broken = (mutate, message) => {
    const doc = structuredClone(good);
    mutate(doc.api_limits);
    assert.throws(() => validateDocument('bridge-output', doc), e => e instanceof ContractError && e.message === message, message);
  };
  broken(n => { n.pools[0].entries[0].minor_units = 1; }, 'minor_units arithmetic mismatch');
  broken(n => { n.pools[0].entries[0].scale = 5; }, 'scale mismatch');
  broken(n => { n.read_status = 'access_denied'; }, 'api_limits read error without error status');
  broken(n => { n.read_status = 'access_denied'; n.status = 'error'; n.reason = 'timeout'; }, 'api_limits read error reason mismatch');
  broken(n => { n.status = 'ok'; n.reason = 'publisher_silent'; }, 'api_limits status/reason mismatch');
  broken(n => { n.pools[0].entries[0].age_ms = 0; }, 'api_limits entry age differs from clocks');
  broken(n => { for (const e of n.pools[0].entries) { e.measured_at = '2025-01-01T00:00:00Z'; e.age_ms = ageMs(e.measured_at, instant(AS_OF)); } }, 'api_limits entry freshness mismatch');
  broken(n => { n.freshness = 'stale'; }, 'api_limits node freshness is not aggregated from entries');
  broken(n => { n.liveness = 'silent'; }, 'api_limits entry alive under silent publisher');
  broken(n => { n.liveness = 'silent'; for (const e of n.pools[0].entries) e.liveness = 'silent'; }, 'api_limits ok node is not fresh, alive and complete');
  broken(n => { n.pools[0].entries[1] = { ...n.pools[0].entries[0] }; }, 'api_limits duplicate pool-level entry');
  broken(n => { n.pools[0].entries[2].connection_id = 'conn_0000000000000000'; }, 'api_limits entry connection is not in its pool');
  broken(n => { n.pools.push(structuredClone(n.pools[0])); }, 'api_limits duplicate pool');
  // schema level through the same document
  broken(n => { n.extra = 1; }, 'JSON Schema validation failed');
  broken(n => { n.pools[0].connection_ids = []; }, 'JSON Schema validation failed');
});

test('ACL: owner/DACL are verified again after rename-replace, in-place change and for null DACL; technical failures are not cached', () => {
  if (process.platform !== 'win32') return;
  const sid = getCurrentUserSid();
  assert.ok(sid);
  // verifySddl: null DACL grants everyone full access; SACL is not parsed as DACL
  assert.equal(verifySddl(`O:${sid}G:SYD:NO_ACCESS_CONTROL`, sid).ok, false, 'null DACL');
  assert.equal(verifySddl(`O:${sid}G:SYD:NO_ACCESS_CONTROLS:(AU;SAFA;FA;;;WD)`, sid).ok, false, 'null DACL before SACL');
  assert.equal(verifySddl(`O:${sid}G:SY`, sid).ok, false, 'no DACL section');
  assert.equal(verifySddl(`O:${sid}G:SYD:AI(A;ID;FA;;;SY)S:AI(AU;SAFA;FA;;;WD)`, sid).ok, true, 'audit ACE of the SACL is not an allow ACE of the DACL');
  assert.equal(verifySddl(`O:${sid}G:SYD:(A;;FA;;;SY)(A;;FA;;;WD)S:(AU;SAFA;FA;;;WD)`, sid).ok, false, 'allow for Everyone before the SACL still fails');
  // SID of other authorities (Entra ID S-1-12-1-...) is recognised
  assert.equal(extractSid('"desktop\\user","S-1-12-1-1111-2222-3333-4444"'), 'S-1-12-1-1111-2222-3333-4444');
  assert.equal(extractSid('"pc\\user","S-1-5-21-1-2-3-1001"'), 'S-1-5-21-1-2-3-1001');
  assert.equal(extractSid('no sid here'), null);
  // technical failure of the query (path vanished): retryable status, no verdict cached
  const ghost = join(work, `ghost-${process.pid}-${Date.now()}`);
  for (let i = 0; i < 2; i++) assert.throws(() => assertPrivateSecurity(ghost, { ino: 7n, ctimeMs: 7n }), e => e.status === 'transient_io_error');
  assert.equal(aclVerificationCache.has(ghost.toLowerCase()), false);

  // same long-lived reader: a file replaced through rename with a wide ACE is rejected, a clean replacement is accepted
  const dir = liveApiDir();
  const reader = new TelemetryReader({ directory: dir, clock: AS_OF });
  assert.equal(reader.read().hardware_state.read_status, 'ok');
  const hw = join(dir, 'hardware-snapshot.json');
  const swap = (name, grant) => {
    const tmp = join(dir, name);
    writeFileSync(tmp, JSON.stringify(liveDoc('normal', 'hardware-snapshot.json')));
    if (grant) execFileSync('icacls.exe', [tmp, '/grant', '*S-1-1-0:(M)']);
    renameSync(tmp, hw);
  };
  swap('.replace-wide.tmp', true);
  const denied = reader.read();
  assert.deepEqual([denied.hardware_state.read_status, denied.hardware_state.status], ['access_denied', 'error']);
  assert.equal(denied.hardware_state.source.published_at, '2026-01-01T11:59:59Z', 'cache kept, not rejuvenated');
  assert.equal(denied.subscription_quotas.read_status, 'ok');
  swap('.replace-clean.tmp', false);
  assert.equal(reader.read().hardware_state.read_status, 'ok', 'a clean replacement is accepted again');
  // in-place ACL change of the already verified file
  execFileSync('icacls.exe', [hw, '/grant', '*S-1-1-0:(M)']);
  assert.equal(reader.read().hardware_state.read_status, 'access_denied');
  // the same for the api snapshot (rename-replace with a wide ACE keeps the cached pools, status error)
  const apiOk = reader.read().api_limits;
  assert.equal(apiOk.read_status, 'ok');
  const tmpApi = join(dir, '.api-wide.tmp');
  writeFileSync(tmpApi, JSON.stringify(makeApiDoc()));
  execFileSync('icacls.exe', [tmpApi, '/grant', '*S-1-1-0:(M)']);
  renameSync(tmpApi, join(dir, 'api-balances-snapshot.json'));
  const apiDenied = reader.read().api_limits;
  assert.deepEqual([apiDenied.status, apiDenied.reason, apiDenied.read_status, apiDenied.pools.length], ['error', 'access_denied', 'access_denied', 1]);
});
