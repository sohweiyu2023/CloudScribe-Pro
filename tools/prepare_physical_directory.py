#!/usr/bin/env python3
"""Create or validate an empty physical directory without following links.

Promotion and runtime-capture scripts use this helper before writing detached
artifacts. It validates every lexical path component with lstat, rejects Windows
reparse points and POSIX symbolic links, creates missing directories one level
at a time, and can forbid the source repository tree.
"""

from __future__ import annotations

import argparse
import os
import pathlib
import stat
import sys

sys.dont_write_bytecode = True


class DirectoryPolicyError(ValueError):
    pass


def absolute_lexical_path(path: pathlib.Path) -> pathlib.Path:
    return pathlib.Path(os.path.abspath(os.fspath(path.expanduser())))


def path_is_link_or_reparse(metadata: os.stat_result) -> bool:
    if stat.S_ISLNK(metadata.st_mode):
        return True
    attributes = getattr(metadata, "st_file_attributes", 0)
    reparse_flag = getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
    return bool(reparse_flag and attributes & reparse_flag)


def lstat_if_present(path: pathlib.Path) -> os.stat_result | None:
    try:
        return path.lstat()
    except FileNotFoundError:
        return None


def lexical_components(path: pathlib.Path) -> tuple[pathlib.Path, ...]:
    return tuple(reversed((path, *path.parents)))


def normalized_key(path: pathlib.Path) -> str:
    return os.path.normcase(os.path.normpath(os.fspath(absolute_lexical_path(path))))


def is_same_or_descendant(path: pathlib.Path, root: pathlib.Path) -> bool:
    path_key = normalized_key(path)
    root_key = normalized_key(root)
    try:
        return os.path.commonpath((path_key, root_key)) == root_key
    except ValueError:
        # Different Windows drives cannot contain one another.
        return False


def validate_existing_directory(path: pathlib.Path, metadata: os.stat_result, label: str) -> None:
    if path_is_link_or_reparse(metadata):
        raise DirectoryPolicyError(
            f"{label} must not traverse a symbolic-link or reparse-point component: {path}"
        )
    if not stat.S_ISDIR(metadata.st_mode):
        raise DirectoryPolicyError(f"{label} has a non-directory path component: {path}")


def ensure_physical_directory(
    path: pathlib.Path,
    *,
    label: str,
    forbidden_roots: tuple[pathlib.Path, ...] = (),
    require_empty: bool = False,
) -> pathlib.Path:
    candidate = absolute_lexical_path(path)
    anchor = pathlib.Path(candidate.anchor)
    if not candidate.anchor or normalized_key(candidate) == normalized_key(anchor):
        raise DirectoryPolicyError(f"{label} must not be a filesystem root")

    for forbidden_root in forbidden_roots:
        if is_same_or_descendant(candidate, forbidden_root):
            raise DirectoryPolicyError(
                f"{label} must not be the forbidden root or one of its descendants: {forbidden_root}"
            )

    for component in lexical_components(candidate):
        metadata = lstat_if_present(component)
        if metadata is None:
            try:
                component.mkdir()
            except FileExistsError:
                # A racing creator must pass the exact same physical-path gate.
                pass
            metadata = lstat_if_present(component)
            if metadata is None:
                raise DirectoryPolicyError(f"{label} directory could not be created: {component}")
        validate_existing_directory(component, metadata, label)

    # Recheck every prefix after creation to narrow the path-swap window.
    for component in lexical_components(candidate):
        metadata = lstat_if_present(component)
        if metadata is None:
            raise DirectoryPolicyError(f"{label} directory disappeared during validation: {component}")
        validate_existing_directory(component, metadata, label)

    if require_empty:
        try:
            with os.scandir(candidate) as entries:
                first = next(entries, None)
        except OSError as exc:
            raise DirectoryPolicyError(f"{label} could not be enumerated: {exc}") from exc
        if first is not None:
            raise DirectoryPolicyError(f"{label} must be empty; existing content is never deleted")

    return candidate


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("path", type=pathlib.Path)
    parser.add_argument("--label", default="Output directory")
    parser.add_argument("--forbid-root", action="append", default=[], type=pathlib.Path)
    parser.add_argument("--require-empty", action="store_true")
    return parser.parse_args()


def main() -> int:
    arguments = parse_arguments()
    try:
        prepared = ensure_physical_directory(
            arguments.path,
            label=arguments.label,
            forbidden_roots=tuple(arguments.forbid_root),
            require_empty=arguments.require_empty,
        )
    except (DirectoryPolicyError, OSError) as exc:
        print(f"Physical directory preparation FAILED: {exc}", file=sys.stderr)
        return 2

    print(prepared)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
