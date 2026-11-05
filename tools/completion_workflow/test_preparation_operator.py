"""Actual file swaps and native ledger readback, disposable synthetic H9 only."""
import json
from contextlib import closing
from pathlib import Path
import shutil
import subprocess
import sys
import sqlite3
import types
from unittest.mock import patch
import unittest
import uuid
import completion_register as m
import gate_cli as g
import maintenance_operator as operator
import pending_install_files as files
from preparation_test_fixture import prepare_clean_reserved_h9
from test_preparation_register import PreparationRegisterTests


class PreparationOperatorTests(unittest.TestCase):
    def test_interrupted_update_clean_held_readback_and_preparation_without_paid_authority(self):
        f, auth = prepare_clean_reserved_h9(self)
        now = m.utc(f.scope['endUtc']) + 1000000; mono = 1000000
        register = m.Register(f.config['RegisterPath'], f.config['ScopeSha256'], lambda:now, lambda:mono)
        with self.assertRaisesRegex(m.Rejected, 'ClockRollback'): register.status()
        with register._open() as db: before = register._read(db)
        target = Path(f.config['ApplicationDirectory']).parent; profile = f.f.b.f.profile
        folder = f.new_folder / 'preparation'; folder.mkdir()
        log_name='app/logs/cloudscribe-bootstrap-20990101.log'
        log=target/log_name;log.parent.mkdir(parents=True,exist_ok=True);log.write_bytes(b'synthetic before startup')
        backup = folder / 'old'; shutil.copytree(target, backup)
        prepared = folder / 'new'; shutil.copytree(target, prepared)
        names=('completion_register.py','maintenance_register.py','successor_register.py','preparation_register.py','gate_cli.py',
            'pending_install_operator.py','pending_install_files.py','maintenance_files.py','maintenance_operator.py')
        installed_reader=folder/'installed-reader';installed_reader.mkdir()
        for name in names:shutil.copyfile(Path(operator.__file__).with_name(name),installed_reader/name)
        # Distinct immutable reader bytes/path prove terminal maintenance is not
        # a hidden rebind of the installed gate's executable trust.
        with (installed_reader/'completion_register.py').open('ab') as stream:stream.write(b'\n')
        installed_operator=types.ModuleType('synthetic_installed_operator')
        installed_operator.__file__=str(installed_reader/'maintenance_operator.py')
        exec(compile(Path(installed_operator.__file__).read_bytes(),installed_operator.__file__,'exec'),installed_operator.__dict__)
        required = profile / '.completion-workflow-required.json'
        saved = folder / 'old-required.json'; saved.write_bytes(required.read_bytes())
        (profile / '.instance.lock').touch(exist_ok=True)
        new = dict(f.config, CandidateApplicationDirectory=str(prepared/'app'), ScriptPath=str(installed_reader/'gate_cli.py'),
            ScriptSha256=files.sha(installed_reader/'gate_cli.py'), ModuleSha256=files.sha(installed_reader/'completion_register.py'))
        for name, field in (('CloudScribe.dll', 'AppSha256'), ('CloudScribe.Infrastructure.dll', 'InfrastructureSha256')):
            (prepared/'app'/name).write_bytes(('synthetic preparation binary '+name).encode())
            new[field] = files.sha(prepared/'app'/name)
        prep = dict(SchemaVersion=1, PreparationId=str(uuid.uuid4()), ApprovalSha256=m.digest({'synthetic':'catalog preparation'}),
            ScopeSha256=register.scope_sha, PreviousGateSha256=f.pin, NewBuildSha256=g.build_digest(new),
            HeldAuthorizationId=auth, HeldLedgerSha256=f.manifest['diagnostic']['reservedResume']['ledgerEvidenceSha256'],
            HeldQueueSha256=f.manifest['diagnostic']['reservedResume']['queueEvidenceSha256'],
            FinancialRowsSha256=f.manifest['diagnostic']['financialRowsSha256'], AcceptedEvidenceSha256=f.scope['acceptedEvidenceSha256'],
            ClockEpochEvidence=dict(ObservedUtcMicros=now, ObservedMonoMicros=mono, TrustedUtcLowerMicros=now-100000,
                TrustedUtcUpperMicros=now+100000, WindowsBootUtc=PreparationRegisterTests.stamp(now-10000000),
                BootReadbackSha256=m.digest({'synthetic':'boot readback'})),
            NotAfterUtc=PreparationRegisterTests.stamp(now+900000000), MaximumSeconds=900, AdministrativeOnly=True, EnableNarration=False)
        prep_pin = m.digest(prep); (prepared/'completion-preparation.json').write_bytes(m.canonical(prep).encode())
        new_path = folder/'new-gate.json'; new_path.write_text(json.dumps(new)); new_pin=files.sha(new_path)
        (prepared/'completion-workflow-gate.json').write_bytes(new_path.read_bytes())
        marker = json.loads((prepared/'cloudscribe-install.json').read_bytes())
        marker.update(AppSha256=new['AppSha256'], CompletionWorkflowGateSha256=new_pin, CompletionPreparationSha256=prep_pin)
        (prepared/'cloudscribe-install.json').write_text(json.dumps(marker))
        new_required=folder/'new-required.json'; new_required.write_text(json.dumps({'GateSha256':new_pin,'ScopeSha256':register.scope_sha}))
        transition=dict(SchemaVersion=1, TransitionKind='expired-reserved-diagnostic-preparation', TransitionId=str(uuid.uuid4()),
            MaintenanceApprovalSha256=m.digest({'synthetic':'install preparation only'}), SourceApprovalSha256=f.scope['approvalSha256'],
            ScopeSha256=register.scope_sha, ExpectedHeadSha256=before['head'], PreviousGateSha256=f.pin, NewGateSha256=new_pin,
            PreviousBuildSha256=before['currentBuild'], NewBuildSha256=g.build_digest(new), DatabaseSha256=files.sha(f.f.db),
            FinancialRowsSha256=prep['FinancialRowsSha256'], AcceptedEvidenceSha256=prep['AcceptedEvidenceSha256'],
            ProfileSha256=f.scope['profileSha256'], OldInventorySha256=m.digest(files.inventory(backup)),
            NewInventorySha256=m.digest(files.inventory(prepared)), PreviousRequiredSha256=files.sha(saved), NewRequiredSha256=files.sha(new_required),
            UsedRefreshes=before['refresh'], UsedAdmissions=before['admissions'], UsedResumeAdmissions=before['resume-admission'],
            LastGoodUtcMicros=before['lastGoodUtc'], AdministrativeOnly=True, EnableNarration=False, PreparationSha256=prep_pin,
            HeldAuthorizationId=auth, HeldLedgerSha256=prep['HeldLedgerSha256'], HeldQueueSha256=prep['HeldQueueSha256'])
        path=folder/'transition.json';path.write_bytes(m.canonical(transition).encode())
        ref=lambda p:dict(Path=str(p),Sha256=files.sha(p))
        plan=dict(SchemaVersion=1,AdministrativeOnly=True,PaidAuthority=False,Transition=ref(path),
            SourceSha256={n:files.sha(installed_reader/n) for n in names},PreviousGate=ref(f.config_path),
            NewGate=ref(new_path),PreviousRequired=str(saved),NewRequired=str(new_required),PreviousRequiredSha256=files.sha(saved),
            NewRequiredSha256=files.sha(new_required),OldInventory=files.inventory(backup),NewInventory=files.inventory(prepared),
            Target=str(target),PreparedRoot=str(prepared),BackupRoot=str(backup),StageRoot=str(target.with_name('prep-stage')),
            DisplacedRoot=str(target.with_name('prep-retained-old')),RollbackStageRoot=str(target.with_name('prep-rollback')),
            RollbackDisplacedRoot=str(target.with_name('prep-retained-new')),
            Required=str(required),TransitionId=transition['TransitionId'])
        original=installed_operator.sources
        def isolated_sources(pins):
            modules=original(pins); module=modules['completion_register.py']; original_register=module.Register
            module.Register=lambda *a,**kw: original_register(*a,clock=lambda:now,monotonic=lambda:mono,**kw)
            return modules
        data_before=f.f.db.read_bytes()
        def interrupt(stage):
            if stage=='displaced':raise RuntimeError('synthetic interrupted swap')
        with patch.object(installed_operator,'sources',isolated_sources):
            self.assertTrue(installed_operator.perform(plan,expected_installation=target,phase='inspect')['valid'])
            with self.assertRaisesRegex(RuntimeError,'synthetic interrupted swap'):
                installed_operator.perform(plan,expected_installation=target,phase='update',fault=interrupt)
            self.assertFalse(target.exists())
            self.assertFalse(installed_operator.perform(plan,expected_installation=target,phase='resume')['authority'])
        self.assertEqual(data_before,f.f.db.read_bytes());self.assertEqual(plan['NewInventory'],files.inventory(target))
        register.require_predecessor_retired(new_pin)
        operation=str(uuid.uuid4()); request=dict(command='claim-preparation',configSha256=new_pin,identitySha256=prep_pin,id=operation,receipt=None)
        bridge_request=dict(request,configPath=str(new_path),appDirectory=new['ApplicationDirectory'])
        checked=subprocess.run([sys.executable,'-I','-S','-B',new['ScriptPath']],input=json.dumps(bridge_request),
            capture_output=True,text=True,timeout=25)
        # The real isolated bridge must load/read the new module, then reject the
        # intentionally future synthetic boot epoch, without appending anything.
        self.assertNotEqual(0,checked.returncode)
        self.assertEqual('PreparationClock',json.loads(checked.stdout)['code'],checked.stdout+checked.stderr)
        for command in ('claim-preparation','check-preparation'):
            request['command']=command
            self.assertFalse(g.preparation_command(request,new,f.manifest,f.scope,m,register,Path(new['ApplicationDirectory']))['authority'])
        receipt=dict(id=operation,identitySha256=prep_pin,outcome='Succeeded',evidenceSha256=m.digest({'synthetic':'verified responses'}),
            catalogVerified=True,probeInputEmpty=True,probeHttpStatus=400,probeStatus='INVALID_ARGUMENT',audioAbsent=True)
        request.update(command='complete-preparation',receipt=receipt)
        self.assertFalse(g.preparation_command(request,new,f.manifest,f.scope,m,register,Path(new['ApplicationDirectory']))['authority'])
        self.assertEqual(data_before,f.f.db.read_bytes())
        with self.assertRaisesRegex(m.Rejected,'RunStopped'):register.status()
        with register._open() as db:after=register._read(db)
        self.assertEqual(before['refresh']+1,after['refresh']);self.assertEqual(before['admissions'],after['admissions'])
        self.assertIsNone(after['diagnosticDispatch']);self.assertIsNone(after['diagnosticPolicySha256'])
        # A separate explicit terminal administrative action restores the ordinary
        # native workflow, not the expired paid window. Every held/accepted row stays.
        with closing(sqlite3.connect(f.f.db)) as metadata, metadata:
            metadata.execute('CREATE TABLE synthetic_catalog_refresh (Observed INTEGER)')
            metadata.execute('INSERT INTO synthetic_catalog_refresh VALUES (1)')
        after_catalog=f.f.db.read_bytes();self.assertNotEqual(data_before,after_catalog)
        log.write_bytes(b'synthetic startup completed')
        release_id=str(uuid.uuid4());archive=required.with_name(required.name+'.'+release_id+'.retired')
        manual=dict(marker);manual.pop('CompletionWorkflowGateSha256');manual.pop('CompletionGateProtocolVersion')
        manual_path=folder/'manual-marker.json';manual_path.write_text(json.dumps(manual))
        manual_inventory=dict(plan['NewInventory']);manual_inventory['cloudscribe-install.json']=files.sha(manual_path)
        manual_inventory[log_name]=files.sha(log)
        release=dict(SchemaVersion=1,ReleaseId=release_id,ReleaseApprovalSha256=m.digest({'synthetic':'explicit stopped-session retirement'}),
            MaintenanceIdentitySha256=plan['Transition']['Sha256'],ScopeSha256=register.scope_sha,ExpectedHeadSha256=after['head'],
            ManualInventorySha256=m.digest(manual_inventory),RequiredArchivePathSha256=m.path_digest(archive),
            AdministrativeOnly=True,PaidAuthority=False,CurrentDatabaseSha256=files.sha(f.f.db),
            CurrentBootstrapLogPath=log_name,CurrentBootstrapLogSha256=files.sha(log))
        release_path=folder/'release.json';release_path.write_bytes(m.canonical(release).encode())
        plan.update(Release=ref(release_path),ManualMarker=ref(manual_path),ManualInventory=manual_inventory,RequiredArchive=str(archive))
        plan['RetirementReaderPath']=str(Path(operator.__file__).resolve())
        plan['SourceSha256']={n:files.sha(Path(operator.__file__).with_name(n)) for n in names}
        retirement_sources=operator.sources
        def isolated_retirement_sources(pins):
            modules=retirement_sources(pins);module=modules['completion_register.py'];original_register=module.Register
            module.Register=lambda *a,**kw:original_register(*a,clock=lambda:now,monotonic=lambda:mono,**kw)
            return modules
        def interrupt_release(stage):
            if stage=='release-marker':raise RuntimeError('synthetic retirement interruption')
        with patch.object(operator,'sources',isolated_retirement_sources):
            stale=dict(release,CurrentDatabaseSha256=transition['DatabaseSha256'])
            wrong=folder/'stale-release.json';wrong.write_bytes(m.canonical(stale).encode())
            bad=dict(plan,Release=ref(wrong))
            with self.assertRaisesRegex(ValueError,'DiagnosticDatabasePin'):
                operator.perform(bad,expected_installation=target,phase='inspect-release')
            # Even a correctly rehashed new database cannot conceal changed holds.
            with closing(sqlite3.connect(f.f.db)) as changed, changed:
                changed.execute('UPDATE chirp_article_successor_attempts SET ReservedMinorUnits=2 WHERE AuthorizationId=?',(auth,))
            changed_release=dict(release,CurrentDatabaseSha256=files.sha(f.f.db));wrong.write_bytes(m.canonical(changed_release).encode())
            bad=dict(plan,Release=ref(wrong))
            with self.assertRaisesRegex(ValueError,'DiagnosticChild'):
                operator.perform(bad,expected_installation=target,phase='inspect-release')
            with closing(sqlite3.connect(f.f.db)) as restored, restored:
                restored.execute('UPDATE chirp_article_successor_attempts SET ReservedMinorUnits=1 WHERE AuthorizationId=?',(auth,))
            after_catalog=f.f.db.read_bytes();release['CurrentDatabaseSha256']=files.sha(f.f.db)
            release_path.write_bytes(m.canonical(release).encode());plan['Release']=ref(release_path)
            self.assertTrue(operator.perform(plan,expected_installation=target,phase='inspect-release')['valid'])
            with self.assertRaisesRegex(RuntimeError,'synthetic retirement interruption'):
                operator.perform(plan,expected_installation=target,phase='release',fault=interrupt_release)
            self.assertTrue(required.exists())
            self.assertTrue(operator.perform(plan,expected_installation=target,phase='resume-release')['manualMode'])
        self.assertFalse(required.exists());self.assertTrue(archive.exists())
        self.assertEqual(after_catalog,f.f.db.read_bytes());self.assertEqual(manual_inventory,files.inventory(target))
        with register._open() as db:terminal=register._read(db)
        self.assertTrue(terminal['retired']);self.assertTrue(terminal['stopped']);self.assertIsNone(terminal['pending'])
        for key in ('scope','refresh','admissions','resume-admission','diagnosticPolicySha256','diagnosticDispatch'):
            self.assertEqual(after[key],terminal[key],key)


if __name__=='__main__':unittest.main()
