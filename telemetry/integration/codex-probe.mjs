import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { mkdir, mkdtemp, readFile, rm, realpath } from 'node:fs/promises';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { join, isAbsolute, relative } from 'node:path';
import assert from 'node:assert/strict';
import { outputSchema } from '../bridge/core.mjs';
import { observeWindowsProcess } from './windows-identity.mjs';

const toolName = 'get_telemetry_and_quotas';
const AS_OF = '2026-01-01T12:00:00Z';
const fixtureDir = fileURLToPath(new URL('../fixtures/normal', import.meta.url));
const readFixture = async () => JSON.parse(await readFile(join(fixtureDir, 'bridge-output.json'), 'utf8'));
const workRoot = fileURLToPath(new URL('../../.work/bridge-tests-integration/', import.meta.url));
async function safeRemoveOwnedDir(target, { transientRetries = 0 } = {}) {
  const [root, resolved] = await Promise.all([realpath(workRoot), realpath(target)]);
  const child = relative(root, resolved);
  if (!child || isAbsolute(child) || child.startsWith('..')) throw new Error('cleanup_outside_work');
  for (let attempt = 0; ; attempt++) {
    try { await rm(resolved, { recursive: true, force: true }); return; }
    catch (error) { if (!['EBUSY', 'EPERM'].includes(error.code) || attempt >= transientRetries) throw error; await new Promise(r => setTimeout(r, 150)); }
  }
}
// Non-model native client probe. This speaks Codex app-server, not a handwritten MCP stack.
// Fresh CODEX_HOME + fresh cwd: zero copied auth, user configs, plugins or other MCP servers.
const executable = process.argv[2];
if (!executable || !isAbsolute(executable)) throw new Error('usage: node codex-probe.mjs <absolute-codex-executable>');
const base = workRoot;
await mkdir(base, { recursive: true });
const home = await mkdtemp(join(base, 'codex-isolated '));
const server = fileURLToPath(new URL('../bridge/server.mjs', import.meta.url));
const config = `mcp_servers={ai_telemetry_bridge={command=${JSON.stringify(process.execPath)},args=${JSON.stringify([server, '--fixture', fixtureDir, '--as-of', AS_OF, '--diagnostics', '--exit-receipt-dir', home])},startup_timeout_sec=8,tool_timeout_sec=5,required=true}}`;
const env = Object.fromEntries(['SystemRoot', 'SYSTEMROOT', 'PATH', 'Path', 'TEMP', 'TMP', 'USERPROFILE', 'LOCALAPPDATA', 'APPDATA', 'PATHEXT'].filter(k => process.env[k] !== undefined).map(k => [k, process.env[k]]));
env.CODEX_HOME = home;
const child = spawn(executable, ['app-server', '--listen', 'stdio://', '-c', config, '-c', 'analytics.enabled=false'], { cwd: home, env, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'], shell: false });
const pending = new Map();
let sequence = 0, closed = false, exited = false, stderrBytes = 0;
const done = new Promise(resolve => child.once('close', () => { closed = true; resolve(); }));
const exit = new Promise(resolve => child.once('exit', (code, signal) => { exited = true; resolve({ code, signal }); }));
const rl = createInterface({ input: child.stdout });
child.stdin.on('error', () => {});
child.stderr.on('data', bytes => { stderrBytes += bytes.length; }); // Never retain/print native logs.
child.once('error', () => { for (const entry of pending.values()) entry.reject(new Error('appserver_spawn_failed')); });
rl.on('line', line => {
  let message;
  try { message = JSON.parse(line); } catch { return; }
  const entry = pending.get(message.id);
  if (!entry) return;
  pending.delete(message.id);
  clearTimeout(entry.timer);
  if (message.error) entry.reject(new Error(`appserver_error_${message.error.code}`));
  else entry.resolve(message.result);
});
function request(method, params) {
  return new Promise((resolve, reject) => {
    const id = ++sequence;
    const timer = setTimeout(() => { pending.delete(id); reject(new Error(`appserver_timeout_${method.replaceAll('/', '_')}`)); }, 9000);
    pending.set(id, { resolve, reject, timer });
    child.stdin.write(`${JSON.stringify({ id, method, params })}\n`);
  });
}
const watchdog = setTimeout(() => { child.kill(); }, 30000);
const report = { schemaVersion: 'client-probe-2', server: 'telemetry/bridge/server.mjs', mode: 'fixture', checked_at: new Date().toISOString(), native_client: 'Codex CLI app-server', isolated_home: true,
  config_persistence: 'temporary isolated home only', model_requests: 0, bridge_live_provider_requests: 0,
  owned_appserver_pid: child.pid, owned_appserver_spawned_at: new Date().toISOString() };
let failed, fixtureIdentity, nativeFixtureIdentity;
try {
  await request('initialize', { clientInfo: { name: 'desktop-widgets-fixture-probe', version: '0.1.0' }, capabilities: { experimentalApi: true } });
  child.stdin.write(`${JSON.stringify({ method: 'initialized', params: {} })}\n`);
  report.appserver_initialized = true;
  const { thread } = await request('thread/start', { cwd: home, ephemeral: true, approvalPolicy: 'never' });
  report.ephemeral_thread_created = true;
  const status = await request('mcpServerStatus/list', { threadId: thread.id, serverName: 'ai_telemetry_bridge', detail: 'toolsAndAuthOnly' });
  const entry = status.data.find(x => x.name === 'ai_telemetry_bridge');
  assert.ok(entry);
  const tools = Object.values(entry.tools);
  assert.ok(tools.some(x => x.name === toolName));
  assert.deepEqual(tools.find(x => x.name === toolName).outputSchema, outputSchema);
  report.native_tool_discovery = true;
  report.tools = tools.map(x => x.name);
  const result = await request('mcpServer/tool/call', { threadId: thread.id, server: 'ai_telemetry_bridge', tool: toolName, arguments: {} });
  assert.notEqual(result.isError, true);
  assert.deepEqual(result.structuredContent, await readFixture());
  report.native_tool_call = true;
  report.fixture_output_verified = true;
  report.fixture_child_pid_from_tool_metadata = result._meta?.bridge?.pid ?? null;
  fixtureIdentity = result._meta?.bridge;
  nativeFixtureIdentity = await observeWindowsProcess(fixtureIdentity.pid);
  report.fixture_native_identity_before_cleanup = nativeFixtureIdentity;
  report.model_tool_invocation = 'not_tested';
} catch (error) {
  failed = error;
  report.failed = error.code ?? (/^appserver_/.test(error.message) ? error.message : 'probe_assertion_failed');
} finally {
  // Installed Codex rejects thread/archive for ephemeral threads (-32600); no such call.
  child.stdin.end();
  // 'exit' proves this owned process exited; 'close' additionally waits for inherited stdio.
  // Treat them separately: a descendant keeping a pipe open must not create a false lifecycle failure.
  await Promise.race([exit, new Promise(resolve => setTimeout(resolve, 2000))]);
  if (!exited) {
    report.owned_handle_termination_requested = child.kill();
    await Promise.race([exit, new Promise(resolve => setTimeout(resolve, 5000))]);
  }
  if (!exited) {
    report.owned_handle_force_termination_requested = child.kill('SIGKILL');
    await Promise.race([exit, new Promise(resolve => setTimeout(resolve, 2000))]);
  }
  clearTimeout(watchdog);
  for (const entry of pending.values()) clearTimeout(entry.timer);
  rl.close();
  // These are only our streams; never enumerate/kill a process by its name or a reused PID.
  child.stdout.destroy();
  child.stderr.destroy();
  await Promise.race([done, new Promise(resolve => setTimeout(resolve, 500))]);
  report.owned_appserver_exited = exited;
  report.owned_appserver_closed = closed;
  report.native_stderr_bytes_discarded = stderrBytes;
  report.cleanup_stage = 'await_fixture_exit_receipt';
  if (exited && closed && fixtureIdentity) {
    try {
      const receipt = JSON.parse(await readFile(join(home, `bridge-exit-${fixtureIdentity.pid}.json`), 'utf8'));
      report.fixture_exit_receipt_verified = receipt.pid === fixtureIdentity.pid && receipt.started_at === fixtureIdentity.started_at && receipt.exit_code === 0;
    } catch (error) { report.fixture_exit_receipt_error = error.code ?? 'receipt_invalid'; }
    // Windows TerminateProcess need not run JavaScript exit handlers. Absence is verified
    // independently; a reused PID is distinguished by its native creation identity.
    try {
      const observed = await observeWindowsProcess(fixtureIdentity.pid);
      report.fixture_native_identity_after_cleanup = observed;
      const originalAbsent = !observed.present || (nativeFixtureIdentity?.present && observed.created_at !== nativeFixtureIdentity.created_at);
      report.fixture_child_exit_verified = originalAbsent;
      report.fixture_cleanup_observation = originalAbsent ? 'owned_identity_absent' : 'owned_identity_still_present';
    } catch (error) { report.fixture_identity_observation_error = error.message; report.fixture_child_exit_verified = false; }
  }
  if (exited && closed && report.fixture_child_exit_verified) {
    report.cleanup_stage = 'remove_isolated_home';
    try {
      await safeRemoveOwnedDir(home, { transientRetries: 6 });
      report.isolated_home_removed = true;
      report.cleanup_stage = 'complete';
    } catch (error) {
      report.cleanup_error_code = error.code ?? 'cleanup_failed';
      report.isolated_home_removed = false;
    }
  }
  if (!report.isolated_home_removed) report.preserved_isolated_home_name = home.split(/[\\/]/).at(-1);
  report.retained_diagnostic_directory = !report.isolated_home_removed && ['EBUSY', 'EPERM'].includes(report.cleanup_error_code);
  report.functional_pass = !failed && report.native_tool_call === true && report.fixture_output_verified === true;
  report.owned_process_cleanup_pass = exited && closed && report.fixture_child_exit_verified === true;
}
process.stdout.write(`${JSON.stringify(report, null, 2)}\n`);
if (!report.functional_pass || !report.owned_process_cleanup_pass || (!report.isolated_home_removed && !report.retained_diagnostic_directory)) process.exitCode = 1;
