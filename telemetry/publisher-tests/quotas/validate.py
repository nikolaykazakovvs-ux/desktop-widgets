"""Offline validation of only the harness-emitted public snapshots."""
import json
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tests"))
from contract import validate_document, derive_output, instant
folder = Path(sys.argv[1])
files = sorted(folder.glob("case-*.json"))
assert files, "No emitted snapshots"
for path in files:
    doc = json.loads(path.read_text(encoding="utf-8-sig"))
    validate_document("quotas-snapshot", doc, instant(doc["published_at"]))
    text = path.read_text(encoding="utf-8-sig").lower()
    for forbidden in ("secret-canary", "account-canary", "provider-raw-canary", "credits", "currency", "budget", "password", "token"):
        assert forbidden not in text, path.name
    if path.name == "case-reset-pending.json":
        output = derive_output(None, doc, doc["published_at"], "fixture")
        assert output["subscription_quotas"]["windows"][0]["reset_time_passed_unverified"] is True
        assert output["subscription_quotas"]["windows"][0]["remaining_percent"] is None
print(f"PASS: {len(files)} quota snapshots validated against stage-1 schema and semantics")
