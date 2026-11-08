"""Проверка снимков прототипа тем же валидатором контракта, что и у WPF-версии (telemetry/tests/contract.py)."""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "telemetry" / "tests"))
from contract import validate_document, instant  # noqa: E402

files = [Path(p) for p in sys.argv[1:]]
files = [f for p in files for f in (sorted(p.glob("*.json")) if p.is_dir() else [p])]
assert files, "нет снимков для проверки"
for path in files:
    doc = json.loads(path.read_text(encoding="utf-8"))
    validate_document("quotas-snapshot", doc, instant(doc["published_at"]))
    text = path.read_text(encoding="utf-8").lower()
    for forbidden in ("canary", "email", "max", "fable", "token", "password"):
        assert forbidden not in text, f"{path.name}: {forbidden}"
    print(f"VALID {path.name}: mode={doc['mode']} status={doc['status']} reason={doc['reason']}")
print(f"PASS: {len(files)} snapshot(s) match quotas-snapshot 1.0.0")
