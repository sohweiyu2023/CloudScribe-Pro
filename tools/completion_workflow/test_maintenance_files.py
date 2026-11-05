"""Every synthetic file-swap boundary; no profile, process or network."""
from pathlib import Path
import json
import tempfile
import unittest
import pending_install_files as files
import maintenance_files as maintenance


class MaintenanceFileTests(unittest.TestCase):
    def fixture(self, root):
        old = root / 'backup'; new = root / 'prepared'; target = root / 'installed'
        for directory, value in ((old, b'old'), (new, b'new'), (target, b'old')):
            directory.mkdir(); (directory / 'app.dll').write_bytes(value)
        required = root / 'required.json'; required.write_bytes(b'old required')
        old_required = root / 'old-required.json'; old_required.write_bytes(required.read_bytes())
        new_required = root / 'new-required.json'; new_required.write_bytes(b'new required')
        plan = dict(Target=str(target), BackupRoot=str(old), PreparedRoot=str(new), StageRoot=str(root / 'forward-stage'),
            DisplacedRoot=str(root / 'retained-old'), RollbackStageRoot=str(root / 'rollback-stage'),
            RollbackDisplacedRoot=str(root / 'retained-new'), Required=str(required), PreviousRequired=str(old_required),
            NewRequired=str(new_required), OldInventory=files.inventory(old), NewInventory=files.inventory(new),
            PreviousRequiredSha256=files.sha(old_required), NewRequiredSha256=files.sha(new_required),
            TransitionId='11111111-1111-4111-8111-111111111111')
        state = {'phase': 'new', 'ledger': 174, 'requests': 0}
        def journal(phase):
            if phase == 'claim':
                if state['phase'] != 'new': raise ValueError('duplicate claim')
                state['phase'] = 'pending'
            elif phase == 'check':
                if state['phase'] != 'pending': raise ValueError('not pending')
            elif phase == 'complete':
                if state['phase'] != 'pending': raise ValueError('not pending')
                self.assertEqual(plan['NewInventory'], files.inventory(target))
                self.assertEqual(plan['NewRequiredSha256'], files.sha(required)); state['phase'] = 'completed'
            return {'valid': True, 'outcome': 'Succeeded', 'authority': False}
        def rollback(phase):
            if phase == 'claim':
                if state['phase'] not in ('pending', 'completed'): raise ValueError('duplicate rollback')
                state['phase'] = 'rollback'
            elif phase == 'check':
                if state['phase'] != 'rollback': raise ValueError('not rolling back')
            elif phase == 'complete':
                if state['phase'] != 'rollback': raise ValueError('not rolling back')
                self.assertEqual(plan['OldInventory'], files.inventory(target))
                self.assertEqual(plan['PreviousRequiredSha256'], files.sha(required)); state['phase'] = 'rolledback'
            return {'valid': True, 'outcome': 'Succeeded', 'authority': False}
        return plan, state, journal, rollback

    def test_forward_resume_and_rollback_at_every_boundary(self):
        for boundary in ('claimed', 'staged', 'displaced', 'restored-installation', 'restored-required', 'completed'):
            with self.subTest(boundary=boundary), tempfile.TemporaryDirectory() as folder:
                plan, state, journal, rollback = self.fixture(Path(folder))
                def fail(stage):
                    if stage == boundary: raise RuntimeError('synthetic interruption')
                with self.assertRaises(RuntimeError): maintenance.update(files, plan, journal, lambda: None, fail)
                # A lost completion result is reconciled; it is never blindly repeated.
                if state['phase'] == 'pending': maintenance.update(files, plan, journal, lambda: None, resume=True)
                self.assertEqual('completed', state['phase'])
                maintenance.rollback(files, plan, rollback, lambda: None)
                self.assertEqual('rolledback', state['phase']); self.assertEqual(174, state['ledger']); self.assertEqual(0, state['requests'])

    def test_direct_rollback_of_every_interrupted_forward_boundary(self):
        for boundary in ('claimed', 'staged', 'displaced', 'restored-installation', 'restored-required'):
            with self.subTest(boundary=boundary), tempfile.TemporaryDirectory() as folder:
                plan, state, journal, rollback = self.fixture(Path(folder))
                def fail(stage):
                    if stage == boundary: raise RuntimeError('synthetic interruption')
                with self.assertRaises(RuntimeError): maintenance.update(files, plan, journal, lambda: None, fail)
                maintenance.rollback(files, plan, rollback, lambda: None)
                self.assertEqual('rolledback', state['phase'])

    def test_interrupted_rollback_resumes_with_same_claim(self):
        for boundary in ('rollback-claimed', 'rollback-staged', 'rollback-displaced', 'rollback-required'):
            with self.subTest(boundary=boundary), tempfile.TemporaryDirectory() as folder:
                plan, state, journal, rollback = self.fixture(Path(folder))
                maintenance.update(files, plan, journal, lambda: None)
                def fail(stage):
                    if stage == boundary: raise RuntimeError('synthetic interruption')
                with self.assertRaises(RuntimeError): maintenance.rollback(files, plan, rollback, lambda: None, fail)
                maintenance.rollback(files, plan, rollback, lambda: None, resume=True)
                self.assertEqual('rolledback', state['phase'])

    def test_unknown_target_or_failed_evidence_never_overwrites_files(self):
        with tempfile.TemporaryDirectory() as folder:
            plan, state, journal, rollback = self.fixture(Path(folder))
            target = Path(plan['Target']); (target / 'app.dll').write_bytes(b'unknown')
            with self.assertRaisesRegex(ValueError, 'RecoveryInitialState'): maintenance.update(files, plan, journal, lambda: None)
            with self.assertRaisesRegex(ValueError, 'RecoveryInstallationChanged'): maintenance.rollback(files, plan, rollback, lambda: None)
            self.assertEqual(b'unknown', (target / 'app.dll').read_bytes()); self.assertEqual('new', state['phase'])


if __name__ == '__main__': unittest.main()
