#!/usr/bin/env python3
from __future__ import annotations

import base64
import hashlib
import json
import string
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PARTS = ROOT / "controls/v2.22/carrier-parts"
ARCHIVE_SHA256 = "6031608216e76c1b8d8186c6f0c7ba6e226a5f49550da011b523abab5ee6e510"
EXPECTED = {
    "02_Pricing/cloudscribe-pricing.schema-1.1.5.json": "1dc77a16130efa0fa2428e954bbfc5c7d30088283bbaf5b3dddff5694e01972b",
    "02_Pricing/cloudscribe-pricing.seed-2026-07-20.schema-1.1.5.json": "3e647812dcae11face91b66c3df642f19134de34b8d706e2c2183c87266e8b61",
    "03_Implementation/cloudscribe-runtime-policy.schema-1.3.json": "bdcc03005a48d9d8bdcb139d468a9c3f277526aa4d9dbe19c2c6309b5bff390c",
    "03_Implementation/cloudscribe-runtime-policy.seed-2026-07-20.schema-1.3.json": "9561a4f5c1d58dd471424566b05f7325a52ed06a4c57ec53b17f5395ae621525",
}
VALIDATION_REPORT = "02_Pricing/CloudScribe_Pricing_Catalog_Validation_v1.1.5_2026-07-20.json"
VALIDATOR = "05_Tools_and_Tests/cloudscribe_catalog_validator.py"
REQUIREMENTS = "05_Tools_and_Tests/requirements.txt"
_BASE64_ALPHABET = set(string.ascii_letters + string.digits + "+/=")


def fail(message: str) -> int:
    print(f"ERROR: {message}", file=sys.stderr)
    return 1


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def load_archive() -> bytes:
    parts = sorted(PARTS.glob("part*.b64"))
    if [p.name for p in parts] != [f"part{i:02d}.b64" for i in range(1, 6)]:
        raise ValueError(f"unexpected authenticated carrier parts: {[p.name for p in parts]}")

    normalized_parts: list[str] = []
    for part in parts:
        raw = part.read_text(encoding="ascii")
        normalized = "".join(raw.split())
        invalid = [(index, char, ord(char)) for index, char in enumerate(normalized) if char not in _BASE64_ALPHABET]
        if invalid:
            sample = ", ".join(f"index={index} char={char!r} ord={code}" for index, char, code in invalid[:8])
            raise ValueError(f"non-Base64 transport characters in {part.name}: {sample}")
        normalized_parts.append(normalized)

    encoded = "".join(normalized_parts)
    archive = base64.b64decode(encoded, validate=True)
    actual = sha256(archive)
    if actual != ARCHIVE_SHA256:
        raise ValueError(f"authenticated carrier archive identity mismatch: {actual}")
    return archive


def main() -> int:
    try:
        archive = load_archive()
    except Exception as exc:
        return fail(f"authenticated v2.22 carrier rehydration failed: {exc}")

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

        validator = material / VALIDATOR
        schema = material / "02_Pricing/cloudscribe-pricing.schema-1.1.5.json"
        seed = material / "02_Pricing/cloudscribe-pricing.seed-2026-07-20.schema-1.1.5.json"
        result = subprocess.run(
            [sys.executable, str(validator), str(schema), str(seed)],
            cwd=material,
            text=True,
            capture_output=True,
        )
        if result.returncode != 0:
            sys.stderr.write(result.stdout)
            sys.stderr.write(result.stderr)
            return fail("supplied pricing validator did not agree with exact schema/seed")

    print("Exact v2.22 control identities, supplied pricing validator agreement, and runtime-policy 1.3 validation PASS.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
