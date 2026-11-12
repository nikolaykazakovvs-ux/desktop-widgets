"""Consuming adapter for AI Telemetry Bridge output (contract schemaVersion 1.0.0).

Designed for S.I.G.M.A. Drop-in, stdlib-only. The adapter only OBSERVES:
it does not touch S.I.G.M.A. process/job supervision, scheduling or executor
choice. It never reads widget state.json, never runs nvidia-smi and never polls
providers; the only input is bridge-output JSON.

Entry points:
  run_bridge_cli(command, timeout_s=5) -> BridgeObservation
  load_bridge_output(path)             -> BridgeObservation
  from_dict(obj)                       -> BridgeObservation
Unknown values stay None; a percentage is never invented.
"""
from __future__ import annotations

import json
import subprocess
from dataclasses import dataclass, field, asdict
from typing import Any, Optional

SUPPORTED_SCHEMA_VERSION = "1.0.0"
SUPPORTED_POLICY_VERSION = "0.1.0"
PROVIDER_IDS = ("codex", "claude", "antigravity", "gemini", "kimi")
# S.I.G.M.A.-side names -> contract ids; used ONLY by lookup helpers.
PROVIDER_ALIASES = {"claude_code": "claude", "gemini_cli": "gemini", "kimi_code": "kimi"}

HOST_METRICS = (
    "cpu_usage_percent", "ram_total_bytes", "ram_available_bytes",
    "commit_headroom_bytes", "commit_limit_bytes",
    "vram_total_bytes", "vram_available_bytes", "gpu_usage_percent",
)
PRESSURE_KEYS = ("physical_memory", "commit_memory", "vram", "cpu")

RESULT_OK = "ok"
RESULT_UNSUPPORTED = "unsupported_contract"
RESULT_INVALID = "invalid_input"
RESULT_ERROR = "error"


class _StrictJsonError(ValueError):
    pass


def _no_dupes(pairs):
    out = {}
    for k, v in pairs:
        if k in out:
            raise _StrictJsonError("duplicate key in JSON object")
        out[k] = v
    return out


def _bad_const(name):
    raise _StrictJsonError("non-finite number in JSON: " + name)


def strict_loads(text: str) -> Any:
    """json.loads rejecting duplicate keys and NaN/Infinity."""
    return json.loads(text, object_pairs_hook=_no_dupes, parse_constant=_bad_const)


def canonical_provider_id(name: str) -> str:
    return PROVIDER_ALIASES.get(name, name)


def _num(v):
    return v if isinstance(v, (int, float)) and not isinstance(v, bool) else None


@dataclass(frozen=True)
class QuotaObservation:
    provider_id: str
    pool_id: str
    window_id: str
    availability: str  # available | reset_pending | stale | unavailable
    freshness: Optional[str]
    age_ms: Optional[int]
    reset_time_passed_unverified: bool
    remaining_percent: Optional[float]
    resets_at: Optional[str]

    @property
    def key(self) -> tuple:
        return (self.provider_id, self.pool_id, self.window_id)


@dataclass(frozen=True)
class HostObservation:
    values: dict  # metric id -> value or None (never 0 for unknown)
    unknown_reasons: dict  # metric id -> reason string, for None values
    gpu_memory_kind: Optional[str]
    pressure: dict  # physical_memory/commit_memory/vram/cpu -> level string
    pressure_reasons: dict
    data_quality: tuple
    status: Optional[str] = None
    freshness: Optional[str] = None


@dataclass(frozen=True)
class BridgeObservation:
    result_status: str  # ok | unsupported_contract | invalid_input | error
    error: Optional[str] = None
    mode: Optional[str] = None
    is_fixture: bool = False
    generated_at: Optional[str] = None
    host: Optional[HostObservation] = None
    quotas: tuple = ()
    host_node: dict = field(default_factory=dict)  # status/reason/liveness/freshness/read_status
    quota_node: dict = field(default_factory=dict)

    def quota(self, provider: str, pool_id: str, window_id: str) -> Optional[QuotaObservation]:
        """Lookup by stable triple; provider aliases accepted here only."""
        pid = canonical_provider_id(provider)
        for q in self.quotas:
            if q.key == (pid, pool_id, window_id):
                return q
        return None

    def quotas_for(self, provider: str) -> tuple:
        pid = canonical_provider_id(provider)
        return tuple(q for q in self.quotas if q.provider_id == pid)

    def to_dict(self) -> dict:
        return asdict(self)


def _fail(status: str, msg: str) -> BridgeObservation:
    return BridgeObservation(result_status=status, error=msg)


def _availability(w: dict) -> str:
    if w.get("reset_time_passed_unverified") is True:
        return "reset_pending"
    if (w.get("freshness") == "fresh" and w.get("reset_time_passed_unverified") is False
            and _num(w.get("remaining_percent")) is not None):
        return "available"
    if w.get("freshness") == "stale":
        return "stale"
    return "unavailable"


def _map_quota(w: dict) -> QuotaObservation:
    return QuotaObservation(
        provider_id=str(w.get("provider_id")), pool_id=str(w.get("pool_id")),
        window_id=str(w.get("window_id")), availability=_availability(w),
        freshness=w.get("freshness"), age_ms=w.get("age_ms"),
        reset_time_passed_unverified=bool(w.get("reset_time_passed_unverified")),
        remaining_percent=_num(w.get("remaining_percent")),
        resets_at=w.get("resets_at"),
    )


def _map_host(hs: dict, hints: dict) -> HostObservation:
    src = hs.get("source") if isinstance(hs.get("source"), dict) else None
    fresh = hs.get("metrics") if isinstance(hs.get("metrics"), dict) else {}
    smetrics = (src or {}).get("metrics") or {}
    values, reasons = {}, {}
    for m in HOST_METRICS:
        fr = (fresh.get(m) or {}).get("freshness")
        if src is None:
            values[m], reasons[m] = None, "source_unavailable"
        elif fr != "fresh":
            values[m], reasons[m] = None, "metric_" + str(fr or "unknown")
        else:
            v = _num((smetrics.get(m) or {}).get("value"))
            values[m] = v
            if v is None:
                reasons[m] = "value_unknown"
    rp = (hints or {}).get("resource_pressure") or {}
    return HostObservation(
        values=values, unknown_reasons=reasons,
        gpu_memory_kind=(src or {}).get("gpu_memory_kind"),
        pressure={k: (rp.get(k) or {}).get("level", "unknown") for k in PRESSURE_KEYS},
        pressure_reasons={k: (rp.get(k) or {}).get("reason") for k in PRESSURE_KEYS},
        data_quality=tuple((hints or {}).get("data_quality") or ()),
        status=hs.get("status"), freshness=hs.get("freshness"),
    )


def _node(n: dict) -> dict:
    return {k: n.get(k) for k in ("status", "reason", "liveness", "freshness", "read_status")}


def from_dict(obj: Any) -> BridgeObservation:
    if not isinstance(obj, dict):
        return _fail(RESULT_INVALID, "bridge output is not a JSON object")
    if (obj.get("schemaVersion") != SUPPORTED_SCHEMA_VERSION
            or obj.get("policy_version") != SUPPORTED_POLICY_VERSION):
        return _fail(RESULT_UNSUPPORTED,
                     "unsupported contract: schemaVersion=%r policy_version=%r (supported %s / %s)"
                     % (obj.get("schemaVersion"), obj.get("policy_version"),
                        SUPPORTED_SCHEMA_VERSION, SUPPORTED_POLICY_VERSION))
    hs, sq = obj.get("hardware_state"), obj.get("subscription_quotas")
    if not isinstance(hs, dict) or not isinstance(sq, dict) or not isinstance(sq.get("windows"), list):
        return _fail(RESULT_INVALID, "hardware_state or subscription_quotas malformed")
    try:
        quotas = tuple(_map_quota(w) for w in sq["windows"] if isinstance(w, dict))
        host = _map_host(hs, obj.get("orchestration_hints") or {})
    except Exception as e:  # report type only, no traceback text
        return _fail(RESULT_INVALID, "malformed bridge output (%s)" % type(e).__name__)
    mode = obj.get("mode")
    return BridgeObservation(
        result_status=RESULT_OK, mode=mode, is_fixture=(mode == "fixture"),
        generated_at=obj.get("generated_at"), host=host, quotas=quotas,
        host_node=_node(hs), quota_node=_node(sq),
    )


def parse_bridge_json(text: str) -> BridgeObservation:
    try:
        return from_dict(strict_loads(text))
    except ValueError as e:
        return _fail(RESULT_INVALID, "invalid JSON: " + str(e)[:120])


def load_bridge_output(path) -> BridgeObservation:
    try:
        with open(path, "r", encoding="utf-8") as f:
            text = f.read()
    except OSError as e:
        return _fail(RESULT_ERROR, "cannot read bridge output (%s)" % type(e).__name__)
    return parse_bridge_json(text)


def run_bridge_cli(command, timeout_s: float = 5) -> BridgeObservation:
    """Run the Bridge CLI (shell=False). Exit 0 -> stdout is pure JSON;
    exit 2 -> configuration error; any other failure -> error result.
    stderr/tracebacks are never copied into the result."""
    if not isinstance(command, (list, tuple)) or not command:
        return _fail(RESULT_INVALID, "command must be a non-empty list")
    try:
        p = subprocess.run(list(command), capture_output=True, timeout=timeout_s,
                           shell=False, stdin=subprocess.DEVNULL)
    except subprocess.TimeoutExpired:
        return _fail(RESULT_ERROR, "bridge cli timeout")
    except OSError as e:
        return _fail(RESULT_ERROR, "bridge cli not runnable (%s)" % type(e).__name__)
    if p.returncode == 2:
        return _fail(RESULT_ERROR, "bridge cli configuration error (exit 2)")
    if p.returncode != 0:
        return _fail(RESULT_ERROR, "bridge cli failed (exit %d)" % p.returncode)
    try:
        text = p.stdout.decode("utf-8")
    except UnicodeDecodeError:
        return _fail(RESULT_INVALID, "bridge cli stdout is not UTF-8")
    return parse_bridge_json(text)
