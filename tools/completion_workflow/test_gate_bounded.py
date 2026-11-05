"""Bounded bridge input tests; disposable files and in-memory streams only."""
from contextlib import redirect_stderr, redirect_stdout
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import gate_cli as g


class TrackingStream(io.BytesIO):
    def __init__(self, data, *, grow=False):
        super().__init__(data)
        self.requests = []
        self.grow = grow

    def read(self, size=-1):
        self.requests.append(size)
        result = super().read(size)
        if self.grow and len(self.requests) == 1:
            position = self.tell()
            self.seek(0, io.SEEK_END)
            self.write(b'!')
            self.seek(position)
        return result


class BoundedInputTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name).resolve() / 'synthetic-input.bin'

    def test_empty_exact_limit_and_limit_plus_one(self):
        for size in (0, 7, 8):
            self.path.write_bytes(b'x' * size)
            with self.subTest(size=size):
                if size > 7:
                    with self.assertRaisesRegex(ValueError, '^Oversized$'):
                        g.bounded(self.path, 7)
                else:
                    self.assertEqual(b'x' * size, g.bounded(self.path, 7))
        self.path.write_bytes(b'')
        self.assertEqual(b'', g.bounded(self.path, 0))
        self.path.write_bytes(b'x')
        with self.assertRaisesRegex(ValueError, '^Oversized$'):
            g.bounded(self.path, 0)

    def test_large_bound_uses_small_reads_and_preserves_exact_bytes(self):
        data = bytes(range(256)) * 1025
        stream = TrackingStream(data)
        with patch.object(g.Path, 'open', return_value=stream):
            self.assertEqual(data, g.bounded(self.path, 512 * 1024 * 1024))
        self.assertGreater(len(stream.requests), 1)
        self.assertTrue(all(0 < size <= 65536 for size in stream.requests))

    def test_growth_past_limit_is_detected_before_returning(self):
        stream = TrackingStream(b'x' * 65536, grow=True)
        with patch.object(g.Path, 'open', return_value=stream):
            with self.assertRaisesRegex(ValueError, '^Oversized$'):
                g.bounded(self.path, 65536)
        self.assertGreaterEqual(len(stream.requests), 2)
        self.assertTrue(all(0 < size <= 65536 for size in stream.requests))

    def test_short_reads_are_accumulated_until_actual_eof(self):
        class ShortStream(TrackingStream):
            def read(self, size=-1):
                return super().read(min(size, 3))
        stream = ShortStream(b'abcdefghij')
        with patch.object(g.Path, 'open', return_value=stream):
            self.assertEqual(b'abcdefghij', g.bounded(self.path, 10))

    def test_linked_file_or_ancestor_is_rejected_before_open(self):
        for linked in (self.path, self.path.parent):
            with self.subTest(linked_component='file' if linked == self.path else 'parent'):
                with patch.object(g.Path, 'is_symlink', lambda path: path == linked), \
                        patch.object(g.Path, 'is_junction', return_value=False), \
                        patch.object(g.Path, 'open') as opened:
                    with self.assertRaisesRegex(ValueError, '^LinkedPath$'):
                        g.bounded(self.path, 100)
                    opened.assert_not_called()

    def test_failure_exports_no_input_content_or_exception_prose(self):
        canary = 'SYNTHETIC_PRIVATE_CANARY_NEVER_LOG'
        out = io.StringIO(); err = io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            with patch.object(g.Path, 'open', side_effect=OSError(canary)):
                with self.assertRaises(OSError) as caught:
                    g.bounded(self.path, 100)
            packet = g.rejection_packet(caught.exception, {'Oversized': 'Storage'})
        self.assertNotIn(canary, json.dumps(packet))
        self.assertEqual('', out.getvalue())
        self.assertEqual('', err.getvalue())
        self.assertEqual('Storage', packet['reason'])


if __name__ == '__main__':
    unittest.main()
