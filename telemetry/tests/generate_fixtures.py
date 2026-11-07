"""Regenerate ONLY the strictly synthetic, fixed-clock contract corpus."""
from copy import deepcopy
import json
from pathlib import Path

from contract import METRICS, SCHEMAS, derive_output, latest, validate_document

AS_OF = "2026-01-01T12:00:00Z"
FIXTURES = Path(__file__).resolve().parents[1] / "fixtures"
GIB = 1024 ** 3


def normal():
    values = {"cpu_usage_percent": 25, "ram_total_bytes": 32 * GIB, "ram_available_bytes": 16 * GIB,
              "commit_total_bytes": 24 * GIB, "commit_limit_bytes": 64 * GIB,
              "commit_headroom_bytes": 40 * GIB, "vram_total_bytes": 8 * GIB,
              "vram_available_bytes": 6 * GIB, "swap_used_bytes": 2 * GIB,
              "network_receive_bytes_per_second": 65536, "network_send_bytes_per_second": 8192,
              "network_reachability": "reachable", "cpu_temperature_celsius": 48.5,
              "gpu_temperature_celsius": 52.0, "gpu_usage_percent": 20,
              "gpu_memory_total_bytes": 8 * GIB, "gpu_memory_available_bytes": 6 * GIB,
              "gateway_rtt_ms": 1.5, "internet_rtt_ms": 24.0}
    metric_schemas = SCHEMAS["hardware-snapshot.schema.json"]["properties"]["metrics"]["properties"]
    metrics = {key: {"value": values[key], "unit": metric_schemas[key]["properties"]["unit"]["const"],
                     "status": "ok", "reason": "none", "measured_at": "2026-01-01T11:59:58Z"} for key in METRICS}
    metrics["network_reachability"].update(scope="gateway", provider_id=None)
    metrics["gateway_rtt_ms"].update(scope="gateway", probe_interval_ms=2000)
    metrics["internet_rtt_ms"].update(scope="internet_probe", probe_interval_ms=2000)
    quality = {"status": "ok", "reason": "none", "measured_at": "2026-01-01T11:59:58Z",
               "window_kind": "local_calendar_day", "window_start": "2026-01-01T00:00:00Z", "window_end": "2026-01-02T00:00:00Z",
               "coverage_scope": "widget_observed_only", "coverage_seconds": 3600, "coverage_unit": "seconds", "count_unit": "count", "loss_unit": "percent",
               "internet_sent": 100, "internet_lost": 1, "internet_loss_percent": 1,
               "gateway_sent": 100, "gateway_lost": 0, "gateway_loss_percent": 0,
               "outages_count": 0, "disconnect_count": 0}
    hardware = {"schemaVersion": "1.0.0", "mode": "fixture", "kind": "hardware-snapshot", "source_id": "resources-widget",
                "measured_at": "2026-01-01T11:59:58Z", "published_at": "2026-01-01T11:59:59Z", "last_attempt_at": "2026-01-01T11:59:58Z",
                "status": "ok", "reason": "none", "sample_interval_ms": 1000, "metrics": metrics,
                "gpu_memory_kind": "dedicated", "network_quality": quality}
    window = {"window_id": "five_hour", "duration_seconds": 18000, "status": "ok", "reason": "none",
              "measured_at": "2026-01-01T11:58:00Z", "last_attempt_at": "2026-01-01T11:58:00Z",
              "used_percent": 30, "remaining_percent": 70, "resets_at": "2026-01-01T13:00:00Z"}
    weekly = {**window, "window_id": "seven_day", "duration_seconds": 604800, "used_percent": 50, "remaining_percent": 50,
              "resets_at": "2026-01-05T12:00:00Z"}
    provider = {"provider_id": "codex", "status": "ok", "reason": "none", "measured_at": window["measured_at"],
                "last_attempt_at": window["last_attempt_at"], "pools": [{"pool_id": "subscription.general", "model_ids": ["codex.general"], "windows": [window, weekly]}]}
    quotas = {"schemaVersion": "1.0.0", "mode": "fixture", "kind": "quotas-snapshot", "source_id": "limits-widget",
              "measured_at": window["measured_at"], "published_at": "2026-01-01T11:59:59Z", "last_attempt_at": window["last_attempt_at"],
              "status": "ok", "reason": "none", "poll_interval_ms": 300000, "heartbeat_interval_ms": 10000, "providers": [provider]}
    return hardware, quotas


def unknown(metric, reason="sensor_unavailable"):
    metric.update(value=None, status="unknown", reason=reason, measured_at=None)
    if metric["unit"] == "reachability":
        metric.update(scope="not_measured", provider_id=None)


def unknown_quality(quality, reason="sensor_unavailable"):
    quality.update(status="unknown", reason=reason, measured_at=None, window_start=None, window_end=None)
    for key in ("coverage_seconds", "internet_sent", "internet_lost", "internet_loss_percent", "gateway_sent", "gateway_lost", "gateway_loss_percent", "outages_count", "disconnect_count"):
        quality[key] = None


def refresh_summary(source):
    if source["kind"] == "hardware-snapshot":
        source["measured_at"] = latest([v["measured_at"] for v in source["metrics"].values()])


def scenarios():
    result = {"normal": normal()}
    hw, quota = normal()
    for key in ("vram_total_bytes", "vram_available_bytes", "network_reachability", "gateway_rtt_ms", "internet_rtt_ms"):
        unknown(hw["metrics"][key])
    hw.update(status="partial", reason="sensor_unavailable")
    result["partial"] = hw, quota
    hw, quota = normal()
    for metric in hw["metrics"].values():
        unknown(metric, "source_unavailable")
    unknown_quality(hw["network_quality"], "source_unavailable")
    hw.update(measured_at=None, status="unavailable", reason="source_unavailable")
    quota.update(measured_at=None, status="unavailable", reason="source_unavailable", providers=[])
    result["offline"] = hw, quota
    hw, quota = normal()
    quota["providers"][0]["pools"][0]["windows"][0]["resets_at"] = "2026-01-01T11:59:00Z"
    result["quota-reset-pending"] = hw, quota
    hw, quota = normal()
    hw["metrics"]["ram_available_bytes"]["value"] = GIB
    result["low-physical-memory"] = hw, quota
    hw, quota = normal()
    hw["metrics"]["commit_total_bytes"]["value"] = 63 * GIB
    hw["metrics"]["commit_headroom_bytes"]["value"] = GIB
    result["high-commit-pressure"] = hw, quota
    hw, quota = normal()
    for provider in quota["providers"]:
        provider["measured_at"] = "2026-01-01T11:00:00Z"
        provider["last_attempt_at"] = "2026-01-01T11:59:58Z"
        provider.update(status="error", reason="collection_failed")
        for pool in provider["pools"]:
            for window in pool["windows"]:
                window["measured_at"] = "2026-01-01T11:00:00Z"
                window["last_attempt_at"] = "2026-01-01T11:59:58Z"
    quota.update(measured_at="2026-01-01T11:00:00Z", last_attempt_at="2026-01-01T11:59:58Z", status="error", reason="collection_failed")
    result["stale-with-heartbeat"] = hw, quota
    hw, quota = normal()
    for metric in hw["metrics"].values():
        unknown(metric)
    unknown_quality(hw["network_quality"])
    hw.update(measured_at=None, status="partial", reason="sensor_unavailable")
    result["unknown-metrics"] = hw, quota
    hw, quota = normal()
    hw["published_at"] = hw["measured_at"] = hw["last_attempt_at"] = "2026-01-01T11:58:00Z"
    for metric in hw["metrics"].values():
        metric["measured_at"] = hw["measured_at"]
    hw["network_quality"]["measured_at"] = hw["measured_at"]
    quota["published_at"] = "2026-01-01T11:58:00Z"
    result["publisher-silent"] = hw, quota
    result["cold-read-unavailable"] = None, None
    return result


def generate():
    catalog = {"synthetic": True, "as_of": AS_OF, "schemaVersion": "1.0.0", "scenarios": []}
    for name, (hardware, quotas) in scenarios().items():
        directory = FIXTURES / name
        directory.mkdir(parents=True, exist_ok=True)
        documents = {"bridge-output": derive_output(hardware, quotas, AS_OF)}
        if hardware is not None:
            documents["hardware-snapshot"] = hardware
        if quotas is not None:
            documents["quotas-snapshot"] = quotas
        for kind, document in documents.items():
            validate_document(kind, document)
            (directory / (kind + ".json")).write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
        catalog["scenarios"].append({"id": name, "files": [kind + ".json" for kind in documents]})
    (FIXTURES / "manifest.json").write_text(json.dumps(catalog, indent=2) + "\n", encoding="utf-8")
    print(f"Generated {len(catalog['scenarios'])} strictly synthetic fixed-clock scenarios.")


if __name__ == "__main__":
    generate()
