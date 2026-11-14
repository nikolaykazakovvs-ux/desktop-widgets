// node --test telemetry/bridge/test.mjs — differential parity with contract.py, reader, CLI, MCP, concurrency.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync, spawn } from 'node:child_process';
import { mkdirSync, mkdtempSync, writeFileSync, readFileSync, renameSync, rmSync, statSync, readdirSync, symlinkSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';
import { Client } from '@modelcontextprotocol/client';
import { StdioClientTransport } from '@modelcontextprotocol/client/stdio';
import { parseStrictJson, validateDocument, deriveOutput, deepEqual, instant, ageMs, ContractError, outputSchema, schemaIssuePaths } from './core.mjs';
import { parseArgs } from './options.mjs';
import { TelemetryReader, loadFixture, ConfigError, getCurrentUserSid, extractSid, verifySddl, assertPrivateSecurity, aclVerificationCache } from './reader.mjs';
import { diagnosticsSchema, validateDiagnostics, scanPackageOverlays, securityDetail } from './diagnostics.mjs';
import fs, { linkSync } from 'node:fs';
import { syncBuiltinESMExports } from 'node:module';

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
      assert.equal(client.getServerVersion()?.name, 'widgets-bridge', 'serverInfo.name equals the registered server name');
      const { tools } = await client.listTools();
      assert.deepEqual(tools.map(t => t.name), ['get_telemetry_and_quotas', 'get_read_diagnostics']);
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
test('CLI: facts is a synonym of the bare call; --json is a no-op; facts is rejected anywhere but first and by the server', () => {
  const run = args => { try { return { code: 0, out: execFileSync(process.execPath, [join(here, 'cli.mjs'), ...args], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }) }; } catch (e) { return { code: e.status, out: e.stdout }; } };
  const base = ['--fixture', join(fixtures, 'normal'), '--as-of', AS_OF];
  const bare = run(base);
  assert.equal(bare.code, 0);
  for (const args of [['facts', ...base], ['facts', '--json', ...base], ['facts', ...base, '--json'], ['--json', ...base]]) {
    const r = run(args);
    assert.equal(r.code, 0, args.join(' '));
    assert.equal(r.out, bare.out, 'identical output: ' + args.join(' '));
  }
  assert.deepEqual(JSON.parse(bare.out), JSON.parse(fixtureJson('normal', 'bridge-output.json')));
  assert.equal(run([...base, 'facts']).code, 2);
  assert.equal(run(['facts', 'facts', ...base]).code, 2);
  assert.equal(run(['facts', '--bogus', ...base]).code, 2);
  assert.throws(() => parseArgs(['facts'], { server: true }), e => e instanceof ConfigError);
  assert.throws(() => parseArgs(['--json'], { server: true }), e => e instanceof ConfigError);
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

// ---------- read diagnostics (read-diagnostics 1.0.0): exact causes, stat/ACL before the retry loop, MSIX overlays ----------
const noPackages = () => ({ ...process.env, LOCALAPPDATA: tempDir('no-packages-') });
function diagnoseChecked(reader) {
  const { output, diagnostics } = reader.diagnose();
  validateDiagnostics(diagnostics);
  validateDocument('bridge-output', output);
  for (const node of ['hardware_state', 'subscription_quotas']) assert.equal(diagnostics.sources[node].read_status, output[node].read_status, `${node}: same read_status as bridge-output`);
  assert.equal(diagnostics.generated_at, output.generated_at);
  return { output, d: diagnostics };
}

test('read diagnostics: exact cause of each failure, same read_status as bridge-output, no file contents', () => {
  const dir = liveDir();
  const reader = new TelemetryReader({ directory: dir, clock: AS_OF, env: noPackages() });
  const first = diagnoseChecked(reader).d;
  assert.deepEqual([first.sources.hardware_state.detail, first.sources.hardware_state.stage, first.sources.hardware_state.cache_used], ['none', 'none', false]);
  assert.deepEqual([first.sources.api_limits.read_status, first.sources.api_limits.detail, first.sources.api_limits.stage], ['missing', 'file_not_found', 'open']);
  assert.equal(first.package_overlay.status, process.platform === 'win32' ? 'none_found' : 'not_applicable');
  const hw = join(dir, 'hardware-snapshot.json');
  const good = liveDoc('normal', 'hardware-snapshot.json');
  const semantic = structuredClone(good); semantic.metrics.commit_headroom_bytes.value -= 1;
  const cases = [
    [Buffer.alloc(4701), 'invalid_input', 'all_nul_bytes', 'parse', { json_offset: 0, size_bytes: 4701 }],
    ['{"note":"sk-LEAKLEAKLEAKLEAK1234",', 'invalid_input', 'invalid_json', 'parse', { json_offset: 34 }],
    [Buffer.from([0x7b, 0xc3, 0x28, 0x7d]), 'invalid_input', 'invalid_utf8', 'decode', { size_bytes: 4 }],
    ['', 'invalid_input', 'empty_file', 'read', { size_bytes: 0 }],
    [' '.repeat(1024 * 1024 + 1), 'invalid_input', 'file_too_large', 'read', { size_bytes: 1024 * 1024 + 1 }],
    ['{"mode":"live","mode":"LEAK"}', 'invalid_input', 'duplicate_json_member', 'parse', {}],
    [JSON.stringify(good).replace('"sample_interval_ms":1000', '"sample_interval_ms":9007199254740993'), 'invalid_input', 'unsafe_integer', 'parse', {}],
    [JSON.stringify({ ...good, sample_interval_ms: 'LEAK' }), 'invalid_input', 'schema_validation_failed', 'validate', { paths: ['/sample_interval_ms'] }],
    [JSON.stringify({ ...good, 'sk-LEAKLEAKLEAKLEAK': 1 }), 'invalid_input', 'schema_validation_failed', 'validate', { paths: [''] }],
    [JSON.stringify(semantic), 'invalid_input', 'semantic_rule_failed', 'validate', { rule: 'commit headroom arithmetic mismatch' }],
    [JSON.stringify({ ...good, mode: 'fixture' }), 'invalid_input', 'mode_mismatch', 'validate', {}],
    [JSON.stringify({ ...good, published_at: '2026-01-01T12:00:05Z' }), 'clock_invalid', 'future_timestamp', 'validate', {}],
  ];
  for (const [content, status, detail, stage, extra] of cases) {
    writeFileSync(hw, content);
    const { output, d } = diagnoseChecked(reader);
    const s = d.sources.hardware_state;
    assert.deepEqual([output.hardware_state.read_status, s.read_status, s.detail, s.stage], [status, status, detail, stage], detail);
    assert.equal(s.cache_used, true, `${detail}: the last verified snapshot is served`);
    assert.equal(output.hardware_state.source.published_at, '2026-01-01T11:59:59Z', 'cache not rejuvenated');
    if ('json_offset' in extra) assert.equal(s.json_offset, extra.json_offset, detail);
    if ('size_bytes' in extra) assert.equal(s.size_bytes, extra.size_bytes, detail);
    if (extra.rule) assert.equal(s.rule, extra.rule);
    else assert.equal(s.rule, null, detail);
    for (const path of extra.paths ?? []) assert.ok(s.schema_issue_paths.includes(path), `${detail}: ${JSON.stringify(s.schema_issue_paths)}`);
    assert.equal(d.sources.subscription_quotas.detail, 'none', 'other source unaffected');
    assert.ok(!JSON.stringify(d).includes('LEAK'), `${detail}: no file contents or odd keys in diagnostics`);
  }
  rmSync(hw);
  const gone = diagnoseChecked(reader).d.sources.hardware_state;
  assert.deepEqual([gone.read_status, gone.detail, gone.cache_used], ['missing', 'file_not_found', true]);
  const cold = diagnoseChecked(new TelemetryReader({ directory: dir, clock: AS_OF, env: noPackages() })).d.sources.hardware_state;
  assert.deepEqual([cold.read_status, cold.detail, cold.cache_used], ['missing', 'file_not_found', false]);
});

test('stat before the retry loop: ENOENT/EPERM/EBUSY/EACCES/EIO keep regular statuses, never invalid_input; a short EBUSY is retried', () => {
  if (process.platform !== 'win32') return;
  const dir = liveDir();
  const hw = join(dir, 'hardware-snapshot.json');
  const original = fs.statSync;
  const fault = { path: null, code: null, left: 0 };
  fs.statSync = function (path, options) {
    if (fault.code && fault.left !== 0 && String(path).toLowerCase() === fault.path.toLowerCase()) {
      fault.left--;
      const error = new Error(`fault ${fault.code}`); error.code = fault.code; throw error;
    }
    return original.call(this, path, options);
  };
  syncBuiltinESMExports();
  try {
    const reader = new TelemetryReader({ directory: dir, clock: AS_OF, env: noPackages() });
    assert.equal(reader.read().hardware_state.read_status, 'ok');
    for (const [code, status, detail] of [['ENOENT', 'missing', 'file_not_found'], ['EPERM', 'access_denied', 'permission_error_after_retries'], ['EBUSY', 'transient_io_error', 'sharing_retries_exhausted'], ['EACCES', 'access_denied', 'os_access_denied'], ['EIO', 'transient_io_error', 'io_error']]) {
      Object.assign(fault, { path: hw, code, left: -1 });
      const { output, d } = diagnoseChecked(reader);
      const s = d.sources.hardware_state;
      // Persistent EPERM: the 25 ms retry deadline may be reached before the last attempt (Windows timer granularity);
      // then it is transient, as in the read loop. Both are regular statuses; invalid_input never.
      const expected = code === 'EPERM' && s.detail === 'sharing_retries_exhausted' ? ['transient_io_error', 'sharing_retries_exhausted'] : [status, detail];
      assert.deepEqual([output.hardware_state.read_status, s.detail, s.stage, s.errno], [...expected, 'security_check', code], code);
      assert.equal(output.subscription_quotas.read_status, 'ok', `${code}: other source unaffected`);
    }
    Object.assign(fault, { path: hw, code: 'EBUSY', left: 1 });
    assert.equal(reader.read().hardware_state.read_status, 'ok', 'a short sharing collision is retried within the budget');
    assert.equal(fault.left, 0);
    // the directory stat at startup: a technical failure no longer ends a long-lived process; the first read checks again
    Object.assign(fault, { path: dir, code: 'EPERM', left: -1 });
    let started;
    assert.doesNotThrow(() => { started = new TelemetryReader({ directory: dir, clock: AS_OF, env: noPackages() }); });
    Object.assign(fault, { code: null, left: 0 });
    assert.equal(started.read().hardware_state.read_status, 'ok');
  } finally {
    fs.statSync = original;
    syncBuiltinESMExports();
  }
});

test('owner/DACL verdicts in diagnostics: the rule name without the SID', () => {
  assert.equal(securityDetail('untrusted_owner: S-1-1-0'), 'untrusted_owner');
  assert.equal(securityDetail('untrusted_allow_ace: WD'), 'untrusted_allow_ace');
  assert.equal(securityDetail('null_dacl'), 'null_dacl');
  assert.equal(securityDetail('something else'), 'unexpected_exception');
  if (process.platform !== 'win32') return;
  const dir = liveDir();
  const hw = join(dir, 'hardware-snapshot.json');
  execFileSync('icacls.exe', [hw, '/grant', '*S-1-1-0:(M)']);
  const s = diagnoseChecked(new TelemetryReader({ directory: dir, clock: AS_OF, env: noPackages() })).d.sources.hardware_state;
  assert.deepEqual([s.read_status, s.detail, s.stage], ['access_denied', 'untrusted_allow_ace', 'security_check']);
  // a cached verdict of another rule is reported with its own rule name
  const fresh = liveDir();
  const file = join(fresh, 'hardware-snapshot.json');
  const st = statSync(file, { bigint: true });
  aclVerificationCache.set(file.toLowerCase(), { ino: st.ino, ctime: st.ctimeMs, result: { ok: false, reason: 'untrusted_owner: S-1-5-21-1-2-3-500' } });
  const { d } = diagnoseChecked(new TelemetryReader({ directory: fresh, clock: AS_OF, env: noPackages() }));
  assert.equal(d.sources.hardware_state.detail, 'untrusted_owner');
  assert.ok(!JSON.stringify(d).includes('S-1-'), 'no SID in diagnostics');
});

test('MSIX package overlay: zero-filled copy served to a packaged client is found and named (incident 14.11.2026)', () => {
  // fixture mode: not applicable
  const fixtureDiag = loadFixture(join(fixtures, 'normal'), { asOf: AS_OF }).reader.diagnose().diagnostics;
  validateDiagnostics(fixtureDiag);
  assert.equal(fixtureDiag.package_overlay.status, 'not_applicable');
  if (process.platform !== 'win32') return;
  const root = tempDir('localappdata-');
  const tel = join(root, 'DesktopWidgets', 'telemetry');
  mkdirSync(tel, { recursive: true });
  const sid = getCurrentUserSid();
  execFileSync('icacls.exe', [root, '/inheritance:r', '/grant:r', `*${sid}:(OI)(CI)F`, '*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F']);
  writeFileSync(join(tel, 'hardware-snapshot.json'), JSON.stringify(liveDoc('normal', 'hardware-snapshot.json')));
  writeFileSync(join(tel, 'quotas-snapshot.json'), JSON.stringify(liveDoc('normal', 'quotas-snapshot.json')));
  const env = { ...process.env, LOCALAPPDATA: root };
  // no Packages directory, then an unrelated package: none found
  assert.deepEqual(scanPackageOverlays({ localAppData: root, telemetryDir: tel, files: ['hardware-snapshot.json'] }), { status: 'none_found', scanned_packages: 0, packages_with_overlay: [], copies: [], truncated: false, check_failed: false });
  mkdirSync(join(root, 'Packages', 'Other.App_1234567890abc', 'LocalCache', 'Local'), { recursive: true });
  const clean = diagnoseChecked(new TelemetryReader({ directory: tel, clock: AS_OF, env })).d;
  assert.deepEqual([clean.package_overlay.status, clean.package_overlay.scanned_packages, clean.package_overlay.copies.length], ['none_found', 1, 0]);
  // the 14.11 incident: the packaged client is served a zero-filled copy of hardware (simulated with a hard link,
  // same file identity); a valid stale copy of quotas sits in the overlay while the real file is still read
  const overlay = join(root, 'Packages', 'OpenAI.Codex_2p2nqsd0c76g0', 'LocalCache', 'Local', 'DesktopWidgets', 'telemetry');
  mkdirSync(overlay, { recursive: true });
  mkdirSync(join(root, 'Packages', 'Empty.Overlay_0000000000000', 'LocalCache', 'Local', 'DesktopWidgets'), { recursive: true });
  writeFileSync(join(overlay, 'hardware-snapshot.json'), Buffer.alloc(4701));
  rmSync(join(tel, 'hardware-snapshot.json'));
  linkSync(join(overlay, 'hardware-snapshot.json'), join(tel, 'hardware-snapshot.json'));
  writeFileSync(join(overlay, 'quotas-snapshot.json'), JSON.stringify(liveDoc('normal', 'quotas-snapshot.json')));
  const { output, d } = diagnoseChecked(new TelemetryReader({ directory: tel, clock: AS_OF, env }));
  assert.deepEqual([output.hardware_state.read_status, d.sources.hardware_state.detail, d.sources.hardware_state.size_bytes], ['invalid_input', 'all_nul_bytes', 4701]);
  assert.equal(output.subscription_quotas.read_status, 'ok');
  const o = d.package_overlay;
  assert.equal(o.status, 'found');
  assert.equal(o.scanned_packages, 3);
  assert.deepEqual(o.packages_with_overlay.slice().sort(), ['Empty.Overlay_0000000000000', 'OpenAI.Codex_2p2nqsd0c76g0']);
  const byFile = Object.fromEntries(o.copies.map(c => [c.file, c]));
  assert.deepEqual(byFile['hardware-snapshot.json'], { package_family_name: 'OpenAI.Codex_2p2nqsd0c76g0', file: 'hardware-snapshot.json', size_bytes: 4701, all_nul_bytes: true, served_to_this_process: true });
  assert.deepEqual([byFile['quotas-snapshot.json'].all_nul_bytes, byFile['quotas-snapshot.json'].served_to_this_process], [false, false]);
  assert.equal(o.copies.length, 2);
  const text = JSON.stringify(d);
  assert.ok(!text.includes(root) && !text.includes('LocalCache'), 'no paths in diagnostics');
});

function overlayRoot() {
  const root = tempDir('overlay-');
  const pkg = (name, files = {}) => {
    const dir = join(root, 'Packages', name, 'LocalCache', 'Local', 'DesktopWidgets');
    mkdirSync(join(dir, 'telemetry'), { recursive: true });
    for (const [file, content] of Object.entries(files)) writeFileSync(join(dir, 'telemetry', file), content);
    return dir;
  };
  return { root, pkg };
}
const OVERLAY_FILES = ['hardware-snapshot.json'];
const scanOf = (root, files = OVERLAY_FILES) => scanPackageOverlays({ localAppData: root, telemetryDir: join(root, 'none'), files });
const withDiagnosticsOverlay = overlay => {
  const base = loadFixture(join(fixtures, 'normal'), { asOf: AS_OF }).reader.diagnose().diagnostics;
  return validateDiagnostics({ ...base, package_overlay: overlay });
};

test('overlay copy above the size limit is not read: all_nul_bytes is unknown, not false; a copy at the limit is read', () => {
  if (process.platform !== 'win32') return;
  const { root, pkg } = overlayRoot();
  pkg('Big.Copy_0000000000001', { 'hardware-snapshot.json': Buffer.alloc(2 * 1024 * 1024) });
  pkg('Edge.Copy_0000000000002', { 'hardware-snapshot.json': Buffer.alloc(1024 * 1024) });
  pkg('Text.Copy_0000000000003', { 'hardware-snapshot.json': Buffer.concat([Buffer.alloc(2000), Buffer.from('x')]) });
  const copies = Object.fromEntries(scanOf(root).copies.map(c => [c.package_family_name, c]));
  assert.deepEqual([copies['Big.Copy_0000000000001'].size_bytes, copies['Big.Copy_0000000000001'].all_nul_bytes], [2 * 1024 * 1024, null]);
  assert.deepEqual([copies['Edge.Copy_0000000000002'].size_bytes, copies['Edge.Copy_0000000000002'].all_nul_bytes], [1024 * 1024, true]);
  assert.equal(copies['Text.Copy_0000000000003'].all_nul_bytes, false);
});

test('overlay scan: copies only of packages in packages_with_overlay (limit 64), check_failed kept, reparse points skipped', () => {
  if (process.platform !== 'win32') return;
  // (a) 66 packages with an overlay: the list stops at 64 and no copy belongs to a package outside it
  const many = overlayRoot();
  for (let i = 0; i < 66; i++) many.pkg(`Pkg.${String(i).padStart(2, '0')}_0000000000000`, { 'hardware-snapshot.json': Buffer.alloc(8) });
  const m = scanOf(many.root);
  assert.equal(m.packages_with_overlay.length, 64);
  assert.equal(m.truncated, true);
  assert.equal(m.copies.length, 64);
  assert.ok(m.copies.every(c => m.packages_with_overlay.includes(c.package_family_name)), 'every copy belongs to a listed package');
  assert.equal(m.check_failed, false);
  withDiagnosticsOverlay(m);
  // (b) a partial failure of the check next to a found overlay is not lost
  const part = overlayRoot();
  part.pkg('Good.Pkg_0000000000001', { 'hardware-snapshot.json': Buffer.alloc(8) });
  const brokenDir = part.pkg('Broken.Pkg_0000000000002', { 'hardware-snapshot.json': Buffer.alloc(8) });
  const original = fs.lstatSync;
  fs.lstatSync = function (path, options) {
    if (String(path).toLowerCase() === join(brokenDir, 'telemetry', 'hardware-snapshot.json').toLowerCase()) { const e = new Error('fault'); e.code = 'EPERM'; throw e; }
    return original.call(this, path, options);
  };
  syncBuiltinESMExports();
  let p;
  try { p = scanOf(part.root); } finally { fs.lstatSync = original; syncBuiltinESMExports(); }
  assert.deepEqual([p.status, p.check_failed, p.packages_with_overlay.length, p.copies.length], ['found', true, 2, 1]);
  assert.equal(withDiagnosticsOverlay(p).package_overlay.check_failed, true);
  // (c) a junction in place of the overlay directory is not followed
  const link = overlayRoot();
  const target = tempDir('overlay-target-');
  mkdirSync(join(target, 'telemetry'), { recursive: true });
  writeFileSync(join(target, 'telemetry', 'hardware-snapshot.json'), Buffer.alloc(8));
  const linkParent = join(link.root, 'Packages', 'Linked.Pkg_0000000000001', 'LocalCache', 'Local');
  mkdirSync(linkParent, { recursive: true });
  symlinkSync(target, join(linkParent, 'DesktopWidgets'), 'junction');
  const l = scanOf(link.root);
  assert.deepEqual([l.status, l.packages_with_overlay.length, l.copies.length, l.check_failed], ['none_found', 0, 0, false]);
});

test('schemaIssuePaths: a pointer longer than 1024 characters is dropped, the others stay; the document stays valid', () => {
  const issue = path => ({ message: `data${path} must be string` });
  const atLimit = '/a'.repeat(512), tooLong = '/a'.repeat(513);
  assert.deepEqual(schemaIssuePaths([issue(tooLong), issue('/ok'), issue(atLimit)]), ['/ok', atLimit]);
  const base = loadFixture(join(fixtures, 'normal'), { asOf: AS_OF }).reader.diagnose().diagnostics;
  const doc = structuredClone(base);
  doc.sources.hardware_state = { ...doc.sources.hardware_state, read_status: 'invalid_input', detail: 'schema_validation_failed', stage: 'validate', schema_issue_paths: schemaIssuePaths([issue(tooLong), issue('/ok')]) };
  assert.deepEqual(validateDiagnostics(doc).sources.hardware_state.schema_issue_paths, ['/ok']);
});

test('api_limits derive failure with a cached source is reported as api_limits_node_violates_schema; bridge-output unchanged', () => {
  const dir = liveApiDir();
  const reader = new TelemetryReader({ directory: dir, clock: AS_OF, env: noPackages() });
  assert.equal(reader.read().api_limits.read_status, 'ok');
  putApi(dir, '{"broken":');
  const plain = diagnoseChecked(reader);
  assert.deepEqual([plain.d.sources.api_limits.detail, plain.d.sources.api_limits.cache_used], ['invalid_json', true], 'a plain read failure keeps its cause');
  assert.deepEqual([plain.output.api_limits.status, plain.output.api_limits.read_status], ['error', 'invalid_input']);
  for (const message of ['api_limits_node_violates_schema', 'conflicting_shared_pool_balance']) {
    reader.apiNodeCheck = () => { throw new ContractError('invalid_input', message); };
    const { output, d } = diagnoseChecked(reader);
    const s = d.sources.api_limits;
    assert.deepEqual([s.read_status, s.detail, s.stage, s.cache_used], ['invalid_input', message, 'derive', false], message);
    assert.deepEqual(output.api_limits, plain.output.api_limits, 'bridge-output api_limits node is not changed by diagnostics');
  }
});

test('non-ReadFailure exception in the security check is reported at stage security_check, not read', () => {
  if (process.platform !== 'win32') return;
  const dir = liveDir();
  const reader = new TelemetryReader({ directory: dir, clock: AS_OF, env: noPackages() });
  aclVerificationCache.get = () => { const e = new Error('fault'); e.code = 'EIO'; throw e; };
  try {
    const s = reader.diagnose().diagnostics.sources.hardware_state;
    assert.deepEqual([s.read_status, s.detail, s.stage, s.errno], ['transient_io_error', 'io_error', 'security_check', 'EIO']);
  } finally { delete aclVerificationCache.get; }
});

test('MCP get_read_diagnostics and CLI diagnose: schema-valid document; bridge-output tool unchanged', { timeout: 60000 }, async () => {
  await withClient(['--fixture', join(fixtures, 'normal'), '--as-of', AS_OF], async client => {
    const { tools } = await client.listTools();
    const tool = tools.find(t => t.name === 'get_read_diagnostics');
    assert.ok(tool);
    assert.deepEqual(tool.outputSchema, diagnosticsSchema);
    assert.equal(tool.inputSchema.additionalProperties, false);
    assert.deepEqual(tools.find(t => t.name === 'get_telemetry_and_quotas').outputSchema, outputSchema);
    const result = await client.callTool({ name: 'get_read_diagnostics', arguments: {} });
    validateDiagnostics(result.structuredContent);
    assert.deepEqual(JSON.parse(result.content[0].text), result.structuredContent);
    assert.deepEqual([result.structuredContent.mode, result.structuredContent.sources.hardware_state.detail, result.structuredContent.sources.api_limits.detail], ['fixture', 'none', 'file_not_found']);
    const facts = await client.callTool({ name: 'get_telemetry_and_quotas', arguments: {} });
    assert.deepEqual(facts.structuredContent, JSON.parse(fixtureJson('normal', 'bridge-output.json')), 'bridge-output 1.0.0 unchanged');
    assert.equal((await client.callTool({ name: 'get_read_diagnostics', arguments: { path: 'x' } })).isError, true);
  });
  const dir = liveDir({ hardware: '\u0000'.repeat(64), quotas: liveDoc('normal', 'quotas-snapshot.json') });
  await withClient(['--telemetry-dir', dir, '--as-of', AS_OF], async client => {
    const facts = (await client.callTool({ name: 'get_telemetry_and_quotas', arguments: {} })).structuredContent;
    const diagnostics = (await client.callTool({ name: 'get_read_diagnostics', arguments: {} })).structuredContent;
    validateDiagnostics(diagnostics);
    assert.deepEqual([facts.hardware_state.read_status, diagnostics.sources.hardware_state.read_status, diagnostics.sources.hardware_state.detail], ['invalid_input', 'invalid_input', 'all_nul_bytes']);
  });
  const run = args => { try { return { code: 0, out: execFileSync(process.execPath, [join(here, 'cli.mjs'), ...args], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }) }; } catch (e) { return { code: e.status, out: e.stdout }; } };
  const base = ['--fixture', join(fixtures, 'normal'), '--as-of', AS_OF];
  const cli = run(['diagnose', '--json', ...base]);
  assert.equal(cli.code, 0);
  validateDiagnostics(JSON.parse(cli.out));
  const prebuilt = run(['diagnose', '--fixture', join(fixtures, 'offline', 'bridge-output.json')]);
  assert.equal(prebuilt.code, 0);
  const pd = validateDiagnostics(JSON.parse(prebuilt.out));
  assert.ok(Object.values(pd.sources).every(s => s.detail === 'none' || s.detail === 'not_recorded'));
  assert.equal(run([...base, 'diagnose']).code, 2, 'diagnose only as the first argument');
  assert.equal(run(['facts', 'diagnose', ...base]).code, 2);
  assert.throws(() => parseArgs(['diagnose'], { server: true }), e => e instanceof ConfigError);
  assert.equal(run(['facts', ...base]).out, run(base).out, 'facts unchanged');
});
