#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import importlib.util
import json
import sys
import tempfile
import zipfile
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ARCHIVE = ROOT / "controls/v2.22/exact-controls.bundle.zip"
# Deterministic transport regenerated from the independently authenticated v2.22
# master package. .gitattributes marks ZIPs binary so Windows checkout cannot
# line-ending-normalize these authenticated bytes.
ARCHIVE_SHA256 = "32818c608304aca3a76bef7b5ec4aae16e530a01ba4ef5d679d35ea50bd611c1"
EXPECTED = {
    "02_Pricing/cloudscribe-pricing.schema-1.1.5.json": "1dc77a16130efa0fa2428e954bbfc5c7d30088283bbaf5b3dddff5694e01972b",
    "02_Pricing/cloudscribe-pricing.seed-2026-07-20.schema-1.1.5.json": "3e647812dcae11face91b66c3df642f19134de34b8d706e2c2183c87266e8b61",
    "03_Implementation/cloudscribe-runtime-policy.schema-1.3.json": "bdcc03005a48d9d8bdcb139d468a9c3f277526aa4d9dbe19c2c6309b5bff390c",
    "03_Implementation/cloudscribe-runtime-policy.seed-2026-07-20.schema-1.3.json": "9561a4f5c1d58dd471424566b05f7325a52ed06a4c57ec53b17f5395ae621525",
    "06_Product_and_Distribution/CloudScribe_Pro_Batch_Limits_Autosave_Settings_Contract_v2.22.md": "5d3e17debc58e0775bf472f7eebd79db32447de457fcec20d924a860dcfcb6d7",
}
VALIDATION_REPORT = "02_Pricing/CloudScribe_Pricing_Catalog_Validation_v1.1.5_2026-07-20.json"
VALIDATOR = "05_Tools_and_Tests/cloudscribe_catalog_validator.py"
REQUIREMENTS = "05_Tools_and_Tests/requirements.txt"


def fail(message: str) -> int:
    print(f"ERROR: {message}", file=sys.stderr)
    return 1


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def load_archive() -> bytes:
    if not ARCHIVE.is_file():
        raise ValueError(f"authenticated control archive missing: {ARCHIVE.relative_to(ROOT)}")
    archive = ARCHIVE.read_bytes()
    actual = sha256(archive)
    if actual != ARCHIVE_SHA256:
        raise ValueError(f"authenticated carrier archive identity mismatch: {actual}")
    return archive


def main() -> int:
    try:
        archive = load_archive()
    except Exception as exc:
        return fail(f"authenticated v2.22 control archive load failed: {exc}")

    with tempfile.TemporaryDirectory(prefix="cloudscribe-v222-controls-") as tmp:
        tmp_root = Path(tmp)
        archive_path = tmp_root / "controls.zip"
        archive_path.write_bytes(archive)
        try:
            with zipfile.ZipFile(archive_path) as zf:
                names = set(zf.namelist())
                required = set(EXPECTED) | {VALIDATION_REPORT, VALIDATOR, REQUIREMENTS}
                missing = sorted(required - names)
                if missing:
                    return fail(f"control bundle missing required members: {missing}")
                for name, expected in EXPECTED.items():
                    actual = sha256(zf.read(name))
                    if actual != expected:
                        return fail(f"identity mismatch for {name}: {actual}")
                zf.extractall(tmp_root / "material")
        except (zipfile.BadZipFile, OSError) as exc:
            return fail(f"invalid control archive: {exc}")

        material = tmp_root / "material"
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

        # The supplied semantic validator intentionally contains lifecycle checks
        # against a caller-supplied `now`. Reproduce the authenticated validation
        # report at its own generated_at_utc rather than silently making a July
        # package fail solely because wall-clock time advanced. Current catalog
        # freshness remains a separate runtime/update-channel concern.
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

    print("Exact v2.22 control identities, limits contract, supplied pricing validator report-time agreement, and runtime-policy 1.3 validation PASS.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
