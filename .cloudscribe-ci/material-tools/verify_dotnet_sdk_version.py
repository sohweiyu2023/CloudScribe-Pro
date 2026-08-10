#!/usr/bin/env python3
from __future__ import annotations

import argparse
import re
import sys

SEMVER = re.compile(r"^(?P<major>0|[1-9]\d*)\.(?P<minor>0|[1-9]\d*)\.(?P<patch>0|[1-9]\d*)(?:[-+][0-9A-Za-z.-]+)?$")
MSBUILD = re.compile(r"(?P<major>\d+)\.(?P<minor>\d+)(?:\.(?P<patch>\d+))?(?:\.(?P<rev>\d+))?")


def parse_semver(value: str, label: str) -> tuple[int, int, int]:
    match = SEMVER.fullmatch(value.strip())
    if not match:
        raise ValueError(f"{label} is not a valid SDK semantic version: {value!r}")
    return tuple(int(match.group(name)) for name in ("major", "minor", "patch"))


def parse_msbuild(value: str) -> tuple[int, int, int, int]:
    match = MSBUILD.search(value.strip())
    if not match:
        raise ValueError(f"MSBuild version is not parseable: {value!r}")
    return tuple(int(match.group(name) or 0) for name in ("major", "minor", "patch", "rev"))


def main() -> int:
    parser = argparse.ArgumentParser(description="Fail closed unless the exact required .NET SDK and its matching MSBuild generation are active.")
    parser.add_argument("--required", required=True)
    parser.add_argument("--actual", required=True)
    parser.add_argument("--msbuild", required=True)
    args = parser.parse_args()

    try:
        required = parse_semver(args.required, "required SDK")
        actual = parse_semver(args.actual, "actual SDK")
        msbuild = parse_msbuild(args.msbuild)
    except ValueError as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        return 2

    if actual != required:
        print(f"FAIL: exact .NET SDK mismatch: required={args.required} actual={args.actual}", file=sys.stderr)
        return 3

    expected_msbuild_major = required[0] + 8
    if msbuild[0] != expected_msbuild_major:
        print(
            f"FAIL: MSBuild generation mismatch for .NET {required[0]}: "
            f"expected major {expected_msbuild_major}, got {msbuild[0]} ({args.msbuild})",
            file=sys.stderr,
        )
        return 4

    print(
        "PASS: exact .NET SDK/toolchain policy satisfied: "
        f"sdk={args.actual} msbuild={'.'.join(str(part) for part in msbuild)}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
