import { spawn } from 'node:child_process';

// ChildProcess retains OS process ownership. Never kill a PID discovered by enumeration.
export async function runOwned(command, args, { cwd, env, timeout = 5000 } = {}) {
  return await new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd, env, stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true, shell: false });
    let stdout = '', stderr = '', timedOut = false;
    const timer = setTimeout(() => { timedOut = true; child.kill(); }, timeout);
    child.stdout.on('data', b => { stdout += b; if (stdout.length > 1024 * 1024) child.kill(); });
    child.stderr.on('data', b => { stderr += b; if (stderr.length > 1024 * 1024) child.kill(); });
    child.once('error', error => { clearTimeout(timer); reject(error); });
    child.once('close', (code, signal) => {
      clearTimeout(timer);
      resolve({ code, signal, timedOut, stdout, stderr, owned_pid: child.pid, closed: true });
    });
  });
}
