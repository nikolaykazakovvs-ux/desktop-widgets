"""Run with python -m unittest discover -s telemetry/tests -v; no live reads."""
from copy import deepcopy
from datetime import timedelta
import json
from pathlib import Path
import tempfile
import unittest

from contract import (METRICS, SCHEMAS, REGISTRY, assessment, derive_output, instant,
                      load_document, validate_document)
from generate_fixtures import AS_OF, FIXTURES, GIB, normal, scenarios
from jsonschema import Draft202012Validator, FormatChecker


class ContractTests(unittest.TestCase):
    def setUp(self):
        self.hardware, self.quotas = normal()
        self.now = instant(AS_OF)

    def valid(self, kind, document):
        validate_document(kind, document, self.now)

    def invalid(self, kind, document):
        with self.assertRaises(ValueError):
            self.valid(kind, document)

    def output(self):
        return derive_output(self.hardware, self.quotas, AS_OF)

    def test_all_schemas_have_supported_dialect_and_validate(self):
        self.assertEqual(len(SCHEMAS), 4)
        for schema in SCHEMAS.values():
            Draft202012Validator.check_schema(schema)
            self.assertEqual(schema["$schema"], "https://json-schema.org/draft/2020-12/schema")

    def test_all_committed_fixture_files_pass_both_layers_and_match_factory(self):
        manifest = load_document(FIXTURES / "manifest.json")
        self.assertTrue(manifest["synthetic"])
        self.assertEqual(manifest["as_of"], AS_OF)
        self.assertEqual(len(manifest["scenarios"]), 10)
        expected = scenarios()
        for scenario in manifest["scenarios"]:
            directory = FIXTURES / scenario["id"]
            for filename in scenario["files"]:
                with self.subTest(scenario=scenario["id"], filename=filename):
                    document = load_document(directory / filename)
                    self.assertEqual(document["mode"], "fixture")
                    self.valid(filename[:-5], document)
            hardware, quotas = expected[scenario["id"]]
            self.assertEqual(load_document(directory / "bridge-output.json"), derive_output(hardware, quotas, AS_OF))

    def test_normal_has_four_nodes_and_reserved_api_is_unavailable(self):
        output = self.output()
        self.valid("bridge-output", output)
        self.assertEqual(output["api_limits"], {"status": "not_available", "reason": "monitor_not_implemented"})
        self.assertEqual(output["subscription_quotas"]["windows"][0]["remaining_percent"], 70)
        self.assertTrue(all(p["level"] == "normal" for p in output["orchestration_hints"]["resource_pressure"].values()))

    def test_low_physical_memory_is_independent_from_commit(self):
        hardware, quotas = scenarios()["low-physical-memory"]
        pressure = derive_output(hardware, quotas, AS_OF)["orchestration_hints"]["resource_pressure"]
        self.assertEqual(pressure["physical_memory"]["level"], "high")
        self.assertEqual(pressure["commit_memory"]["level"], "normal")

    def test_high_commit_pressure_is_independent_from_ram(self):
        hardware, quotas = scenarios()["high-commit-pressure"]
        pressure = derive_output(hardware, quotas, AS_OF)["orchestration_hints"]["resource_pressure"]
        self.assertEqual(pressure["commit_memory"]["level"], "high")
        self.assertEqual(pressure["physical_memory"]["level"], "normal")

    def test_pressure_thresholds_are_inclusive(self):
        # 100-byte totals make reserve fractions exact and easy to audit.
        for available, expected in ((21, "normal"), (20, "elevated"), (11, "elevated"), (10, "high"), (0, "high")):
            self.hardware["metrics"]["ram_total_bytes"]["value"] = 100
            self.hardware["metrics"]["ram_available_bytes"]["value"] = available
            self.assertEqual(self.output()["orchestration_hints"]["resource_pressure"]["physical_memory"]["level"], expected)
        for used, expected in ((84.99, "normal"), (85, "elevated"), (94.99, "elevated"), (95, "high")):
            self.hardware["metrics"]["cpu_usage_percent"]["value"] = used
            self.assertEqual(self.output()["orchestration_hints"]["resource_pressure"]["cpu"]["level"], expected)

    def test_unknown_never_becomes_normal_pressure(self):
        hardware, quotas = scenarios()["unknown-metrics"]
        output = derive_output(hardware, quotas, AS_OF)
        self.assertTrue(all(p["level"] == "unknown" for p in output["orchestration_hints"]["resource_pressure"].values()))
        self.assertTrue(all(a["age_ms"] is None for a in output["hardware_state"]["metrics"].values()))

    def test_partial_optional_sensor_preserves_other_facts(self):
        hardware, quotas = scenarios()["partial"]
        output = derive_output(hardware, quotas, AS_OF)
        self.assertEqual(output["hardware_state"]["status"], "partial")
        self.assertEqual(output["orchestration_hints"]["resource_pressure"]["vram"]["level"], "unknown")
        self.assertEqual(output["orchestration_hints"]["resource_pressure"]["physical_memory"]["level"], "normal")
        self.assertIsNone(hardware["metrics"]["network_reachability"]["value"])

    def test_heartbeat_and_failed_attempt_do_not_rejuvenate_quotas(self):
        hardware, quotas = scenarios()["stale-with-heartbeat"]
        output = derive_output(hardware, quotas, AS_OF)
        state = output["subscription_quotas"]
        self.assertEqual(state["liveness"], "alive")
        self.assertEqual(state["freshness"], "stale")
        self.assertEqual(state["windows"][0]["age_ms"], 3600000)
        self.assertIsNone(state["windows"][0]["remaining_percent"])
        self.assertEqual(quotas["measured_at"], "2026-01-01T11:00:00Z")

    def test_reset_pending_is_per_window_and_withholds_effective_availability(self):
        hardware, quotas = scenarios()["quota-reset-pending"]
        output = derive_output(hardware, quotas, AS_OF)
        windows = output["subscription_quotas"]["windows"]
        self.assertTrue(windows[0]["reset_time_passed_unverified"])
        self.assertIsNone(windows[0]["remaining_percent"])
        self.assertFalse(windows[1]["reset_time_passed_unverified"])
        self.assertEqual(windows[1]["remaining_percent"], 50)

    def test_post_reset_observation_clears_pending_without_touching_other_windows(self):
        hardware, quotas = scenarios()["quota-reset-pending"]
        window = quotas["providers"][0]["pools"][0]["windows"][0]
        window["measured_at"] = window["last_attempt_at"] = "2026-01-01T11:59:30Z"
        quotas["providers"][0]["measured_at"] = quotas["providers"][0]["last_attempt_at"] = window["measured_at"]
        quotas["measured_at"] = quotas["last_attempt_at"] = window["measured_at"]
        output = derive_output(hardware, quotas, AS_OF)
        self.assertFalse(output["subscription_quotas"]["windows"][0]["reset_time_passed_unverified"])
        self.assertEqual(output["subscription_quotas"]["windows"][0]["remaining_percent"], 70)

    def test_unknown_reset_never_claims_reset_confirmed(self):
        self.quotas["providers"][0]["pools"][0]["windows"][0]["resets_at"] = None
        state = self.output()["subscription_quotas"]["windows"][0]
        self.assertIsNone(state["reset_time_passed_unverified"])
        self.assertIsNone(state["remaining_percent"])

    def test_expired_heartbeat_does_not_claim_closed_by_user(self):
        hardware, quotas = scenarios()["publisher-silent"]
        output = derive_output(hardware, quotas, AS_OF)
        self.assertEqual(output["hardware_state"]["liveness"], "silent")
        self.assertEqual(output["subscription_quotas"]["liveness"], "silent")
        self.assertNotIn("closed_by_user", json.dumps(output))

    def test_cold_read_returns_unavailable_and_unknown(self):
        output = derive_output(None, None, AS_OF)
        self.valid("bridge-output", output)
        for key in ("hardware_state", "subscription_quotas"):
            self.assertEqual(output[key]["status"], "unavailable")
            self.assertEqual(output[key]["liveness"], "unknown")

    def test_read_failures_distinguish_cache_and_cold_failure_without_rejuvenation(self):
        for read_status in ("missing", "invalid_input", "clock_invalid", "transient_io_error", "access_denied"):
            with self.subTest(read_status=read_status):
                cached = derive_output(self.hardware, self.quotas, AS_OF, hardware_read_status=read_status)
                self.valid("bridge-output", cached)
                self.assertEqual(cached["hardware_state"]["read_status"], read_status)
                self.assertEqual(cached["hardware_state"]["status"], "error")
                self.assertEqual(cached["hardware_state"]["metrics"]["cpu_usage_percent"]["age_ms"], 2000)
                self.assertEqual(cached["hardware_state"]["source"]["published_at"], self.hardware["published_at"])
                cold = derive_output(None, None, AS_OF, hardware_read_status=read_status)
                self.valid("bridge-output", cold)
                self.assertIsNone(cold["hardware_state"]["source"])
                self.assertEqual(cold["hardware_state"]["freshness"], "unknown")

    def test_freshness_boundary_and_custom_sampling_interval(self):
        metric = self.hardware["metrics"]["cpu_usage_percent"]
        self.assertEqual(assessment(metric, self.now + timedelta(seconds=3), 5000)["freshness"], "fresh")
        self.assertEqual(assessment(metric, self.now + timedelta(seconds=3, milliseconds=1), 5000)["freshness"], "stale")
        self.hardware["sample_interval_ms"] = 10000
        output = derive_output(self.hardware, self.quotas, "2026-01-01T12:00:20Z")
        self.assertEqual(output["hardware_state"]["metrics"]["cpu_usage_percent"]["freshness"], "fresh")

    def test_unsupported_schema_or_policy_versions_fail_closed(self):
        for kind, document in (("hardware-snapshot", self.hardware), ("quotas-snapshot", self.quotas), ("bridge-output", self.output())):
            for version in ("2.0.0", "1.1.0", "1.0", 1):
                changed = deepcopy(document)
                changed["schemaVersion"] = version
                self.invalid(kind, changed)
        output = self.output()
        output["policy_version"] = "0.2.0"
        self.invalid("bridge-output", output)

    def test_secret_and_raw_fields_are_rejected_at_each_boundary(self):
        paths = [[], ["metrics"], ["metrics", "cpu_usage_percent"], ["metrics", "network_reachability"]]
        for path in paths:
            changed = deepcopy(self.hardware)
            target = changed
            for key in path:
                target = target[key]
            target["api_token"] = "synthetic-forbidden-marker"
            self.invalid("hardware-snapshot", changed)
        paths = [[], ["providers", 0], ["providers", 0, "pools", 0], ["providers", 0, "pools", 0, "windows", 0]]
        for path in paths:
            changed = deepcopy(self.quotas)
            target = changed
            for key in path:
                target = target[key]
            target["account_email"] = "synthetic-forbidden-marker"
            self.invalid("quotas-snapshot", changed)
        output = self.output()
        output["orchestration_hints"]["recommended_workers"] = 2
        self.invalid("bridge-output", output)

    def test_percent_units_and_sizes_are_strict(self):
        for value in (-1, 100.01, "39", True):
            changed = deepcopy(self.hardware)
            changed["metrics"]["cpu_usage_percent"]["value"] = value
            self.invalid("hardware-snapshot", changed)
        for value in (-1, 1.5, 9007199254740992):
            changed = deepcopy(self.hardware)
            changed["metrics"]["ram_available_bytes"]["value"] = value
            self.invalid("hardware-snapshot", changed)
        self.hardware["metrics"]["ram_available_bytes"]["unit"] = "GiB"
        self.invalid("hardware-snapshot", self.hardware)

    def test_timestamp_forms_and_impossible_dates_are_rejected(self):
        for value in ("2026-01-01T11:59:59+00:00", "2026-01-01 11:59:59Z", "2026-01-01T11:59:59", "2026-02-30T12:00:00Z", "2026-01-01T11:59:60Z"):
            changed = deepcopy(self.hardware)
            changed["published_at"] = value
            self.invalid("hardware-snapshot", changed)

    def test_future_time_is_semantic_failure_not_schema_failure(self):
        self.hardware["published_at"] = "2026-01-01T12:00:01Z"
        validator = Draft202012Validator(SCHEMAS["hardware-snapshot.schema.json"], registry=REGISTRY, format_checker=FormatChecker())
        self.assertTrue(validator.is_valid(self.hardware))
        self.invalid("hardware-snapshot", self.hardware)

    def test_window_invalid_duration_percentage_and_identity(self):
        path = self.quotas["providers"][0]["pools"][0]["windows"][0]
        for key, value in (("duration_seconds", 0), ("used_percent", 101), ("remaining_percent", -1), ("window_id", "Five Hour Display")):
            changed = deepcopy(self.quotas)
            changed["providers"][0]["pools"][0]["windows"][0][key] = value
            self.invalid("quotas-snapshot", changed)
        path["remaining_percent"] = 69
        self.invalid("quotas-snapshot", self.quotas)

    def test_duplicate_provider_pool_and_window_identities_rejected(self):
        for key in ("provider", "pool", "window"):
            changed = deepcopy(self.quotas)
            items = changed["providers"] if key == "provider" else changed["providers"][0]["pools"] if key == "pool" else changed["providers"][0]["pools"][0]["windows"]
            items.append(deepcopy(items[0]))
            self.invalid("quotas-snapshot", changed)

    def test_unknown_values_and_status_cannot_disagree(self):
        self.hardware["metrics"]["cpu_usage_percent"].update(status="unknown", reason="sensor_unavailable")
        self.invalid("hardware-snapshot", self.hardware)
        self.hardware["metrics"]["cpu_usage_percent"].update(value=None, status="ok")
        self.invalid("hardware-snapshot", self.hardware)

    def test_commit_arithmetic_and_observation_alignment(self):
        self.hardware["metrics"]["commit_headroom_bytes"]["value"] -= 1
        self.invalid("hardware-snapshot", self.hardware)
        self.hardware["metrics"]["commit_headroom_bytes"]["value"] += 1
        self.hardware["metrics"]["commit_headroom_bytes"]["measured_at"] = "2026-01-01T11:59:57Z"
        self.invalid("hardware-snapshot", self.hardware)

    def test_partial_commit_requires_original_operands_for_computed_headroom(self):
        self.hardware["metrics"]["commit_total_bytes"].update(value=None, status="unknown", reason="sensor_unavailable", measured_at=None)
        self.hardware.update(status="partial", reason="source_partial")
        self.hardware["metrics"]["commit_headroom_bytes"]["value"] = self.hardware["metrics"]["commit_limit_bytes"]["value"] + 1
        self.invalid("hardware-snapshot", self.hardware)
        # Even an arithmetically plausible headroom cannot be independently
        # asserted when one of its two original observations is unavailable.
        self.hardware["metrics"]["commit_headroom_bytes"]["value"] = GIB
        self.invalid("hardware-snapshot", self.hardware)
        self.hardware["metrics"]["commit_headroom_bytes"].update(value=None, status="unknown", reason="sensor_unavailable", measured_at=None)
        self.valid("hardware-snapshot", self.hardware)
        self.assertEqual(self.output()["orchestration_hints"]["resource_pressure"]["commit_memory"]["level"], "unknown")

    def test_temperature_units_negative_readings_and_strict_bounds(self):
        for key in ("cpu_temperature_celsius", "gpu_temperature_celsius"):
            for value in (-100, -20.5, 0, 200):
                self.hardware["metrics"][key]["value"] = value
                self.valid("hardware-snapshot", self.hardware)
            for value in (-100.01, 200.01):
                changed = deepcopy(self.hardware)
                changed["metrics"][key]["value"] = value
                self.invalid("hardware-snapshot", changed)
            self.hardware["metrics"][key]["value"] = 45
        self.hardware["metrics"]["gpu_usage_percent"]["value"] = 101
        self.invalid("hardware-snapshot", self.hardware)

    def test_shared_gpu_pool_is_not_dedicated_vram(self):
        self.hardware["gpu_memory_kind"] = "shared"
        self.invalid("hardware-snapshot", self.hardware)
        for key in ("vram_total_bytes", "vram_available_bytes"):
            self.hardware["metrics"][key].update(value=None, status="unknown", reason="not_supported", measured_at=None)
        self.hardware.update(status="partial", reason="source_partial")
        self.valid("hardware-snapshot", self.hardware)
        output = self.output()
        self.assertEqual(output["orchestration_hints"]["resource_pressure"]["vram"]["level"], "unknown")
        self.assertEqual(output["hardware_state"]["source"]["metrics"]["gpu_memory_total_bytes"]["value"], 8 * GIB)

    def test_rtt_uses_own_probe_interval_and_original_time(self):
        self.hardware["sample_interval_ms"] = 10000
        self.hardware["metrics"]["internet_rtt_ms"].update(measured_at="2026-01-01T11:59:55Z", probe_interval_ms=1000)
        self.hardware["metrics"]["gateway_rtt_ms"].update(measured_at="2026-01-01T11:59:54Z", probe_interval_ms=10000)
        output = self.output()
        self.assertEqual(output["hardware_state"]["metrics"]["cpu_usage_percent"]["freshness"], "fresh")
        self.assertEqual(output["hardware_state"]["metrics"]["internet_rtt_ms"], {"freshness": "stale", "age_ms": 5000})
        self.assertEqual(output["hardware_state"]["metrics"]["gateway_rtt_ms"], {"freshness": "fresh", "age_ms": 6000})
        self.hardware["metrics"]["internet_rtt_ms"]["probe_interval_ms"] = None
        self.invalid("hardware-snapshot", self.hardware)

    def test_network_quality_requires_bound_window_and_loss_arithmetic(self):
        for key, value in (("internet_lost", 101), ("internet_loss_percent", 0), ("window_start", None), ("window_end", "2026-01-03T00:00:00Z"), ("coverage_seconds", 86400)):
            changed = deepcopy(self.hardware)
            changed["network_quality"][key] = value
            self.invalid("hardware-snapshot", changed)
        # A converted local calendar day need not have exactly 24 UTC hours.
        self.hardware["network_quality"]["window_start"] = "2026-01-01T01:00:00Z"
        self.valid("hardware-snapshot", self.hardware)

    def test_zero_sent_does_not_report_zero_loss(self):
        quality = self.hardware["network_quality"]
        quality.update(internet_sent=0, internet_lost=0, internet_loss_percent=0)
        self.invalid("hardware-snapshot", self.hardware)
        quality.update(internet_loss_percent=None, status="partial", reason="source_partial")
        self.hardware.update(status="partial", reason="source_partial")
        self.valid("hardware-snapshot", self.hardware)
        self.assertIsNone(self.output()["hardware_state"]["source"]["network_quality"]["internet_loss_percent"])

    def test_safe_integer_duration_and_new_counters_roundtrip(self):
        limit = 9007199254740991
        window = self.quotas["providers"][0]["pools"][0]["windows"][0]
        window["duration_seconds"] = limit
        self.valid("quotas-snapshot", json.loads(json.dumps(self.quotas)))
        for value in (limit + 1, limit + 2):
            window["duration_seconds"] = value
            self.invalid("quotas-snapshot", self.quotas)
        quality = self.hardware["network_quality"]
        quality.update(internet_sent=limit, internet_lost=0, internet_loss_percent=0, outages_count=limit, disconnect_count=limit)
        self.valid("hardware-snapshot", json.loads(json.dumps(self.hardware)))
        for key in ("internet_sent", "internet_lost", "gateway_sent", "gateway_lost", "outages_count", "disconnect_count"):
            changed = deepcopy(self.hardware)
            changed["network_quality"][key] = limit + 1
            self.invalid("hardware-snapshot", changed)

    def test_mode_mixing_and_forged_derived_fields_are_rejected(self):
        self.quotas["mode"] = "live"
        with self.assertRaises(ValueError):
            self.output()
        self.quotas["mode"] = "fixture"
        output = self.output()
        output["orchestration_hints"]["resource_pressure"]["cpu"]["level"] = "high"
        self.invalid("bridge-output", output)

    def test_network_probe_has_scope_and_no_global_readiness(self):
        network = self.hardware["metrics"]["network_reachability"]
        self.assertEqual(network["scope"], "gateway")
        self.assertNotIn("network_ready", json.dumps(self.output()))
        network["scope"] = "provider_probe"
        self.invalid("hardware-snapshot", self.hardware)
        network["provider_id"] = "codex"
        self.valid("hardware-snapshot", self.hardware)

    def test_unmeasured_reachability_cannot_claim_gateway_scope(self):
        hardware, _ = scenarios()["partial"]
        network = hardware["metrics"]["network_reachability"]
        self.assertEqual(network["status"], "unknown")
        self.assertEqual(network["scope"], "not_measured")
        self.valid("hardware-snapshot", hardware)
        network["scope"] = "gateway"
        self.invalid("hardware-snapshot", hardware)

    def test_synthetic_corpus_contains_no_extra_top_nodes(self):
        self.assertEqual(set(self.output()), {"schemaVersion", "mode", "generated_at", "policy_version", "hardware_state", "subscription_quotas", "api_limits", "orchestration_hints"})

    def test_json_parser_rejects_duplicate_keys_and_non_finite_numbers(self):
        for content in ('{"mode":"fixture","mode":"live"}', '{"value":NaN}', '{"value":Infinity}'):
            with tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / "input.json"
                path.write_text(content, encoding="utf-8")
                with self.assertRaises(ValueError):
                    load_document(path)


if __name__ == "__main__":
    unittest.main()
