"""Unknown helper failures retain safe location, never private exception material."""
import json
from pathlib import Path
import subprocess
import sys
import unittest
import gate_cli as g

CANARY='SYNTHETIC_PRIVATE_EXCEPTION_CANARY_DO_NOT_EXPORT'


class FailureEvidenceTests(unittest.TestCase):
    def test_known_bounded_storage_failure_retains_safe_location(self):
        import tempfile
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder)/'synthetic';path.write_bytes(b'xx')
            try:g.bounded(path,1)
            except ValueError as error:packet=g.rejection_packet(error,{'Oversized':'Storage'})
        self.assertEqual('Oversized',packet['code']);self.assertEqual('Storage',packet['reason'])
        self.assertEqual('bridge',packet['failureComponent'])
        self.assertTrue(1<=packet['failureSourceLine']<=10000)
        self.assertNotIn(folder,json.dumps(packet))

    def test_keyerror_uses_last_owned_frame_without_message_or_paths(self):
        class Hostile(dict):
            def __getitem__(self,key):raise KeyError(CANARY)
        try:g.profile_database(Hostile())
        except KeyError as error:packet=g.rejection_packet(error,{})
        self.assertEqual('KeyError',packet['failureExceptionType']);self.assertEqual('bridge',packet['failureComponent'])
        self.assertTrue(1<=packet['failureSourceLine']<=10000)
        self.assertNotIn(CANARY,json.dumps(packet));self.assertNotIn(str(Path.cwd()),json.dumps(packet))
    def test_typeerror_actual_isolated_entrypoint_retains_location(self):
        request={'command':'status','configPath':{CANARY:CANARY}}
        p=subprocess.run([sys.executable,'-I','-S',str(Path(g.__file__))],input=json.dumps(request),text=True,capture_output=True,timeout=20)
        self.assertEqual(1,p.returncode);self.assertEqual('',p.stderr);packet=json.loads(p.stdout)
        self.assertEqual('Unclassified',packet['code']);self.assertEqual('TypeError',packet['failureExceptionType'])
        self.assertEqual('bridge',packet['failureComponent']);self.assertNotIn(CANARY,p.stdout)
    def test_known_codes_preserved_and_unknown_str_is_never_called(self):
        self.assertEqual('EOFError',g.rejection_packet(EOFError(CANARY),{})['failureExceptionType'])
        self.assertEqual('PinMismatch',g.rejection_packet(ValueError('GatePin'),{'GatePin':'PinMismatch'})['reason'])
        self.assertEqual('Unclassified',g.rejection_packet(KeyError('GatePin'),{'GatePin':'PinMismatch'})['code'])
        class PrivateException(Exception):
            def __str__(self):raise AssertionError(CANARY)
        packet=g.rejection_packet(PrivateException(CANARY),{})
        self.assertEqual('Other',packet['category']);self.assertEqual('unavailable',packet['failureComponent'])
        self.assertIsNone(packet['failureSourceLine']);self.assertNotIn(CANARY,json.dumps(packet))


if __name__=='__main__':unittest.main()
