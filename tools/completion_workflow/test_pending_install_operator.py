"""Actual closed-profile adapter, Windows leases, SQLite and staged replacement. All synthetic."""
import json
import copy
import ctypes
from ctypes import wintypes
import hashlib
import os
import tempfile
from pathlib import Path
import shutil
import unittest
import uuid
import completion_register as m
import gate_cli as g
import pending_install_operator as operator
import pending_install_files as files
import test_unused_diagnostic_renewal_cli as fixture
import test_gate_cli as base_fixture


class PendingInstallOperatorTests(unittest.TestCase):
    def test_short_temp_alias_is_canonical_before_sealing_profile_identity(self):
        if os.name!='nt':self.skipTest('Windows short-path identity fixture')
        with tempfile.TemporaryDirectory(prefix='cloudscribe-long-identity-fixture-') as parent:
            kernel=ctypes.WinDLL('kernel32',use_last_error=True)
            kernel.GetShortPathNameW.argtypes=(wintypes.LPCWSTR,wintypes.LPWSTR,wintypes.DWORD)
            kernel.GetShortPathNameW.restype=wintypes.DWORD
            buffer=ctypes.create_unicode_buffer(32768)
            length=kernel.GetShortPathNameW(parent,buffer,len(buffer))
            if not 0<length<len(buffer) or buffer.value==str(Path(parent).resolve()):
                self.skipTest('No distinct short alias available; no OS settings changed')
            self.assertTrue(Path(buffer.value).samefile(parent))
            from unittest.mock import patch
            with patch.object(tempfile,'tempdir',buffer.value):
                f=base_fixture.CliTests();f.setUp()
            try:
                self.assertNotEqual(str(Path(f.temporary.name)),str(f.root))
                self.assertEqual(f.root,f.root.resolve())
                self.assertEqual(f.config['ProfileRootSha256'],hashlib.sha256(str(f.profile.resolve()).rstrip('/\\').upper().encode()).hexdigest())
                self.assertFalse(f.profile.resolve().is_relative_to(f.app.parent.resolve()))
            finally:f.doCleanups()

    def test_actual_replacement_interruption_and_explicit_administrative_recovery(self):
        f,_,_,old,_=fixture.prepare_unused_renewal(self)
        target=Path(f.config['ApplicationDirectory']).parent;folder=f.new_folder
        backup=target.with_name('retained-reviewed-old-installation');shutil.copytree(target,backup)
        profile=f.f.b.f.profile;required=profile/'.completion-workflow-required.json'
        saved=folder/'retained-original-required.json';saved.write_bytes(required.read_bytes())
        original_db=f.f.db.read_bytes();old_gate=Path(f.h['PreviousConfigPath'])
        self.assertEqual(0,f.command('claim-successor')[0])
        for _ in range(2):self.assertEqual(0,f.command('check-successor')[0])
        # The real install ordering: installed slot is replaced before completion.
        f.f.b.write_installed(f.config,f.pin,f.config_path)
        with old._open() as db:s=old._read(db)
        child=m.Register(f.config['RegisterPath'],f.config['ScopeSha256'])
        with child._open() as db:n=child._read(db)
        record=dict(SchemaVersion=1,WithdrawalId=str(uuid.uuid4()),WithdrawalApprovalSha256=m.digest({'synthetic':'separate admin recovery approval'}),
            RepairTransitionId=f.h['TransitionId'],RepairIdentitySha256=f.h_pin,ScopeSha256=old.scope_sha,ExpectedHeadSha256=s['head'],
            PreviousGateSha256=f.pin,RestoredGateSha256=f.h['PreviousGateSha256'],BackupPath=str(backup),
            PreviousInstallSha256=files.sha(backup/'cloudscribe-install.json'),PreviousRequiredSha256=files.sha(saved),
            NewScopeSha256=child.scope_sha,NewHeadSha256=n['head'],AdministrativeOnly=True)
        record_path=folder/'reviewed-synthetic-withdrawal.json';record_path.write_text(json.dumps(record))
        ref=lambda p:{'Path':str(p),'Sha256':files.sha(p)}
        plan=dict(SchemaVersion=1,AdministrativeOnly=True,PaidAuthority=False,Withdrawal=ref(record_path),OriginalHandoff=ref(f.h_path),
            ImmutableOldGate=ref(old_gate),ImmutableNewGate=ref(f.config_path),
            SourceSha256={name:files.sha(Path(__file__).with_name(name)) for name in
                ('completion_register.py','successor_register.py','gate_cli.py','pending_install_files.py','pending_install_operator.py')},
            OldInventory=files.inventory(backup),NewInventory=files.inventory(target),SavedRequired=ref(saved),
            NewRequiredSha256=files.sha(required),StageRoot=str(target.with_name('reviewed-restore-stage')),
            DisplacedRoot=str(target.with_name('retained-incomplete-installation')),OperatorPath=str(Path(operator.__file__).resolve()))
        (profile/'.instance.lock').touch(exist_ok=True)
        def run(phase, fault=lambda _:None):return operator.perform(plan,expected_installation=target,phase=phase,fault=fault)
        self.assertFalse(run('inspect')['authority'])
        mutable=copy.deepcopy(plan);mutable['ImmutableNewGate']=ref(target/'completion-workflow-gate.json')
        with self.assertRaisesRegex(ValueError,'RecoveryMutableEvidence'):
            operator.perform(mutable,expected_installation=target,phase='restore')
        with self.assertRaisesRegex(ValueError,'RecoveryMutableEvidence'):
            operator.perform(plan,expected_installation=target,phase='restore',plan_path=target/'unsafe-plan.json')
        with old._open() as db:self.assertEqual(s['head'],old._read(db)['head'])
        # A held app lease denies recovery without mutating either journal or installation.
        with operator.windows_handle(profile/'.instance.lock',exclusive=True):
            with self.assertRaises(OSError):run('restore')
        with old._open() as db:self.assertEqual(s['head'],old._read(db)['head'])
        def interruption(stage):
            if stage=='displaced':raise RuntimeError('synthetic interruption after actual directory rename')
        with self.assertRaisesRegex(RuntimeError,'synthetic interruption'):run('restore',interruption)
        self.assertFalse(target.exists());self.assertTrue(Path(plan['DisplacedRoot']).is_dir())
        result=run('resume');self.assertFalse(result['authority']);self.assertEqual(0,result['providerRequests'])
        self.assertEqual(plan['OldInventory'],files.inventory(target));self.assertEqual(original_db,f.f.db.read_bytes())
        with old._open() as db:restored=old._read(db)
        self.assertIsNone(restored['pending']);self.assertTrue(restored['stopped']);self.assertEqual(s['refresh'],restored['refresh'])
        with child._open() as db:self.assertEqual(n,child._read(db))
        with self.assertRaises(RuntimeError):run('resume')

    def test_safe_errors_preserve_known_code_and_exclude_unknown_sensitive_prose(self):
        for message in ('RecoveryOriginalHandoff','RecoveryJournalNotConfirmed'):
            self.assertEqual(message,operator.safe_failure(ValueError(message))['code'])
        secret='fake-private-article-and-token-canary'
        result=operator.safe_failure(ValueError(secret))
        self.assertEqual('Unclassified',result['code']);self.assertFalse(result['detailsAvailable'])
        self.assertNotIn(secret,json.dumps(result))
        lease=operator.safe_failure(OSError(32,'RecoveryLeaseUnavailable'))
        self.assertEqual('RecoveryLeaseUnavailable',lease['code']);self.assertEqual(32,lease['systemErrorCode'])


if __name__=='__main__':unittest.main()
