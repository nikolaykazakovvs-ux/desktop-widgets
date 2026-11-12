// Production reader: fixed directory at startup, bounded reads, strict parse, full contract
// validation through core.mjs and a per-source last-verified cache. Read-only: never writes,
// never polls OS/providers, never launches widgets.
import { openSync, closeSync, fstatSync, lstatSync, readSync, realpathSync, existsSync, statSync } from 'node:fs';
import { isAbsolute, join, resolve, dirname, basename, sep } from 'node:path';
import { execFileSync } from 'node:child_process';
import { performance } from 'node:perf_hooks';
import { querySddlSync, whoamiExe, cleanWinEnv } from './sddl-helper.mjs';
import { ContractError, MAX_SNAPSHOT_BYTES, decodeUtf8Strict, parseStrictJson, validateDocument, deriveOutput, formatInstant, nowMicros, instant } from './core.mjs';

export const FILES = { hardware: 'hardware-snapshot.json', quotas: 'quotas-snapshot.json', api: 'api-balances-snapshot.json' };
export const RETRY = { attempts: 4, waits_ms: [1, 2, 4], deadline_ms: 25 };

export class ConfigError extends Error { constructor(message) { super(message); this.code = 'config_error'; } }

const sleepSync = ms => Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);

// Absolute local drive path only: no relative, UNC (\\server), device (\\?\, \\.\) or drive-relative forms.
export function assertLocalAbsolute(path) {
  if (typeof path !== 'string' || !isAbsolute(path) || path.startsWith('\\\\') || path.startsWith('//') || !/^[A-Za-z]:[\\/]/.test(path)) throw new ConfigError('absolute_local_directory_required');
  return resolve(path);
}

export function standardLiveDir(env = process.env) {
  return env.LOCALAPPDATA ? join(env.LOCALAPPDATA, 'DesktopWidgets', 'telemetry') : null;
}

// --telemetry-dir -> DESKTOP_WIDGETS_TELEMETRY_DIR -> %LOCALAPPDATA%\DesktopWidgets\telemetry, resolved once.
export function resolveTelemetryDir(cliDir, env = process.env) {
  const selected = cliDir || env.DESKTOP_WIDGETS_TELEMETRY_DIR || standardLiveDir(env);
  if (!selected) throw new ConfigError('telemetry_directory_unresolved');
  return assertLocalAbsolute(selected);
}

const same = (a, b) => a.toLowerCase() === b.toLowerCase();
// Canonical path of an existing or not-yet-created directory: final path of the nearest existing
// ancestor (GetFinalPathNameByHandle via realpath.native) plus the remaining tail. Expands 8.3
// aliases and resolves junctions/symlinks.
export function canonicalPath(path) {
  const tail = [];
  let current = path;
  while (true) {
    try { return join(realpathSync.native(current), ...tail.reverse()); }
    catch (error) {
      if (error.code !== 'ENOENT' && error.code !== 'ENOTDIR') throw new ConfigError('canonical_path_resolution_failed');
      const parent = dirname(current);
      if (parent === current) throw new ConfigError('canonical_path_resolution_failed');
      tail.push(basename(current));
      current = parent;
    }
  }
}
export function within(path, root) {
  const r = root.replace(/[\\/]+$/, '');
  return same(path, r) || path.toLowerCase().startsWith(`${r.toLowerCase()}${sep}`);
}

class ReadFailure extends Error { constructor(status) { super(status); this.status = status; } }

// Any authority: S-1-5-21-... (local/domain), S-1-12-1-... (Entra ID), S-1-5-5-... and so on.
export function extractSid(text) {
  const match = String(text).match(/S-1-\d+(?:-\d+)+/i);
  return match ? match[0].toUpperCase() : null;
}

let currentUserSid = null;
export function getCurrentUserSid() {
  if (currentUserSid) return currentUserSid;
  if (process.platform !== 'win32') return null;
  try {
    const out = execFileSync(whoamiExe, ['/user', '/fo', 'csv', '/nh'], {
      encoding: 'utf8',
      env: cleanWinEnv,
      timeout: 5000,
      windowsHide: true,
    });
    currentUserSid = extractSid(out);
  } catch {}
  return currentUserSid;
}

export function isTrustedSid(sid, currentSid) {
  if (!sid) return false;
  const s = sid.toUpperCase();
  if (s === 'SY' || s === 'S-1-5-18') return true;
  if (s === 'BA' || s === 'S-1-5-32-544') return true;
  if (currentSid && (s === currentSid.toUpperCase())) return true;
  return false;
}

const KNOWN_ACE_TYPES = new Set(['A', 'OA', 'XA', 'ZA', 'D', 'OD', 'XD']);
const ALLOW_ACE_TYPES = new Set(['A', 'OA', 'XA', 'ZA']);

// Top-level SDDL sections (O:, G:, D:, S:) outside parentheses, so that the DACL never includes the SACL.
function sddlSections(sddl) {
  const sections = {};
  let depth = 0, current = null, start = 0;
  for (let i = 0; i < sddl.length; i++) {
    const c = sddl[i];
    if (c === '(') depth++;
    else if (c === ')') depth = Math.max(0, depth - 1);
    else if (depth === 0 && sddl[i + 1] === ':' && (c === 'O' || c === 'G' || c === 'D' || c === 'S')) {
      if (current) sections[current] = sddl.slice(start, i);
      current = c; start = i + 2; i++;
    }
  }
  if (current) sections[current] = sddl.slice(start);
  return sections;
}

export function verifySddl(sddl, currentSid) {
  if (!sddl || typeof sddl !== 'string') return { ok: false, reason: 'missing_sddl' };
  const sections = sddlSections(sddl);
  if (!sections.O) return { ok: false, reason: 'missing_owner' };
  const owner = sections.O;
  if (!isTrustedSid(owner, currentSid)) return { ok: false, reason: `untrusted_owner: ${owner}` };

  if (sections.D === undefined) return { ok: false, reason: 'missing_dacl' };
  // A null DACL (NO_ACCESS_CONTROL) grants full access to everyone.
  if (/NO_ACCESS_CONTROL/i.test(sections.D)) return { ok: false, reason: 'null_dacl' };
  const daclPart = sections.D;

  const aceRegex = /\(([^)]+)\)/g;
  let match;
  while ((match = aceRegex.exec(daclPart)) !== null) {
    const parts = match[1].split(';');
    const type = parts[0]?.toUpperCase();
    if (!KNOWN_ACE_TYPES.has(type)) {
      return { ok: false, reason: `unknown_ace_type: ${type}` };
    }
    const trustee = parts[5];
    if (ALLOW_ACE_TYPES.has(type)) {
      if (!isTrustedSid(trustee, currentSid)) {
        return { ok: false, reason: `untrusted_allow_ace: ${trustee}` };
      }
    }
  }
  return { ok: true };
}

// Verdicts of owner/DACL checks: lower-cased path -> { ino, ctime, result }. Only real verdicts are stored (never
// a technical failure of the query) and only the latest identity per path, so republishing does not grow it.
export const aclVerificationCache = new Map();

// Batch form kept for callers that want several descriptors: lower-cased path -> SDDL; paths without an answer are absent.
export function fetchSddlBatch(paths) {
  const result = new Map();
  for (const path of paths ?? []) {
    const sddl = querySddlSync(path);
    if (sddl) result.set(path.toLowerCase(), sddl);
  }
  return result;
}

export function assertPrivateSecurity(canonicalPath, statObj, isDir = false) {
  if (process.platform !== 'win32') return;
  const key = canonicalPath.toLowerCase();
  const ino = statObj?.ino ?? 0n;
  const ctime = isDir ? 0 : (statObj?.ctimeMs ?? 0);
  const cached = aclVerificationCache.get(key);
  if (cached && cached.ino === ino && cached.ctime === ctime) {
    if (!cached.result.ok) throw new ReadFailure('access_denied');
    return;
  }
  const userSid = getCurrentUserSid();
  const sddl = userSid ? querySddlSync(canonicalPath) : null;
  // No answer (PowerShell failure, timeout, path vanished): retryable, not a verdict, never cached.
  if (!sddl) throw new ReadFailure('transient_io_error');
  const result = verifySddl(sddl, userSid);
  aclVerificationCache.set(key, { ino, ctime, result });
  if (!result.ok) throw new ReadFailure('access_denied');
}

export class TelemetryReader {
  // mode: live | fixture. In fixture mode the directory must not be inside any live root.
  constructor({ directory, mode = 'live', env = process.env, clock = null }) {
    this.directory = assertLocalAbsolute(directory);
    this.mode = mode;
    this.clock = clock; // fixed UTC ISO string for deterministic checks, else real clock
    this.canonical = canonicalPath(this.directory);
    // Configured path must already be canonical: 8.3 aliases, junctions or symlinks in the
    // configured path are rejected instead of silently followed.
    if (!same(this.canonical, this.directory)) throw new ConfigError('alias_or_reparse_in_telemetry_path');
    if (mode === 'fixture') {
      const roots = [standardLiveDir(env), env.DESKTOP_WIDGETS_TELEMETRY_DIR].filter(x => x && isAbsolute(x)).map(x => canonicalPath(resolve(x)));
      if (roots.some(root => within(this.canonical, root))) throw new ConfigError('fixture_live_directory_forbidden');
    }
    this.cache = { hardware: null, quotas: null, api: null };
    this.verifiedDir = null; // { ino, at }: last full canonical-path verification of the directory object
    this.verifiedFiles = new Map(); // name -> { ino, ctime, at }
    this.stats = { reads: 0, retries: 0, max_attempts: 0, max_retry_elapsed_ms: 0 };
    // Verify directory security in live mode on Windows. A real verdict (untrusted owner/DACL) fails at startup;
    // a technical failure of the query is retried on the first read instead of ending a long-lived process.
    if (this.mode === 'live' && process.platform === 'win32' && existsSync(this.canonical)) {
      try { assertPrivateSecurity(this.canonical, statSync(this.canonical, { bigint: true }), true); }
      catch (error) { if (!(error instanceof ReadFailure && error.status === 'transient_io_error')) throw error; }
    }
  }

  // Returns raw bytes of one snapshot, honoring the bounded retry policy for sharing collisions.
  readBytes(name) {
    const path = join(this.directory, name);
    // Security assertion: outside the retry loop to avoid breaking the 25ms sharing-retry deadline
    if (this.mode === 'live' && process.platform === 'win32') {
      const lastVerified = this.verifiedFiles.get(name);
      const t = performance.now();
      const fileExists = existsSync(path);
      if (fileExists) {
        const fileStat = statSync(path, { bigint: true });
        // Owner/DACL are verified for every file identity: the first read, a replacement through rename (new
        // ino) and an in-place change (new ctime). The previously verified identity is never carried over.
        const changed = !lastVerified || lastVerified.ino !== fileStat.ino || lastVerified.ctime !== fileStat.ctimeMs;
        if (changed) {
          this.verifiedFiles.delete(name);
          assertPrivateSecurity(path, fileStat);
          this.verifiedFiles.set(name, { ino: fileStat.ino, ctime: fileStat.ctimeMs, at: t });
        }
      }
    }
    const started = performance.now();
    for (let attempt = 0; attempt < RETRY.attempts; attempt++) {
      if (attempt > 0) {
        const wait = RETRY.waits_ms[attempt - 1];
        if (performance.now() - started + wait >= RETRY.deadline_ms) break;
        sleepSync(wait);
        this.stats.retries++;
      }
      this.stats.max_attempts = Math.max(this.stats.max_attempts, attempt + 1);
      let fd = null;
      try {
        // Re-check on every read: the directory and the file must still resolve to the verified
        // canonical location (catches junction/symlink substitution after startup).
        const dirLink = lstatSync(this.directory, { bigint: true });
        if (dirLink.isSymbolicLink() || !dirLink.isDirectory()) throw new ReadFailure('access_denied');
        // Full canonical resolution (GetFinalPathNameByHandle) when the directory object changed or at
        // least once per second; a cheap lstat identity check in between.
        const t = performance.now();
        const dirInoChanged = !this.verifiedDir || this.verifiedDir.ino !== dirLink.ino;
        const dirCanonicalDue = dirInoChanged || (t - this.verifiedDir.at > 1000);
        if (dirCanonicalDue) {
          if (!same(realpathSync.native(this.directory), this.canonical)) throw new ReadFailure('access_denied');
          if (dirInoChanged && this.mode === 'live' && process.platform === 'win32') {
            assertPrivateSecurity(this.canonical, dirLink, true);
          }
          this.verifiedDir = {
            ino: dirLink.ino,
            at: t
          };
        }
        fd = openSync(path, 'r'); // libuv default share mode: Read|Write|Delete, compatible with rename-replace
        const st = fstatSync(fd, { bigint: true });
        if (!st.isFile()) throw new ReadFailure('access_denied');
        // The opened object must be the regular file named in the verified directory, not a link target.
        const link = lstatSync(path, { bigint: true, throwIfNoEntry: false });
        if (link && (link.isSymbolicLink() || link.ino !== st.ino)) {
          if (link.isSymbolicLink()) throw new ReadFailure('access_denied');
          const error = new Error('replaced_during_open'); error.code = 'EBUSY'; throw error; // benign race with rename-replace
        }
        const size = Number(st.size);
        if (size <= 0 || size > MAX_SNAPSHOT_BYTES) throw new ReadFailure('invalid_input');
        const buffer = Buffer.allocUnsafe(size + 1);
        let total = 0, n;
        while ((n = readSync(fd, buffer, total, buffer.length - total, total)) > 0) {
          total += n;
          if (total > MAX_SNAPSHOT_BYTES) throw new ReadFailure('invalid_input');
        }
        if (total === 0) throw new ReadFailure('invalid_input');
        this.stats.max_retry_elapsed_ms = Math.max(this.stats.max_retry_elapsed_ms, performance.now() - started);
        return buffer.subarray(0, total);
      } catch (error) {
        if (error instanceof ReadFailure) throw error;
        if (error.code === 'ENOENT' || error.code === 'ENOTDIR') throw new ReadFailure('missing');
        if (error.code === 'EACCES') throw new ReadFailure('access_denied');
        if (error.code !== 'EBUSY' && error.code !== 'EPERM' && error.code !== 'EAGAIN') throw new ReadFailure('transient_io_error');
        // EBUSY/EPERM: transient sharing (WinError 32/33 or delete-pending) — retry within budget.
        if (attempt === RETRY.attempts - 1) throw new ReadFailure(error.code === 'EPERM' ? 'access_denied' : 'transient_io_error');
      } finally {
        if (fd !== null) closeSync(fd);
      }
    }
    this.stats.max_retry_elapsed_ms = Math.max(this.stats.max_retry_elapsed_ms, performance.now() - started);
    throw new ReadFailure('transient_io_error');
  }

  readSource(key) {
    try {
      return { source: parseStrictJson(decodeUtf8Strict(this.readBytes(FILES[key]))), status: 'ok' };
    } catch (error) {
      if (error instanceof ReadFailure) return { source: null, status: error.status };
      if (error instanceof ContractError) return { source: null, status: error.code };
      return { source: null, status: 'invalid_input' };
    }
  }

  // One complete read of both sources and a validated bridge-output.
  read() {
    this.stats.reads++;
    const hardware = this.readSource('hardware');
    const quotas = this.readSource('quotas');
    const api = this.readSource('api');
    // Clock taken after all reads: every published timestamp precedes it.
    const generatedAt = this.clock ?? formatInstant(nowMicros());
    const now = instant(generatedAt);
    const resolveSource = (key, result, kind) => {
      if (result.status === 'ok') {
        // Full schema + clock (against the response clock) + semantic + mode validation.
        try {
          validateDocument(kind, result.source, now);
          if (result.source.mode !== this.mode) throw new ContractError('invalid_input', 'fixture/live mixing');
        } catch (error) { return { source: this.cache[key], status: error instanceof ContractError ? error.code : 'invalid_input' }; }
        this.cache[key] = result.source;
        return result;
      }
      // Last verified snapshot keeps its original timestamps; never rejuvenated.
      return { source: this.cache[key], status: result.status };
    };
    const h = resolveSource('hardware', hardware, 'hardware-snapshot');
    const q = resolveSource('quotas', quotas, 'quotas-snapshot');
    const a = resolveSource('api', api, 'api-balances-snapshot');
    // A cached source was validated against an earlier clock; re-check it against this clock.
    const cachedUsed = (h.status !== 'ok' && h.source !== null) || (q.status !== 'ok' && q.source !== null) || (a.status !== 'ok' && a.source !== null);
    return deriveOutput(h.source, q.source, generatedAt, this.mode, h.status, q.status, !cachedUsed, a.source, a.status);
  }
}

// Explicit fixture input: a scenario directory or a pre-built bridge-output.json.
export function loadFixture(path, { asOf = null, env = process.env } = {}) {
  const absolute = assertLocalAbsolute(path);
  if (!existsSync(absolute)) throw new ConfigError('fixture_not_found');
  const roots = [standardLiveDir(env), env.DESKTOP_WIDGETS_TELEMETRY_DIR].filter(x => x && isAbsolute(x)).map(x => canonicalPath(resolve(x)));
  if (roots.some(root => within(canonicalPath(absolute), root))) throw new ConfigError('fixture_live_directory_forbidden');
  if (statSync(absolute).isDirectory()) return { kind: 'directory', reader: new TelemetryReader({ directory: absolute, mode: 'fixture', env, clock: asOf }) };
  const bytes = (() => { const r = new TelemetryReader({ directory: dirname(absolute), mode: 'fixture', env, clock: asOf }); try { return r.readBytes(basename(absolute)); } catch { throw new ConfigError('fixture_unreadable'); } })();
  let output;
  try {
    output = parseStrictJson(decodeUtf8Strict(bytes));
    if (output.mode !== 'fixture') throw new ConfigError('fixture_mode_required');
    validateDocument('bridge-output', output, asOf ? instant(asOf) : null);
  } catch (error) { throw error instanceof ConfigError ? error : new ConfigError('fixture_invalid'); }
  return { kind: 'output', output };
}
