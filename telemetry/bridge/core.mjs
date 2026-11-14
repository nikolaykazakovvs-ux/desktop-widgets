// Unified computational core of AI Telemetry Bridge 1.0.0 / policy 0.1.0.
// Pure functions only: strict JSON parsing, canonical schema validation, semantic rules and
// deterministic derivation. It is a line-by-line port of telemetry/tests/contract.py and is
// checked against it by test.mjs (differential test). No file system, OS or provider access.
import { readFileSync } from 'node:fs';
import { fromJsonSchema } from '@modelcontextprotocol/server';

export const SCHEMA_VERSION = '1.0.0';
export const POLICY_VERSION = '0.1.0';
export const MAX_SNAPSHOT_BYTES = 1024 * 1024;

export class ContractError extends Error {
  // code: invalid_input | clock_invalid. Messages are fixed strings, never input values.
  // details (optional, for read diagnostics only): { json_offset } or { schema_issue_paths }; never input values.
  constructor(code, message, details = null) { super(message); this.code = code; this.details = details; }
}
const fail = (condition, message, code = 'invalid_input') => { if (condition) throw new ContractError(code, message); };

// ---------- canonical schemas, bundled without external $ref ----------
const names = ['bridge-output.schema.json', 'common.schema.json', 'hardware-snapshot.schema.json', 'quotas-snapshot.schema.json', 'extensions/api-limits-v1.1.schema.json'];
const defKey = name => `telemetry_${name.replaceAll('.', '_').replaceAll('-', '_').replaceAll('/', '_')}`;
const schemas = new Map(names.map(name => [name, JSON.parse(readFileSync(new URL(`../schema/${name}`, import.meta.url), 'utf8'))]));
const ids = new Map([...schemas].map(([name, schema]) => [schema.$id, name]));
function rewrite(value, current) {
  if (Array.isArray(value)) return value.map(x => rewrite(x, current));
  if (!value || typeof value !== 'object') return value;
  return Object.fromEntries(Object.entries(value).filter(([k]) => k !== '$id' && k !== '$schema').map(([k, v]) => {
    if (k !== '$ref') return [k, rewrite(v, current)];
    const [file, fragment = ''] = v.split('#');
    const target = file ? (ids.get(file) ?? file) : current;
    if (!schemas.has(target) || (fragment && !fragment.startsWith('/'))) throw new Error('unsupported_schema_reference');
    return [k, `#/$defs/${defKey(target)}${fragment}`];
  }));
}
export function bundledSchema(rootName, fragment = '') {
  return {
    $schema: 'https://json-schema.org/draft/2020-12/schema', type: 'object',
    allOf: [{ $ref: `#/$defs/${defKey(rootName)}${fragment}` }],
    $defs: Object.fromEntries([...schemas].map(([name, schema]) => [defKey(name), rewrite(schema, name)])),
  };
}
export const outputSchema = bundledSchema('bridge-output.schema.json');
export const outputStandardSchema = fromJsonSchema(outputSchema);
const validators = {
  'hardware-snapshot': fromJsonSchema(bundledSchema('hardware-snapshot.schema.json'))['~standard'],
  'quotas-snapshot': fromJsonSchema(bundledSchema('quotas-snapshot.schema.json'))['~standard'],
  'api-balances-snapshot': fromJsonSchema(bundledSchema('extensions/api-limits-v1.1.schema.json'))['~standard'],
  // the Bridge api_limits node of the extension (second anyOf branch of bridge-output.schema.json)
  'bridge-api-limits': fromJsonSchema(bundledSchema('extensions/api-limits-v1.1.schema.json', '/$defs/bridge_api_limits'))['~standard'],
  'bridge-output': outputStandardSchema['~standard'],
};
export const METRICS = Object.keys(schemas.get('hardware-snapshot.schema.json').properties.metrics.properties);

// ---------- strict JSON: duplicate keys, safe integers, no NaN/Infinity, depth bound ----------
const MAX_SAFE = BigInt(Number.MAX_SAFE_INTEGER);
export function parseStrictJson(text) {
  let i = 0;
  const n = text.length;
  const bad = () => { throw new ContractError('invalid_input', 'invalid_json', { json_offset: i }); };
  const ws = () => { while (i < n) { const c = text.charCodeAt(i); if (c === 32 || c === 9 || c === 10 || c === 13) i++; else break; } };
  function string() {
    // text[i] === '"'
    let out = '', start = ++i;
    while (true) {
      if (i >= n) bad();
      const c = text.charCodeAt(i);
      if (c === 34) { out += text.slice(start, i); i++; return out; }
      if (c < 32) bad();
      if (c === 92) {
        out += text.slice(start, i);
        const e = text[i + 1];
        const map = { '"': '"', '\\': '\\', '/': '/', b: '\b', f: '\f', n: '\n', r: '\r', t: '\t' };
        if (e in map) { out += map[e]; i += 2; }
        else if (e === 'u') {
          const hex = text.slice(i + 2, i + 6);
          if (!/^[0-9a-fA-F]{4}$/.test(hex)) bad();
          out += String.fromCharCode(parseInt(hex, 16)); i += 6;
        } else bad();
        start = i;
      } else i++;
    }
  }
  function number(key) {
    const m = /^-?(?:0|[1-9]\d*)(\.\d+)?([eE][+-]?\d+)?/.exec(text.slice(i, i + 400));
    if (!m) bad();
    const literal = m[0];
    i += literal.length;
    if (key === 'minor_units' || key === 'scale') {
      if (m[1] !== undefined || m[2] !== undefined) {
        throw new ContractError('invalid_input', 'float_for_integer_metric');
      }
    }
    if (m[1] === undefined && m[2] === undefined) {
      const big = BigInt(literal);
      if (big > MAX_SAFE || big < -MAX_SAFE) throw new ContractError('invalid_input', 'unsafe_integer');
    }
    const value = Number(literal);
    if (!Number.isFinite(value)) bad();
    return value;
  }
  function value(depth, currentKey = null) {
    if (depth > 64) bad();
    ws();
    if (i >= n) bad();
    const c = text[i];
    if (c === '{') {
      i++; const obj = {}; const seen = new Set(); ws();
      if (text[i] === '}') { i++; return obj; }
      while (true) {
        ws(); if (text[i] !== '"') bad();
        const key = string();
        if (seen.has(key)) throw new ContractError('invalid_input', 'duplicate_json_member');
        seen.add(key); ws();
        if (text[i] !== ':') bad(); i++;
        const v = value(depth + 1, key);
        // __proto__ and similar keys are data, never prototype mutation.
        Object.defineProperty(obj, key, { value: v, enumerable: true, writable: true, configurable: true });
        ws();
        if (text[i] === ',') { i++; continue; }
        if (text[i] === '}') { i++; return obj; }
        bad();
      }
    }
    if (c === '[') {
      i++; const arr = []; ws();
      if (text[i] === ']') { i++; return arr; }
      while (true) {
        arr.push(value(depth + 1, null)); ws();
        if (text[i] === ',') { i++; continue; }
        if (text[i] === ']') { i++; return arr; }
        bad();
      }
    }
    if (c === '"') return string();
    if (text.startsWith('true', i)) { i += 4; return true; }
    if (text.startsWith('false', i)) { i += 5; return false; }
    if (text.startsWith('null', i)) { i += 4; return null; }
    if (c === '-' || (c >= '0' && c <= '9')) return number(currentKey);
    bad();
  }
  const result = value(0);
  ws();
  if (i !== n) bad();
  return result;
}

export function decodeUtf8Strict(bytes) {
  try { return new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(bytes); }
  catch { throw new ContractError('invalid_input', 'invalid_utf8'); }
}

// ---------- time: exact microseconds as BigInt, equal to Python datetime semantics ----------
const TS = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,6}))?Z$/;
function daysFromCivil(y, m, d) {
  y -= m <= 2 ? 1 : 0;
  const era = Math.floor(y / 400), yoe = y - era * 400;
  const doy = Math.floor((153 * (m + (m > 2 ? -3 : 9)) + 2) / 5) + d - 1;
  const doe = yoe * 365 + Math.floor(yoe / 4) - Math.floor(yoe / 100) + doy;
  return era * 146097 + doe - 719468;
}
export function instant(value) {
  const m = typeof value === 'string' ? TS.exec(value) : null;
  fail(!m, 'invalid_timestamp');
  const [y, mo, d, h, mi, s] = m.slice(1, 7).map(Number);
  const leap = (y % 4 === 0 && y % 100 !== 0) || y % 400 === 0;
  const dim = [31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
  fail(y < 1 || mo < 1 || mo > 12 || d < 1 || d > dim[mo - 1] || h > 23 || mi > 59 || s > 59, 'invalid_timestamp');
  const micro = m[7] ? Number(m[7].padEnd(6, '0')) : 0;
  return (BigInt(daysFromCivil(y, mo, d)) * 86400n + BigInt(h * 3600 + mi * 60 + s)) * 1000000n + BigInt(micro);
}
// Python: int((now - t).total_seconds() * 1000); total_seconds is the correctly rounded us/1e6.
export const ageMs = (value, now) => value === null ? null : Math.trunc((Number(now - instant(value)) / 1e6) * 1000);
const equivalentTime = (a, b) => (a === null || b === null) ? a === b : instant(a) === instant(b);
function latest(values) {
  let best = null;
  for (const v of values) if (v !== null && (best === null || instant(v) > instant(best))) best = v;
  return best;
}
export function formatInstant(us) {
  const days = Number(us / 86400000000n);
  const rest = us % 86400000000n;
  const date = new Date(days * 86400000);
  const sec = Number(rest / 1000000n), micro = Number(rest % 1000000n);
  const pad = (x, w = 2) => String(x).padStart(w, '0');
  return `${pad(date.getUTCFullYear(), 4)}-${pad(date.getUTCMonth() + 1)}-${pad(date.getUTCDate())}T${pad(Math.floor(sec / 3600))}:${pad(Math.floor(sec / 60) % 60)}:${pad(sec % 60)}.${pad(micro, 6)}Z`;
}

function clocks(document, now) {
  if (Array.isArray(document)) { for (const item of document) clocks(item, now); return; }
  if (document && typeof document === 'object') {
    for (const [key, value] of Object.entries(document)) {
      if ((key === 'measured_at' || key === 'published_at' || key === 'last_attempt_at' || key === 'generated_at') && value !== null) {
        fail(instant(value) > now, 'future_observation_publication_or_attempt', 'clock_invalid');
      }
      clocks(value, now);
    }
  }
}

// ---------- semantic validation (contract.py) ----------
function sourceRelationships(source, measurements) {
  fail(!equivalentTime(source.measured_at, latest(measurements)), 'source measurement is not latest original observation');
  fail(source.measured_at !== null && source.last_attempt_at === null, 'observation without collection attempt');
  if (source.last_attempt_at !== null) {
    fail(instant(source.last_attempt_at) > instant(source.published_at), 'attempt after publication');
    if (source.measured_at !== null) fail(instant(source.measured_at) > instant(source.last_attempt_at), 'observation after last attempt');
  }
  for (const v of measurements) if (v !== null) fail(instant(v) > instant(source.published_at), 'observation after publication');
  fail(source.status === 'ok' && source.reason !== 'none', 'ok source has error reason');
  fail(source.status !== 'ok' && source.reason === 'none', 'non-ok source lacks reason');
}

function validateHardware(source) {
  const metrics = source.metrics, quality = source.network_quality, reach = metrics.network_reachability;
  fail(reach.status !== 'ok' && reach.scope !== 'not_measured', 'unmeasured reachability claims a measured scope');
  sourceRelationships(source, [...Object.values(metrics).map(m => m.measured_at), quality.measured_at]);
  const known = new Map(Object.entries(metrics).filter(([, v]) => v.status === 'ok').map(([k, v]) => [k, v.value]));
  fail(source.status === 'ok' && (known.size !== METRICS.length || quality.status !== 'ok'), 'ok hardware has missing metrics');
  fail(source.status === 'unavailable' && (known.size > 0 || quality.measured_at !== null), 'unavailable hardware has usable metrics');
  for (const [a, t] of [['ram_available_bytes', 'ram_total_bytes'], ['vram_available_bytes', 'vram_total_bytes'], ['gpu_memory_available_bytes', 'gpu_memory_total_bytes'], ['commit_total_bytes', 'commit_limit_bytes'], ['commit_headroom_bytes', 'commit_limit_bytes']]) {
    if (known.has(a) && known.has(t)) fail(known.get(a) > known.get(t), 'memory quantity exceeds total/limit');
  }
  const commit = ['commit_total_bytes', 'commit_limit_bytes', 'commit_headroom_bytes'];
  fail(known.has(commit[2]) && !commit.slice(0, 2).every(k => known.has(k)), 'computed commit headroom without both source operands');
  if (commit.every(k => known.has(k))) {
    fail(known.get(commit[2]) !== known.get(commit[1]) - known.get(commit[0]), 'commit headroom arithmetic mismatch');
    fail(new Set(commit.map(k => instant(metrics[k].measured_at))).size !== 1, 'commit arithmetic combines different observations');
  }
  if (source.gpu_memory_kind !== 'dedicated') fail(['vram_total_bytes', 'vram_available_bytes'].some(k => known.has(k)), 'shared/unknown GPU memory presented as dedicated VRAM');
  if (source.gpu_memory_kind === 'unknown') fail(['gpu_memory_total_bytes', 'gpu_memory_available_bytes'].some(k => known.has(k)), 'known GPU memory without kind');
  if (source.gpu_memory_kind === 'dedicated') {
    for (const [d, g] of [['vram_total_bytes', 'gpu_memory_total_bytes'], ['vram_available_bytes', 'gpu_memory_available_bytes']]) {
      if (known.has(d) && known.has(g)) fail(known.get(d) !== known.get(g) || !equivalentTime(metrics[d].measured_at, metrics[g].measured_at), 'dedicated GPU memory aliases disagree');
    }
  }
  validateNetworkQuality(quality);
}

function validateNetworkQuality(q) {
  const counters = ['internet_sent', 'internet_lost', 'gateway_sent', 'gateway_lost', 'outages_count', 'disconnect_count'];
  const numbers = [...counters, 'coverage_seconds', 'internet_loss_percent', 'gateway_loss_percent'];
  const known = numbers.some(k => q[k] !== null);
  fail(known && (q.measured_at === null || q.window_start === null || q.window_end === null), 'network counts without original time/window');
  fail(q.status === 'ok' && (numbers.some(k => q[k] === null) || q.reason !== 'none'), 'ok network quality has missing facts');
  fail(q.status !== 'ok' && q.reason === 'none', 'non-ok network quality lacks reason');
  fail((q.status === 'unknown' || q.status === 'not_supported') && known, 'unknown network quality has known values');
  fail((q.window_start === null) !== (q.window_end === null), 'incomplete network window');
  if (q.window_start !== null) {
    const start = instant(q.window_start), end = instant(q.window_end);
    const seconds = Number(end - start) / 1e6;
    fail(seconds < 1 || seconds > 26 * 3600, 'invalid local-calendar-day UTC bounds');
    if (q.measured_at !== null) {
      const observed = instant(q.measured_at);
      fail(!(start <= observed && observed < end), 'network observation outside its window');
      fail(q.coverage_seconds !== null && q.coverage_seconds > Number(observed - start) / 1e6, 'network coverage exceeds elapsed window');
    }
  }
  for (const scope of ['internet', 'gateway']) {
    const sent = q[`${scope}_sent`], lost = q[`${scope}_lost`], loss = q[`${scope}_loss_percent`];
    if (sent !== null && lost !== null) fail(lost > sent, 'network lost exceeds sent');
    if (sent === null || lost === null || sent === 0) fail(loss !== null, 'unknown/unmeasured loss reported as known percentage');
    else if (loss !== null) fail(Math.abs(loss - 100 * lost / sent) > 0.001, 'network loss percentage arithmetic mismatch');
  }
}

function* windowsOf(source) {
  for (const provider of source.providers) for (const pool of provider.pools) for (const window of pool.windows) yield [provider, pool, window];
}

function validateQuotas(source) {
  const providers = source.providers;
  fail(new Set(providers.map(p => p.provider_id)).size !== providers.length, 'duplicate provider identity');
  const observations = [];
  for (const provider of providers) {
    const pools = provider.pools;
    fail(new Set(pools.map(p => p.pool_id)).size !== pools.length, 'duplicate pool identity');
    const providerTimes = [];
    for (const pool of pools) {
      const items = pool.windows;
      fail(new Set(items.map(w => w.window_id)).size !== items.length, 'duplicate window identity');
      for (const w of items) {
        const observed = w.measured_at, attempted = w.last_attempt_at;
        fail(observed !== null && attempted === null, 'window observation without attempt');
        providerTimes.push(observed);
        if (observed !== null && attempted !== null) fail(instant(observed) > instant(attempted), 'window observation after attempt');
        if (attempted !== null) fail(provider.last_attempt_at === null || instant(attempted) > instant(provider.last_attempt_at), 'window attempt after provider attempt');
        if (w.status === 'ok') fail(Math.abs(w.used_percent + w.remaining_percent - 100) > 0.001, 'quota percentage complement mismatch');
      }
    }
    fail(!equivalentTime(provider.measured_at, latest(providerTimes)), 'provider observation mismatch');
    const all = pools.flatMap(p => p.windows);
    const knownWindows = all.filter(w => w.status === 'ok');
    fail(provider.status === 'unavailable' && knownWindows.length > 0, 'unavailable provider has usable windows');
    fail(provider.status === 'ok' && (knownWindows.length === 0 || all.some(w => w.status !== 'ok')), 'ok provider has missing windows');
    if (provider.measured_at !== null && provider.last_attempt_at !== null) fail(instant(provider.measured_at) > instant(provider.last_attempt_at), 'provider observation after attempt');
    if (provider.last_attempt_at !== null) fail(source.last_attempt_at === null || instant(provider.last_attempt_at) > instant(source.last_attempt_at), 'provider attempt after source attempt');
    fail(provider.status === 'ok' && provider.reason !== 'none', 'ok provider has error reason');
    fail(provider.status !== 'ok' && provider.reason === 'none', 'non-ok provider lacks reason');
    observations.push(...providerTimes);
  }
  sourceRelationships(source, observations);
  const all = [...windowsOf(source)].map(([, , w]) => w);
  const known = all.filter(w => w.status === 'ok');
  fail(source.status === 'unavailable' && known.length > 0, 'unavailable quotas have usable windows');
  fail(source.status === 'ok' && (known.length === 0 || all.some(w => w.status !== 'ok')), 'ok quotas have missing windows');
  fail(source.status === 'ok' && providers.some(p => p.status !== 'ok'), 'ok quotas contain non-ok provider');
}

export function deepEqual(a, b) {
  if (a === b) return true;
  if (typeof a !== typeof b || a === null || b === null || typeof a !== 'object') return false;
  if (Array.isArray(a) !== Array.isArray(b)) return false;
  if (Array.isArray(a)) return a.length === b.length && a.every((x, i) => deepEqual(x, b[i]));
  const ka = Object.keys(a), kb = Object.keys(b);
  return ka.length === kb.length && ka.every(k => Object.hasOwn(b, k) && deepEqual(a[k], b[k]));
}

// kind: hardware-snapshot | quotas-snapshot | bridge-output; now: BigInt microseconds or null.

const SECRET_PII_PATTERNS = [
  /\x00/,
  /\b(?:https?|wss?|ftp):\/\/\S+/i,
  /\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b/,
  /(?:\bsk-[A-Za-z0-9_-]{12,}|\bBearer\s+[A-Za-z0-9._~+/=-]{8,}|gh[pousr]_[A-Za-z0-9]{16,}|AIza[0-9A-Za-z_-]{20,}|op:\/\/)/,
  /(?:[A-Za-z]:[\\/]|\\\\[A-Za-z0-9_.-]+[\\/]|\/home\/|\/Users\/|\/etc\/)/
];

function scanPayloadSecurity(node) {
  if (typeof node === 'string') {
    for (const pat of SECRET_PII_PATTERNS) {
      fail(pat.test(node), 'secret_or_pii_detected');
    }
    return;
  }
  if (Array.isArray(node)) {
    for (const item of node) scanPayloadSecurity(item);
    return;
  }
  if (node && typeof node === 'object') {
    for (const [key, value] of Object.entries(node)) {
      scanPayloadSecurity(key);
      scanPayloadSecurity(value);
    }
  }
}

// Money arithmetic and unit rules of one entry; shared by the producer file check and by the
// internal-consistency check of a prebuilt Bridge node. Messages are fixed strings.
function checkApiAmount(e) {
  if (e.status === 'ok') {
    fail(typeof e.amount !== 'string', 'amount must be string');
    fail(typeof e.scale !== 'number' || e.scale < 0 || e.scale > 9, 'invalid scale');
    fail(typeof e.minor_units !== 'number' && typeof e.minor_units !== 'bigint', 'invalid minor_units');
    fail(Math.abs(Number(e.minor_units)) > 9007199254740991, 'minor_units exceeds safe bound');
    const parts = e.amount.split('.');
    const computedScale = parts[1] ? parts[1].length : 0;
    fail(computedScale !== e.scale, 'scale mismatch');
    const digitsBig = BigInt(e.amount.replace('.', ''));
    fail(digitsBig !== BigInt(e.minor_units), 'minor_units arithmetic mismatch');
    if (e.metric_kind === 'period_spend' || e.metric_kind === 'key_allowance') {
      fail(e.amount.startsWith('-'), 'negative amount for non-negative metric');
    }
    if (e.currency === 'credits') {
      fail(!e.credit_unit, 'missing credit_unit for credits');
    } else {
      fail(e.credit_unit !== null, 'fiat currency must not have credit_unit');
    }
  } else {
    fail(e.amount !== null || e.scale !== null || e.minor_units !== null, 'values present on non-ok entry');
    fail(e.reason === 'none', 'non-ok entry lacks reason');
  }
}

const poolLevelKey = e => `${e.metric_kind}:${e.currency}:${e.credit_unit || ''}`;
const isPoolLevel = e => e.connection_id === null || e.metric_kind !== 'key_allowance';

function validateApiBalances(source) {
  scanPayloadSecurity(source);
  // The source observation time is the latest original observation of its entries (as for hardware/quotas).
  sourceRelationships(source, source.entries.map(e => e.measured_at));
  fail(source.status === 'ok' && !source.entries.some(e => e.status === 'ok'), 'ok snapshot without usable entries');
  fail(source.heartbeat_interval_ms !== 10000, 'invalid heartbeat_interval_ms');
  fail(source.poll_interval_ms < 60000 || source.poll_interval_ms > 86400000, 'invalid poll_interval_ms');

  const poolById = new Map(source.pools.map(p => [p.shared_pool_id, p]));
  fail(poolById.size !== source.pools.length, 'duplicate shared_pool_id');

  const connPool = new Map(); // connection_id -> shared_pool_id
  const poolsWithConnection = new Set();
  for (const c of source.connections) {
    fail(connPool.has(c.connection_id), 'duplicate connection_id');
    const pool = poolById.get(c.shared_pool_id);
    fail(!pool, 'connection references unknown pool');
    fail(pool.provider_id !== c.provider_id, 'connection provider differs from its pool');
    connPool.set(c.connection_id, c.shared_pool_id);
    poolsWithConnection.add(c.shared_pool_id);
  }
  // The Bridge node lists connection_ids per pool (minItems 1): a pool without a connection has no valid output form.
  fail(poolById.size !== poolsWithConnection.size, 'pool without connection');

  const entriesSeen = new Map();
  for (const e of source.entries) {
    fail(!poolById.has(e.shared_pool_id), 'entry references unknown pool');
    if (e.connection_id !== null) {
      fail(!connPool.has(e.connection_id), 'entry references unknown connection');
      fail(connPool.get(e.connection_id) !== e.shared_pool_id, 'entry connection belongs to another pool');
    }
    // Order of entry times: measured_at <= last_attempt_at <= source.last_attempt_at (<= published_at, see above).
    fail(e.measured_at !== null && e.last_attempt_at === null, 'entry observation without attempt');
    if (e.last_attempt_at !== null) {
      fail(source.last_attempt_at === null || instant(e.last_attempt_at) > instant(source.last_attempt_at), 'entry attempt after source attempt');
      if (e.measured_at !== null) fail(instant(e.measured_at) > instant(e.last_attempt_at), 'entry observation after its attempt');
    }
    checkApiAmount(e);

    if (isPoolLevel(e)) {
      const key = `${e.shared_pool_id}:${poolLevelKey(e)}`;
      const group = entriesSeen.get(key) || [];
      group.push(e);
      entriesSeen.set(key, group);
    }
  }

  for (const group of entriesSeen.values()) {
    if (group.length <= 1) continue;
    for (let i = 0; i < group.length; i++) {
      for (let j = i + 1; j < group.length; j++) {
        const a = group[i];
        const b = group[j];
        if (equivalentTime(a.measured_at, b.measured_at)) {
          fail(a.amount !== b.amount || a.scale !== b.scale || a.minor_units !== b.minor_units, 'conflicting_shared_pool_balance');
        }
      }
    }
  }
}

// Internal invariants of a prebuilt Bridge api_limits node (extension form), checked without the producer file.
// Not re-derived: published_at based liveness, poll_interval_ms and the status/reason of the source.
const FRESH_MIN_MS = 180000, FRESH_MAX_MS = 86400000 * 3;
function checkApiLimitsNode(node, now) {
  const readStatus = node.read_status;
  fail((node.status === 'ok') !== (node.reason === 'none'), 'api_limits status/reason mismatch');
  if (readStatus !== 'ok') {
    fail(node.status !== 'error', 'api_limits read error without error status');
    const expected = ERROR_REASON[readStatus] ?? 'source_unavailable';
    const allowed = readStatus === 'invalid_input' ? [expected, 'conflicting_shared_pool_balance'] : [expected];
    fail(!allowed.includes(node.reason), 'api_limits read error reason mismatch');
  }
  const poolIds = new Set();
  const entries = [];
  for (const pool of node.pools) {
    fail(poolIds.has(pool.shared_pool_id), 'api_limits duplicate pool');
    poolIds.add(pool.shared_pool_id);
    const connections = new Set(pool.connection_ids);
    const poolLevel = new Set();
    for (const e of pool.entries) {
      if (e.connection_id !== null) fail(!connections.has(e.connection_id), 'api_limits entry connection is not in its pool');
      if (isPoolLevel(e)) {
        const key = poolLevelKey(e);
        fail(poolLevel.has(key), 'api_limits duplicate pool-level entry');
        poolLevel.add(key);
      }
      checkApiAmount(e);
      fail(e.status === 'ok' && e.measured_at === null, 'api_limits ok entry without observation time');
      const age = ageMs(e.measured_at, now);
      fail(e.age_ms !== age, 'api_limits entry age differs from clocks');
      if (e.status !== 'ok' || age === null) fail(e.freshness !== 'unknown', 'api_limits entry freshness mismatch');
      else if (age <= FRESH_MIN_MS) fail(e.freshness !== 'fresh', 'api_limits entry freshness mismatch');
      else if (age > FRESH_MAX_MS) fail(e.freshness !== 'stale', 'api_limits entry freshness mismatch');
      else fail(e.freshness === 'unknown', 'api_limits entry freshness mismatch');
      fail(node.liveness === 'silent' && e.liveness !== 'silent', 'api_limits entry alive under silent publisher');
      fail(node.liveness === 'alive' && e.liveness === 'silent', 'api_limits entry silent under alive publisher');
      entries.push(e);
    }
  }
  fail(node.pools.length > 0 && node.liveness === 'unknown', 'api_limits pools without liveness');
  const fresh = entries.some(e => e.freshness === 'stale') ? 'stale' : entries.some(e => e.freshness === 'fresh') ? 'fresh' : 'unknown';
  fail(node.freshness !== fresh, 'api_limits node freshness is not aggregated from entries');
  if (node.status === 'ok') {
    fail(readStatus !== 'ok' || node.liveness !== 'alive' || fresh !== 'fresh' || entries.some(e => e.freshness === 'unknown'), 'api_limits ok node is not fresh, alive and complete');
  }
}

// JSON pointers of schema violations, taken from the validator message ("data/a/0 must ..."). Only the instance
// location is kept: segments outside [A-Za-z0-9_] become *, so no input value or odd key leaves the Bridge.
export function schemaIssuePaths(issues) {
  const paths = [];
  for (const issue of issues ?? []) {
    for (const match of String(issue?.message ?? '').matchAll(/(?:^|, )data((?:\/[^\s,]*)?) must/g)) {
      const pointer = match[1].split('/').slice(1).map(seg => /^[A-Za-z0-9_]{1,64}$/.test(seg) ? seg : '*').map(seg => `/${seg}`).join('');
      // read-diagnostics schema: maxLength 1024 per pointer; a longer one is dropped, not published.
      if (pointer.length > 1024) continue;
      if (!paths.includes(pointer)) paths.push(pointer);
      if (paths.length >= 8) return paths;
    }
  }
  return paths;
}

export function validateDocument(kind, document, now = null) {
  const result = validators[kind].validate(document);
  if (result.issues) throw new ContractError('invalid_input', 'JSON Schema validation failed', { schema_issue_paths: schemaIssuePaths(result.issues) });
  const clock = now ?? (kind === 'bridge-output' ? instant(document.generated_at) : nowMicros());
  clocks(document, clock);
  if (kind === 'hardware-snapshot') validateHardware(document);
  else if (kind === 'quotas-snapshot') validateQuotas(document);
  else if (kind === 'api-balances-snapshot') validateApiBalances(document);
  else {
    const expected = deriveOutput(document.hardware_state.source, document.subscription_quotas.source, document.generated_at, document.mode, document.hardware_state.read_status, document.subscription_quotas.read_status, true);
    if (document.api_limits && document.api_limits.status !== 'not_available') {
      // Extension form: not derivable without the producer file; internal invariants are checked instead.
      checkApiLimitsNode(document.api_limits, clock);
      expected.api_limits = document.api_limits;
    }
    fail(!deepEqual(document, expected), 'derived fields differ from policy');
  }
  return document;
}

// ---------- deterministic derivation (contract.py derive_output) ----------
function assessment(metric, now, threshold) {
  const age = ageMs(metric.measured_at, now);
  if (metric.status !== 'ok' || age === null) return { freshness: 'unknown', age_ms: age };
  return { freshness: age <= threshold ? 'fresh' : 'stale', age_ms: age };
}
function hardwareAssessment(source, key, now, threshold) {
  const metric = source.metrics[key];
  if (key === 'gateway_rtt_ms' || key === 'internet_rtt_ms') {
    const interval = metric.probe_interval_ms;
    if (interval !== null) {
      const limit = Math.max(5000, 3 * interval);
      const result = assessment(metric, now, limit);
      if (result.freshness === 'fresh' && result.age_ms >= limit) result.freshness = 'stale';
      return result;
    }
  }
  return assessment(metric, now, threshold);
}
const ERROR_REASON = { invalid_input: 'invalid_input', clock_invalid: 'clock_invalid', transient_io_error: 'transient_io_error', access_denied: 'access_denied' };
function nodeState(source, assessments, now, heartbeatThreshold, readStatus) {
  if (readStatus === 'ok') fail(source === null, 'successful read without validated source');
  if (source === null) return { status: readStatus in ERROR_REASON ? 'error' : 'unavailable', reason: ERROR_REASON[readStatus] ?? 'source_unavailable', liveness: 'unknown', freshness: 'unknown', read_status: readStatus };
  const fresh = assessments.some(a => a.freshness === 'stale') ? 'stale' : assessments.some(a => a.freshness === 'fresh') ? 'fresh' : 'unknown';
  const alive = ageMs(source.published_at, now) <= heartbeatThreshold;
  let status = source.status, reason = source.reason;
  if (status === 'ok' && (!alive || fresh !== 'fresh' || assessments.some(a => a.freshness === 'unknown'))) {
    status = 'partial';
    reason = !alive ? 'publisher_silent' : fresh === 'stale' ? 'source_stale' : 'source_partial';
  }
  if (readStatus !== 'ok') { status = 'error'; reason = ERROR_REASON[readStatus] ?? 'source_unavailable'; }
  return { status, reason, liveness: alive ? 'alive' : 'silent', freshness: fresh, read_status: readStatus };
}
function resourcePressure(source, assessments, idsList, high, elevated, cpu) {
  const inputs = source === null ? [] : idsList.map(key => ({ metric_id: key, value: source.metrics[key].value, unit: source.metrics[key].unit, measured_at: source.metrics[key].measured_at }));
  const result = (level, reason) => ({ level, reason, inputs });
  if (source === null || source.status === 'unavailable') return result('unknown', 'source_unavailable');
  if (idsList.some(k => assessments[k].freshness === 'unknown')) return result('unknown', 'metric_unknown');
  if (idsList.some(k => assessments[k].freshness === 'stale')) return result('unknown', 'metric_stale');
  const values = idsList.map(k => source.metrics[k].value);
  if (!cpu && values[1] === 0) return result('unknown', 'denominator_zero');
  const fraction = cpu ? values[0] : 100 * values[0] / values[1];
  const level = (cpu ? fraction >= high : fraction <= high) ? 'high' : (cpu ? fraction >= elevated : fraction <= elevated) ? 'elevated' : 'normal';
  return result(level, level === 'normal' ? 'below_pressure_threshold' : cpu ? 'utilization_high' : 'reserve_fraction_low');
}


// Isolated error form of the api_limits node: the failure stays inside this node, hardware and quotas are unaffected.
const apiErrorNode = (reason, readStatus) => ({
  schema_extension: 'api-limits/1.1.0',
  status: 'error',
  reason,
  read_status: readStatus,
  liveness: 'unknown',
  freshness: 'unknown',
  pools: []
});

export function deriveApiLimits(apiBalances, now, apiReadStatus = null) {
  apiReadStatus = apiReadStatus || (apiBalances !== null ? 'ok' : 'missing');
  if (apiBalances === null) {
    if (apiReadStatus === 'ok' || apiReadStatus === 'missing') {
      return { status: 'not_available', reason: 'monitor_not_implemented' };
    }
    return apiErrorNode(ERROR_REASON[apiReadStatus] ?? 'source_unavailable', apiReadStatus);
  }

  const rawVer = apiBalances.schemaVersion;
  const schemaExtension = rawVer.startsWith('api-limits/') ? rawVer : `api-limits/${rawVer}`;
  const heartbeatThreshold = 30000;
  const pollLimit = Math.max(180000, 3 * (apiBalances.poll_interval_ms || 60000));
  const alive = ageMs(apiBalances.published_at, now) <= heartbeatThreshold;

  const poolsMap = new Map();
  for (const pool of apiBalances.pools) {
    poolsMap.set(pool.shared_pool_id, {
      shared_pool_id: pool.shared_pool_id,
      provider_id: pool.provider_id,
      account_id: pool.account_id,
      connection_ids: [],
      entries: []
    });
  }
  for (const conn of apiBalances.connections) {
    const p = poolsMap.get(conn.shared_pool_id);
    if (p && !p.connection_ids.includes(conn.connection_id)) {
      p.connection_ids.push(conn.connection_id);
    }
  }

  const poolEntriesMap = new Map();
  for (const entry of apiBalances.entries) {
    const list = poolEntriesMap.get(entry.shared_pool_id) || [];
    list.push(entry);
    poolEntriesMap.set(entry.shared_pool_id, list);
  }

  const allEntries = [];
  for (const [poolId, rawEntries] of poolEntriesMap) {
    const pool = poolsMap.get(poolId);
    if (!pool) continue;

    const poolLevelGroups = new Map();
    const connectionEntries = [];

    for (const e of rawEntries) {
      if (isPoolLevel(e)) {
        const key = poolLevelKey(e);
        const group = poolLevelGroups.get(key) || [];
        group.push(e);
        poolLevelGroups.set(key, group);
      } else {
        connectionEntries.push(e);
      }
    }

    const deduplicatedEntries = [];
    for (const [, group] of poolLevelGroups) {
      if (group.length === 1) {
        deduplicatedEntries.push(group[0]);
      } else {
        group.sort((a, b) => {
          if (a.measured_at === null) return 1;
          if (b.measured_at === null) return -1;
          return instant(b.measured_at) > instant(a.measured_at) ? 1 : (instant(b.measured_at) < instant(a.measured_at) ? -1 : 0);
        });
        const freshest = group[0];
        for (let i = 1; i < group.length; i++) {
          const other = group[i];
          if (equivalentTime(freshest.measured_at, other.measured_at)) {
            if (freshest.amount !== other.amount || freshest.scale !== other.scale || BigInt(freshest.minor_units) !== BigInt(other.minor_units)) {
              throw new ContractError('invalid_input', 'conflicting_shared_pool_balance');
            }
          }
        }
        deduplicatedEntries.push(freshest);
      }
    }

    for (const e of [...deduplicatedEntries, ...connectionEntries]) {
      const eAge = ageMs(e.measured_at, now);
      const eFresh = (e.status === 'ok' && eAge !== null && eAge <= pollLimit) ? 'fresh' : (e.status !== 'ok' ? 'unknown' : 'stale');
      const eAlive = !alive ? 'silent' : ((e.last_attempt_at !== null && ageMs(e.last_attempt_at, now) <= heartbeatThreshold) ? 'alive' : (e.status === 'ok' ? 'alive' : 'unknown'));

      const bridgeEntry = {
        connection_id: e.connection_id ?? null,
        metric_kind: e.metric_kind,
        currency: e.currency,
        credit_unit: e.credit_unit ?? null,
        amount: e.status === 'ok' ? e.amount : null,
        scale: e.status === 'ok' ? e.scale : null,
        minor_units: e.status === 'ok' ? e.minor_units : null,
        value_origin: e.value_origin,
        status: e.status,
        reason: e.reason,
        measured_at: e.measured_at,
        last_attempt_at: e.last_attempt_at,
        freshness: eFresh,
        liveness: eAlive,
        age_ms: eAge
      };
      if (e.degradation_status) {
        bridgeEntry.degradation_status = e.degradation_status;
      }
      pool.entries.push(bridgeEntry);
      allEntries.push(bridgeEntry);
    }
  }

  // Node state is aggregated from its entries, as for hardware and quotas: any stale entry makes the node stale;
  // an ok source is lowered to partial when the publisher is silent, entries are stale or some are unknown.
  const fresh = allEntries.some(e => e.freshness === 'stale') ? 'stale' : allEntries.some(e => e.freshness === 'fresh') ? 'fresh' : 'unknown';
  let status = apiBalances.status;
  let reason = apiBalances.reason;
  if (status === 'ok' && (!alive || fresh !== 'fresh' || allEntries.some(e => e.freshness === 'unknown'))) {
    status = 'partial';
    reason = !alive ? 'publisher_silent' : fresh === 'stale' ? 'source_stale' : 'source_partial';
  }
  // Read error with a cached snapshot: the same rule as hardware and quotas (status error, cause from the read
  // status); the cached pools stay with their original times and growing age.
  if (apiReadStatus !== 'ok') {
    status = 'error';
    reason = ERROR_REASON[apiReadStatus] ?? 'source_unavailable';
  }

  return {
    schema_extension: schemaExtension,
    status: status,
    reason: reason,
    read_status: apiReadStatus,
    liveness: alive ? 'alive' : 'silent',
    freshness: fresh,
    pools: Array.from(poolsMap.values())
  };
}

// The node of the published form: the accepted source must still produce a node inside its schema; otherwise
// the failure is isolated by the caller (deriveOutput, and the reader for read diagnostics).
export function deriveApiLimitsChecked(apiBalances, now, apiReadStatus = null) {
  const node = deriveApiLimits(apiBalances, now, apiReadStatus);
  if (node.status !== 'not_available' && validators['bridge-api-limits'].validate(node).issues) {
    throw new ContractError('invalid_input', 'api_limits_node_violates_schema');
  }
  return node;
}

// prevalidated: the caller (reader) already ran validateDocument with this exact clock on each source.
export function deriveOutput(hardware, quotas, generatedAt, mode = 'fixture', hardwareReadStatus = null, quotasReadStatus = null, prevalidated = false, apiBalances = null, apiReadStatus = null) {
  const now = instant(generatedAt);
  for (const [kind, source] of [['hardware-snapshot', hardware], ['quotas-snapshot', quotas], ['api-balances-snapshot', apiBalances]]) {
    if (source !== null) { if (!prevalidated) validateDocument(kind, source, now); fail(source.mode !== mode, 'fixture/live mixing'); }
  }
  const threshold = hardware ? Math.max(5000, 3 * hardware.sample_interval_ms) : 5000;
  const hwMetrics = Object.fromEntries(METRICS.map(key => [key, hardware ? hardwareAssessment(hardware, key, now, threshold) : { freshness: 'unknown', age_ms: null }]));
  const qualityMetric = hardware === null ? null : { ...hardware.network_quality, status: ['ok', 'partial'].includes(hardware.network_quality.status) ? 'ok' : hardware.network_quality.status };
  const qualityAssessment = qualityMetric ? assessment(qualityMetric, now, threshold) : { freshness: 'unknown', age_ms: null };
  hardwareReadStatus = hardwareReadStatus || (hardware !== null ? 'ok' : 'missing');
  quotasReadStatus = quotasReadStatus || (quotas !== null ? 'ok' : 'missing');
  const hwState = { ...nodeState(hardware, [...Object.values(hwMetrics), qualityAssessment], now, threshold, hardwareReadStatus), source: hardware, metrics: hwMetrics, network_quality: qualityAssessment };
  const quotaAssessments = [];
  if (quotas !== null) {
    const limit = Math.max(900000, 3 * quotas.poll_interval_ms);
    for (const [provider, pool, window] of windowsOf(quotas)) {
      const age = assessment(window, now, limit);
      const reset = window.resets_at, observed = window.measured_at;
      const pending = reset === null || observed === null ? null : instant(reset) <= now && instant(observed) < instant(reset);
      quotaAssessments.push({ provider_id: provider.provider_id, pool_id: pool.pool_id, window_id: window.window_id, ...age,
        reset_time_passed_unverified: pending,
        remaining_percent: age.freshness === 'fresh' && pending === false ? window.remaining_percent : null,
        resets_at: reset });
    }
  }
  const quotaState = { ...nodeState(quotas, quotaAssessments, now, 30000, quotasReadStatus), source: quotas, windows: quotaAssessments };
  const quality = [];
  for (const [name, state] of [['hardware', hwState], ['quotas', quotaState]]) {
    if (state.source === null || state.status === 'unavailable') quality.push(`${name}_unavailable`);
    else if (state.freshness === 'stale') quality.push(`${name}_stale`);
  }
  if ([hwState, quotaState].some(s => s.liveness === 'silent')) quality.push('publisher_silent');
  if ([...Object.values(hwMetrics), qualityAssessment, ...quotaAssessments].some(a => a.freshness === 'unknown')) quality.push('unknown_metrics');
  if (quotaAssessments.some(a => a.reset_time_passed_unverified === true)) quality.push('quota_reset_unverified');
  const pressure = {};
  for (const [key, idsList, high, elevated] of [['physical_memory', ['ram_available_bytes', 'ram_total_bytes'], 10, 20], ['commit_memory', ['commit_headroom_bytes', 'commit_limit_bytes'], 5, 10], ['vram', ['vram_available_bytes', 'vram_total_bytes'], 5, 10], ['cpu', ['cpu_usage_percent'], 95, 85]]) {
    pressure[key] = resourcePressure(hardware, hwMetrics, idsList, high, elevated, key === 'cpu');
  }
  let apiLimitsState;
  try {
    apiLimitsState = deriveApiLimitsChecked(apiBalances, now, apiReadStatus);
  } catch (err) {
    const errReason = (err?.message === 'conflicting_shared_pool_balance')
      ? 'conflicting_shared_pool_balance'
      : ((err instanceof ContractError && err.code in ERROR_REASON) ? ERROR_REASON[err.code] : 'invalid_input');
    apiLimitsState = apiErrorNode(errReason, apiReadStatus === 'ok' ? 'invalid_input' : (apiReadStatus || 'invalid_input'));
  }
  return { schemaVersion: SCHEMA_VERSION, mode, generated_at: generatedAt, policy_version: POLICY_VERSION,
    hardware_state: hwState, subscription_quotas: quotaState,
    api_limits: apiLimitsState,
    orchestration_hints: { policy_version: POLICY_VERSION, resource_pressure: pressure, data_quality: quality } };
}

// Bridge clock: Date.now() is floor(ms); +999 us keeps generated_at >= the real read instant, so a
// snapshot published within the same millisecond never looks like a future observation.
export function nowMicros() { return BigInt(Date.now()) * 1000n + 999n; }
