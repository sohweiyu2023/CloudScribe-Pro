from __future__ import annotations

import argparse
import base64
import gzip
import hashlib
import os
import subprocess
from pathlib import Path


def run(*args: str, capture: bool = False) -> str:
    result = subprocess.run(args, check=False, text=True, stdout=subprocess.PIPE if capture else None, stderr=subprocess.PIPE if capture else None)
    if result.returncode:
        detail = ((result.stdout or '') + (result.stderr or ''))[-4000:]
        raise SystemExit(f"command failed ({result.returncode}): {' '.join(args)}\n{detail}")
    return (result.stdout or '').strip()


def changed_paths(include_untracked: bool) -> list[str]:
    paths = set(filter(None, run('git', 'diff', '--name-only', capture=True).splitlines()))
    if include_untracked:
        paths.update(filter(None, run('git', 'ls-files', '--others', '--exclude-standard', capture=True).splitlines()))
    return sorted(paths)


def reconstruct(args: argparse.Namespace) -> None:
    head = run('git', 'rev-parse', 'HEAD', capture=True)
    if head != args.base_head:
        raise SystemExit(f'Stage 4 target moved before Batch 8 lock capture: {head}')
    carrier = Path(args.carrier_root)
    text = ''.join((carrier / f'batch8-{index:02d}.b64').read_text(encoding='ascii').strip() for index in range(1, 9))
    if len(text) != args.b64_length:
        raise SystemExit(f'Unexpected Batch 8 Base64 length: {len(text)}')
    patch_bytes = gzip.decompress(base64.b64decode(text, validate=True))
    digest = hashlib.sha256(patch_bytes).hexdigest()
    if digest != args.patch_sha:
        raise SystemExit(f'Batch 8 primary SHA mismatch: {digest}')
    patch_path = Path(os.environ['RUNNER_TEMP']) / 'batch8-primary.patch'
    patch_path.write_bytes(patch_bytes)
    run('git', 'apply', '--check', '--', str(patch_path))
    run('git', 'apply', '--whitespace=nowarn', '--', str(patch_path))
    run('python', 'tools/update_sha256_manifest.py', '--check')
    paths = changed_paths(include_untracked=True)
    if len(paths) != args.expected_paths:
        raise SystemExit(f'Expected {args.expected_paths} primary paths; found {len(paths)}: {paths}')
    forbidden = [p for p in paths if 'packages.lock.json' in p or any(part in {'bin', 'obj', 'TestResults', '__pycache__'} for part in Path(p).parts)]
    if forbidden:
        raise SystemExit(f'Primary patch contains generated state or lock files: {forbidden}')
    run('git', 'add', '-A')
    run('git', 'diff', '--cached', '--check')
    print(f'CLOUDSCRIBE_STAGE4_BATCH8_PRIMARY_RECONSTRUCTED files={len(paths)} sha256={digest}')


def capture(_: argparse.Namespace) -> None:
    run('python', 'tools/update_sha256_manifest.py')
    paths = changed_paths(include_untracked=False)
    bad = [p for p in paths if p != 'SHA256SUMS.txt' and not p.endswith('packages.lock.json')]
    locks = [p for p in paths if p.endswith('packages.lock.json')]
    if bad:
        raise SystemExit(f'Lock capture changed forbidden tracked paths: {bad}')
    if not locks:
        raise SystemExit('No dependency lock file changed.')
    if 'SHA256SUMS.txt' not in paths:
        raise SystemExit('Manifest did not change with regenerated dependency locks.')
    patch_bytes = subprocess.check_output(['git', 'diff', '--binary', '--full-index'])
    if not patch_bytes:
        raise SystemExit('Lock repair patch is empty.')
    temp = Path(os.environ['RUNNER_TEMP'])
    repair = temp / 'batch8-lock-repair.patch'
    repair.write_bytes(patch_bytes)
    digest = hashlib.sha256(patch_bytes).hexdigest()
    metadata = temp / 'batch8-lock-capture.txt'
    metadata.write_text(
        f'BATCH8_LOCK_REPAIR_SHA256={digest}\n'
        f'BATCH8_LOCK_REPAIR_PATHS={len(paths)}\n'
        f"BATCH8_LOCK_REPAIR_FILES={','.join(paths)}\n",
        encoding='utf-8',
    )
    print(metadata.read_text(encoding='utf-8'), end='')


def main() -> None:
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest='mode', required=True)
    reconstruct_parser = sub.add_parser('reconstruct')
    reconstruct_parser.add_argument('--carrier-root', required=True)
    reconstruct_parser.add_argument('--base-head', required=True)
    reconstruct_parser.add_argument('--patch-sha', required=True)
    reconstruct_parser.add_argument('--b64-length', required=True, type=int)
    reconstruct_parser.add_argument('--expected-paths', required=True, type=int)
    sub.add_parser('capture')
    args = parser.parse_args()
    reconstruct(args) if args.mode == 'reconstruct' else capture(args)


if __name__ == '__main__':
    main()
