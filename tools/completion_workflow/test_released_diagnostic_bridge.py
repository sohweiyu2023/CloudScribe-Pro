"""Actual -I -S H8 bridge, inert installation and separately admitted synthetic policy."""
import json
from pathlib import Path
import unittest
import uuid

import completion_register as m
import gate_cli as g
from make_synthetic_released_diagnostic_fixture import prepare_released_diagnostic


class ReleasedDiagnosticBridgeTests(unittest.TestCase):
    def test_deployed_h8_transition_and_separate_one_cent_admission_preserve_data(self):
        f, policy_path, policy, old, before, metadata = prepare_released_diagnostic(self)
        original = f.f.db.read_bytes()
        self.assertTrue(before['retired'])
        self.assertFalse(Path(f.config['RegisterPath']).exists())
        for name in ('maintenance_register.py', 'successor_register.py'):
            self.assertTrue((f.source / name).is_file())
        # Actual frozen bridge reads the manual marker, archive, full inventory,
        # failed parent and every synthetic accepted receipt/media/financial row.
        result = f.command('inspect-successor')
        self.assertEqual(0, result[0], result)
        self.assertFalse(Path(f.config['RegisterPath']).exists())
        marker_path = Path(metadata['target']) / 'cloudscribe-install.json'
        original_marker = marker_path.read_bytes()
        marker = json.loads(original_marker)
        marker['CompletionWorkflowGateSha256'] = 'a' * 64
        marker_path.write_text(json.dumps(marker))
        result = f.command('inspect-successor')
        self.assertNotEqual(0, result[0], result)
        marker_path.write_bytes(original_marker)
        with old._open() as db:
            self.assertEqual(before['head'], old._read(db)['head'])
        for command in ('claim-successor', 'check-successor'):
            result = f.command(command)
            self.assertEqual(0, result[0], result)
            with old._open() as db:
                self.assertTrue(old._read(db)['retired'])
        # Missing installed new marker cannot be certified from caller booleans.
        self.assertNotEqual(0, f.command('complete-successor')[0])
        f.f.b.write_installed(f.config, f.pin, f.config_path)
        result = f.command('complete-successor')
        self.assertEqual(0, result[0], result)
        current = m.Register(f.config['RegisterPath'], f.config['ScopeSha256'])
        with old._open() as db:
            completed_old = old._read(db)
        with current._open() as db:
            completed_new = current._read(db)
        result = f.command('complete-successor')
        self.assertEqual(0, result[0], result)
        with old._open() as db:
            self.assertEqual(completed_old, old._read(db))
        with current._open() as db:
            self.assertEqual(completed_new, current._read(db))
        current.require_predecessor_retired(f.pin)
        with current._open() as db:
            activated = current._read(db)
        self.assertIsNone(activated['diagnosticPolicySha256'])
        self.assertIsNone(activated['diagnosticDispatch'])
        for key in ('refresh', 'admissions', 'resume-admission'):
            self.assertEqual(before[key], activated[key])
        result = f.command('status')
        self.assertEqual(0, result[0], result)
        # Separate supported financial-policy admission after installed H8
        # activation; no reservation row, credential access or HTTP is created.
        self.assertEqual(1, policy['MaximumNewAttempts'])
        self.assertEqual(1, policy['MaximumAdditionalReservationMinorUnits'])
        self.assertEqual(1, policy['Budget']['ProjectCeilingMinorUnits'] - policy['Budget']['ProjectCommittedMinorUnits'])
        operation = str(uuid.uuid4())
        for command in ('claim-completion', 'check'):
            result = f.command(command, policy_path, operation)
            self.assertEqual(0, result[0], result)
        f.install_policy(policy_path, f.config, f.pin)
        result = f.command('complete', policy_path, operation, {'outcome': 'Succeeded'})
        self.assertEqual(0, result[0], result)
        with current._open() as db:
            admitted = current._read(db)
        self.assertEqual(g.sha(policy_path.read_bytes()), admitted['diagnosticPolicySha256'])
        self.assertEqual(before['admissions'] + 1, admitted['admissions'])
        self.assertEqual(before['refresh'], admitted['refresh'])
        self.assertIsNone(admitted['diagnosticDispatch'])
        self.assertEqual(original, f.f.db.read_bytes())
        with old._open() as db:
            final_old = old._read(db)
        self.assertTrue(final_old['retired'])
        self.assertIsNone(final_old['pending'])
        self.assertEqual(before['refresh'], final_old['refresh'])
        self.assertEqual(before['admissions'], final_old['admissions'])


if __name__ == '__main__':
    unittest.main()
