// stdio MCP server (official SDK). Tool get_telemetry_and_quotas: strict empty input, canonical outputSchema.
import { McpServer } from '@modelcontextprotocol/server';
import { serveStdio } from '@modelcontextprotocol/server/stdio';
import * as z from 'zod/v4';
import { writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { performance } from 'node:perf_hooks';
import { outputStandardSchema } from './core.mjs';
import { parseArgs, createSource, ownMetrics } from './options.mjs';

export const toolName = 'get_telemetry_and_quotas';
try {
  const options = parseArgs(process.argv.slice(2), { server: true });
  const source = createSource(options); // fail closed at startup; never falls back fixture -> live
  if (options.exitReceiptDir) {
    const identity = ownMetrics();
    process.once('exit', code => { try { writeFileSync(join(options.exitReceiptDir, `bridge-exit-${identity.pid}.json`), JSON.stringify({ pid: identity.pid, started_at: identity.started_at, exit_code: code }), { flag: 'wx' }); } catch {} });
  }
  // stdin EOF/close ends the process: no orphan server after the client goes away.
  process.stdin.once('end', () => process.exit(0));
  process.stdin.once('close', () => process.exit(0));
  await serveStdio(() => {
    const server = new McpServer({ name: 'widgets-bridge', version: '1.0.0' });
    server.registerTool(toolName, {
      description: 'Read-only facts from Desktop Widgets telemetry snapshots (hardware resources, subscription quota windows, API balances when a producer publishes them, data quality and deterministic pressure hints). Does not poll providers or the OS, does not choose executors.',
      inputSchema: z.strictObject({}),
      outputSchema: outputStandardSchema,
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    }, async () => {
      const started = performance.now();
      try {
        const output = source.read();
        return { content: [{ type: 'text', text: JSON.stringify(output) }], structuredContent: output,
          ...(options.diagnostics ? { _meta: { bridge: { ...ownMetrics(), elapsed_ms: performance.now() - started, reader: source.reader?.stats ?? null } } } : {}) };
      } catch {
        return { content: [{ type: 'text', text: 'bridge_read_failed' }], isError: true };
      }
    });
    return server;
  });
} catch (error) {
  process.stderr.write(`${error.code === 'config_error' ? error.message : 'bridge_server_startup_failed'}\n`);
  process.exitCode = 2;
}
