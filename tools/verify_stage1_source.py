#!/usr/bin/env python3
from __future__ import annotations

import json
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

EXPECTED_PROJECTS = (
    "src/CloudScribe.App/CloudScribe.App.csproj",
    "src/CloudScribe.Application/CloudScribe.Application.csproj",
    "src/CloudScribe.Domain/CloudScribe.Domain.csproj",
    "src/CloudScribe.Infrastructure/CloudScribe.Infrastructure.csproj",
    "src/CloudScribe.Providers.Abstractions/CloudScribe.Providers.Abstractions.csproj",
    "tests/CloudScribe.Domain.Tests/CloudScribe.Domain.Tests.csproj",
    "tests/CloudScribe.Application.Tests/CloudScribe.Application.Tests.csproj",
    "tests/CloudScribe.Infrastructure.Tests/CloudScribe.Infrastructure.Tests.csproj",
    "tests/CloudScribe.Architecture.Tests/CloudScribe.Architecture.Tests.csproj",
)
REQUIRED_STAGE1 = (
    "src/CloudScribe.App/Program.cs",
    "src/CloudScribe.App/CloudScribeApplication.axaml",
    "src/CloudScribe.App/MainWindow.axaml",
    "src/CloudScribe.App/MainWindow.axaml.cs",
    "src/CloudScribe.Infrastructure/Configuration/AppPaths.cs",
    "scripts/smoke-stage1-windows.ps1",
)


def fail(message: str) -> int:
    print(f"FAIL: {message}", file=sys.stderr)
    return 1


def main() -> int:
    root = Path.cwd().resolve()
    for relative in (*EXPECTED_PROJECTS, *REQUIRED_STAGE1):
        if not (root / relative).is_file():
            return fail(f"Stage 1 structural dependency missing: {relative}")

    for relative in EXPECTED_PROJECTS:
        project = root / relative
        try:
            ET.parse(project)
        except ET.ParseError as exc:
            return fail(f"invalid project XML {relative}: {exc}")
        if not (project.parent / "packages.lock.json").is_file():
            return fail(f"locked package graph missing for {relative}")

    try:
        state = json.loads((root / "SESSION_STATE.json").read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as exc:
        return fail(f"SESSION_STATE.json invalid: {exc}")
    if state.get("stage1_checkpoint_promoted") is not True:
        return fail("Stage 1 checkpoint is not marked promoted")
    if state.get("stage1_checkpoint_version") != "0.2.1-stage1":
        return fail(f"unexpected Stage 1 checkpoint identity: {state.get('stage1_checkpoint_version')!r}")
    if "33/33" not in str(state.get("stage1_checkpoint_tests", "")):
        return fail("Stage 1 checkpoint test provenance is missing")
    if "Linux/Xvfb" not in str(state.get("stage1_checkpoint_runtime", "")):
        return fail("Stage 1 runtime provenance is missing")

    csharp = [p for p in root.rglob("*.cs") if not ({"bin", "obj"} & set(p.relative_to(root).parts))]
    if len(csharp) < 80:
        return fail(f"unexpectedly small C# source inventory: {len(csharp)} files")

    print(f"PASS: Stage 1 promoted checkpoint contract, 9 locked projects and {len(csharp)} C# source files verified.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
