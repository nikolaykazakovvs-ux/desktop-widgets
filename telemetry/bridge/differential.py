"""Differential oracle cases for telemetry/bridge: emits JSON cases with contract.py verdicts.

Run by test.mjs; reuses telemetry/tests/contract.py and generate_fixtures.py unchanged.
Each case: raw texts of both snapshots, clock, mode, read-status overrides; expected per-source
class (ok | invalid_input | clock_invalid | missing) and the oracle derive_output.
"""
import json
import sys
from copy import deepcopy
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tests"))
from contract import derive_output, instant, validate_hardware, validate_quotas, clocks, SCHEMAS, REGISTRY  # noqa: E402
from generate_fixtures import AS_OF, normal, scenarios  # noqa: E402
from jsonschema import Draft202012Validator, FormatChecker  # noqa: E402

LIMIT = 9007199254740991


def load(text):
    def pairs(items):
        result = {}
        for k, v in items:
            if k in result:
                raise ValueError("dup")
            result[k] = v
        return result

    def const(_):
        raise ValueError("nonfinite")
    return json.loads(text, object_pairs_hook=pairs, parse_constant=const)


def classify(kind, text, now, mode):
    try:
        doc = load(text)
    except (ValueError, RecursionError):
        return "invalid_input", None
    validator = Draft202012Validator(SCHEMAS[kind + ".schema.json"], registry=REGISTRY, format_checker=FormatChecker())
    if not isinstance(doc, dict) or any(validator.iter_errors(doc)):
        return "invalid_input", None
    try:
        clocks(doc, now)
    except ValueError as error:
        return ("clock_invalid" if str(error) == "future observation/publication/attempt" else "invalid_input"), None
    try:
        (validate_hardware if kind == "hardware-snapshot" else validate_quotas)(doc)
        # contract.py parses resets_at only inside derive_output; an impossible date there is a
        # rejection of the input (FormatChecker without rfc3339-validator does not check it).
        if kind == "quotas-snapshot":
            for provider in doc["providers"]:
                for pool in provider["pools"]:
                    for window in pool["windows"]:
                        if window["resets_at"] is not None:
                            instant(window["resets_at"])
        if doc["mode"] != mode:
            raise ValueError("mixing")
    except (ValueError, KeyError, TypeError):
        return "invalid_input", None
    return "ok", doc


cases = []


def add(name, hardware, quotas, as_of=AS_OF, mode="fixture", hrs=None, qrs=None, raw=False):
    h = hardware if raw or hardware is None else json.dumps(hardware)
    q = quotas if raw or quotas is None else json.dumps(quotas)
    now = instant(as_of)
    hc, hd = ("missing", None) if h is None else classify("hardware-snapshot", h, now, mode)
    qc, qd = ("missing", None) if q is None else classify("quotas-snapshot", q, now, mode)
    # A read-status override models the cache path: the given valid doc is the cached source.
    out = derive_output(hd, qd, as_of, mode, hrs or hc, qrs or qc)
    cases.append({"name": name, "hardware": h, "quotas": q, "as_of": as_of, "mode": mode,
                  "hardware_read_status": hrs, "quotas_read_status": qrs,
                  "expected_classes": [hc, qc], "expected": out})


def mutate(name, kind, fn):
    h, q = normal()
    fn(h if kind == "h" else q)
    add(name, h, q)


def set_path(path, key, value):
    def apply(document):
        target = document
        for step in path:
            target = target[step]
        target[key] = value
    return apply


for sid, (hw, qu) in scenarios().items():
    add("scenario:" + sid, hw, qu)
hw, qu = normal()
for rs in ("missing", "invalid_input", "clock_invalid", "transient_io_error", "access_denied"):
    add("cached-read-failure:" + rs, hw, qu, hrs=rs, qrs=rs)
    add("cold-read-failure:" + rs, None, None, hrs=rs, qrs=rs)
for v in ("2.0.0", "1.1.0", "1.0", 1):
    mutate(f"version-h:{v}", "h", set_path([], "schemaVersion", v))
    mutate(f"version-q:{v}", "q", set_path([], "schemaVersion", v))
for path in ([], ["metrics"], ["metrics", "cpu_usage_percent"], ["metrics", "network_reachability"]):
    mutate("extra-h:" + "/".join(path), "h", set_path(path, "api_token", "synthetic-forbidden-marker"))
for path in ([], ["providers", 0], ["providers", 0, "pools", 0], ["providers", 0, "pools", 0, "windows", 0]):
    mutate("extra-q:" + "/".join(map(str, path)), "q", set_path(path, "account_email", "synthetic-forbidden-marker"))
for v in (-1, 100.01, "39", True, 84.99, 85, 95):
    mutate(f"cpu:{v}", "h", set_path(["metrics", "cpu_usage_percent"], "value", v))
for v in (-1, 1.5, 9007199254740992):
    mutate(f"ram:{v}", "h", set_path(["metrics", "ram_available_bytes"], "value", v))
mutate("unit-gib", "h", set_path(["metrics", "ram_available_bytes"], "unit", "GiB"))
for v in ("2026-01-01T11:59:59+00:00", "2026-01-01 11:59:59Z", "2026-01-01T11:59:59", "2026-02-30T12:00:00Z",
          "2026-01-01T11:59:60Z", "2026-01-01T12:00:01Z", "2026-01-01T11:59:59.123456Z"):
    mutate(f"published_at:{v}", "h", set_path([], "published_at", v))
mutate("future-measured-q", "q", set_path([], "measured_at", "2026-01-01T12:00:05Z"))
mutate("future-attempt-q", "q", set_path(["providers", 0], "last_attempt_at", "2026-01-01T12:10:00Z"))
mutate("unknown-status-value", "h", lambda d: d["metrics"]["cpu_usage_percent"].update(status="unknown", reason="sensor_unavailable"))
mutate("ok-null-value", "h", set_path(["metrics", "cpu_usage_percent"], "value", None))
mutate("commit-arith", "h", lambda d: d["metrics"]["commit_headroom_bytes"].update(value=d["metrics"]["commit_headroom_bytes"]["value"] - 1))
mutate("commit-align", "h", set_path(["metrics", "commit_headroom_bytes"], "measured_at", "2026-01-01T11:59:57Z"))


def partial_commit(d, headroom):
    d["metrics"]["commit_total_bytes"].update(value=None, status="unknown", reason="sensor_unavailable", measured_at=None)
    d.update(status="partial", reason="source_partial")
    if headroom is None:
        d["metrics"]["commit_headroom_bytes"].update(value=None, status="unknown", reason="sensor_unavailable", measured_at=None)
    else:
        d["metrics"]["commit_headroom_bytes"]["value"] = headroom


mutate("partial-commit-invalid", "h", lambda d: partial_commit(d, 2 ** 30))
mutate("partial-commit-valid", "h", lambda d: partial_commit(d, None))
for key in ("cpu_temperature_celsius", "gpu_temperature_celsius"):
    for v in (-100, -20.5, 0, 200, -100.01, 200.01):
        mutate(f"{key}:{v}", "h", set_path(["metrics", key], "value", v))
mutate("gpu-usage-101", "h", set_path(["metrics", "gpu_usage_percent"], "value", 101))
mutate("shared-gpu-invalid", "h", set_path([], "gpu_memory_kind", "shared"))


def shared_ok(d):
    d["gpu_memory_kind"] = "shared"
    for key in ("vram_total_bytes", "vram_available_bytes"):
        d["metrics"][key].update(value=None, status="unknown", reason="not_supported", measured_at=None)
    d.update(status="partial", reason="source_partial")


mutate("shared-gpu-valid", "h", shared_ok)


def rtt(d, interval):
    d["sample_interval_ms"] = 10000
    d["metrics"]["internet_rtt_ms"].update(measured_at="2026-01-01T11:59:55Z", probe_interval_ms=interval)
    d["metrics"]["gateway_rtt_ms"].update(measured_at="2026-01-01T11:59:54Z", probe_interval_ms=10000)


mutate("rtt-own-interval", "h", lambda d: rtt(d, 1000))
mutate("rtt-null-interval", "h", lambda d: rtt(d, None))
for key, v in (("internet_lost", 101), ("internet_loss_percent", 0), ("window_start", None),
               ("window_end", "2026-01-03T00:00:00Z"), ("coverage_seconds", 86400), ("window_start", "2026-01-01T01:00:00Z")):
    mutate(f"netq-{key}:{v}", "h", set_path(["network_quality"], key, v))


def zero_sent(d, fixed):
    d["network_quality"].update(internet_sent=0, internet_lost=0, internet_loss_percent=0)
    if fixed:
        d["network_quality"].update(internet_loss_percent=None, status="partial", reason="source_partial")
        d.update(status="partial", reason="source_partial")


mutate("zero-sent-invalid", "h", lambda d: zero_sent(d, False))
mutate("zero-sent-valid", "h", lambda d: zero_sent(d, True))
for key in ("internet_sent", "outages_count", "disconnect_count"):
    mutate(f"counter-limit:{key}", "h", lambda d, key=key: d["network_quality"].update(
        {"internet_sent": LIMIT, "internet_lost": 0, "internet_loss_percent": 0, key: LIMIT}))
    mutate(f"counter-limit+1:{key}", "h", set_path(["network_quality"], key, LIMIT + 1))
mutate("provider-probe-no-id", "h", set_path(["metrics", "network_reachability"], "scope", "provider_probe"))
mutate("provider-probe-id", "h", lambda d: d["metrics"]["network_reachability"].update(scope="provider_probe", provider_id="codex"))
partial_h, partial_q = scenarios()["partial"]
partial_h["metrics"]["network_reachability"]["scope"] = "gateway"
add("unmeasured-gateway", partial_h, partial_q)
mutate("mode-live-h", "h", set_path([], "mode", "live"))
mutate("mode-live-q", "q", set_path([], "mode", "live"))
for key, v in (("duration_seconds", 0), ("used_percent", 101), ("remaining_percent", -1), ("window_id", "Five Hour Display"),
               ("remaining_percent", 69), ("duration_seconds", LIMIT), ("duration_seconds", LIMIT + 1), ("duration_seconds", LIMIT + 2),
               ("resets_at", None), ("resets_at", "2026-02-30T00:00:00Z")):
    mutate(f"window-{key}:{v}", "q", set_path(["providers", 0, "pools", 0, "windows", 0], key, v))
for kind in ("provider", "pool", "window"):
    def dup(d, kind=kind):
        items = d["providers"] if kind == "provider" else d["providers"][0]["pools"] if kind == "pool" else d["providers"][0]["pools"][0]["windows"]
        items.append(deepcopy(items[0]))
    mutate("duplicate-" + kind, "q", dup)
h, q = scenarios()["quota-reset-pending"]
w = q["providers"][0]["pools"][0]["windows"][0]
w["measured_at"] = w["last_attempt_at"] = "2026-01-01T11:59:30Z"
q["providers"][0]["measured_at"] = q["providers"][0]["last_attempt_at"] = w["measured_at"]
q["measured_at"] = q["last_attempt_at"] = w["measured_at"]
add("post-reset-observation", h, q)
h, q = normal()
add("freshness+3s", h, q, as_of="2026-01-01T12:00:03Z")
add("freshness+3.001s", h, q, as_of="2026-01-01T12:00:03.001Z")
add("fractional-clock", h, q, as_of="2026-01-01T12:00:04.123457Z")
h["sample_interval_ms"] = 10000
add("custom-interval", h, q, as_of="2026-01-01T12:00:20Z")
h, q = normal()
h["mode"] = q["mode"] = "live"
add("live-mode", h, q, mode="live")
add("live-rejects-fixture", *normal(), mode="live")
text_h, text_q = json.dumps(normal()[0]), json.dumps(normal()[1])
sample = '"sample_interval_ms": 1000'
raws = {"dup-top": '{"mode":"live","mode":"fixture"}',
        "dup-nested": text_h.replace('"metrics": {', '"metrics": {"cpu_usage_percent": {}, ', 1),
        "nan": text_h.replace(sample, '"sample_interval_ms": NaN'), "infinity": text_h.replace(sample, '"sample_interval_ms": Infinity'),
        "truncated": text_h[:len(text_h) // 2], "empty-object": "{}", "bom": "﻿" + text_h, "array": "[]",
        "big-int-2^53": text_h.replace(sample, '"sample_interval_ms": 9007199254740992'),
        "big-int-2^53+1": text_h.replace(sample, '"sample_interval_ms": 9007199254740993'),
        "exp-int": text_h.replace(sample, '"sample_interval_ms": 1e3'),
        "trailing-garbage": text_h + " x", "comment": "// c\n" + text_h}
for name, text in raws.items():
    add("raw:" + name, text, text_q, raw=True)
sys.stdout.reconfigure(encoding="utf-8")
json.dump(cases, sys.stdout, ensure_ascii=False)
