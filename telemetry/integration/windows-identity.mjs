import { join } from 'node:path';
import { runOwned } from './owned-process.mjs';

// Read-only exact-PID observation. Never terminates the observed process.
export async function observeWindowsProcess(pid) {
  if (process.platform !== 'win32' || !Number.isInteger(pid) || pid <= 0 || pid > 0xffffffff) throw new Error('invalid_observation_identity');
  const script = `$p = Get-CimInstance Win32_Process -Filter 'ProcessId = ${pid}'; if ($null -eq $p) { '{"present":false}' } else { @{present=$true;pid=[int]$p.ProcessId;created_at=$p.CreationDate.ToUniversalTime().ToString('o')} | ConvertTo-Json -Compress }`;
  const command = join(process.env.SystemRoot ?? process.env.SYSTEMROOT, 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
  const env = Object.fromEntries(['SystemRoot', 'SYSTEMROOT', 'PATH', 'Path', 'TEMP', 'TMP', 'USERPROFILE', 'LOCALAPPDATA', 'APPDATA', 'PATHEXT'].filter(k => process.env[k] !== undefined).map(k => [k, process.env[k]]));
  const result = await runOwned(command, ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', script], { env, timeout: 5000 });
  if (result.code !== 0 || result.timedOut) throw new Error('native_identity_observation_failed');
  return JSON.parse(result.stdout);
}
