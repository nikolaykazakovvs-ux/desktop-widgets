import json
import os
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import bridge_telemetry_adapter as A  # noqa: E402

ROOT = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
FIX = os.path.join(ROOT, "telemetry", "fixtures")


def fx(name):
    return os.path.join(FIX, name, "bridge-output.json")


def load(name):
    return A.load_bridge_output(fx(name))


def raw(name):
    with open(fx(name), encoding="utf-8") as f:
        return json.load(f)


class T(unittest.TestCase):
    def test_normal(self):
        o = load("normal")
        self.assertEqual(o.result_status, "ok")
        self.assertTrue(o.is_fixture)
        q = o.quota("codex", "subscription.general", "five_hour")
        self.assertEqual((q.availability, q.remaining_percent), ("available", 70))
        self.assertEqual(o.host.values["cpu_usage_percent"], 25)
        self.assertEqual(o.host.gpu_memory_kind, "dedicated")
        self.assertEqual(o.host.pressure["physical_memory"], "normal")
        self.assertEqual(o.host_node["liveness"], "alive")
        self.assertEqual(o.to_dict()["host"]["values"]["ram_total_bytes"], 34359738368)

    def test_reset_pending(self):
        o = load("quota-reset-pending")
        a = o.quota("codex", "subscription.general", "five_hour")
        b = o.quota("codex", "subscription.general", "seven_day")
        self.assertEqual((a.availability, a.remaining_percent), ("reset_pending", None))
        self.assertEqual((b.availability, b.remaining_percent), ("available", 50))

    def test_stale(self):
        o = load("stale-with-heartbeat")
        for q in o.quotas:
            self.assertEqual((q.availability, q.remaining_percent), ("stale", None))
        self.assertEqual(o.quota_node["liveness"], "alive")

    def test_publisher_silent(self):
        o = load("publisher-silent")
        self.assertEqual(o.host_node["liveness"], "silent")
        self.assertIsNone(o.host.values["cpu_usage_percent"])
        self.assertEqual(o.host.pressure["physical_memory"], "unknown")
        self.assertIn("publisher_silent", o.host.data_quality)

    def test_unknown_metrics(self):
        o = load("unknown-metrics")
        self.assertTrue(all(v is None for v in o.host.values.values()))
        self.assertTrue(all(v == "unknown" for v in o.host.pressure.values()))
        self.assertTrue(o.host.unknown_reasons)

    def test_cold_read(self):
        o = load("cold-read-unavailable")
        self.assertEqual(o.result_status, "ok")
        self.assertEqual(o.quotas, ())
        self.assertEqual(o.host_node["read_status"], "missing")
        self.assertTrue(all(v is None for v in o.host.values.values()))

    def test_partial(self):
        o = load("partial")
        self.assertIsNone(o.host.values["vram_total_bytes"])
        self.assertEqual(o.host.pressure["vram"], "unknown")
        self.assertEqual(o.host.values["cpu_usage_percent"], 25)

    def test_pressure_fixtures(self):
        lp = load("low-physical-memory")
        self.assertEqual(lp.host.pressure["physical_memory"], "high")
        self.assertEqual(lp.host.pressure["commit_memory"], "normal")
        self.assertEqual(load("high-commit-pressure").host.pressure["commit_memory"], "high")

    def test_duplicate_key_rejected(self):
        o = A.parse_bridge_json('{"schemaVersion":"1.0.0","schemaVersion":"1.0.0"}')
        self.assertEqual(o.result_status, "invalid_input")

    def test_nan_rejected(self):
        self.assertEqual(A.parse_bridge_json('{"a": NaN}').result_status, "invalid_input")

    def test_unsupported(self):
        d = raw("normal")
        d["schemaVersion"] = "1.1.0"
        o = A.from_dict(d)
        self.assertEqual(o.result_status, "unsupported_contract")
        self.assertIsNone(o.host)
        d["schemaVersion"] = "1.0.0"
        d["policy_version"] = "9"
        self.assertEqual(A.from_dict(d).result_status, "unsupported_contract")

    def test_api_limits_node_is_ignored_not_interpreted(self):
        # This adapter predates the api_limits extension: an output with the extension node is still read
        # (hardware and quotas intact), but API balances are neither exposed nor evaluated by it.
        d = raw("normal")
        d["api_limits"] = {"schema_extension": "api-limits/1.1.0", "status": "ok", "reason": "none", "read_status": "ok",
                           "liveness": "alive", "freshness": "fresh", "pools": []}
        o = A.from_dict(d)
        self.assertEqual(o.result_status, "ok")
        self.assertEqual(o.host.values["cpu_usage_percent"], 25)
        self.assertEqual(o.quota("codex", "subscription.general", "five_hour").remaining_percent, 70)
        self.assertNotIn("api_limits", json.dumps(o.to_dict()))
        self.assertNotIn("api_limits", open(os.path.join(HERE, "bridge_telemetry_adapter.py"), encoding="utf-8").read())

    def test_alias(self):
        d = raw("normal")
        d["subscription_quotas"]["windows"].append(
            dict(d["subscription_quotas"]["windows"][0], provider_id="claude"))
        o = A.from_dict(d)
        self.assertEqual(
            o.quota("claude_code", "subscription.general", "five_hour").provider_id, "claude")
        self.assertIsNone(o.quota("claude_code", "x", "five_hour"))
        self.assertEqual(len(o.quotas_for("claude_code")), 1)

    def test_cli_fake(self):
        cmd = [sys.executable, "-c", "print(open(r'%s', encoding='utf-8').read())" % fx("normal")]
        self.assertEqual(A.run_bridge_cli(cmd, timeout_s=10).result_status, "ok")

    def test_cli_nonzero_no_leak(self):
        cmd = [sys.executable, "-c", "import sys;sys.stderr.write('Traceback SECRET');sys.exit(1)"]
        o = A.run_bridge_cli(cmd, timeout_s=10)
        self.assertEqual(o.result_status, "error")
        self.assertNotIn("Traceback", repr(o.to_dict()))
        self.assertNotIn("SECRET", repr(o.to_dict()))
        o2 = A.run_bridge_cli([sys.executable, "-c", "import sys;sys.exit(2)"], timeout_s=10)
        self.assertIn("configuration", o2.error)

    def test_cli_missing_and_timeout(self):
        self.assertEqual(A.run_bridge_cli(["definitely-no-such-binary-xyz"]).result_status, "error")
        o = A.run_bridge_cli([sys.executable, "-c", "import time;time.sleep(5)"], timeout_s=0.5)
        self.assertEqual(o.result_status, "error")


if __name__ == "__main__":
    unittest.main()
