#!/usr/bin/env python3
from __future__ import annotations

import argparse
import hashlib
from pathlib import Path

PREIMAGE_SHA256 = {
    "tools/run_bounded_process.py": "ddb1fb15694b9778d55746c6806fec2fc2a8386a82d4936be42f2b0efe2d5c3c",
    "tests/test_verification_tools.py": "f4a28aad496443b378044e2fd88309ea0993f4556df3232094bfce9008271add",
}

POSTIMAGE_SHA256 = {
    "tools/run_bounded_process.py": "ffddaebada1a9c426bb0566164fe914dca527159edfeb7de7e11a00768ba8dcb",
    "tests/test_verification_tools.py": "5d8fa9a3c0791b53495aa746edacf056bc7860f57aaf9c74f96915a8bef2ca2d",
}


def sha256_file(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label} replacement expected exactly once; found {count}")
    return text.replace(old, new, 1)


def verify_hashes(root: Path, expected: dict[str, str], phase: str) -> None:
    for relative, digest in expected.items():
        path = root / relative
        if not path.is_file():
            raise RuntimeError(f"{phase} file missing: {relative}")
        actual = sha256_file(path)
        if actual != digest:
            raise RuntimeError(f"{phase} hash mismatch for {relative}: expected={digest} actual={actual}")


def main() -> int:
    parser = argparse.ArgumentParser(description="Repair the Windows fast-child Job Object assignment race.")
    parser.add_argument("--source-root", required=True)
    args = parser.parse_args()
    root = Path(args.source_root).resolve()
    if not (root / "CloudScribe.sln").is_file():
        raise RuntimeError(f"CloudScribe source root is invalid: {root}")

    verify_hashes(root, PREIMAGE_SHA256, "bounded-process race-fix preimage")

    runner_path = root / "tools/run_bounded_process.py"
    runner = runner_path.read_text(encoding="utf-8-sig").replace("\r\n", "\n").replace("\r", "\n")
    runner = replace_once(
        runner,
        '''        if not kernel32.AssignProcessToJobObject(job, wintypes.HANDLE(int(process._handle))):\n            error = ctypes.get_last_error()\n            kernel32.CloseHandle(job)\n            raise OSError(error, "AssignProcessToJobObject failed")\n        self._handle = int(job)\n        self._kernel32 = kernel32\n''',
        '''        if not kernel32.AssignProcessToJobObject(job, wintypes.HANDLE(int(process._handle))):\n            error = ctypes.get_last_error()\n            # A very short-lived child can exit in the narrow interval between CreateProcess\n            # and AssignProcessToJobObject. Windows reports ERROR_ACCESS_DENIED for an exited\n            # process handle. That is already a bounded terminal state, so do not turn a\n            # successful fast command into a verifier failure. A still-running process must\n            # still be job-bound; otherwise fail closed and let the caller tear it down.\n            if error == 5 and process.poll() is not None:  # ERROR_ACCESS_DENIED\n                kernel32.CloseHandle(job)\n                return\n            kernel32.CloseHandle(job)\n            raise OSError(error, "AssignProcessToJobObject failed")\n        self._handle = int(job)\n        self._kernel32 = kernel32\n''',
        "Windows Job Object fast-child race",
    )
    runner_path.write_text(runner, encoding="utf-8", newline="\n")

    tests_path = root / "tests/test_verification_tools.py"
    tests = tests_path.read_text(encoding="utf-8-sig").replace("\r\n", "\n").replace("\r", "\n")
    tests = replace_once(
        tests,
        '''    def test_accepts_bounded_successful_process(self):\n        with tempfile.TemporaryDirectory(prefix='cloudscribe-runner-') as temporary:\n            output = Path(temporary) / 'stdout.log'\n            result = self.run_runner([\n                '--timeout-seconds', '2',\n                '--max-output-bytes', '1024',\n                '--stdout-file', str(output),\n                '--', sys.executable, '-c', "print('ok')",\n            ])\n            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)\n            self.assertEqual(output.read_text(encoding='utf-8').strip(), 'ok')\n''',
        '''    def test_accepts_bounded_successful_process(self):\n        # Repeat intentionally tiny commands to exercise the Windows race where the child\n        # can exit before AssignProcessToJobObject observes it. A completed child is already\n        # bounded and must not be misreported as a runner failure.\n        with tempfile.TemporaryDirectory(prefix='cloudscribe-runner-') as temporary:\n            root = Path(temporary)\n            for index in range(20):\n                output = root / f'stdout-{index}.log'\n                result = self.run_runner([\n                    '--timeout-seconds', '2',\n                    '--max-output-bytes', '1024',\n                    '--stdout-file', str(output),\n                    '--', sys.executable, '-c', "print('ok')",\n                ])\n                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)\n                self.assertEqual(output.read_text(encoding='utf-8').strip(), 'ok')\n''',
        "bounded-process fast-success regression stress",
    )
    tests_path.write_text(tests, encoding="utf-8", newline="\n")

    verify_hashes(root, POSTIMAGE_SHA256, "bounded-process race-fix postimage")
    print("CLOUDSCRIBE_STAGE2_BOUNDED_PROCESS_RACE_FIX=PASS fast_child_exit_is_terminal=true")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
