from __future__ import annotations

import hashlib
import importlib.util
import json
import os
import shutil
import struct
import subprocess
import sys
import tempfile
import time
import unittest
import zlib
from pathlib import Path

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
TOOLS = ROOT / "tools"
VALIDATOR = TOOLS / "verify_dotnet_package_scan.py"
SDK_VERSION_VALIDATOR = TOOLS / "verify_dotnet_sdk_version.py"
RUNNER = TOOLS / "run_bounded_process.py"


def _load_module(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


_VISUAL = _load_module("cloudscribe_visual_validator", TOOLS / "verify_stage2_visual_evidence.py")
_MANIFEST = _load_module("cloudscribe_source_manifest", TOOLS / "update_sha256_manifest.py")
_PHYSICAL_DIRECTORY = _load_module("cloudscribe_physical_directory_policy", TOOLS / "prepare_physical_directory.py")


def _env() -> dict[str, str]:
    environment = os.environ.copy()
    environment["PYTHONDONTWRITEBYTECODE"] = "1"
    return environment


def _run(command: list[str], cwd: Path = ROOT, timeout: int = 120) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        command,
        cwd=cwd,
        text=True,
        capture_output=True,
        check=False,
        env=_env(),
        timeout=timeout,
    )


def _run_tool(name: str, *arguments: str, cwd: Path = ROOT, timeout: int = 120) -> subprocess.CompletedProcess[str]:
    return _run([sys.executable, "-B", str(TOOLS / name), *arguments], cwd=cwd, timeout=timeout)


def _copy_source(destination: Path) -> Path:
    root = destination / "source"
    shutil.copytree(
        ROOT,
        root,
        ignore=shutil.ignore_patterns("bin", "obj", "TestResults", "__pycache__", ".vs", ".git"),
    )
    return root


def _png_chunk(kind: bytes, data: bytes) -> bytes:
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)


def _write_test_png(path: Path, extra_chunks=()):
    width = height = 64
    rows = []
    for y in range(height):
        row = bytearray([0])
        for x in range(width):
            row.extend(((x * 17 + y * 3) & 0xFF, (x * 7 + y * 19) & 0xFF, (x * 11 + y * 13) & 0xFF))
        rows.append(bytes(row))
    ihdr = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    payload = bytearray(_VISUAL.PNG_SIGNATURE)
    payload.extend(_png_chunk(b"IHDR", ihdr))
    for kind, data in extra_chunks:
        payload.extend(_png_chunk(kind, data))
    payload.extend(_png_chunk(b"IDAT", zlib.compress(b"".join(rows))))
    payload.extend(_png_chunk(b"IEND", b""))
    path.write_bytes(payload)

class PackageScanValidatorTests(unittest.TestCase):
    def run_validator(self, documents):
        with tempfile.TemporaryDirectory(prefix='cloudscribe-package-scan-') as temporary:
            root = Path(temporary)
            for name, document in documents.items():
                (root / name).write_text(json.dumps(document), encoding='utf-8')
            environment = os.environ.copy()
            environment['PYTHONDONTWRITEBYTECODE'] = '1'
            return subprocess.run(
                [sys.executable, str(VALIDATOR), str(root)],
                cwd=ROOT,
                text=True,
                capture_output=True,
                check=False,
                env=environment,
                timeout=30,
            )

    @staticmethod
    def scan(kind, packages=None):
        return {
            'version': 1,
            'parameters': f'--{kind} --include-transitive',
            'projects': [{
                'path': 'sample.csproj',
                'frameworks': [{
                    'framework': 'net10.0',
                    'topLevelPackages': packages or [],
                }],
            }],
        }

    def test_accepts_complete_clean_pair(self):
        result = self.run_validator({
            '0-vulnerable.json': self.scan('vulnerable'),
            '0-deprecated.json': self.scan('deprecated'),
        })
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_rejects_vulnerability_finding(self):
        result = self.run_validator({
            '0-vulnerable.json': self.scan('vulnerable', [{
                'id': 'Example',
                'vulnerabilities': [{'severity': 'High'}],
            }]),
            '0-deprecated.json': self.scan('deprecated'),
        })
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('vulnerabilities', result.stderr)

    def test_rejects_wrong_scan_parameters(self):
        result = self.run_validator({
            '0-vulnerable.json': self.scan('deprecated'),
            '0-deprecated.json': self.scan('deprecated'),
        })
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('do not prove a --vulnerable scan', result.stderr)

    def test_rejects_missing_pair(self):
        result = self.run_validator({
            '0-vulnerable.json': self.scan('vulnerable'),
        })
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('both vulnerable and deprecated scans', result.stderr)


    def test_rejects_excessive_scan_directory_entries_during_enumeration(self):
        with tempfile.TemporaryDirectory(prefix='cloudscribe-package-scan-many-') as temporary:
            root = Path(temporary)
            for index in range(129):
                (root / f'unexpected-{index}.json').write_text('{}', encoding='utf-8')
            environment = os.environ.copy()
            environment['PYTHONDONTWRITEBYTECODE'] = '1'
            result = subprocess.run(
                [sys.executable, str(VALIDATOR), str(root)],
                cwd=ROOT,
                text=True,
                capture_output=True,
                check=False,
                env=environment,
                timeout=30,
            )
            self.assertNotEqual(result.returncode, 0)
            self.assertIn('more than', result.stderr)

    def test_rejects_symbolic_link_scan(self):
        with tempfile.TemporaryDirectory(prefix='cloudscribe-package-scan-link-') as temporary:
            root = Path(temporary)
            target = root / 'target.json'
            target.write_text(json.dumps(self.scan('vulnerable')), encoding='utf-8')
            link = root / '0-vulnerable.json'
            try:
                link.symlink_to(target.name)
            except (OSError, NotImplementedError) as exc:
                self.skipTest(f'symbolic links unavailable: {exc}')
            (root / '0-deprecated.json').write_text(json.dumps(self.scan('deprecated')), encoding='utf-8')
            environment = os.environ.copy()
            environment['PYTHONDONTWRITEBYTECODE'] = '1'
            result = subprocess.run(
                [sys.executable, str(VALIDATOR), str(root)],
                cwd=ROOT,
                text=True,
                capture_output=True,
                check=False,
                env=environment,
                timeout=30,
            )
            self.assertNotEqual(result.returncode, 0)
            self.assertIn('symbolic-link', result.stderr)

class SourceManifestToolTests(unittest.TestCase):
    def test_collects_regular_bounded_files_deterministically(self):
        with tempfile.TemporaryDirectory(prefix='cloudscribe-manifest-') as temporary:
            root = Path(temporary)
            (root / 'b.txt').write_text('b', encoding='utf-8')
            (root / 'a.txt').write_text('a', encoding='utf-8')
            manifest = root / 'SHA256SUMS.txt'
            rendered = _MANIFEST.render(root, manifest)
            self.assertEqual([line.split('  ', 1)[1] for line in rendered.splitlines()], ['a.txt', 'b.txt'])

    def test_rejects_symbolic_link_file(self):
        with tempfile.TemporaryDirectory(prefix='cloudscribe-manifest-link-') as temporary:
            root = Path(temporary)
            target = root / 'target.txt'
            target.write_text('secret', encoding='utf-8')
            link = root / 'linked.txt'
            try:
                link.symlink_to(target.name)
            except (OSError, NotImplementedError) as exc:
                self.skipTest(f'symbolic links unavailable: {exc}')
            with self.assertRaisesRegex(_MANIFEST.ManifestError, 'symbolic-link file'):
                _MANIFEST.render(root, root / 'SHA256SUMS.txt')

    def test_rejects_excessive_tree_entries(self):
        with tempfile.TemporaryDirectory(prefix='cloudscribe-manifest-many-') as temporary:
            root = Path(temporary)
            original = _MANIFEST.MAX_TREE_ENTRIES
            try:
                _MANIFEST.MAX_TREE_ENTRIES = 2
                for index in range(3):
                    (root / f'{index}.txt').write_text('x', encoding='utf-8')
                with self.assertRaisesRegex(_MANIFEST.ManifestError, 'more than 2 entries'):
                    _MANIFEST.render(root, root / 'SHA256SUMS.txt')
            finally:
                _MANIFEST.MAX_TREE_ENTRIES = original

class VisualEvidencePngParserTests(unittest.TestCase):
    def test_accepts_small_valid_png(self):
        with tempfile.TemporaryDirectory(prefix='cloudscribe-png-') as temporary:
            path = Path(temporary) / 'valid.png'
            _write_test_png(path)
            width, height, colors = _VISUAL.parse_png(path)
            self.assertEqual((width, height), (64, 64))
            self.assertGreaterEqual(colors, 16)

    def test_rejects_pathological_chunk_count(self):
        with tempfile.TemporaryDirectory(prefix='cloudscribe-png-') as temporary:
            path = Path(temporary) / 'many-chunks.png'
            _write_test_png(path, [(b'tEXt', b'')] * _VISUAL.MAX_PNG_CHUNKS)
            with self.assertRaisesRegex(_VISUAL.EvidenceError, 'more than'):
                _VISUAL.parse_png(path)

    def test_rejects_unknown_critical_chunk(self):
        with tempfile.TemporaryDirectory(prefix='cloudscribe-png-') as temporary:
            path = Path(temporary) / 'unknown-critical.png'
            _write_test_png(path, [(b'ABCD', b'')])
            with self.assertRaisesRegex(_VISUAL.EvidenceError, 'unknown critical'):
                _VISUAL.parse_png(path)


    def test_rejects_excessive_visual_directory_entries(self):
        with tempfile.TemporaryDirectory(prefix='cloudscribe-visual-many-') as temporary:
            root = Path(temporary)
            for index in range(_VISUAL.MAX_EVIDENCE_ENTRIES + 1):
                (root / f'entry-{index}.log').write_text('', encoding='utf-8')
            with self.assertRaisesRegex(_VISUAL.EvidenceError, 'more than'):
                _VISUAL.bounded_directory_entries(root)

    def test_rejects_stale_visual_evidence_timestamp(self):
        now = _VISUAL.datetime(2026, 7, 23, 12, 0, tzinfo=_VISUAL.timezone.utc)
        stale = (now - _VISUAL.MAX_EVIDENCE_AGE - _VISUAL.timedelta(seconds=1)).isoformat()
        with self.assertRaisesRegex(_VISUAL.EvidenceError, 'stale'):
            _VISUAL.validate_generated_at(stale, now)

    def test_accepts_fresh_visual_evidence_timestamp(self):
        now = _VISUAL.datetime(2026, 7, 23, 12, 0, tzinfo=_VISUAL.timezone.utc)
        fresh = (now - _VISUAL.timedelta(minutes=5)).isoformat()
        self.assertEqual(_VISUAL.validate_generated_at(fresh, now).tzinfo, now.tzinfo)

    def test_rejects_symbolic_link_png(self):
        with tempfile.TemporaryDirectory(prefix='cloudscribe-png-link-') as temporary:
            root = Path(temporary)
            target = root / 'target.png'
            _write_test_png(target)
            link = root / 'linked.png'
            try:
                link.symlink_to(target.name)
            except (OSError, NotImplementedError) as exc:
                self.skipTest(f'symbolic links unavailable: {exc}')
            with self.assertRaisesRegex(_VISUAL.EvidenceError, 'symbolic-link'):
                _VISUAL.parse_png(link)


    @staticmethod
    def truthful_capture_manifest():
        return {
            'capture_surface': _VISUAL.EXPECTED_CAPTURE_SURFACE,
            'capture_bitmap_dpi_x': _VISUAL.EXPECTED_CAPTURE_BITMAP_DPI,
            'capture_bitmap_dpi_y': _VISUAL.EXPECTED_CAPTURE_BITMAP_DPI,
            'typography_scale_method': _VISUAL.EXPECTED_TYPOGRAPHY_SCALE_METHOD,
            'operating_system_text_scale_verified': False,
            'mixed_dpi_verified': False,
            'windows_accessibility_verified': False,
        }

    def test_accepts_truthful_render_target_capture_boundary(self):
        _VISUAL.validate_capture_truth_boundary(self.truthful_capture_manifest())
        case = {
            'EditorFocused': True,
            'EditorVisualAudit': {
                'Focused': True,
                'Foreground': '#241D2E',
                'SurfaceBackground': '#FFFDF8',
                'Caret': '#241D2E',
                'SelectionBackground': '#FFE6A3',
                'SelectionForeground': '#241D2E',
                'PlaceholderForeground': '#5F5668',
            }
        }
        _VISUAL.validate_editor_visual_audit(case, 'paper-focused')
        case['EditorVisualAudit']['Focused'] = False
        with self.assertRaisesRegex(_VISUAL.EvidenceError, 'editor actual focus'):
            _VISUAL.validate_editor_visual_audit(case, 'focus-metadata-mismatch')
        case['EditorVisualAudit']['Focused'] = True
        case['EditorVisualAudit']['SurfaceBackground'] = '#100E22'
        with self.assertRaisesRegex(_VISUAL.EvidenceError, 'editor text contrast'):
            _VISUAL.validate_editor_visual_audit(case, 'dark-on-dark-focused')

    def test_rejects_mixed_dpi_overclaim_from_render_target_capture(self):
        manifest = self.truthful_capture_manifest()
        manifest['mixed_dpi_verified'] = True
        with self.assertRaisesRegex(_VISUAL.EvidenceError, 'mixed_dpi_verified'):
            _VISUAL.validate_capture_truth_boundary(manifest)

class PhysicalDirectoryToolTests(unittest.TestCase):
    def test_creates_nested_physical_directory(self):
        with tempfile.TemporaryDirectory(prefix='cloudscribe-physical-directory-') as temporary:
            target = Path(temporary) / 'new' / 'nested'
            prepared = _PHYSICAL_DIRECTORY.ensure_physical_directory(
                target,
                label='test output',
                require_empty=True,
            )
            self.assertEqual(prepared, target.absolute())
            self.assertTrue(target.is_dir())

    def test_rejects_symbolic_link_ancestor_before_creating_leaf(self):
        with tempfile.TemporaryDirectory(prefix='cloudscribe-physical-directory-link-') as temporary:
            root = Path(temporary)
            physical = root / 'physical'
            physical.mkdir()
            linked = root / 'linked'
            try:
                linked.symlink_to(physical.name, target_is_directory=True)
            except (OSError, NotImplementedError) as exc:
                self.skipTest(f'symbolic links unavailable: {exc}')
            target = linked / 'nested'
            with self.assertRaisesRegex(_PHYSICAL_DIRECTORY.DirectoryPolicyError, 'symbolic-link'):
                _PHYSICAL_DIRECTORY.ensure_physical_directory(target, label='test output')
            self.assertFalse((physical / 'nested').exists())

    def test_rejects_forbidden_repository_descendant(self):
        with tempfile.TemporaryDirectory(prefix='cloudscribe-physical-directory-forbidden-') as temporary:
            root = Path(temporary)
            target = root / 'evidence'
            with self.assertRaisesRegex(_PHYSICAL_DIRECTORY.DirectoryPolicyError, 'forbidden root'):
                _PHYSICAL_DIRECTORY.ensure_physical_directory(
                    target,
                    label='test output',
                    forbidden_roots=(root,),
                )
            self.assertFalse(target.exists())

    def test_rejects_nonempty_directory_without_deleting_content(self):
        with tempfile.TemporaryDirectory(prefix='cloudscribe-physical-directory-nonempty-') as temporary:
            root = Path(temporary)
            preserved = root / 'preserved.txt'
            preserved.write_text('keep', encoding='utf-8')
            with self.assertRaisesRegex(_PHYSICAL_DIRECTORY.DirectoryPolicyError, 'must be empty'):
                _PHYSICAL_DIRECTORY.ensure_physical_directory(
                    root,
                    label='test output',
                    require_empty=True,
                )
            self.assertEqual(preserved.read_text(encoding='utf-8'), 'keep')

class ZBoundedProcessRunnerTests(unittest.TestCase):
    def run_runner(self, arguments):
        environment = os.environ.copy()
        environment['PYTHONDONTWRITEBYTECODE'] = '1'
        return subprocess.run(
            [sys.executable, str(RUNNER), *arguments],
            cwd=ROOT,
            text=True,
            capture_output=True,
            check=False,
            env=environment,
            timeout=30,
        )

    def test_accepts_bounded_successful_process(self):
        # Repeat intentionally tiny commands to exercise the Windows race where the child
        # can exit before AssignProcessToJobObject observes it. A completed child is already
        # bounded and must not be misreported as a runner failure.
        with tempfile.TemporaryDirectory(prefix='cloudscribe-runner-') as temporary:
            root = Path(temporary)
            for index in range(20):
                output = root / f'stdout-{index}.log'
                result = self.run_runner([
                    '--timeout-seconds', '2',
                    '--max-output-bytes', '1024',
                    '--stdout-file', str(output),
                    '--', sys.executable, '-c', "print('ok')",
                ])
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertEqual(output.read_text(encoding='utf-8').strip(), 'ok')

    def test_rejects_timeout(self):
        result = self.run_runner([
            '--timeout-seconds', '0.1',
            '--max-output-bytes', '1024',
            '--', sys.executable, '-c', 'import time; time.sleep(2)',
        ])
        self.assertEqual(result.returncode, 124)
        self.assertIn('exceeded', result.stderr)

    @staticmethod
    def process_exists(process_id):
        if os.name == 'nt':
            result = subprocess.run(
                ['tasklist', '/FI', f'PID eq {process_id}', '/NH'],
                text=True,
                capture_output=True,
                check=False,
                timeout=10,
            )
            return str(process_id) in result.stdout
        try:
            os.kill(process_id, 0)
            return True
        except ProcessLookupError:
            return False
        except PermissionError:
            return True

    @staticmethod
    def force_remove_process(process_id):
        if os.name == 'nt':
            subprocess.run(
                ['taskkill', '/PID', str(process_id), '/T', '/F'],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                check=False,
                timeout=10,
            )
            return
        try:
            os.kill(process_id, 9)
        except ProcessLookupError:
            pass

    @unittest.skipUnless(os.name == "nt", "Windows process-tree cleanup acceptance")
    def test_terminates_descendant_that_keeps_output_pipe_open(self):
        script = (
            'import subprocess,sys; '
            'child=subprocess.Popen([sys.executable,"-c","import time; time.sleep(60)"]); '
            'print(child.pid, flush=True)'
        )
        result = self.run_runner([
            '--timeout-seconds', '20',
            '--max-output-bytes', '4096',
            '--tee',
            '--', sys.executable, '-c', script,
        ])
        self.assertEqual(result.returncode, 126, result.stdout + result.stderr)
        self.assertIn('output reader did not terminate', result.stderr)
        process_id = int(result.stdout.strip())
        try:
            deadline = time.monotonic() + 3
            while self.process_exists(process_id) and time.monotonic() < deadline:
                time.sleep(0.05)
            self.assertFalse(self.process_exists(process_id), f'descendant {process_id} leaked')
        finally:
            if self.process_exists(process_id):
                self.force_remove_process(process_id)

    def test_rejects_output_cap(self):
        result = self.run_runner([
            '--timeout-seconds', '2',
            '--max-output-bytes', '128',
            '--', sys.executable, '-c', "import sys; sys.stdout.write('x' * 4096)",
        ])
        self.assertEqual(result.returncode, 125)
        self.assertIn('exceeded', result.stderr)

    def test_rejects_symbolic_link_output(self):
        with tempfile.TemporaryDirectory(prefix='cloudscribe-runner-link-') as temporary:
            root = Path(temporary)
            target = root / 'target.log'
            target.write_text('', encoding='utf-8')
            link = root / 'stdout.log'
            try:
                link.symlink_to(target.name)
            except (OSError, NotImplementedError) as exc:
                self.skipTest(f'symbolic links unavailable: {exc}')
            result = self.run_runner([
                '--timeout-seconds', '2',
                '--stdout-file', str(link),
                '--', sys.executable, '-c', "print('no')",
            ])
            self.assertEqual(result.returncode, 2)
            self.assertIn('symbolic-link', result.stderr)

    def test_rejects_non_finite_timeout(self):
        for value in ('nan', 'inf', '-inf'):
            with self.subTest(value=value):
                result = self.run_runner([
                    f'--timeout-seconds={value}',
                    '--', sys.executable, '-c', "print('no')",
                ])
                self.assertEqual(result.returncode, 2)
                self.assertIn('finite number greater than zero', result.stderr)

    def test_rejects_same_stdout_and_stderr_file(self):
        with tempfile.TemporaryDirectory(prefix='cloudscribe-runner-same-') as temporary:
            output = Path(temporary) / 'combined.log'
            result = self.run_runner([
                '--timeout-seconds', '2',
                '--stdout-file', str(output),
                '--stderr-file', str(output),
                '--', sys.executable, '-c', "print('no')",
            ])
            self.assertEqual(result.returncode, 2)
            self.assertIn('distinct output files', result.stderr)

    def test_rejects_symbolic_link_ancestor(self):
        with tempfile.TemporaryDirectory(prefix='cloudscribe-runner-ancestor-') as temporary:
            root = Path(temporary)
            physical = root / 'physical'
            physical.mkdir()
            linked = root / 'linked'
            try:
                linked.symlink_to(physical.name, target_is_directory=True)
            except (OSError, NotImplementedError) as exc:
                self.skipTest(f'symbolic links unavailable: {exc}')
            result = self.run_runner([
                '--timeout-seconds', '2',
                '--stdout-file', str(linked / 'stdout.log'),
                '--', sys.executable, '-c', "print('no')",
            ])
            self.assertEqual(result.returncode, 2)
            self.assertIn('symbolic-link ancestor', result.stderr)
            self.assertFalse((physical / 'stdout.log').exists())

    def test_rejects_existing_regular_output_without_overwrite(self):
        with tempfile.TemporaryDirectory(prefix='cloudscribe-runner-existing-') as temporary:
            output = Path(temporary) / 'stdout.log'
            output.write_text('preserve', encoding='utf-8')
            result = self.run_runner([
                '--timeout-seconds', '2',
                '--stdout-file', str(output),
                '--', sys.executable, '-c', "print('replace')",
            ])
            self.assertEqual(result.returncode, 2)
            self.assertIn('refusing to overwrite evidence', result.stderr)
            self.assertEqual(output.read_text(encoding='utf-8'), 'preserve')

    def test_creates_nested_physical_output_parent(self):
        with tempfile.TemporaryDirectory(prefix='cloudscribe-runner-nested-') as temporary:
            output = Path(temporary) / 'new' / 'nested' / 'stdout.log'
            result = self.run_runner([
                '--timeout-seconds', '2',
                '--stdout-file', str(output),
                '--', sys.executable, '-c', "print('nested')",
            ])
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertEqual(output.read_text(encoding='utf-8').strip(), 'nested')

class DotnetSdkVersionPolicyTests(unittest.TestCase):
    @staticmethod
    def run_validator(required: str, actual: str, msbuild: str):
        return _run_tool(
            "verify_dotnet_sdk_version.py",
            "--required", required,
            "--actual", actual,
            "--msbuild", msbuild,
        )

    def test_accepts_exact_pinned_sdk_and_matching_msbuild(self):
        result = self.run_validator("10.0.302", "10.0.302", "18.6.11.33009")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("exact .NET SDK/toolchain policy satisfied", result.stdout)

    def test_rejects_later_sdk_patch_even_in_same_feature_band(self):
        result = self.run_validator("10.0.302", "10.0.303", "18.6.11.33009")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("exact .NET SDK mismatch", result.stderr)

    def test_rejects_lower_sdk_patch(self):
        result = self.run_validator("10.0.302", "10.0.301", "18.6.11.33009")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("exact .NET SDK mismatch", result.stderr)

    def test_rejects_different_feature_band(self):
        result = self.run_validator("10.0.302", "10.0.400", "18.9.1")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("exact .NET SDK mismatch", result.stderr)

    def test_rejects_prerelease_sdk_that_would_compare_numerically_equal(self):
        result = self.run_validator("10.0.302", "10.0.302-preview.1", "18.6.11.33009")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("stable three-component .NET SDK version", result.stderr)

    def test_accepts_msbuild_18_6_floor_for_10_0_3xx(self):
        result = self.run_validator("10.0.302", "10.0.302", "18.6.0")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_rejects_msbuild_below_18_6_for_10_0_3xx(self):
        result = self.run_validator("10.0.302", "10.0.302", "18.5.99")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("requires MSBuild 18.6 or later", result.stderr)

    def test_rejects_malformed_msbuild_version(self):
        result = self.run_validator("10.0.302", "10.0.302", "Microsoft Build Engine")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("MSBuild version is not parseable", result.stderr)


class RepositoryVerifierTests(unittest.TestCase):
    def test_accepts_current_release_source(self):
        result = _run_tool("verify_repository.py")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_rejects_non_exact_global_json_roll_forward(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-repository-policy-") as temporary:
            root = _copy_source(Path(temporary))
            payload = json.loads((root / "global.json").read_text(encoding="utf-8"))
            payload["sdk"]["rollForward"] = "latestPatch"
            (root / "global.json").write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
            result = _run_tool("verify_repository.py", cwd=root)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("rollForward must be 'disable'", result.stderr)

    def test_rejects_prerelease_sdk_selection(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-repository-prerelease-") as temporary:
            root = _copy_source(Path(temporary))
            payload = json.loads((root / "global.json").read_text(encoding="utf-8"))
            payload["sdk"]["allowPrerelease"] = True
            (root / "global.json").write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
            result = _run_tool("verify_repository.py", cwd=root)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("allowPrerelease must be false", result.stderr)

    def test_rejects_false_self_contained_context_claim(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-repository-context-") as temporary:
            root = _copy_source(Path(temporary))
            path = root / "SESSION_STATE.json"
            payload = json.loads(path.read_text(encoding="utf-8"))
            payload["controlling_context_self_contained"] = True
            payload["controlling_context_manifest"] = "governance/MISSING.json"
            payload["immutable_master_package_present"] = True
            path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
            result = _run_tool("verify_repository.py", cwd=root)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("self-contained controlling context", result.stderr)

    def test_rejects_missing_auxiliary_python_test_suite(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-repository-tests-") as temporary:
            root = _copy_source(Path(temporary))
            (root / "tests/test_verification_tools.py").unlink()
            result = _run_tool("verify_repository.py", cwd=root)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("tests/test_verification_tools.py", result.stderr)


class Stage2SourceContractTests(unittest.TestCase):
    def test_current_checkpoint_preserves_stage2_source_contract(self):
        result = _run_tool("verify_stage2_source.py")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_rejects_paper_text_box_theme_regression(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-stage2-theme-") as temporary:
            root = _copy_source(Path(temporary))
            path = root / "src/CloudScribe.App/MainWindow.axaml"
            text = path.read_text(encoding="utf-8")
            needle = 'TargetType="controls:PaperTextBox"'
            self.assertIn(needle, text)
            path.write_text(text.replace(needle, 'TargetType="TextBox"', 1), encoding="utf-8")
            result = _run_tool("verify_stage2_source.py", cwd=root)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("derived PaperTextBox control theme", result.stderr)

    def test_rejects_unverified_user_clicked_editor_claim(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-stage2-user-claim-") as temporary:
            root = _copy_source(Path(temporary))
            path = root / "SESSION_STATE.json"
            payload = json.loads(path.read_text(encoding="utf-8"))
            payload["current_stage"] = 2
            payload["repository_version"] = "0.3.48-stage2-repair-in-progress"
            payload["stage2_promoted"] = False
            payload["stage2_promotion_blocked"] = True
            payload["stage2_manual_visual_acceptance"] = False
            payload["stage2_user_clicked_editor_retest"] = True
            path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
            result = _run_tool("verify_stage2_source.py", cwd=root)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("real-user Stage 2 acceptance pending", result.stderr)

    def test_all_deterministic_material_regression_shards_pass(self):
        result = _run_tool("run_python_regression_shards.py", "--all", timeout=180)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("153/153 material regression checks", result.stdout)


class Stage4SourceContractTests(unittest.TestCase):
    def test_current_checkpoint_passes_stage4_foundation_contract(self):
        result = _run_tool("verify_stage4_source.py")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_rejects_false_exact_catalog_admission_claim(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-stage4-catalog-claim-") as temporary:
            root = _copy_source(Path(temporary))
            path = root / "SESSION_STATE.json"
            payload = json.loads(path.read_text(encoding="utf-8"))
            payload["stage4_catalog_contract_admitted"] = True
            path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
            result = _run_tool("verify_stage4_source.py", cwd=root)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("must not pretend", result.stderr)

    def test_rejects_wrong_stage3_promotion_binding(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-stage4-lineage-") as temporary:
            root = _copy_source(Path(temporary))
            path = root / "SESSION_STATE.json"
            payload = json.loads(path.read_text(encoding="utf-8"))
            payload["stage3_promoted_commit"] = "0" * 40
            path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
            result = _run_tool("verify_stage4_source.py", cwd=root)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("authoritative Stage 3 promoted evidence", result.stderr)

    def test_rejects_relaxed_strict_json_contract(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-stage4-json-contract-") as temporary:
            root = _copy_source(Path(temporary))
            path = root / "src/CloudScribe.Infrastructure/Pricing/StrictJsonObjectReader.cs"
            text = path.read_text(encoding="utf-8")
            path.write_text(text.replace("AllowTrailingCommas = false", "AllowTrailingCommas = true"), encoding="utf-8")
            result = _run_tool("verify_stage4_source.py", cwd=root)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("AllowTrailingCommas = false", result.stderr)

    def test_rejects_false_batch2_admission_state(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-stage4-batch2-admission-") as temporary:
            root = _copy_source(Path(temporary))
            path = root / "SESSION_STATE.json"
            payload = json.loads(path.read_text(encoding="utf-8"))
            payload["stage4_foundation_batch2_admitted"] = False
            path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
            result = _run_tool("verify_stage4_source.py", cwd=root)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("authoritative successful Batch 2 admission", result.stderr)

    def test_rejects_silent_catalog_activation_regression(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-stage4-silent-activation-") as temporary:
            root = _copy_source(Path(temporary))
            path = root / "src/CloudScribe.Infrastructure/Pricing/EfPricingCatalogHistoryStore.cs"
            text = path.read_text(encoding="utf-8")
            path.write_text(text.replace("explicit user confirmation", "implicit activation", 1), encoding="utf-8")
            result = _run_tool("verify_stage4_source.py", cwd=root)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("explicit user confirmation", result.stderr)

    def test_rejects_pricing_override_separation_regression(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-stage4-override-separation-") as temporary:
            root = _copy_source(Path(temporary))
            path = root / "SESSION_STATE.json"
            payload = json.loads(path.read_text(encoding="utf-8"))
            payload["stage4_pricing_contract_overrides_separate"] = False
            path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
            result = _run_tool("verify_stage4_source.py", cwd=root)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("separate from upstream catalog truth", result.stderr)

    def test_rejects_quota_observation_contract_regression(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-stage4-quota-contract-") as temporary:
            root = _copy_source(Path(temporary))
            path = root / "src/CloudScribe.Providers.Abstractions/IProviderQuotaSource.cs"
            path.write_text("namespace CloudScribe.Providers.Abstractions;\n", encoding="utf-8")
            result = _run_tool("verify_stage4_source.py", cwd=root)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("GetQuotaObservationsAsync", result.stderr)

    def test_rejects_database_secret_persistence_claim(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-stage4-secret-persistence-") as temporary:
            root = _copy_source(Path(temporary))
            path = root / "SESSION_STATE.json"
            payload = json.loads(path.read_text(encoding="utf-8"))
            payload["stage4_provider_credentials_persisted_in_database"] = True
            path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
            result = _run_tool("verify_stage4_source.py", cwd=root)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("must never be persisted", result.stderr)

    def test_rejects_default_provider_account_selection_claim(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-stage4-default-account-") as temporary:
            root = _copy_source(Path(temporary))
            path = root / "SESSION_STATE.json"
            payload = json.loads(path.read_text(encoding="utf-8"))
            payload["stage4_provider_default_account_selected"] = True
            path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
            result = _run_tool("verify_stage4_source.py", cwd=root)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("must not silently select", result.stderr)

    def test_rejects_wrong_batch5_admission_binding(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-stage4-batch5-binding-") as temporary:
            root = _copy_source(Path(temporary))
            path = root / "SESSION_STATE.json"
            payload = json.loads(path.read_text(encoding="utf-8"))
            payload["stage4_foundation_batch5_commit"] = "0" * 40
            path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
            result = _run_tool("verify_stage4_source.py", cwd=root)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("authoritative Batch 5 Windows admission evidence", result.stderr)

    def test_rejects_unresolved_tax_credit_fx_guessing_claim(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-stage4-pricing-assumption-") as temporary:
            root = _copy_source(Path(temporary))
            path = root / "SESSION_STATE.json"
            payload = json.loads(path.read_text(encoding="utf-8"))
            payload["stage4_pricing_unresolved_tax_credit_fx_guessed"] = True
            path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
            result = _run_tool("verify_stage4_source.py", cwd=root)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("must not guess unresolved tax, credit, or FX", result.stderr)

    def test_rejects_wrong_batch6_admission_binding(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-stage4-batch6-binding-") as temporary:
            root = _copy_source(Path(temporary))
            path = root / "SESSION_STATE.json"
            payload = json.loads(path.read_text(encoding="utf-8"))
            payload["stage4_foundation_batch6_commit"] = "0" * 40
            path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
            result = _run_tool("verify_stage4_source.py", cwd=root)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("authoritative Batch 6 Windows admission evidence", result.stderr)

    def test_rejects_pricing_contract_hardening_regression(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-stage4-pricing-hardening-") as temporary:
            root = _copy_source(Path(temporary))
            path = root / "SESSION_STATE.json"
            payload = json.loads(path.read_text(encoding="utf-8"))
            payload["stage4_pricing_modifier_set_validated"] = False
            path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
            result = _run_tool("verify_stage4_source.py", cwd=root)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("reject malformed modifier sets", result.stderr)



    def test_rejects_wrong_batch7_admission_evidence(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = _copy_source(Path(temporary))
            path = root / "SESSION_STATE.json"
            payload = json.loads(path.read_text(encoding="utf-8"))
            payload["stage4_foundation_batch7_commit"] = "0" * 40
            path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
            result = _run_tool("verify_stage4_source.py", cwd=root)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("authoritative Batch 7 Windows admission evidence", result.stderr)

    def test_rejects_built_in_pricing_trust_key(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = _copy_source(Path(temporary))
            path = root / "src/CloudScribe.App/appsettings.json"
            payload = json.loads(path.read_text(encoding="utf-8"))
            payload["CloudScribe"]["PricingCatalogTrust"]["TrustedEd25519PublicKeys"]["forbidden-built-in"] = "AA=="
            path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
            result = _run_tool("verify_stage4_source.py", cwd=root)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("zero built-in trusted Ed25519 public keys", result.stderr)

    def test_rejects_false_private_signing_key_absence_claim(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = _copy_source(Path(temporary))
            path = root / "SESSION_STATE.json"
            payload = json.loads(path.read_text(encoding="utf-8"))
            payload["stage4_private_catalog_signing_keys_present"] = True
            path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
            result = _run_tool("verify_stage4_source.py", cwd=root)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("must never contain catalog private signing keys", result.stderr)

class Stage2EvidenceInventoryCliTests(unittest.TestCase):
    @staticmethod
    def create_valid_inventory(root: Path) -> None:
        scans = root / "package-scans"
        visual = root / "visual"
        tests = root / "test-results"
        logs = root / "logs"
        for directory in (scans, visual, tests, logs):
            directory.mkdir(parents=True, exist_ok=True)

        for index in range(18):
            (scans / f"{index:02d}.json").write_text("{}\n", encoding="utf-8")

        dimensions = [
            (1600, 1000), (1366, 900), (1100, 820), (900, 760), (720, 720),
            (1600, 1100), (1280, 960), (1024, 800), (850, 750), (700, 700),
            (1500, 1000), (1400, 980), (1300, 940), (1200, 900), (1000, 800),
            (800, 720), (600, 700),
        ]
        for index, (width, height) in enumerate(dimensions, 1):
            data = bytearray(b"\x89PNG\r\n\x1a\n")
            data.extend(b"\x00" * 8)
            data.extend(struct.pack(">II", width, height))
            data.extend(b"\x00" * 16)
            (visual / f"{index:02d}.png").write_bytes(bytes(data))
        (visual / "manifest.json").write_text(json.dumps({"Cases": [{} for _ in range(17)]}), encoding="utf-8")

        totals = [6, 8, 64, 69]
        for index, total in enumerate(totals):
            directory = tests / str(index)
            directory.mkdir()
            trx = f'<TestRun><ResultSummary><Counters total="{total}" failed="0" skipped="0" /></ResultSummary></TestRun>'
            (directory / "stage2-tests.trx").write_text(trx, encoding="utf-8")

        records = [json.dumps({"sequence": index, "status": "passed"}) for index in range(1, 88)]
        (logs / "command-ledger.jsonl").write_text("\n".join(records) + "\n", encoding="utf-8")

    def test_accepts_current_18_17_4_147_87_inventory_shape(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-evidence-inventory-") as temporary:
            root = Path(temporary)
            self.create_valid_inventory(root)
            result = _run_tool("verify_stage2_evidence_inventory.py", str(root))
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertIn("147 tests", result.stdout)
            self.assertIn("87 completed ledger steps", result.stdout)

    def test_rejects_wrong_test_total(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-evidence-total-") as temporary:
            root = Path(temporary)
            self.create_valid_inventory(root)
            path = root / "test-results/0/stage2-tests.trx"
            path.write_text('<TestRun><ResultSummary><Counters total="5" failed="0" skipped="0" /></ResultSummary></TestRun>', encoding="utf-8")
            result = _run_tool("verify_stage2_evidence_inventory.py", str(root))
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("unexpected .NET test evidence totals", result.stderr)

    def test_rejects_ledger_gap(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-evidence-ledger-") as temporary:
            root = Path(temporary)
            self.create_valid_inventory(root)
            records = [json.dumps({"sequence": index, "status": "passed"}) for index in range(1, 88) if index != 44]
            (root / "logs/command-ledger.jsonl").write_text("\n".join(records) + "\n", encoding="utf-8")
            result = _run_tool("verify_stage2_evidence_inventory.py", str(root))
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("sequence is not contiguous", result.stderr)

    def test_rejects_linked_evidence(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-evidence-link-") as temporary:
            root = Path(temporary)
            self.create_valid_inventory(root)
            target = root / "package-scans/00.json"
            link = root / "package-scans/linked.json"
            try:
                link.symlink_to(target.name)
            except (OSError, NotImplementedError) as exc:
                self.skipTest(f"symbolic links unavailable: {exc}")
            result = _run_tool("verify_stage2_evidence_inventory.py", str(root))
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("link/junction", result.stderr)


class SourceArchiveCliTests(unittest.TestCase):
    def test_current_source_archive_is_deterministic_and_no_overwrite(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-source-archive-a-") as first_temp, tempfile.TemporaryDirectory(prefix="cloudscribe-source-archive-b-") as second_temp:
            first = _run_tool("create_source_archive.py", "--output-directory", first_temp, timeout=180)
            second = _run_tool("create_source_archive.py", "--output-directory", second_temp, timeout=180)
            self.assertEqual(first.returncode, 0, first.stdout + first.stderr)
            self.assertEqual(second.returncode, 0, second.stdout + second.stderr)
            first_zip = next(Path(first_temp).glob("*.zip"))
            second_zip = next(Path(second_temp).glob("*.zip"))
            self.assertEqual(hashlib.sha256(first_zip.read_bytes()).hexdigest(), hashlib.sha256(second_zip.read_bytes()).hexdigest())
            sidecar = Path(str(first_zip) + ".sha256").read_text(encoding="utf-8").strip()
            self.assertTrue(sidecar.startswith(hashlib.sha256(first_zip.read_bytes()).hexdigest() + " *"))
            overwrite = _run_tool("create_source_archive.py", "--output-directory", first_temp, timeout=180)
            self.assertNotEqual(overwrite.returncode, 0)
            self.assertIn("no-overwrite publication refused", overwrite.stderr)

    def test_archive_name_must_match_internal_version(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-source-archive-name-") as temporary:
            result = _run_tool("create_source_archive.py", "--output-directory", temporary, "--name", "CloudScribe_wrong", timeout=180)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("archive name must match source identity exactly", result.stderr)


if __name__ == "__main__":
    unittest.main()
