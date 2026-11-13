// Shared startup options for CLI and MCP server; resolved once, never from tool arguments.
import { isAbsolute } from 'node:path';
import { instant } from './core.mjs';
import { ConfigError, TelemetryReader, loadFixture, resolveTelemetryDir } from './reader.mjs';

// `facts` (first CLI argument only) is a synonym of the bare CLI call; `--json` is accepted as a no-op (the output is always JSON).
export function parseArgs(argv, { server = false, cli = false } = {}) {
  const options = { telemetryDir: null, fixture: null, asOf: null, diagnostics: false, exitReceiptDir: null };
  for (let i = 0; i < argv.length; i++) {
    const flag = argv[i], next = argv[i + 1];
    if (cli && i === 0 && flag === 'facts') continue;
    if (cli && flag === '--json') continue;
    if (flag === '--telemetry-dir' && next !== undefined) options.telemetryDir = argv[++i];
    else if (flag === '--fixture' && next !== undefined) options.fixture = argv[++i];
    else if (flag === '--as-of' && next !== undefined) options.asOf = argv[++i];
    else if (flag === '--diagnostics') options.diagnostics = true;
    else if (server && flag === '--exit-receipt-dir' && next !== undefined && isAbsolute(next)) options.exitReceiptDir = argv[++i];
    else throw new ConfigError('usage');
  }
  if (options.fixture && options.telemetryDir) throw new ConfigError('fixture_and_live_are_exclusive');
  if (options.asOf !== null) { try { instant(options.asOf); } catch { throw new ConfigError('invalid_as_of'); } }
  return options;
}

// Returns a function producing one validated bridge-output per call (same core for CLI and MCP).
export function createSource(options, env = process.env) {
  if (options.fixture) {
    const fixture = loadFixture(options.fixture, { asOf: options.asOf, env });
    if (fixture.kind === 'output') return { mode: 'fixture', read: () => fixture.output, reader: null };
    return { mode: 'fixture', read: () => fixture.reader.read(), reader: fixture.reader };
  }
  const reader = new TelemetryReader({ directory: resolveTelemetryDir(options.telemetryDir, env), mode: 'live', env, clock: options.asOf });
  return { mode: 'live', read: () => reader.read(), reader };
}

const startedAt = new Date(Date.now() - process.uptime() * 1000).toISOString();
export function ownMetrics() {
  const cpu = process.cpuUsage(), memory = process.memoryUsage();
  return { pid: process.pid, started_at: startedAt, cpu_user_us: cpu.user, cpu_system_us: cpu.system, rss_bytes: memory.rss, heap_used_bytes: memory.heapUsed };
}
