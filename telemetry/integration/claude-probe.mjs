// Isolated Claude Code probe of the production Bridge server: no model turn, no real profile.
// 1) `claude mcp add-json/get/remove` in a temporary CLAUDE_CONFIG_DIR (native client health-check);
// 2) the same registered command is driven over stdio by the official MCP client: tools/list + tools/call
//    for normal, partial and quota-reset-pending, compared with the stored bridge-output.json.
import { spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, realpathSync } from 'node:fs';
import { join, isAbsolute, relative } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const executable = process.argv[2];
if (!executable || !isAbsolute(executable)) throw new Error('usage: node claude-probe.mjs <absolute-claude-executable>');
const here = fileURLToPath(new URL('.', import.meta.url));
const repo = join(here, '..', '..');
const bridge = join(repo, 'telemetry', 'bridge');
const sdk = p => import(pathToFileURL(join(bridge, 'node_modules', '@modelcontextprotocol', 'client', 'dist', p)).href);
const { Client } = await sdk('index.mjs');
const { StdioClientTransport } = await sdk('stdio.mjs');
const base = join(repo, '.work', 'bridge-tests-integration');
mkdirSync(base, { recursive: true });
const home = mkdtempSync(join(base, 'claude-isolated '));
const AS_OF = '2026-01-01T12:00:00Z';
const env = Object.fromEntries(['SystemRoot', 'SYSTEMROOT', 'PATH', 'Path', 'TEMP', 'TMP', 'USERPROFILE', 'LOCALAPPDATA', 'APPDATA', 'PATHEXT'].filter(k => process.env[k] !== undefined).map(k => [k, process.env[k]]));
env.CLAUDE_CONFIG_DIR = home;
env.CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC = '1';
const serverArgs = id => [join(bridge, 'server.mjs'), '--fixture', join(repo, 'telemetry', 'fixtures', id), '--as-of', AS_OF];
const config = { type: 'stdio', command: process.execPath, args: serverArgs('normal') };
const run = (args, timeout) => spawnSync(executable, args, { cwd: home, env, encoding: 'utf8', timeout, windowsHide: true });
const report = { schemaVersion: 'client-probe-2', mode: 'fixture', checked_at: new Date().toISOString(), native_client: 'Claude Code CLI', server: 'telemetry/bridge/server.mjs',
  isolated_config: true, model_requests: 0, model_tool_invocation: 'not_tested', scenarios: {} };
try {
  const added = run(['mcp', 'add-json', '--scope', 'user', 'ai_telemetry_bridge', JSON.stringify(config)], 10000);
  report.config_recognized = added.status === 0;
  const status = run(['mcp', 'get', 'ai_telemetry_bridge'], 20000);
  report.native_mcp_connected = status.status === 0 && /Connected/i.test(status.stdout) && !/not connected|failed/i.test(status.stdout);
  for (const id of ['normal', 'partial', 'quota-reset-pending']) {
    const client = new Client({ name: 'claude-probe', version: '1.0.0' });
    await client.connect(new StdioClientTransport({ command: config.command, args: serverArgs(id), stderr: 'pipe' }));
    try {
      const { tools } = await client.listTools();
      const result = await client.callTool({ name: 'get_telemetry_and_quotas', arguments: {} });
      const expected = JSON.parse(readFileSync(join(repo, 'telemetry', 'fixtures', id, 'bridge-output.json'), 'utf8'));
      report.scenarios[id] = { tools: tools.map(t => t.name), call_ok: result.isError !== true,
        output_equals_stored_fixture: JSON.stringify(result.structuredContent) === JSON.stringify(expected) };
    } finally { await client.close(); }
  }
} catch (error) {
  report.failed = error.code ?? 'probe_failed';
} finally {
  const removed = run(['mcp', 'remove', '--scope', 'user', 'ai_telemetry_bridge'], 10000);
  report.isolated_config_removed = removed.status === 0;
  const rel = relative(realpathSync(base), realpathSync(home));
  if (rel && !rel.startsWith('..') && !isAbsolute(rel)) { try { rmSync(home, { recursive: true, force: true }); report.isolated_home_removed = true; } catch (e) { report.isolated_home_removed = false; report.cleanup_error = e.code; } }
}
report.pass = !report.failed && report.config_recognized && report.native_mcp_connected && Object.values(report.scenarios).length === 3 && Object.values(report.scenarios).every(s => s.call_ok && s.output_equals_stored_fixture);
process.stdout.write(`${JSON.stringify(report, null, 2)}\n`);
if (!report.pass) process.exitCode = 1;
