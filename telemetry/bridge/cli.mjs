// CLI: one bridge-output JSON on stdout (exit 0) or a fixed error code on stderr (exit 2).
// `widgets-bridge facts [--json] [...]` is the stable agent entry point: the same output as the bare call.
import { performance } from 'node:perf_hooks';
import { parseArgs, createSource, ownMetrics } from './options.mjs';

const started = performance.now();
let output;
try {
  const options = parseArgs(process.argv.slice(2), { cli: true });
  const source = createSource(options);
  output = source.read();
  process.stdout.write(`${JSON.stringify(output)}\n`);
  if (options.diagnostics) process.stderr.write(`${JSON.stringify({ bridge: { ...ownMetrics(), elapsed_ms: performance.now() - started, reader: source.reader?.stats ?? null } })}\n`);
} catch (error) {
  process.stderr.write(`${error.code === 'config_error' ? error.message : 'bridge_startup_failed'}\n`);
  process.exitCode = 2;
}
