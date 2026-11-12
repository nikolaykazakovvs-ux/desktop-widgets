// Synchronous "path -> SDDL (owner, group, DACL)" query for the reader on Windows. A worker thread keeps one
// Windows PowerShell alive (sddl-worker.mjs); the caller blocks on a shared buffer, so a verdict after a file
// replacement costs milliseconds instead of a process start. Read-only: it only reads security descriptors.
import { Worker } from 'node:worker_threads';
import { join } from 'node:path';

const systemRoot = process.env.SystemRoot || process.env.windir || 'C:\\Windows';
const system32 = join(systemRoot, 'System32');
export const whoamiExe = join(system32, 'whoami.exe');
export const psExe = join(system32, 'WindowsPowerShell', 'v1.0', 'powershell.exe');
export const cleanWinEnv = {
  SystemRoot: systemRoot,
  windir: systemRoot,
  TEMP: process.env.TEMP || 'C:\\Windows\\Temp',
  TMP: process.env.TMP || 'C:\\Windows\\Temp',
  PSModulePath: join(system32, 'WindowsPowerShell', 'v1.0', 'Modules'),
};

export const FIRST_QUERY_TIMEOUT_MS = 30000; // includes the start of PowerShell, which can be slow
export const QUERY_TIMEOUT_MS = 10000;

let helper = null;

function startHelper() {
  const sab = new SharedArrayBuffer(16 + 64 * 1024);
  const worker = new Worker(new URL('./sddl-worker.mjs', import.meta.url), { workerData: { sab, psExe, env: cleanWinEnv } });
  worker.unref();
  const current = { worker, ctl: new Int32Array(sab, 0, 4), data: new Uint8Array(sab, 16), queries: 0 };
  worker.once('error', () => { if (helper === current) helper = null; });
  worker.once('exit', () => { if (helper === current) helper = null; });
  return current;
}

export function closeSddlHelper() {
  const current = helper;
  helper = null;
  if (current) current.worker.terminate().catch(() => {});
}

// Returns the SDDL string, or null when it could not be obtained (path missing, technical failure, timeout).
export function querySddlSync(path, timeoutMs = null) {
  if (process.platform !== 'win32') return null;
  try {
    helper ??= startHelper();
    const current = helper;
    Atomics.store(current.ctl, 0, 0);
    current.worker.postMessage({ path });
    const wait = timeoutMs ?? (current.queries === 0 ? FIRST_QUERY_TIMEOUT_MS : QUERY_TIMEOUT_MS);
    current.queries++;
    Atomics.wait(current.ctl, 0, 0, wait);
    const state = Atomics.load(current.ctl, 0);
    if (state === 1) return Buffer.from(current.data.slice(0, current.ctl[1])).toString('utf8');
    if (state === 0) closeSddlHelper(); // timeout: the helper may be stuck, start a fresh one next time
    return null;
  } catch {
    closeSddlHelper();
    return null;
  }
}
