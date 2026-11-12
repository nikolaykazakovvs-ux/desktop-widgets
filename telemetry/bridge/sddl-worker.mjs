// Worker thread of sddl-helper.mjs: owns one long-lived Windows PowerShell that answers "path -> SDDL" requests,
// so the reader can re-check owner/DACL after every file replacement without starting a process each time.
// Paths arrive as base64 (UTF-8) lines and leave nowhere: they never become script text.
import { parentPort, workerData } from 'node:worker_threads';
import { spawn } from 'node:child_process';

const ctl = new Int32Array(workerData.sab, 0, 4); // [0] state: 0 waiting, 1 answer, 2 failure; [1] answer length in bytes
const data = new Uint8Array(workerData.sab, 16);

const script = [
  "$ErrorActionPreference='Stop'",
  '$o=[Console]::Out',
  'while(($l=[Console]::In.ReadLine()) -ne $null){',
  '  try{',
  '    $p=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($l))',
  "    $i=Get-Item -LiteralPath $p -Force",
  "    $o.WriteLine('OK '+$i.GetAccessControl('Owner,Group,Access').GetSecurityDescriptorSddlForm('Owner,Group,Access'))",
  "  }catch{ $o.WriteLine('ERR') }",
  '  $o.Flush()',
  '}',
].join('\n');

let child = null;
let buffer = '';
let waiting = false;

function finish(state, text = '') {
  waiting = false;
  const bytes = Buffer.from(text, 'utf8');
  const length = Math.min(bytes.length, data.length);
  data.set(bytes.subarray(0, length));
  ctl[1] = length;
  Atomics.store(ctl, 0, state);
  Atomics.notify(ctl, 0);
}

function ensureChild() {
  if (child) return child;
  const started = spawn(workerData.psExe, ['-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', Buffer.from(script, 'utf16le').toString('base64')], {
    env: workerData.env, windowsHide: true, stdio: ['pipe', 'pipe', 'ignore'],
  });
  buffer = '';
  started.stdout.setEncoding('utf8');
  started.stdout.on('data', chunk => {
    buffer += chunk;
    const end = buffer.indexOf('\n');
    if (end === -1 || !waiting) return;
    const line = buffer.slice(0, end).replace(/^﻿/, '').trim();
    buffer = buffer.slice(end + 1);
    if (line.startsWith('OK ')) finish(1, line.slice(3)); else finish(2);
  });
  const lost = () => { if (child === started) child = null; if (waiting) finish(2); };
  started.once('error', lost);
  started.once('exit', lost);
  started.stdin.on('error', lost);
  child = started;
  return started;
}

parentPort.on('message', message => {
  try {
    const target = ensureChild();
    waiting = true;
    target.stdin.write(`${Buffer.from(String(message.path), 'utf8').toString('base64')}\n`);
  } catch { if (waiting) finish(2); else { Atomics.store(ctl, 0, 2); Atomics.notify(ctl, 0); } }
});
