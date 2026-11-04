"""Actual paired bridge/readback with synthetic SQLite/audio; no provider or credentials."""
import copy
from contextlib import closing
from datetime import datetime,timezone
import json
from pathlib import Path
import sqlite3
import subprocess
import time
import unittest
import uuid
import completion_register as m
import gate_cli as g
import test_sentence_diagnostic_cli as prior


def prepare_unused_renewal(owner):
    f,path,policy,_,_=prior.prepare_diagnostic(owner,short_window=True)
    f.activate();operation=str(uuid.uuid4())
    for command in ('claim-completion','check'):
        result=f.command(command,path,operation);owner.assertEqual(0,result[0],result)
    f.install_policy(path,f.config,f.pin)
    owner.assertEqual(0,f.command('complete',path,operation,{'outcome':'Succeeded'})[0])
    operation=str(uuid.uuid4());owner.assertEqual(0,f.command('claim-refresh',operation=operation)[0])
    old=m.Register(f.config['RegisterPath'],f.config['ScopeSha256'])
    with old._open() as db:pending=old._read(db)['pending']
    receipt=dict(id=operation,identitySha256=pending['identity'],outcome='Failed',evidenceSha256=m.digest({'synthetic':'failed local refresh'}))
    owner.assertEqual(0,f.command('complete',operation=operation,receipt=receipt)[0])
    time.sleep(max(0,m.utc(f.scope['endUtc'])/1e6-time.time()+0.05))
    with old._open() as db:before=old._read(db)
    old_config=copy.deepcopy(f.config);old_path=f.config_path;old_pin=f.pin;old_manifest=copy.deepcopy(f.manifest)
    folder=f.new_folder/'unused-renewal7';folder.mkdir();f.new_folder=folder
    approval=m.digest({'synthetic':'new exact unused-renewal approval'})
    consent=m.digest({'synthetic':'new separate private-response consent'})
    policy=copy.deepcopy(policy);policy['SourceApprovalSha256']=approval;policy['SuccessorGrant']['GrantId']=str(uuid.uuid4())
    f.manifest=copy.deepcopy(old_manifest)
    d=f.manifest['diagnostic'];d['successorGrantSha256']=g.sha(json.dumps(policy['SuccessorGrant'],separators=(',',':')).encode())
    d['privateCaptureConsentSha256']=consent
    policy['DiagnosticEvidence'].update(SuccessorGrantSha256=d['successorGrantSha256'],PrivateCaptureConsentSha256=consent)
    manifest_path=folder/'manifest.json';manifest_path.write_text(json.dumps(f.manifest))
    observed=time.time_ns()//1000;mono=time.monotonic_ns()//1000
    stamp=lambda value:datetime.fromtimestamp(value/1e6,timezone.utc).isoformat().replace('+00:00','Z')
    epoch=dict(ObservedUtcMicros=observed,ObservedMonoMicros=mono,TrustedUtcLowerMicros=observed-10_000,
        TrustedUtcUpperMicros=observed+100_000,PreviousLastGoodUtcMicros=before['lastGoodUtc'],
        WindowsBootUtc=before['scope']['startUtc'],BootReadbackSha256=m.digest({'synthetic':'boot readback'}))
    f.scope=dict(f.scope,approvalSha256=approval,privateCaptureConsentSha256=consent,plansSha256=m.digest(f.manifest),
        diagnosticWindowMode='fresh-10-minute',startUtc=stamp(observed),endUtc=stamp(observed+600_000_000),
        predecessorRegisterPath=old_config['RegisterPath'],predecessorScopeSha256=old.scope_sha,
        registerPathSha256=m.path_digest(folder/'register.db'),transitionId=str(uuid.uuid4()))
    f.scope_path=folder/'scope.json';f.scope_path.write_text(json.dumps(f.scope))
    f.config=dict(f.config,RegisterPath=str(folder/'register.db'),ScopeSha256=m.digest(f.scope),
        PlanManifestPath=str(manifest_path),PlanManifestSha256=g.sha(manifest_path.read_bytes()))
    f.config_path=folder/'gate.json';f.config_path.write_text(json.dumps(f.config));f.pin=g.sha(f.config_path.read_bytes())
    f.h=dict(f.h,SchemaVersion=7,TransitionKind='expired-unused-diagnostic-renewal',TransitionId=f.scope['transitionId'],
        SourceApprovalSha256=approval,WindowApprovalSha256=approval,ClockEpochEvidence=epoch,
        InheritedClockEpochEvidence=before['clockEpoch'],PreviousConfigPath=str(old_path),PreviousGateSha256=old_pin,
        PreviousScopeSha256=old.scope_sha,ExpectedPreviousHeadSha256=before['head'],NewConfigPath=str(f.config_path),
        NewGateSha256=f.pin,NewScopePath=str(f.scope_path),NewScopeSha256=f.config['ScopeSha256'],
        DatabaseSha256=g.sha(f.f.db.read_bytes()),OriginalManifestSha256=m.digest(old_manifest),
        UsedRefreshes=before['refresh'],UsedAdmissions=before['admissions'],UsedResumeAdmissions=before['resume-admission'])
    f.h_path=folder/'handoff.json';f.h_path.write_text(json.dumps(f.h));f.h_pin=g.sha(f.h_path.read_bytes())
    policy['ExpiresAtUtc']=m.effective_deadline(f.scope,epoch)
    path=folder/'diagnostic-policy.json';path.write_text(json.dumps(policy,separators=(',',':')))
    return f,path,policy,old,before


class UnusedRenewalCliTests(unittest.TestCase):
    def test_real_paired_transition_rejects_child_and_changed_funding_then_requires_new_admission(self):
        f,path,policy,old,before=prepare_unused_renewal(self)
        original=f.f.db.read_bytes();parent=m.uuid_identity(policy['SuccessorGrant']['ParentAuthorizationId'])
        def inspect_changed_snapshot():
            saved=f.h_path.read_bytes();pin=f.h_pin
            changed=dict(f.h,DatabaseSha256=g.sha(f.f.db.read_bytes()))
            f.h_path.write_text(json.dumps(changed));f.h_pin=g.sha(f.h_path.read_bytes())
            try:return f.command('inspect-successor')
            finally:f.h_path.write_bytes(saved);f.h_pin=pin
        with closing(sqlite3.connect(f.f.db)) as db,db:
            db.execute('UPDATE chirp_article_attempts SET ReservedMinorUnits=ReservedMinorUnits+1 WHERE AuthorizationId=?',(parent,))
        result=inspect_changed_snapshot();self.assertNotEqual(0,result[0]);self.assertEqual('DiagnosticFinancialChanged',result[1]['code'])
        with closing(sqlite3.connect(f.f.db)) as db,db:
            db.execute('UPDATE chirp_article_attempts SET ReservedMinorUnits=ReservedMinorUnits-1 WHERE AuthorizationId=?',(parent,))
        # Restore only the caller-owned synthetic fixture bytes; never a real profile.
        f.f.db.write_bytes(original)
        child=uuid.uuid4().hex
        with closing(sqlite3.connect(f.f.db)) as db,db:
            db.execute('INSERT INTO chirp_article_successor_attempts(AuthorizationId,ParentAuthorizationId,Consumed) VALUES(?,?,0)',(child,parent))
        result=inspect_changed_snapshot();self.assertNotEqual(0,result[0]);self.assertEqual('DiagnosticChildExists',result[1]['code']);f.f.db.write_bytes(original)
        with closing(sqlite3.connect(f.f.db)) as db,db:
            db.execute('UPDATE google_generation_queue_states SET UnresolvedSubmission=1 WHERE LocalAttemptId=?',(policy['SuccessorGrant']['ParentLocalAttemptId'],))
        result=inspect_changed_snapshot();self.assertNotEqual(0,result[0]);self.assertEqual('DiagnosticParent',result[1]['code']);f.f.db.write_bytes(original)
        with closing(sqlite3.connect(f.f.db)) as db,db:
            db.execute('INSERT INTO google_generation_queue_states VALUES(?,?,?,?,?,?,?,?,?,?,?)',
                (f.config['AccountId'],'synthesize-speech','synthetic-other-pending',1,1,None,'google-dispatch-claimed',0,uuid.uuid4().hex,m.digest({'synthetic':'other payload'}),1))
        result=inspect_changed_snapshot();self.assertNotEqual(0,result[0]);self.assertEqual('PendingOrUncertainWork',result[1]['code']);f.f.db.write_bytes(original)
        with old._open() as db:self.assertEqual(before['head'],old._read(db)['head'])
        for command in ('inspect-successor','claim-successor','check-successor'):
            result=f.command(command);self.assertEqual(0,result[0],result)
        # Normal supported readback after a simulated interrupted install.
        self.assertNotEqual(0,f.command('complete-successor')[0])
        f.f.b.write_installed(f.config,f.pin,f.config_path)
        result=f.command('complete-successor');self.assertEqual(0,result[0],result)
        self.assertNotEqual(0,f.command('complete-successor')[0])
        self.assertEqual(original,f.f.db.read_bytes())
        current=m.Register(f.config['RegisterPath'],f.config['ScopeSha256'])
        with current._open() as db:state=current._read(db)
        self.assertEqual(before['refresh'],state['refresh']);self.assertEqual(before['admissions'],state['admissions'])
        self.assertIsNone(state['diagnosticPolicySha256']);self.assertIsNone(state['diagnosticDispatch'])
        count=1;ancestor=state
        while ancestor['scope']['schema']!=1:
            previous=m.Register(ancestor['scope']['predecessorRegisterPath'],ancestor['scope']['predecessorScopeSha256'])
            with previous._open() as db:ancestor=previous._read(db)
            count+=1
        self.assertLessEqual(count,8);self.assertGreaterEqual(count,7)
        operation=str(uuid.uuid4())
        for command in ('claim-completion','check'):
            result=f.command(command,path,operation);self.assertEqual(0,result[0],result)
        f.install_policy(path,f.config,f.pin)
        self.assertEqual(0,f.command('complete',path,operation,{'outcome':'Succeeded'})[0])
        self.assertEqual(before['admissions']+1,f.command('status')[1]['totalAdmissions'])
        self.assertEqual(original,f.f.db.read_bytes())
        refresh=str(uuid.uuid4());self.assertEqual(0,f.command('claim-refresh',operation=refresh)[0])
        with closing(sqlite3.connect(f.f.db)) as writer:
            writer.execute('PRAGMA journal_mode=WAL')
            writer.execute('CREATE TABLE synthetic_startup_metadata(value INTEGER)');writer.commit()
            result=f.command('check',operation=refresh);self.assertEqual(0,result[0],result)


if __name__=='__main__':unittest.main()
