"""Temporary inert controls only; no installation, credentials or provider."""
import tempfile
from pathlib import Path
import unittest
from unittest.mock import patch
import gate_cli as g


class PredecessorSnapshotTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup)
        self.root=Path(self.temp.name);self.install=self.root/'installed';self.app=self.install/'app'
        self.app.mkdir(parents=True);self.previous=self.install/'completion-workflow-gate.json'
        self.previous.write_bytes(b'{"synthetic":"old gate"}')
        self.pin=g.sha(self.previous.read_bytes());self.frozen=self.root/'frozen.json'

    def test_frozen_bytes_survive_actual_installed_slot_replacement(self):
        record=g.freeze_predecessor_config(self.previous,self.pin,self.frozen,self.app)
        self.previous.write_bytes(b'{"synthetic":"new gate"}')
        self.assertEqual(self.pin,g.sha(g.bounded(record['Path'],32768)))
        self.assertNotEqual(self.pin,g.sha(self.previous.read_bytes()))

    def test_installed_or_nested_output_rejects_before_write(self):
        for path in (self.previous,self.install/'nested.json'):
            with self.subTest(path=path),self.assertRaisesRegex(ValueError,'MutablePreviousGatePath'):
                g.freeze_predecessor_config(self.previous,self.pin,path,self.app)
        self.assertEqual(self.pin,g.sha(self.previous.read_bytes()))

    def test_relative_path_and_linked_ancestry_reject(self):
        with self.assertRaisesRegex(ValueError,'PredecessorSnapshotLocation'):
            g.freeze_predecessor_config(self.previous,self.pin,'relative.json',self.app)
        original=Path.is_symlink
        with patch.object(Path,'is_symlink',lambda p:p==self.frozen or original(p)):
            with self.assertRaisesRegex(ValueError,'LinkedPath'):
                g.freeze_predecessor_config(self.previous,self.pin,self.frozen,self.app)
        self.assertFalse(self.frozen.exists())

    def test_existing_snapshot_never_overwritten_and_wrong_pin_rejects(self):
        with self.assertRaisesRegex(ValueError,'PreviousGatePin'):
            g.freeze_predecessor_config(self.previous,'a'*64,self.frozen,self.app)
        self.frozen.write_bytes(b'synthetic retained evidence')
        with self.assertRaises(FileExistsError):g.freeze_predecessor_config(self.previous,self.pin,self.frozen,self.app)
        self.assertEqual(b'synthetic retained evidence',self.frozen.read_bytes())

    def test_source_change_during_write_is_recorded_as_failure(self):
        with patch.object(g.os,'fsync',side_effect=lambda _:self.previous.write_bytes(b'changed')):
            with self.assertRaisesRegex(ValueError,'PredecessorSnapshotChanged'):
                g.freeze_predecessor_config(self.previous,self.pin,self.frozen,self.app)
        self.assertEqual(self.pin,g.sha(self.frozen.read_bytes()))


if __name__=='__main__':unittest.main()
