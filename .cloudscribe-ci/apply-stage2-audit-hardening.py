#!/usr/bin/env python3
from __future__ import annotations

import argparse
import base64
import hashlib
import io
import pathlib
import zipfile

PAYLOAD_SHA256 = "8f411e726352df7cdf8b02bd468274a504624994eec433143d2ec763061cba60"
PAYLOAD_PARTS = tuple(f"{i:02d}.txt" for i in range(8))

PREIMAGE_SHA256 = {
    "global.json": "b132d105648c7dbe49f2c391fe0b9898027e470ddc7cfd4d7239af5df63401fa",
    "SESSION_STATE.json": "c7b6194505ab229f47bc1577e233c5fbee03ecc8fe03b5466417a14b19819496",
    "tests/test_verification_tools.py": "d7b42c460cf55658e3c3ebebeeab09ae99931a3688d488832faab7e30c8598d6",
    "tools/verify_dotnet_sdk_version.py": "dcdc09c821806fb43d60280af7fdaeef0a6de8bdcde8936c2e9a8082b7cd38e3",
    "tools/verify_repository.py": "1226a18e27ff3e862f2cef9981794d6e980105958fb20e2ff48cc643e9c6d562",
    "tools/verify_source_release.py": "4dab89bb1d04f9eac5e18bb915226b6cf95cb3ba1ca1867d0dcd810d0eb0b3e7",
}

POSTIMAGE_SHA256 = {
    "global.json": "0588e32d44bdf884e0305ded21820b13a59b1518efd39976c34d909eff3b1044",
    "SESSION_STATE.json": "9f783dba1cca3449a8808fe168237322e1d2855c57a97dd6126c92baa70cc093",
    "tests/test_verification_tools.py": "f4a28aad496443b378044e2fd88309ea0993f4556df3232094bfce9008271add",
    "tools/verify_dotnet_sdk_version.py": "212e1fab784460247f3e93b18bb2c3db1245589f97c0f1f4df3fabf2f3d84600",
    "tools/verify_repository.py": "0f7edf34289ea9d947eef18d17420a3f448c021c1b2cc391f9211e44f666ebd9",
    "tools/verify_source_release.py": "9958e44f969c861fc8c85e9b40a05665c8a4a91a710f271f4051a658430279d4",
    "tools/run_verifier_self_tests.py": "d3f0d66371d86d534806be4d5e9a771259c9c1e7368fd7e0ec9e7f27f376889a",
}


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha256_file(path: pathlib.Path) -> str:
    return sha256_bytes(path.read_bytes())


def main() -> int:
    parser = argparse.ArgumentParser(description="Apply the exact Stage 2 adversarial-audit hardening overlay.")
    parser.add_argument("--source-root", required=True)
    args = parser.parse_args()
    root = pathlib.Path(args.source_root).resolve()
    if not (root / "CloudScribe.sln").is_file():
        raise RuntimeError(f"CloudScribe source root is invalid: {root}")

    for relative, expected in PREIMAGE_SHA256.items():
        path = root / relative
        if not path.is_file():
            raise RuntimeError(f"audit-hardening preimage missing: {relative}")
        actual = sha256_file(path)
        if actual != expected:
            raise RuntimeError(
                f"audit-hardening preimage mismatch for {relative}: expected={expected} actual={actual}"
            )
    new_runner = root / "tools/run_verifier_self_tests.py"
    if new_runner.exists():
        raise RuntimeError(f"audit-hardening new verifier runner unexpectedly already exists: {new_runner}")

    parts_root = pathlib.Path(__file__).with_name("audit-payload")
    encoded = "".join((parts_root / name).read_text(encoding="ascii").strip() for name in PAYLOAD_PARTS)
    payload = base64.b64decode(encoded, validate=True)
    actual_payload_sha = sha256_bytes(payload)
    if actual_payload_sha != PAYLOAD_SHA256:
        raise RuntimeError(
            f"audit-hardening payload SHA-256 mismatch: expected={PAYLOAD_SHA256} actual={actual_payload_sha}"
        )

    expected_names = set(POSTIMAGE_SHA256)
    with zipfile.ZipFile(io.BytesIO(payload), "r") as archive:
        names = archive.namelist()
        if len(names) != len(set(names)) or set(names) != expected_names:
            raise RuntimeError(f"audit-hardening payload inventory mismatch: {names}")
        for info in archive.infolist():
            pure = pathlib.PurePosixPath(info.filename)
            if pure.is_absolute() or ".." in pure.parts:
                raise RuntimeError(f"unsafe audit-hardening payload path: {info.filename!r}")
            data = archive.read(info)
            destination = root / pure
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(data)

    for relative, expected in POSTIMAGE_SHA256.items():
        path = root / relative
        if not path.is_file():
            raise RuntimeError(f"audit-hardening postimage missing: {relative}")
        actual = sha256_file(path)
        if actual != expected:
            raise RuntimeError(
                f"audit-hardening postimage mismatch for {relative}: expected={expected} actual={actual}"
            )

    print(
        "CLOUDSCRIBE_STAGE2_AUDIT_HARDENING=PASS "
        "exact_sdk=true python_self_tests=maintained context_metadata=truthful"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
