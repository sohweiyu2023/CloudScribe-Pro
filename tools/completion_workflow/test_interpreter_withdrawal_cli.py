"""Actual no-provider reverse bridge with each metadata restoration boundary."""
import json
from pathlib import Path
import subprocess
import sys
import unittest
import uuid
import completion_register as m
import interpreter_test_fixture as fixture

class InterpreterWithdrawalCliTests(unittest.TestCase):
    def test_exact_backup_partial_restoration_replay_and_no_authority(self):
        f=fixture.prepare(self);t=fixture.repair(f)
        installed=Path(f.config['ApplicationDirectory']).parent;required=Path(f.f.b.f.profile)/'.completion-workflow-required.json'
        backup=f.new_folder/'reviewed-original-backup';backup.mkdir()
        files=[('previous-gate.json',installed/'completion-workflow-gate.json'),('previous-install.json',installed/'cloudscribe-install.json'),('previous-required.json',required)]
        for name,path in files:(backup/name).write_bytes(path.read_bytes())
        self.assertEqual(0,fixture.command(f,t,'claim-build')[0])
        f.f.b.write_installed(f.config,f.pin,f.config_path)
        self.assertEqual(0,fixture.command(f,t,'complete-build')[0])
        r=m.Register(f.config['RegisterPath'],f.config['ScopeSha256'])
        with r._open() as c:state=r._read(c)
        record=dict(SchemaVersion=1,WithdrawalId=str(uuid.uuid4()),WithdrawalApprovalSha256=m.digest({'synthetic':'separate withdrawal approval'}),RepairTransitionId=t['record']['TransitionId'],RepairIdentitySha256=t['pin'],ScopeSha256=r.scope_sha,ExpectedHeadSha256=state['head'],PreviousGateSha256=f.pin,RestoredGateSha256=t['oldPin'],BackupPath=str(backup),PreviousInstallSha256=fixture.fixtures.sha((backup/'previous-install.json').read_bytes()),PreviousRequiredSha256=fixture.fixtures.sha((backup/'previous-required.json').read_bytes()))
        path=f.new_folder/'withdrawal.json';path.write_text(json.dumps(record));pin=fixture.fixtures.sha(path.read_bytes())
        def call(verb,success=True):
            request=dict(command=verb,configPath=str(f.config_path),configSha256=f.pin,appDirectory=f.config['ApplicationDirectory'],id=record['WithdrawalId'],identitySha256=pin,transitionPath=str(t['path']),transitionSha256=t['pin'],withdrawalPath=str(path),withdrawalSha256=pin)
            p=subprocess.run([sys.executable,'-I','-S',f.config['ScriptPath']],input=json.dumps(request),capture_output=True,text=True,timeout=20)
            self.assertEqual('',p.stderr);result=json.loads(p.stdout);self.assertEqual(success,p.returncode==0,result);return result
        original=(backup/'previous-required.json').read_bytes();(backup/'previous-required.json').write_bytes(original+b' ')
        call('inspect-interpreter-withdrawal',False);(backup/'previous-required.json').write_bytes(original)
        call('inspect-interpreter-withdrawal');call('claim-interpreter-withdrawal');call('claim-interpreter-withdrawal',False)
        self.assertNotEqual(0,fixture.command(f,t,'status')[0])
        for index,(name,target) in enumerate(files):
            call('check-interpreter-withdrawal');target.write_bytes((backup/name).read_bytes());call('check-interpreter-withdrawal')
            if index<2:call('complete-interpreter-withdrawal',False)
        call('complete-interpreter-withdrawal');call('complete-interpreter-withdrawal',False)
        with r._open() as c:final=r._read(c)
        self.assertTrue(final['stopped']);self.assertIsNone(final['pending']);self.assertEqual(t['oldPin'],final['currentGate'])
        self.assertEqual((0,0,1),(final['refresh'],final['admissions'],final['helperSourceRepairs']))

if __name__=='__main__':unittest.main()
