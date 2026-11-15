"""Offline synthetic boundary regression checks; no installed logs or providers."""
import errno
import io
import json
from pathlib import Path
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import patch

import review_google_structure_history as reader


def row(status=200, padding=0):
    return json.dumps({"EventId": 1611, "SafeFields": {"Correlation": "a" * 32, "HttpStatus": str(status)},
                       "Message": "PRIVATE_CANARY" + "x" * padding}).encode()


class StreamingHistoryTests(unittest.TestCase):
    def test_realistic_large_history_is_read_without_whole_file_materialization(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            data = (row(200, 630) + b"\n") * 10000 + row(400) + b"\n"
            self.assertGreater(len(data), 7_600_000)
            (root / "history.jsonl").write_bytes(data)
            with patch.object(Path, "read_bytes", side_effect=AssertionError("whole file read")):
                result = reader.review(root)
            self.assertEqual(result["Coverage"]["OmittedFiles"], 0)
            self.assertEqual(result["Coverage"]["ReadBytes"], len(data))
            self.assertEqual(result["Requests"][0]["HttpStatus"], 400)
            self.assertNotIn("PRIVATE_CANARY", json.dumps(result))

    def test_tail_budget_retains_latest_complete_record(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            older, newest = row(200), row(400)
            data = older + b"\n" + newest + b"\n"
            (root / "history.jsonl").write_bytes(data)
            result = reader.review(root, max_bytes=len(newest) + 5)
            self.assertEqual(result["Requests"][0]["HttpStatus"], 400)
            self.assertEqual(result["Coverage"]["PartiallyScannedFiles"], 1)
            self.assertEqual(result["Coverage"]["PartialBoundaryLines"], 1)
            self.assertEqual(result["Coverage"]["OmittedBytes"], len(data) - len(newest) - 5)

    def test_exact_line_boundary_does_not_drop_record(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            newest = row(400) + b"\n"
            (root / "history.jsonl").write_bytes(row(200) + b"\n" + newest)
            result = reader.review(root, max_bytes=len(newest))
            self.assertEqual(result["Requests"][0]["HttpStatus"], 400)
            self.assertEqual(result["Coverage"]["PartialBoundaryLines"], 0)

    def test_oversized_line_discard_preserves_neighboring_records(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "history.jsonl").write_bytes(row(200) + b"\n" + row(500, 200000) + b"\n" + b"not-json\n" + row(400))
            result = reader.review(root, max_line_bytes=256)
            self.assertEqual(result["Requests"][0]["HttpStatus"], 400)
            self.assertEqual(result["Coverage"]["OversizedLines"], 1)
            self.assertEqual(result["Coverage"]["MalformedLines"], 1)
            self.assertNotIn("PRIVATE_CANARY", json.dumps(result))

    def test_reverse_iterator_matches_splitlines_across_blocks(self):
        for data in (b"", b"\n", b"\n\n", b"abc", b"abc\n", b"abc\r\n", b"first\rlast\r", b"x" * 65535 + b"\r\n" + b"y" * 65535 + b"\r\n", b"x" * 65536 + b"\nlast", b"x" * 65535 + b"\n" + b"y" * 65540 + b"\n"):
            with self.subTest(length=len(data)):
                coverage = {"OversizedLines": 0, "PartialBoundaryLines": 0}
                actual = list(reader.reverse_lines(io.BytesIO(data), 0, len(data), 200000, coverage))
                self.assertEqual(actual, list(reversed(data.splitlines())))

    def test_in_place_source_change_fails_without_path_disclosure(self):
        with tempfile.TemporaryDirectory(prefix="PRIVATE_PATH_") as folder:
            root = Path(folder)
            p = root / "history.jsonl"
            p.write_bytes(row(200))
            original = reader.reverse_lines
            def changed(*args):
                yield from original(*args)
                p.write_bytes(row(400))
            with patch.object(reader, "reverse_lines", side_effect=changed):
                with self.assertRaisesRegex(ValueError, "Log changed") as error:
                    reader.review(root)
            self.assertNotIn("PRIVATE_PATH", str(error.exception))

    def check_api_local_ctime(self, change_fstat=False, change_lstat=False):
        with tempfile.TemporaryDirectory() as folder:
            p = Path(folder) / "history.jsonl"
            data = row(200)
            p.write_bytes(data)
            expected = p.lstat()
            def metadata(ctime):
                fields = {name: getattr(expected, name) for name in
                          ("st_dev", "st_ino", "st_size", "st_mtime_ns", "st_mode")}
                return SimpleNamespace(**fields, st_ctime_ns=ctime)
            opened = metadata(expected.st_ctime_ns + 123456)
            finished = metadata(opened.st_ctime_ns + int(change_fstat))
            path_after = metadata(expected.st_ctime_ns + int(change_lstat))
            coverage = {"OversizedLines": 0, "PartialBoundaryLines": 0}
            with patch.object(reader.os, "fstat", side_effect=[opened, finished]), \
                    patch.object(Path, "lstat", return_value=path_after):
                return list(reader.snapshot_lines(p, 0, expected, 1048576, coverage)), data

    def test_cross_api_ctime_difference_does_not_reject_unchanged_file(self):
        lines, data = self.check_api_local_ctime()
        self.assertEqual(lines, [data])

    def test_fstat_native_ctime_change_still_rejected(self):
        with self.assertRaisesRegex(ValueError, "Log changed"):
            self.check_api_local_ctime(change_fstat=True)

    def test_lstat_native_ctime_change_still_rejected(self):
        with self.assertRaisesRegex(ValueError, "Log changed"):
            self.check_api_local_ctime(change_lstat=True)

    def test_read_error_is_privacy_safe(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "history.jsonl").write_bytes(row())
            with patch.object(reader.os, "open", side_effect=OSError("PRIVATE_TOKEN_PATH")):
                with self.assertRaisesRegex(ValueError, "could not be read safely") as error:
                    reader.review(root)
            self.assertNotIn("PRIVATE_TOKEN", str(error.exception))

    def test_symlink_is_omitted_and_target_not_read(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "target").write_bytes(row())
            try:
                (root / "history.jsonl").symlink_to(root / "target")
            except NotImplementedError:
                self.skipTest("Symbolic-link creation is unavailable on this platform")
            except OSError as error:
                if (error.errno in (errno.EPERM, errno.EACCES, errno.ENOSYS, errno.ENOTSUP)
                        or getattr(error, "winerror", None) == 1314):
                    self.skipTest("Symbolic-link creation is unavailable or not permitted in this test environment")
                raise
            result = reader.review(root)
            self.assertEqual(result["Requests"], [])
            self.assertEqual(result["Coverage"]["OmittedFiles"], 1)

    def test_invalid_limits_rejected(self):
        for field in ("max_files", "max_bytes", "max_line_bytes"):
            for value in (0, -1, True, 2.5):
                with self.assertRaises(ValueError):
                    reader.review(Path("."), **{field: value})


if __name__ == "__main__":
    unittest.main()
