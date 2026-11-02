"""Real isolated expired4->fresh4 bridge; exact interpreter evidence, no provider."""
import copy
from datetime import datetime,timedelta,timezone
import json
from pathlib import Path
import sys
import time
import unittest
import uuid
import completion_register as m
import interpreter_test_fixture as fixture


def prepare_successor(owner):
    f=fixture.prepare(owner,whole_end_after=8)
    old_config=dict(f.config);old_path=f.config_path;old_pin=f.pin;old_scope=dict(f.scope)
    old=m.Register(old_config['RegisterPath'],old_config['ScopeSha256'])
    with old._open() as c:before=old._read(c)
    time.sleep(max(0,m.utc(old_scope['endUtc'])/1_000_000-time.time()+0.05))
    root=f.new_folder/'fresh-window';root.mkdir();f.new_folder=root;start=datetime.now(timezone.utc)
    grant=dict(GrantId=str(uuid.uuid4()),PlanSha256s=[f.f.proposal['PlanSha256']],MaximumCatalogRefreshes=24)
    f.scope=dict(old_scope,approvalSha256=m.digest({'synthetic':'fresh explicit narration approval'}),wholeArticleGrantSha256=m.digest(grant),predecessorRegisterPath=old_config['RegisterPath'],predecessorScopeSha256=old_config['ScopeSha256'],registerPathSha256=m.path_digest(root/'register.db'),transitionId=str(uuid.uuid4()),startUtc=start.isoformat().replace('+00:00','Z'),endUtc=(start+timedelta(hours=6)).isoformat().replace('+00:00','Z'))
    f.scope_path=root/'scope.json';f.scope_path.write_text(json.dumps(f.scope))
    f.config=dict(old_config,RegisterPath=str(root/'register.db'),ScopeSha256=m.digest(f.scope),PythonSha256=fixture.fixtures.sha(Path(sys.executable).read_bytes()),ScriptPath=str(f.f.source/'gate_cli.py'),ScriptSha256=fixture.fixtures.sha((f.f.source/'gate_cli.py').read_bytes()),ModuleSha256=fixture.fixtures.sha((f.f.source/'completion_register.py').read_bytes()))
    f.config_path=root/'gate.json';f.config_path.write_text(json.dumps(f.config));f.pin=fixture.fixtures.sha(f.config_path.read_bytes())
    repair_approval=m.digest({'synthetic':'separate exact Python binding approval'})
    evidence=dict(SchemaVersion=1,RepairApprovalSha256=repair_approval,PreviousPythonSha256=old_config['PythonSha256'],NewPythonSha256=f.config['PythonSha256'],PythonPathSha256=m.path_digest(sys.executable),FileBytes=Path(sys.executable).stat().st_size,PythonVersion=sys.version.split()[0],PublisherName='Python Software Foundation',SignerThumbprint='A'*40)
    evidence_path=root/'interpreter-evidence.json';evidence_path.write_text(json.dumps(evidence))
    f.h=dict(f.h,TransitionKind='whole-article-interpreter-continuation',TransitionId=f.scope['transitionId'],SourceApprovalSha256=f.scope['approvalSha256'],PreviousConfigPath=str(old_path),PreviousGateSha256=old_pin,PreviousScopeSha256=old_config['ScopeSha256'],ExpectedPreviousHeadSha256=before['head'],NewConfigPath=str(f.config_path),NewGateSha256=f.pin,NewScopePath=str(f.scope_path),NewScopeSha256=f.config['ScopeSha256'],DatabaseSha256=fixture.fixtures.sha(f.f.db.read_bytes()),OriginalManifestSha256=m.digest(f.manifest),AcceptedEvidenceSha256=f.scope['acceptedEvidenceSha256'],PreviousPythonSha256=old_config['PythonSha256'],NewPythonSha256=f.config['PythonSha256'],InterpreterEvidencePath=str(evidence_path),InterpreterEvidenceSha256=fixture.fixtures.sha(evidence_path.read_bytes()),RepairApprovalSha256=repair_approval)
    f.h_path=root/'handoff.json';f.h_path.write_text(json.dumps(f.h));f.h_pin=fixture.fixtures.sha(f.h_path.read_bytes());f.f.new_app=Path(f.config['CandidateApplicationDirectory'])
    return f,old,old_config,grant,evidence_path

class ExpiredInterpreterSuccessorCliTests(unittest.TestCase):
    def test_actual_transition_new_binding_and_policy_keep_old_journal_immutable(self):
        f,old,old_config,grant,evidence_path=prepare_successor(self)
        with old._open() as c:before=c.execute('SELECT body,sha FROM events ORDER BY seq').fetchall()
        original=evidence_path.read_bytes();evidence_path.write_bytes(original+b' ')
        self.assertNotEqual(0,f.command('inspect-successor')[0]);self.assertFalse(Path(f.config['RegisterPath']).exists())
        evidence_path.write_bytes(original)
        f.activate()
        status=f.command('status');self.assertEqual(0,status[0],status);self.assertEqual((0,0),(status[1]['refreshes'],status[1]['totalAdmissions']))
        # The unchanged116 coverage grant requires its own separate admission.
        policy=json.loads(f.policy().read_text());policy.update(SchemaVersion=3,SourceApprovalSha256=f.scope['approvalSha256'],ExpiresAtUtc=f.scope['endUtc'],MaximumNewAttempts=116,MaximumAdditionalReservationMinorUnits=116,WholeArticleGeneration=grant)
        policy['Budget'].update(ProjectCommittedMinorUnits=163,AllocationCommittedMinorUnits=33)
        policy['Articles'][0]['AllowedSegments']=[dict(CoverageOrdinal=i,MaximumMinorUnits=1) for i in range(11,127)]
        path=f.new_folder/'admission.json';path.write_text(json.dumps(policy));op=str(uuid.uuid4())
        result=f.command('claim-completion',path,op);self.assertEqual(0,result[0],result)
        f.install_policy(path,f.config,f.pin);result=f.command('complete',path,op,dict(outcome='Succeeded'));self.assertEqual(0,result[0],result)
        self.assertEqual(0,f.command('verify-completion',path)[0])
        with old._open() as c:
            self.assertEqual(before,c.execute('SELECT body,sha FROM events ORDER BY seq LIMIT ?',(len(before),)).fetchall());self.assertTrue(old._read(c)['retired'])
        self.assertNotEqual(0,f.command('claim-successor')[0])

if __name__=='__main__':unittest.main()
