"""Synthetic native-profile database and file swaps; no credentials or provider."""
import copy
import json
from pathlib import Path
import shutil
import unittest
import uuid
import completion_register as m
import gate_cli as g
import maintenance_operator as operator
import pending_install_files as files
import pending_install_operator as leases
import test_unused_diagnostic_renewal_cli as fixture


class MaintenanceOperatorTests(unittest.TestCase):
    def prepare(self):
        f, _, _, old, _ = fixture.prepare_unused_renewal(self)
        self.f = f; self.old = old; target = Path(f.config['ApplicationDirectory']).parent
        folder = f.new_folder; old_path = Path(f.h['PreviousConfigPath'])
        old_config = json.loads(old_path.read_bytes()); profile = f.f.b.f.profile
        backup = target.with_name('maintenance-immutable-old'); shutil.copytree(target, backup)
        prepared = target.with_name('maintenance-prepared-new'); shutil.copytree(target, prepared)
        saved = folder / 'old-required.json'; saved.write_bytes((profile / '.completion-workflow-required.json').read_bytes())
        old.claim_successor(m.Register(f.config['RegisterPath'], f.config['ScopeSha256']), f.scope, f.h, f.h_pin)
        with old._open() as connection: state = old._read(connection)
        child = m.Register(f.config['RegisterPath'], f.config['ScopeSha256'])
        with child._open() as connection: child_state = child._read(connection)
        withdrawal = dict(SchemaVersion=1, WithdrawalId=str(uuid.uuid4()), WithdrawalApprovalSha256=m.digest({'synthetic': 'admin withdraw'}),
            RepairTransitionId=f.h['TransitionId'], RepairIdentitySha256=f.h_pin, ScopeSha256=old.scope_sha, ExpectedHeadSha256=state['head'],
            PreviousGateSha256=f.pin, RestoredGateSha256=f.h['PreviousGateSha256'], BackupPath=str(backup),
            PreviousInstallSha256=files.sha(backup / 'cloudscribe-install.json'), PreviousRequiredSha256=files.sha(saved),
            NewScopeSha256=child.scope_sha, NewHeadSha256=child_state['head'], AdministrativeOnly=True)
        for phase in ('claim', 'complete'):
            old.successor_withdrawal(child, withdrawal, m.digest(withdrawal), phase, m.digest({'synthetic': 'readback'}))
        with old._open() as connection: self.before = old._read(connection)
        self.db = f.f.db; self.db_before = self.db.read_bytes()
        (profile / '.instance.lock').touch(exist_ok=True)
        new = dict(old_config, ScriptPath=str(Path(g.__file__).resolve()), ScriptSha256=files.sha(Path(g.__file__)),
            ModuleSha256=files.sha(Path(m.__file__)), CandidateApplicationDirectory=str(prepared / 'app'))
        for name, field in (('CloudScribe.dll', 'AppSha256'), ('CloudScribe.Infrastructure.dll', 'InfrastructureSha256')):
            (prepared / 'app' / name).write_bytes(b'synthetic newer binary ' + name.encode())
            new[field] = files.sha(prepared / 'app' / name)
        ref = lambda p: {'Path': str(p), 'Sha256': files.sha(p)}
        new_path = folder / 'maintenance-new-gate.json'; new_path.write_text(json.dumps(new))
        new_pin = files.sha(new_path); (prepared / 'completion-workflow-gate.json').write_bytes(new_path.read_bytes())
        marker = json.loads((prepared / 'cloudscribe-install.json').read_bytes())
        marker.update(AppSha256=new['AppSha256'], CompletionWorkflowGateSha256=new_pin)
        (prepared / 'cloudscribe-install.json').write_text(json.dumps(marker))
        new_required = folder / 'new-required.json'; new_required.write_text(json.dumps({'GateSha256': new_pin, 'ScopeSha256': old.scope_sha}))
        s = self.before
        record = dict(SchemaVersion=1, TransitionKind='stopped-unused-diagnostic-maintenance', TransitionId=str(uuid.uuid4()),
            MaintenanceApprovalSha256=m.digest({'synthetic': 'fresh maintenance-only approval'}), SourceApprovalSha256=s['scope']['approvalSha256'],
            ScopeSha256=old.scope_sha, ExpectedHeadSha256=s['head'], PreviousGateSha256=f.h['PreviousGateSha256'], NewGateSha256=new_pin,
            PreviousBuildSha256=s['currentBuild'], NewBuildSha256=g.sha((new['AppSha256'] + '|' + new['InfrastructureSha256']).encode()),
            DatabaseSha256=files.sha(self.db), FinancialRowsSha256=json.loads(Path(old_config['PlanManifestPath']).read_bytes())['diagnostic']['financialRowsSha256'],
            AcceptedEvidenceSha256=s['scope']['acceptedEvidenceSha256'], ProfileSha256=s['scope']['profileSha256'],
            OldInventorySha256=m.digest(files.inventory(backup)), NewInventorySha256=m.digest(files.inventory(prepared)),
            PreviousRequiredSha256=files.sha(saved), NewRequiredSha256=files.sha(new_required), UsedRefreshes=s['refresh'],
            UsedAdmissions=s['admissions'], UsedResumeAdmissions=s['resume-admission'], LastGoodUtcMicros=s['lastGoodUtc'],
            AdministrativeOnly=True, EnableNarration=False)
        record_path = folder / 'maintenance-transition.json'; record_path.write_bytes(m.canonical(record).encode())
        plan = dict(SchemaVersion=1, AdministrativeOnly=True, PaidAuthority=False, Transition=ref(record_path),
            SourceSha256={name: files.sha(Path(operator.__file__).with_name(name)) for name in
                ('completion_register.py','successor_register.py', 'maintenance_register.py', 'gate_cli.py', 'pending_install_operator.py',
                 'pending_install_files.py', 'maintenance_files.py', 'maintenance_operator.py')},
            PreviousGate=ref(old_path), NewGate=ref(new_path), PreviousRequired=str(saved), NewRequired=str(new_required),
            PreviousRequiredSha256=files.sha(saved), NewRequiredSha256=files.sha(new_required), OldInventory=files.inventory(backup),
            NewInventory=files.inventory(prepared), Target=str(target), PreparedRoot=str(prepared), BackupRoot=str(backup),
            StageRoot=str(target.with_name('maintenance-stage')), DisplacedRoot=str(target.with_name('maintenance-retained-old')),
            RollbackStageRoot=str(target.with_name('maintenance-rollback-stage')), RollbackDisplacedRoot=str(target.with_name('maintenance-retained-new')),
            Required=str(profile / '.completion-workflow-required.json'), TransitionId=record['TransitionId'])
        self.plan = plan; self.new = new; self.new_pin = new_pin
        return plan

    def run_phase(self, phase, fault=lambda _: None, plan=None):
        return operator.perform(plan or self.plan, expected_installation=Path(self.plan['Target']), phase=phase, fault=fault)

    def preserved(self):
        self.assertEqual(self.db_before, self.db.read_bytes())
        with self.old._open() as connection: state = self.old._read(connection)
        for key in ('scope', 'refresh', 'admissions', 'resume-admission', 'stopReason', 'lastFailedKind', 'lastGoodUtc', 'clockEpoch'):
            self.assertEqual(self.before[key], state[key], key)
        self.assertTrue(state['stopped']); self.assertIsNone(state['diagnosticDispatch'])
        return state

    def test_actual_profile_forward_restart_readback_and_rollback(self):
        self.prepare(); self.assertTrue(self.run_phase('inspect')['valid'])
        self.rejections_before_claim()
        def interrupt(stage):
            if stage == 'displaced': raise RuntimeError('synthetic interruption')
        with self.assertRaisesRegex(RuntimeError, 'synthetic interruption'): self.run_phase('update', interrupt)
        self.assertFalse(Path(self.plan['Target']).exists()); self.preserved()
        self.assertFalse(self.run_phase('resume')['authority']); self.preserved()
        self.assertEqual(self.plan['NewInventory'], files.inventory(Path(self.plan['Target'])))
        # Actual current bridge reads the completed administrative history, then rejects before provider access.
        import subprocess, sys
        request = dict(command='status', configPath=self.plan['NewGate']['Path'], configSha256=self.new_pin,
            appDirectory=self.new['ApplicationDirectory'], id=str(uuid.uuid4()), identitySha256=m.digest({'synthetic': 'blocked'}))
        result = subprocess.run([sys.executable, '-I', '-S', '-B', self.new['ScriptPath']], input=json.dumps(request),
            text=True, capture_output=True, timeout=15)
        self.assertNotEqual(0, result.returncode); self.assertEqual('RunStopped', json.loads(result.stdout)['code'], result.stdout)
        self.assertFalse(self.run_phase('rollback')['authority']); self.preserved()
        self.assertEqual(self.plan['OldInventory'], files.inventory(Path(self.plan['Target'])))
        with self.assertRaisesRegex(RuntimeError, 'MaintenancePending'): self.run_phase('rollback')
        # A distinct reviewed synthetic maintenance action, then the separately
        # reviewed terminal manual-mode release; neither can reuse old authority.
        state = self.preserved(); record = json.loads(Path(self.plan['Transition']['Path']).read_bytes())
        record.update(TransitionId=str(uuid.uuid4()), MaintenanceApprovalSha256=m.digest({'synthetic': 'second separate maintenance'}),
            ExpectedHeadSha256=state['head'])
        path = Path(self.plan['Transition']['Path']).with_name('second-maintenance-transition.json'); path.write_bytes(m.canonical(record).encode())
        self.plan = copy.deepcopy(self.plan); self.plan['Transition'] = {'Path': str(path), 'Sha256': files.sha(path)}
        self.plan['TransitionId'] = record['TransitionId']
        for key in ('StageRoot', 'DisplacedRoot', 'RollbackStageRoot', 'RollbackDisplacedRoot'): self.plan[key] += '-second'
        self.run_phase('update'); state = self.preserved()
        release_id = str(uuid.uuid4()); required = Path(self.plan['Required'])
        archive = required.with_name(required.name + '.' + release_id + '.retired')
        marker = json.loads((Path(self.plan['PreparedRoot']) / 'cloudscribe-install.json').read_bytes())
        marker = {key: value for key, value in marker.items() if key not in ('CompletionWorkflowGateSha256', 'CompletionGateProtocolVersion')}
        manual = path.with_name('reviewed-manual-marker.json'); manual.write_text(json.dumps(marker))
        inventory = dict(self.plan['NewInventory']); inventory['cloudscribe-install.json'] = files.sha(manual)
        release = dict(SchemaVersion=1, ReleaseId=release_id, ReleaseApprovalSha256=m.digest({'synthetic': 'explicit manual-mode exit'}),
            MaintenanceIdentitySha256=self.plan['Transition']['Sha256'], ScopeSha256=self.old.scope_sha, ExpectedHeadSha256=state['head'],
            ManualInventorySha256=m.digest(inventory), RequiredArchivePathSha256=m.path_digest(archive), AdministrativeOnly=True, PaidAuthority=False)
        release_path = path.with_name('reviewed-manual-release.json'); release_path.write_bytes(m.canonical(release).encode())
        self.plan.update(Release={'Path': str(release_path), 'Sha256': files.sha(release_path)},
            ManualMarker={'Path': str(manual), 'Sha256': files.sha(manual)}, ManualInventory=inventory, RequiredArchive=str(archive))
        self.assertTrue(self.run_phase('inspect-release')['valid'])
        self.assertTrue(self.run_phase('release')['manualMode']); final = self.preserved()
        self.assertTrue(final['retired']); self.assertFalse(required.exists()); self.assertTrue(archive.exists())
        self.assertEqual(inventory, files.inventory(Path(self.plan['Target'])))

    def rejections_before_claim(self):
        for field in ('NewInventory', 'OldInventory'):
            plan = copy.deepcopy(self.plan); plan[field]['unknown'] = 'a' * 64
            with self.assertRaises(ValueError): self.run_phase('update', plan=plan)
        required = Path(self.plan['Required']); original = required.read_bytes(); required.write_bytes(b'{}')
        with self.assertRaises(ValueError): self.run_phase('update')
        required.write_bytes(original)
        with leases.windows_handle(Path(self.plan['Required']).parent / '.instance.lock', exclusive=True):
            with self.assertRaises(OSError): self.run_phase('update')
        self.assertEqual(self.before['head'], self.preserved()['head'])

    def test_safe_failure_excludes_unknown_private_prose(self):
        secret = 'fake-token-and-private-text-canary'
        result = operator.safe_failure(ValueError(secret))
        self.assertNotIn(secret, json.dumps(result)); self.assertEqual('Unclassified', result['code'])
        self.assertEqual('MaintenanceIdentity', operator.safe_failure(ValueError('MaintenanceIdentity'))['code'])


if __name__ == '__main__': unittest.main()
