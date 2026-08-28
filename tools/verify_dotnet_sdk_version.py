#!/usr/bin/env python3
from __future__ import annotations

import argparse
import re
import sys

# The release contract intentionally accepts only stable three-component SDK versions.
# A prerelease such as 10.0.302-preview.1 must never compare equal to the stable pin.
SDK_VERSION = re.compile(
    r"^(?P<major>0|[1-9]\d*)\.(?P<minor>0|[1-9]\d*)\.(?P<patch>0|[1-9]\d*)$"
)
MSBUILD_VERSION = re.compile(
    r"^(?P<major>0|[1-9]\d*)\.(?P<minor>0|[1-9]\d*)"
    r"(?:\.(?P<patch>0|[1-9]\d*))?(?:\.(?P<rev>0|[1-9]\d*))?$"
)


def parse_sdk(value: str, label: str) -> tuple[int, int, int]:
    match = SDK_VERSION.fullmatch(value.strip())
    if not match:
        raise ValueError(f"{label} is not a stable three-component .NET SDK version: {value!r}")
    return tuple(int(match.group(name)) for name in ("major", "minor", "patch"))


def parse_msbuild(value: str) -> tuple[int, int, int, int]:
    match = MSBUILD_VERSION.fullmatch(value.strip())
    if not match:
        raise ValueError(f"MSBuild version is not parseable: {value!r}")
    return tuple(int(match.group(name) or 0) for name in ("major", "minor", "patch", "rev"))


def minimum_msbuild(required: tuple[int, int, int]) -> tuple[int, int, int, int]:
    major, minor, patch = required
    feature_band = patch // 100
    if (major, minor) == (10, 0):
        # Microsoft maps .NET 10 feature bands to VS/MSBuild generations:
        # 1xx -> 18.0, 2xx -> 18.4, 3xx -> 18.6, 4xx -> 18.9.
        floors = {1: (18, 0, 0, 0), 2: (18, 4, 0, 0), 3: (18, 6, 0, 0), 4: (18, 9, 0, 0)}
        if feature_band not in floors:
            raise ValueError(f"unsupported .NET 10 SDK feature band for toolchain policy: {major}.{minor}.{feature_band}xx")
        return floors[feature_band]
    return (major + 8, 0, 0, 0)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Fail closed unless the exact stable required .NET SDK and a supported MSBuild generation are active."
    )
    parser.add_argument("--required", required=True)
    parser.add_argument("--actual", required=True)
    parser.add_argument("--msbuild", required=True)
    args = parser.parse_args()

    try:
        required = parse_sdk(args.required, "required SDK")
        actual = parse_sdk(args.actual, "actual SDK")
        msbuild = parse_msbuild(args.msbuild)
        msbuild_floor = minimum_msbuild(required)
    except ValueError as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        return 2

    if args.actual.strip() != args.required.strip() or actual != required:
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

    if msbuild < msbuild_floor:
        print(
            f"FAIL: .NET {required[0]}.{required[1]}.{required[2] // 100}xx "
            f"requires MSBuild {msbuild_floor[0]}.{msbuild_floor[1]} or later; got {args.msbuild}",
            file=sys.stderr,
        )
        return 5

    print(
        "PASS: exact .NET SDK/toolchain policy satisfied: "
        f"sdk={args.actual} msbuild={'.'.join(str(part) for part in msbuild)} "
        f"minimum={msbuild_floor[0]}.{msbuild_floor[1]}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
