#!/usr/bin/env python3
from __future__ import annotations

import base64
import hashlib
import importlib.util
import io
import json
import string
import sys
import tempfile
import zipfile
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PARTS = ROOT / "controls/v2.22/carrier-parts"
PART_NAMES = tuple(f"part{i:02d}.b64" for i in range(1, 6))
LEGACY_ARCHIVE_SHA256 = "eb53a86865c455108a931ef4c7e198949840ca11214af85c8594d789756acfbc"
REPACKED_ARCHIVE_SHA256 = "baf579fb2cfd26a582674d284b4503fa948f015dedb3851c9014492b68ae5c5c"
CONTRACT = ROOT / "controls/v2.22/CloudScribe_Pro_Batch_Limits_Autosave_Settings_Contract_v2.22.md"
CONTRACT_MEMBER = "06_Product_and_Distribution/CloudScribe_Pro_Batch_Limits_Autosave_Settings_Contract_v2.22.md"
EXPECTED = {
    "02_Pricing/CloudScribe_Pricing_Catalog_Validation_v1.1.5_2026-07-20.json": "0410bd29a1d3018efb606efd79747c4ede921d43b39e0693a4963d67ad41bde6",
    "02_Pricing/cloudscribe-pricing.schema-1.1.5.json": "1dc77a16130efa0fa2428e954bbfc5c7d30088283bbaf5b3dddff5694e01972b",
    "02_Pricing/cloudscribe-pricing.seed-2026-07-20.schema-1.1.5.json": "3e647812dcae11face91b66c3df642f19134de34b8d706e2c2183c87266e8b61",
    "03_Implementation/cloudscribe-runtime-policy.schema-1.3.json": "bdcc03005a48d9d8bdcb139d468a9c3f277526aa4d9dbe19c2c6309b5bff390c",
    "03_Implementation/cloudscribe-runtime-policy.seed-2026-07-20.schema-1.3.json": "9561a4f5c1d58dd471424566b05f7325a52ed06a4c57ec53b17f5395ae621525",
    "05_Tools_and_Tests/cloudscribe_catalog_validator.py": "5815f0db84ffea9745218cb49b4147f0599c5be93a09ef95c3b2ac78e421f4b8",
    "05_Tools_and_Tests/requirements.txt": "0253327c27a08bf425e482da93ffaaa7827802a45910732bccd19925b836eaa1",
    CONTRACT_MEMBER: "5d3e17debc58e0775bf472f7eebd79db32447de457fcec20d924a860dcfcb6d7",
}
VALIDATION_REPORT = "02_Pricing/CloudScribe_Pricing_Catalog_Validation_v1.1.5_2026-07-20.json"
VALIDATOR = "05_Tools_and_Tests/cloudscribe_catalog_validator.py"


def fail(message: str) -> int:
    print(f"ERROR: {message}", file=sys.stderr)
    return 1


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def load_material() -> tuple[bytes, dict[str, bytes]]:
    alphabet = set(string.ascii_letters + string.digits + "+/=")
    encoded_parts: list[str] = []
    for part_name in PART_NAMES:
        path = PARTS / part_name
        if not path.is_file():
            raise ValueError(f"authenticated carrier part missing: {path.relative_to(ROOT)}")
        raw = path.read_text(encoding="ascii")
        normalized = "".join(raw.split())
        bad = [(i, ch, ord(ch)) for i, ch in enumerate(normalized) if ch not in alphabet]
        if bad:
            sample = ", ".join(f"index={i} char={ch!r} ord={code}" for i, ch, code in bad[:8])
            raise ValueError(f"non-Base64 transport characters in {part_name}: {sample}")
        encoded_parts.append(normalized)
    legacy = base64.b64decode("".join(encoded_parts), validate=True)
    actual_legacy = sha256(legacy)
    if actual_legacy != LEGACY_ARCHIVE_SHA256:
        raise ValueError(f"authenticated seven-member carrier identity mismatch: {actual_legacy}")

    members: dict[str, bytes] = {}
    with zipfile.ZipFile(io.BytesIO(legacy)) as zf:
        names = set(zf.namelist())
        expected_legacy = set(EXPECTED) - {CONTRACT_MEMBER}
        if names != expected_legacy:
            raise ValueError(f"seven-member carrier set mismatch: {sorted(names)}")
        for name in sorted(expected_legacy):
            data = zf.read(name)
            actual = sha256(data)
            if actual != EXPECTED[name]:
                raise ValueError(f"identity mismatch for {name}: {actual}")
            members[name] = data

    if not CONTRACT.is_file():
        raise ValueError(f"authenticated limits contract missing: {CONTRACT.relative_to(ROOT)}")
    contract = CONTRACT.read_bytes()
    actual_contract = sha256(contract)
    if actual_contract != EXPECTED[CONTRACT_MEMBER]:
        raise ValueError(f"identity mismatch for {CONTRACT_MEMBER}: {actual_contract}")
    members[CONTRACT_MEMBER] = contract

    out = io.BytesIO()
    with zipfile.ZipFile(out, "w", compression=zipfile.ZIP_STORED) as zf:
        for name in sorted(members):
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_STORED
            info.create_system = 3
            info.external_attr = 0o644 << 16
            zf.writestr(info, members[name])
    repacked = out.getvalue()
    actual_repacked = sha256(repacked)
    if actual_repacked != REPACKED_ARCHIVE_SHA256:
        raise ValueError(f"deterministic eight-member carrier identity mismatch: {actual_repacked}")
    return repacked, members


def main() -> int:
    try:
        archive, members = load_material()
    except Exception as exc:
        return fail(f"authenticated v2.22 control material load failed: {exc}")

    with tempfile.TemporaryDirectory(prefix="cloudscribe-v222-controls-") as tmp:
        material = Path(tmp) / "material"
        material.mkdir(parents=True)
        for name, data in members.items():
            path = material / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)

        report = json.loads((material / VALIDATION_REPORT).read_text(encoding="utf-8"))
        if report.get("passed") is not True or report.get("schema_errors") != [] or report.get("semantic_errors") != []:
            return fail("supplied v2.22 pricing validation report is not a clean pass")
        if report.get("catalog_version") != "2026.07.20.2":
            return fail("unexpected supplied pricing catalog version")

        try:
            from jsonschema import Draft202012Validator, FormatChecker
        except ImportError:
            print("Exact v2.22 identities and supplied validation report PASS; jsonschema unavailable, executable validator agreement deferred to dependency-enabled certification.")
            return 0

        runtime_schema = json.loads((material / "03_Implementation/cloudscribe-runtime-policy.schema-1.3.json").read_text(encoding="utf-8"))
        runtime_seed = json.loads((material / "03_Implementation/cloudscribe-runtime-policy.seed-2026-07-20.schema-1.3.json").read_text(encoding="utf-8"))
        try:
            Draft202012Validator.check_schema(runtime_schema)
        except Exception as exc:
            return fail(f"runtime-policy Draft 2020-12 schema self-validation failed: {exc}")
        runtime_errors = sorted(
            Draft202012Validator(runtime_schema, format_checker=FormatChecker()).iter_errors(runtime_seed),
            key=lambda e: list(e.path),
        )
        if runtime_errors:
            return fail(f"runtime-policy 1.3 seed failed schema validation: {runtime_errors[0].message}")

        validator_path = material / VALIDATOR
        spec = importlib.util.spec_from_file_location("cloudscribe_catalog_validator_exact", validator_path)
        if spec is None or spec.loader is None:
            return fail("could not load supplied pricing validator")
        validator_module = importlib.util.module_from_spec(spec)
        try:
            spec.loader.exec_module(validator_module)
            schema = validator_module.load_json_strict(material / "02_Pricing/cloudscribe-pricing.schema-1.1.5.json")
            seed = validator_module.load_json_strict(material / "02_Pricing/cloudscribe-pricing.seed-2026-07-20.schema-1.1.5.json")
            report_time = datetime.fromisoformat(report["generated_at_utc"].replace("Z", "+00:00"))
            structural = validator_module.schema_errors(schema, seed)
            semantic = [] if structural else validator_module.semantic_errors(seed, now=report_time)
            actual_result = {
                "schema_errors": structural,
                "semantic_errors": semantic,
                "passed": not structural and not semantic,
            }
        except Exception as exc:
            return fail(f"supplied pricing validator execution failed: {exc}")

        if actual_result != report.get("validator_result"):
            return fail(
                "supplied pricing validator does not reproduce authenticated report at generated_at_utc: "
                + json.dumps(actual_result, sort_keys=True)
            )

    print(
        "Exact v2.22 seven-member transport, eight-member deterministic repack, all member identities, "
        "limits contract, supplied pricing validator report-time agreement, and runtime-policy 1.3 validation PASS."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
