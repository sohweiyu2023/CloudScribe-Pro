#!/usr/bin/env python3
from __future__ import annotations

import sys
import xml.etree.ElementTree as ET
from pathlib import Path

EXPECTED_PROJECTS = {
    "src/CloudScribe.App/CloudScribe.App.csproj",
    "src/CloudScribe.Application/CloudScribe.Application.csproj",
    "src/CloudScribe.Domain/CloudScribe.Domain.csproj",
    "src/CloudScribe.Infrastructure/CloudScribe.Infrastructure.csproj",
    "src/CloudScribe.Providers.Abstractions/CloudScribe.Providers.Abstractions.csproj",
    "tests/CloudScribe.Domain.Tests/CloudScribe.Domain.Tests.csproj",
    "tests/CloudScribe.Application.Tests/CloudScribe.Application.Tests.csproj",
    "tests/CloudScribe.Infrastructure.Tests/CloudScribe.Infrastructure.Tests.csproj",
    "tests/CloudScribe.Architecture.Tests/CloudScribe.Architecture.Tests.csproj",
}


def fail(message: str) -> int:
    print(f"FAIL: {message}", file=sys.stderr)
    return 1


def main() -> int:
    root = Path.cwd().resolve()
    projects = sorted(
        p for p in root.rglob("*.csproj")
        if not ({"bin", "obj"} & set(p.relative_to(root).parts))
    )
    rel_projects = {p.relative_to(root).as_posix() for p in projects}
    if rel_projects != EXPECTED_PROJECTS:
        return fail(
            "project inventory mismatch; "
            f"missing={sorted(EXPECTED_PROJECTS-rel_projects)} extra={sorted(rel_projects-EXPECTED_PROJECTS)}"
        )

    by_rel = {p.relative_to(root).as_posix(): p for p in projects}
    graph: dict[str, set[str]] = {rel: set() for rel in by_rel}
    for rel, project in by_rel.items():
        try:
            tree = ET.parse(project)
        except ET.ParseError as exc:
            return fail(f"invalid project XML {rel}: {exc}")
        if project.parent.joinpath("packages.lock.json").is_file() is False:
            return fail(f"locked restore contract missing packages.lock.json beside {rel}")

        for node in tree.getroot().iter():
            if node.tag.rsplit("}", 1)[-1] != "ProjectReference":
                continue
            include = node.attrib.get("Include", "").strip()
            if not include:
                return fail(f"empty ProjectReference in {rel}")
            if Path(include).is_absolute():
                return fail(f"absolute ProjectReference in {rel}: {include}")
            target = (project.parent / include).resolve()
            try:
                target_rel = target.relative_to(root).as_posix()
            except ValueError:
                return fail(f"ProjectReference escapes repository in {rel}: {include}")
            if target_rel not in by_rel or not target.is_file():
                return fail(f"ProjectReference target missing in {rel}: {include} -> {target_rel}")
            if rel.startswith("src/") and target_rel.startswith("tests/"):
                return fail(f"production project references test project: {rel} -> {target_rel}")
            graph[rel].add(target_rel)

    visiting: set[str] = set()
    visited: set[str] = set()

    def visit(node: str, chain: list[str]) -> str | None:
        if node in visiting:
            return " -> ".join(chain + [node])
        if node in visited:
            return None
        visiting.add(node)
        for child in sorted(graph[node]):
            cycle = visit(child, chain + [node])
            if cycle:
                return cycle
        visiting.remove(node)
        visited.add(node)
        return None

    for node in sorted(graph):
        cycle = visit(node, [])
        if cycle:
            return fail(f"project dependency cycle: {cycle}")

    domain_refs = graph["src/CloudScribe.Domain/CloudScribe.Domain.csproj"]
    if domain_refs:
        return fail(f"Domain layer must remain dependency-rooted; found {sorted(domain_refs)}")

    print(f"PASS: {len(projects)} project files, locked restore files, in-repo references and acyclic layering verified.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
