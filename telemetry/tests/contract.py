"""Offline stage-1 contract oracle, not an installed bridge or a producer.

JSON Schema checks structure. Functions below check clocks, relationships and
deterministic policy separately. No OS/provider reads, writes or remote refs.
"""
from __future__ import annotations

import argparse
import json
from datetime import datetime, timezone
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker
from referencing import Registry, Resource

ROOT = Path(__file__).resolve().parents[1]
SCHEMAS = {}
REGISTRY = Registry()
for _path in (ROOT / "schema").rglob("*.schema.json"):
    _schema = json.loads(_path.read_text(encoding="utf-8"))
    Draft202012Validator.check_schema(_schema)
    SCHEMAS[_path.name] = _schema
    REGISTRY = REGISTRY.with_resource(_schema["$id"], Resource.from_contents(_schema))

POLICY_VERSION = "0.1.0"
METRICS = tuple(SCHEMAS["hardware-snapshot.schema.json"]["properties"]["metrics"]["properties"])


def load_document(path):
    """Reject duplicate keys and non-JSON NaN/Infinity before schema validation."""
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise ValueError("duplicate JSON member")
            result[key] = value
        return result

    def invalid_constant(_):
        raise ValueError("non-finite JSON number")

    return json.loads(Path(path).read_text(encoding="utf-8"), object_pairs_hook=pairs,
                      parse_constant=invalid_constant)


def instant(value):
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def age_ms(value, now):
    return None if value is None else int((now - instant(value)).total_seconds() * 1000)


def fail(condition, message):
    if condition:
        raise ValueError(message)


def clocks(document, now):
    """Reset times can be future; measurement/publication/attempt times cannot."""
    if isinstance(document, dict):
        for key, value in document.items():
            if key in ("measured_at", "published_at", "last_attempt_at", "generated_at") and value is not None:
                fail(instant(value) > now, "future observation/publication/attempt")
            clocks(value, now)
    elif isinstance(document, list):
        for item in document:
            clocks(item, now)


def latest(values):
    values = [value for value in values if value is not None]
    return max(values, key=instant) if values else None


def equivalent_time(left, right):
    return left == right if left is None or right is None else instant(left) == instant(right)


def source_relationships(source, measurements):
    fail(not equivalent_time(source["measured_at"], latest(measurements)), "source measurement is not latest original observation")
    fail(source["measured_at"] is not None and source["last_attempt_at"] is None, "observation without collection attempt")
    if source["last_attempt_at"] is not None:
        fail(instant(source["last_attempt_at"]) > instant(source["published_at"]), "attempt after publication")
        if source["measured_at"] is not None:
            fail(instant(source["measured_at"]) > instant(source["last_attempt_at"]), "observation after last attempt")
    for value in measurements:
        if value is not None:
            fail(instant(value) > instant(source["published_at"]), "observation after publication")
    fail(source["status"] == "ok" and source["reason"] != "none", "ok source has error reason")
    fail(source["status"] != "ok" and source["reason"] == "none", "non-ok source lacks reason")


def validate_hardware(source):
    metrics = source["metrics"]
    quality = source["network_quality"]
    reachability = metrics["network_reachability"]
    fail(reachability["status"] != "ok" and reachability["scope"] != "not_measured",
         "unmeasured reachability claims a measured scope")
    source_relationships(source, [m["measured_at"] for m in metrics.values()] + [quality["measured_at"]])
    known = {key: value["value"] for key, value in metrics.items() if value["status"] == "ok"}
    fail(source["status"] == "ok" and (len(known) != len(METRICS) or quality["status"] != "ok"), "ok hardware has missing metrics")
    fail(source["status"] == "unavailable" and (bool(known) or quality["measured_at"] is not None), "unavailable hardware has usable metrics")
    for available, total in (("ram_available_bytes", "ram_total_bytes"),
                             ("vram_available_bytes", "vram_total_bytes"),
                             ("gpu_memory_available_bytes", "gpu_memory_total_bytes"),
                             ("commit_total_bytes", "commit_limit_bytes"),
                             ("commit_headroom_bytes", "commit_limit_bytes")):
        if available in known and total in known:
            fail(known[available] > known[total], "memory quantity exceeds total/limit")
    commit = ("commit_total_bytes", "commit_limit_bytes", "commit_headroom_bytes")
    fail(commit[2] in known and not all(key in known for key in commit[:2]),
         "computed commit headroom without both source operands")
    if all(key in known for key in commit):
        fail(known[commit[2]] != known[commit[1]] - known[commit[0]], "commit headroom arithmetic mismatch")
        fail(len({instant(metrics[key]["measured_at"]) for key in commit}) != 1,
             "commit arithmetic combines different observations")
    if source["gpu_memory_kind"] != "dedicated":
        fail(any(key in known for key in ("vram_total_bytes", "vram_available_bytes")), "shared/unknown GPU memory presented as dedicated VRAM")
    if source["gpu_memory_kind"] == "unknown":
        fail(any(key in known for key in ("gpu_memory_total_bytes", "gpu_memory_available_bytes")), "known GPU memory without kind")
    if source["gpu_memory_kind"] == "dedicated":
        for dedicated, generic in (("vram_total_bytes", "gpu_memory_total_bytes"), ("vram_available_bytes", "gpu_memory_available_bytes")):
            if dedicated in known and generic in known:
                fail(known[dedicated] != known[generic] or not equivalent_time(metrics[dedicated]["measured_at"], metrics[generic]["measured_at"]), "dedicated GPU memory aliases disagree")
    validate_network_quality(quality)


def validate_network_quality(quality):
    counters = ("internet_sent", "internet_lost", "gateway_sent", "gateway_lost", "outages_count", "disconnect_count")
    numbers = (*counters, "coverage_seconds", "internet_loss_percent", "gateway_loss_percent")
    known = any(quality[key] is not None for key in numbers)
    fail(known and (quality["measured_at"] is None or quality["window_start"] is None or quality["window_end"] is None), "network counts without original time/window")
    fail(quality["status"] == "ok" and (any(quality[key] is None for key in numbers) or quality["reason"] != "none"), "ok network quality has missing facts")
    fail(quality["status"] != "ok" and quality["reason"] == "none", "non-ok network quality lacks reason")
    fail(quality["status"] in ("unknown", "not_supported") and known, "unknown network quality has known values")
    fail((quality["window_start"] is None) != (quality["window_end"] is None), "incomplete network window")
    if quality["window_start"] is not None:
        start, end = instant(quality["window_start"]), instant(quality["window_end"])
        seconds = (end - start).total_seconds()
        fail(seconds < 1 or seconds > 26 * 3600, "invalid local-calendar-day UTC bounds")
        if quality["measured_at"] is not None:
            observed = instant(quality["measured_at"])
            fail(not start <= observed < end, "network observation outside its window")
            fail(quality["coverage_seconds"] is not None and quality["coverage_seconds"] > (observed - start).total_seconds(), "network coverage exceeds elapsed window")
    for scope in ("internet", "gateway"):
        sent, lost, loss = (quality[scope + suffix] for suffix in ("_sent", "_lost", "_loss_percent"))
        if sent is not None and lost is not None:
            fail(lost > sent, "network lost exceeds sent")
        if sent is None or lost is None or sent == 0:
            fail(loss is not None, "unknown/unmeasured loss reported as known percentage")
        elif loss is not None:
            fail(abs(loss - 100 * lost / sent) > 0.001, "network loss percentage arithmetic mismatch")


def windows(source):
    for provider in source["providers"]:
        for pool in provider["pools"]:
            for window in pool["windows"]:
                yield provider, pool, window


def validate_quotas(source):
    providers = source["providers"]
    fail(len({p["provider_id"] for p in providers}) != len(providers), "duplicate provider identity")
    observations = []
    for provider in providers:
        pools = provider["pools"]
        fail(len({p["pool_id"] for p in pools}) != len(pools), "duplicate pool identity")
        provider_times = []
        for pool in pools:
            items = pool["windows"]
            fail(len({w["window_id"] for w in items}) != len(items), "duplicate window identity")
            for window in items:
                observed, attempted = window["measured_at"], window["last_attempt_at"]
                fail(observed is not None and attempted is None, "window observation without attempt")
                provider_times.append(observed)
                if observed is not None and attempted is not None:
                    fail(instant(observed) > instant(attempted), "window observation after attempt")
                if attempted is not None:
                    fail(provider["last_attempt_at"] is None or instant(attempted) > instant(provider["last_attempt_at"]), "window attempt after provider attempt")
                if window["status"] == "ok":
                    fail(abs(window["used_percent"] + window["remaining_percent"] - 100) > 0.001,
                         "quota percentage complement mismatch")
        fail(not equivalent_time(provider["measured_at"], latest(provider_times)), "provider observation mismatch")
        known_windows = [w for pool in pools for w in pool["windows"] if w["status"] == "ok"]
        fail(provider["status"] == "unavailable" and bool(known_windows), "unavailable provider has usable windows")
        fail(provider["status"] == "ok" and (not known_windows or any(w["status"] != "ok" for pool in pools for w in pool["windows"])), "ok provider has missing windows")
        if provider["measured_at"] is not None and provider["last_attempt_at"] is not None:
            fail(instant(provider["measured_at"]) > instant(provider["last_attempt_at"]), "provider observation after attempt")
        if provider["last_attempt_at"] is not None:
            fail(source["last_attempt_at"] is None or instant(provider["last_attempt_at"]) > instant(source["last_attempt_at"]), "provider attempt after source attempt")
        fail(provider["status"] == "ok" and provider["reason"] != "none", "ok provider has error reason")
        fail(provider["status"] != "ok" and provider["reason"] == "none", "non-ok provider lacks reason")
        observations.extend(provider_times)
    source_relationships(source, observations)
    known = [w for _, _, w in windows(source) if w["status"] == "ok"]
    fail(source["status"] == "unavailable" and bool(known), "unavailable quotas have usable windows")
    fail(source["status"] == "ok" and (not known or any(w["status"] != "ok" for _, _, w in windows(source))), "ok quotas have missing windows")
    fail(source["status"] == "ok" and any(p["status"] != "ok" for p in providers), "ok quotas contain non-ok provider")


def assessment(metric, now, threshold):
    age = age_ms(metric["measured_at"], now)
    if metric["status"] != "ok" or age is None:
        return {"freshness": "unknown", "age_ms": age}
    return {"freshness": "fresh" if age <= threshold else "stale", "age_ms": age}


def hardware_assessment(source, key, now, threshold):
    metric = source["metrics"][key]
    if key in ("gateway_rtt_ms", "internet_rtt_ms"):
        interval = metric["probe_interval_ms"]
        if interval is not None:
            # Match NetJournal.Health: exact expiry boundary is already stale.
            result = assessment(metric, now, max(5000, 3 * interval))
            if result["freshness"] == "fresh" and result["age_ms"] >= max(5000, 3 * interval):
                result["freshness"] = "stale"
            return result
    return assessment(metric, now, threshold)


def node_state(source, assessments, now, heartbeat_threshold, read_status):
    if read_status == "ok":
        fail(source is None, "successful read without validated source")
    error_reason = {"invalid_input": "invalid_input", "clock_invalid": "clock_invalid", "transient_io_error": "transient_io_error", "access_denied": "access_denied"}
    if source is None:
        return {"status": "error" if read_status in error_reason else "unavailable", "reason": error_reason.get(read_status, "source_unavailable"), "liveness": "unknown", "freshness": "unknown", "read_status": read_status}
    values = list(assessments)
    fresh = "stale" if any(a["freshness"] == "stale" for a in values) else "fresh" if any(a["freshness"] == "fresh" for a in values) else "unknown"
    alive = age_ms(source["published_at"], now) <= heartbeat_threshold
    status, reason = source["status"], source["reason"]
    if status == "ok" and (not alive or fresh != "fresh" or any(a["freshness"] == "unknown" for a in values)):
        status = "partial"
        reason = "publisher_silent" if not alive else "source_stale" if fresh == "stale" else "source_partial"
    if read_status != "ok":
        status, reason = "error", error_reason.get(read_status, "source_unavailable")
    return {"status": status, "reason": reason, "liveness": "alive" if alive else "silent", "freshness": fresh, "read_status": read_status}


def resource_pressure(source, assessments, ids, now, high, elevated, cpu=False):
    inputs = [] if source is None else [{"metric_id": key, "value": source["metrics"][key]["value"],
                                        "unit": source["metrics"][key]["unit"],
                                        "measured_at": source["metrics"][key]["measured_at"]} for key in ids]
    def result(level, reason):
        return {"level": level, "reason": reason, "inputs": inputs}
    if source is None or source["status"] == "unavailable":
        return result("unknown", "source_unavailable")
    if any(assessments[key]["freshness"] == "unknown" for key in ids):
        return result("unknown", "metric_unknown")
    if any(assessments[key]["freshness"] == "stale" for key in ids):
        return result("unknown", "metric_stale")
    values = [source["metrics"][key]["value"] for key in ids]
    if not cpu and values[1] == 0:
        return result("unknown", "denominator_zero")
    fraction = values[0] if cpu else 100 * values[0] / values[1]
    level = "high" if (fraction >= high if cpu else fraction <= high) else "elevated" if (fraction >= elevated if cpu else fraction <= elevated) else "normal"
    return result(level, "below_pressure_threshold" if level == "normal" else "utilization_high" if cpu else "reserve_fraction_low")


def derive_output(hardware, quotas, generated_at, mode="fixture", hardware_read_status=None, quotas_read_status=None):
    """Pure oracle for synthetic acceptance; never loads live source files."""
    now = instant(generated_at)
    for kind, source in (("hardware-snapshot", hardware), ("quotas-snapshot", quotas)):
        if source is not None:
            validate_document(kind, source, now)
            fail(source["mode"] != mode, "fixture/live mixing")
    threshold = max(5000, 3 * hardware["sample_interval_ms"]) if hardware else 5000
    hw_metrics = {key: hardware_assessment(hardware, key, now, threshold) if hardware else {"freshness": "unknown", "age_ms": None} for key in METRICS}
    quality_metric = None if hardware is None else {**hardware["network_quality"], "status": "ok" if hardware["network_quality"]["status"] in ("ok", "partial") else hardware["network_quality"]["status"]}
    quality_assessment = assessment(quality_metric, now, threshold) if quality_metric else {"freshness": "unknown", "age_ms": None}
    hardware_read_status = hardware_read_status or ("ok" if hardware is not None else "missing")
    quotas_read_status = quotas_read_status or ("ok" if quotas is not None else "missing")
    hw_state = {**node_state(hardware, [*hw_metrics.values(), quality_assessment], now, threshold, hardware_read_status), "source": hardware, "metrics": hw_metrics, "network_quality": quality_assessment}
    quota_assessments = []
    if quotas is not None:
        for provider, pool, window in windows(quotas):
            age = assessment(window, now, max(900000, 3 * quotas["poll_interval_ms"]))
            reset = window["resets_at"]
            observed = window["measured_at"]
            pending = None if reset is None or observed is None else instant(reset) <= now and instant(observed) < instant(reset)
            quota_assessments.append({"provider_id": provider["provider_id"], "pool_id": pool["pool_id"], "window_id": window["window_id"], **age,
                                      "reset_time_passed_unverified": pending,
                                      "remaining_percent": window["remaining_percent"] if age["freshness"] == "fresh" and pending is False else None,
                                      "resets_at": reset})
    quota_state = {**node_state(quotas, quota_assessments, now, 30000, quotas_read_status), "source": quotas, "windows": quota_assessments}
    quality = []
    for name, state in (("hardware", hw_state), ("quotas", quota_state)):
        if state["source"] is None or state["status"] == "unavailable":
            quality.append(name + "_unavailable")
        elif state["freshness"] == "stale":
            quality.append(name + "_stale")
    if any(n["liveness"] == "silent" for n in (hw_state, quota_state)):
        quality.append("publisher_silent")
    if any(a["freshness"] == "unknown" for a in [*hw_metrics.values(), quality_assessment, *quota_assessments]):
        quality.append("unknown_metrics")
    if any(a["reset_time_passed_unverified"] is True for a in quota_assessments):
        quality.append("quota_reset_unverified")
    pressure = {}
    for key, ids, high, elevated in (("physical_memory", ["ram_available_bytes", "ram_total_bytes"], 10, 20),
                                    ("commit_memory", ["commit_headroom_bytes", "commit_limit_bytes"], 5, 10),
                                    ("vram", ["vram_available_bytes", "vram_total_bytes"], 5, 10),
                                    ("cpu", ["cpu_usage_percent"], 95, 85)):
        pressure[key] = resource_pressure(hardware, hw_metrics, ids, now, high, elevated, key == "cpu")
    return {"schemaVersion": "1.0.0", "mode": mode, "generated_at": generated_at, "policy_version": POLICY_VERSION,
            "hardware_state": hw_state, "subscription_quotas": quota_state,
            "api_limits": {"status": "not_available", "reason": "monitor_not_implemented"},
            "orchestration_hints": {"policy_version": POLICY_VERSION, "resource_pressure": pressure, "data_quality": quality}}


def validate_document(kind, document, as_of=None):
    schema = SCHEMAS[kind + ".schema.json"]
    validator = Draft202012Validator(schema, registry=REGISTRY, format_checker=FormatChecker())
    # Only safe, fixed messages; avoid dumping rejected fields or secret values.
    if any(validator.iter_errors(document)):
        raise ValueError("JSON Schema validation failed")
    now = as_of or (instant(document["generated_at"]) if kind == "bridge-output" else datetime.now(timezone.utc))
    clocks(document, now)
    if kind == "hardware-snapshot":
        validate_hardware(document)
    elif kind == "quotas-snapshot":
        validate_quotas(document)
    else:
        expected = derive_output(document["hardware_state"]["source"], document["subscription_quotas"]["source"], document["generated_at"], document["mode"], document["hardware_state"]["read_status"], document["subscription_quotas"]["read_status"])
        if document["api_limits"].get("status") != "not_available":
            # Extension form (api-limits/1.x): validated here by schema and clocks only; the oracle does not
            # derive it. The Bridge (telemetry/bridge/core.mjs) additionally checks the node's internal invariants.
            expected["api_limits"] = document["api_limits"]
        fail(document != expected, "derived fields differ from policy")
    return document


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Offline stage-1 telemetry contract validation")
    parser.add_argument("kind", choices=("hardware-snapshot", "quotas-snapshot", "bridge-output"))
    parser.add_argument("path", type=Path)
    parser.add_argument("--as-of", help="explicit UTC clock for deterministic fixture checks")
    args = parser.parse_args()
    try:
        validate_document(args.kind, load_document(args.path), instant(args.as_of) if args.as_of else None)
    except (ValueError, OSError):
        raise SystemExit("INVALID: contract/schema/clock check failed")
    print("VALID: schema + semantic contract")
