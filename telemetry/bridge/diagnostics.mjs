// Read diagnostics of the Widgets Bridge: the exact cause of each snapshot read and copies of snapshots in MSIX
// package overlays. A separate versioned document (read-diagnostics 1.0.0, schema/read-diagnostics.schema.json):
// bridge-output 1.0.0 and its closed read_status values are not changed. Never carries file contents, values,
// SIDs or full paths. Read-only: it only lists directories and reads snapshot copies to tell whether they are
// all NUL bytes.
import { readFileSync, readdirSync, statSync, lstatSync, fstatSync, openSync, readSync, closeSync } from 'node:fs';
import { join } from 'node:path';
import { fromJsonSchema } from '@modelcontextprotocol/server';
import { ContractError, MAX_SNAPSHOT_BYTES } from './core.mjs';

export const DIAGNOSTICS_SCHEMA_VERSION = '1.0.0';
export const DIAGNOSTICS_KIND = 'widgets-bridge-read-diagnostics';
const raw = JSON.parse(readFileSync(new URL('../schema/read-diagnostics.schema.json', import.meta.url), 'utf8'));
// Published tool outputSchema: the self-contained schema without its registry $id (local refs only).
export const diagnosticsSchema = Object.fromEntries(Object.entries(raw).filter(([k]) => k !== '$id'));
export const diagnosticsStandardSchema = fromJsonSchema(diagnosticsSchema);
const validator = diagnosticsStandardSchema['~standard'];
export function validateDiagnostics(document) {
  if (validator.validate(document).issues) throw new Error('read_diagnostics_violates_schema');
  return document;
}

const DETAILS = new Set(raw.$defs.source.properties.detail.enum);
const READ_STATUSES = new Set(raw.$defs.source.properties.read_status.enum);
const ERRNO = /^E[A-Z0-9]{1,24}$/;
const RULE = /^[A-Za-z0-9 _/:.-]{1,120}$/;
export const errnoOf = error => (typeof error?.code === 'string' && ERRNO.test(error.code) ? error.code : null);

// One read outcome. status: a bridge-output read_status; detail: a value of the diagnostics detail enum.
export function diag(status, detail, info = {}) {
  return {
    read_status: status,
    detail: DETAILS.has(detail) ? detail : 'unexpected_exception',
    stage: info.stage ?? 'none',
    errno: info.errno ?? null,
    size_bytes: Number.isSafeInteger(info.size_bytes) ? info.size_bytes : null,
    json_offset: Number.isSafeInteger(info.json_offset) ? info.json_offset : null,
    rule: typeof info.rule === 'string' && RULE.test(info.rule) ? info.rule : null,
    schema_issue_paths: Array.isArray(info.schema_issue_paths) ? info.schema_issue_paths.slice(0, 8) : [],
    cache_used: false,
  };
}
export const okDiag = size => diag('ok', 'none', { stage: 'none', size_bytes: size });

// File-system error -> status and detail. Retryable codes reach here only after the retry budget is spent.
export function fsDiag(error, stage) {
  const errno = errnoOf(error);
  const info = { stage, errno };
  switch (errno) {
    case 'ENOENT': return diag('missing', 'file_not_found', info);
    case 'ENOTDIR': return diag('missing', 'path_not_directory', info);
    case 'EACCES': return diag('access_denied', 'os_access_denied', info);
    case 'EPERM': return diag('access_denied', 'permission_error_after_retries', info);
    case 'EBUSY': case 'EAGAIN': return diag('transient_io_error', 'sharing_retries_exhausted', info);
    case null: return diag('transient_io_error', 'unexpected_exception', info);
    default: return diag('transient_io_error', 'io_error', info);
  }
}

// Owner/DACL verdict reason of verifySddl ("untrusted_owner: S-1-...") -> detail without the SID.
export function securityDetail(reason) {
  const head = String(reason ?? '').split(':')[0].trim();
  return DETAILS.has(head) ? head : 'unexpected_exception';
}

const CONTRACT_DETAILS = {
  invalid_json: 'invalid_json',
  invalid_utf8: 'invalid_utf8',
  duplicate_json_member: 'duplicate_json_member',
  unsafe_integer: 'unsafe_integer',
  float_for_integer_metric: 'float_for_integer_metric',
  invalid_timestamp: 'invalid_timestamp',
  secret_or_pii_detected: 'secret_or_pii_detected',
  conflicting_shared_pool_balance: 'conflicting_shared_pool_balance',
  api_limits_node_violates_schema: 'api_limits_node_violates_schema',
  'JSON Schema validation failed': 'schema_validation_failed',
  'fixture/live mixing': 'mode_mismatch',
};
// Contract failure -> diagnostics. Messages of ContractError are fixed strings of the Bridge code, so an unknown
// one is reported as the failed rule; any other exception is a validation exception without its message.
export function contractDiag(error, stage, size) {
  if (!(error instanceof ContractError)) return diag('invalid_input', 'validation_exception', { stage, size_bytes: size });
  const status = error.code === 'clock_invalid' ? 'clock_invalid' : 'invalid_input';
  const known = CONTRACT_DETAILS[error.message];
  const detail = error.code === 'clock_invalid' ? 'future_timestamp' : (known ?? 'semantic_rule_failed');
  return diag(status, detail, {
    stage, size_bytes: size,
    json_offset: error.details?.json_offset,
    schema_issue_paths: error.details?.schema_issue_paths,
    rule: detail === 'semantic_rule_failed' ? error.message : null,
  });
}

export const allNul = bytes => bytes.length > 0 && bytes.every(b => b === 0);

// Bytes of a copy no larger than MAX_SNAPSHOT_BYTES; null for a larger one (its content is not read).
function readBounded(path) {
  const fd = openSync(path, 'r');
  try {
    const st = fstatSync(fd);
    if (st.size > MAX_SNAPSHOT_BYTES) return null;
    const buffer = Buffer.allocUnsafe(Number(st.size) + 1);
    let total = 0, n;
    while (total < buffer.length && (n = readSync(fd, buffer, total, buffer.length - total, total)) > 0) total += n;
    return total > MAX_SNAPSHOT_BYTES ? null : buffer.subarray(0, total);
  } finally { closeSync(fd); }
}

const allNulOrUnknown = bytes => (bytes === null ? null : allNul(bytes));

export const OVERLAY_TAIL = ['LocalCache', 'Local', 'DesktopWidgets'];
const PACKAGE_NAME = /^[A-Za-z0-9._-]{1,128}$/;
const MAX_COPIES = 64;

// MSIX file-system virtualization: a packaged app (and the Bridge it starts) that once wrote under
// %LOCALAPPDATA%\DesktopWidgets keeps a private copy in %LOCALAPPDATA%\Packages\<package>\LocalCache\Local\DesktopWidgets,
// and that copy is served to it instead of the real file. The real paths of these copies are visible to every process,
// so the scan finds them from inside and outside a package. served_to_this_process compares file identity with the
// file this process reads under its telemetry directory.
export function scanPackageOverlays({ localAppData, telemetryDir, files }) {
  const result = { status: 'not_applicable', scanned_packages: null, packages_with_overlay: [], copies: [], truncated: false, check_failed: false };
  if (!localAppData || process.platform !== 'win32') return result;
  const packagesRoot = join(localAppData, 'Packages');
  let packages;
  try { packages = readdirSync(packagesRoot, { withFileTypes: true }); }
  catch (error) {
    if (error.code === 'ENOENT') return { ...result, status: 'none_found', scanned_packages: 0 };
    return { ...result, status: 'check_failed', check_failed: true };
  }
  result.scanned_packages = 0;
  let failed = false;
  const ownIdentity = new Map();
  for (const name of files) {
    try { const st = statSync(join(telemetryDir, name), { bigint: true, throwIfNoEntry: false }); ownIdentity.set(name, st ? `${st.dev}:${st.ino}` : null); }
    catch { ownIdentity.set(name, undefined); }
  }
  for (const entry of packages) {
    if (!entry.isDirectory()) continue;
    result.scanned_packages++;
    if (!PACKAGE_NAME.test(entry.name)) continue;
    const root = join(packagesRoot, entry.name, ...OVERLAY_TAIL);
    try {
      // lstat: a reparse point (symlink/junction) is never followed.
      const st = lstatSync(root, { throwIfNoEntry: false });
      if (!st?.isDirectory()) continue;
    } catch { failed = true; continue; }
    if (result.packages_with_overlay.length >= MAX_COPIES) { result.truncated = true; continue; }
    result.packages_with_overlay.push(entry.name);
    for (const name of files) {
      const path = join(root, 'telemetry', name);
      let st;
      try { st = lstatSync(path, { bigint: true, throwIfNoEntry: false }); } catch { failed = true; continue; }
      if (!st || !st.isFile()) continue;
      if (result.copies.length >= MAX_COPIES) { result.truncated = true; continue; }
      let nul = null;
      try { nul = allNulOrUnknown(readBounded(path)); } catch { nul = null; }
      const own = ownIdentity.get(name);
      result.copies.push({
        package_family_name: entry.name,
        file: name,
        size_bytes: Number(st.size),
        all_nul_bytes: nul,
        served_to_this_process: own === undefined ? null : own === `${st.dev}:${st.ino}`,
      });
    }
  }
  result.check_failed = failed;
  result.status = result.packages_with_overlay.length > 0 ? 'found' : failed ? 'check_failed' : 'none_found';
  return result;
}

export const SOURCE_KEYS = { hardware: 'hardware_state', quotas: 'subscription_quotas', api: 'api_limits' };

// The full diagnostics document of one read.
export function buildDiagnostics({ mode, generatedAt, sources, overlay }) {
  return validateDiagnostics({
    schemaVersion: DIAGNOSTICS_SCHEMA_VERSION,
    kind: DIAGNOSTICS_KIND,
    bridge_output_schemaVersion: '1.0.0',
    mode,
    generated_at: generatedAt,
    sources,
    package_overlay: overlay,
  });
}

// Prebuilt bridge-output fixture: the read happened elsewhere, only its read_status is known.
export function diagnosticsOfOutput(output, files) {
  const sources = {};
  for (const [key, node] of Object.entries(SOURCE_KEYS)) {
    const recorded = output[node]?.read_status;
    const status = READ_STATUSES.has(recorded) ? recorded : 'missing';
    sources[node] = { file: files[key], ...diag(status, status === 'ok' ? 'none' : 'not_recorded') };
  }
  return buildDiagnostics({ mode: output.mode, generatedAt: output.generated_at, sources, overlay: { status: 'not_applicable', scanned_packages: null, packages_with_overlay: [], copies: [], truncated: false, check_failed: false } });
}
