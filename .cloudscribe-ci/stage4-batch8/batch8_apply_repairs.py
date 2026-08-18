from __future__ import annotations

import argparse
import base64
import hashlib
import subprocess
from pathlib import Path


def run(*args: str) -> None:
    result = subprocess.run(args, check=False)
    if result.returncode:
        raise SystemExit(f"command failed ({result.returncode}): {' '.join(args)}")


def apply_b64(path: Path, expected_sha: str, output: Path) -> None:
    payload = base64.b64decode(path.read_text(encoding='ascii').strip(), validate=True)
    digest = hashlib.sha256(payload).hexdigest()
    if digest != expected_sha:
        raise SystemExit(f'SHA mismatch for {path.name}: {digest}')
    output.write_bytes(payload)
    run('git', 'apply', '--check', '--', str(output))
    run('git', 'apply', '--whitespace=nowarn', '--', str(output))


def main() -> None:
    p = argparse.ArgumentParser()
    p.add_argument('--carrier-root', required=True)
    p.add_argument('--temp-root', required=True)
    p.add_argument('--lock-sha', required=True)
    p.add_argument('--manifest-sha', required=True)
    p.add_argument('--source-repair-sha', required=True)
    p.add_argument('--compile-repair-sha', required=True)
    p.add_argument('--analyzer-repair-sha', required=True)
    p.add_argument('--test-repair-sha', required=True)
    args = p.parse_args()
    carrier = Path(args.carrier_root)
    temp = Path(args.temp_root)
    apply_b64(carrier / 'batch8-lock-repair.b64', args.lock_sha, temp / 'batch8-lock-repair.patch')
    apply_b64(carrier / 'batch8-manifest-repair.b64', args.manifest_sha, temp / 'batch8-manifest-repair.patch')
    apply_b64(carrier / 'batch8-ma0016-repair.b64', args.source_repair_sha, temp / 'batch8-ma0016-repair.patch')
    apply_b64(carrier / 'batch8-cs0266-repair.b64', args.compile_repair_sha, temp / 'batch8-cs0266-repair.patch')
    apply_b64(carrier / 'batch8-ca1859-repair.b64', args.analyzer_repair_sha, temp / 'batch8-ca1859-repair.patch')
    apply_b64(carrier / 'batch8-test-memory-repair.b64', args.test_repair_sha, temp / 'batch8-test-memory-repair.patch')
    run('python', 'tools/update_sha256_manifest.py', '--check')
    run('git', 'add', '-A')
    paths = sorted(filter(None, subprocess.check_output(['git', 'diff', '--cached', '--name-only'], text=True).splitlines()))
    if len(paths) != 20:
        raise SystemExit(f'Expected 20 changed paths; found {len(paths)}: {paths}')
    expected_locks = sorted([
        'src/CloudScribe.App/packages.lock.json',
        'src/CloudScribe.Infrastructure/packages.lock.json',
        'tests/CloudScribe.Application.Tests/packages.lock.json',
        'tests/CloudScribe.Architecture.Tests/packages.lock.json',
        'tests/CloudScribe.Domain.Tests/packages.lock.json',
        'tests/CloudScribe.Infrastructure.Tests/packages.lock.json',
    ])
    locks = sorted(path for path in paths if path.endswith('packages.lock.json'))
    if locks != expected_locks:
        raise SystemExit(f'Unexpected lockfile set: {locks}')
    if any(set(Path(path).parts) & {'bin', 'obj', 'TestResults', '__pycache__'} for path in paths):
        raise SystemExit('Generated path entered the Batch 8 candidate.')
    run('git', 'diff', '--cached', '--check')
    print(f'CLOUDSCRIBE_STAGE4_BATCH8_V6_RECONSTRUCTED files={len(paths)} locks={len(locks)}')


if __name__ == '__main__':
    main()
