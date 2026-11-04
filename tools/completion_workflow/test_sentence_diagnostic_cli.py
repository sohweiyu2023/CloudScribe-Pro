"""Actual isolated bridge and inert native-schema data; never real profiles or HTTP."""
import copy
from contextlib import closing
import json
from pathlib import Path
import sqlite3
import subprocess
import sys
import unittest
from unittest.mock import patch
import uuid
import completion_register as m
import gate_cli as g
import test_sentence_replan_cli as previous


def prepare_diagnostic(owner, expired=False, short_window=False):
    fixture=previous.SentenceCliTests();owner.addCleanup(fixture.doCleanups)
    f,_,_,_,proposal,new_parts,first,_,old_policy,reuse,_,_,_=fixture.prepare(whole_end_after=90 if expired or short_window else None)
    f.activate()
    probe=m.digest({'synthetic':'legacy transport payload'})
    request=dict(command='check-diagnostic-dispatch',configPath=str(f.config_path),configSha256=f.pin,
        appDirectory=f.config['ApplicationDirectory'],id=str(uuid.uuid4()),identitySha256=probe,payloadSha256=probe,receipt=None)
    response=subprocess.run([sys.executable,'-I','-S',f.config['ScriptPath']],input=json.dumps(request),capture_output=True,text=True,timeout=25)
    owner.assertEqual(0,response.returncode,response.stdout);legacy=json.loads(response.stdout)
    owner.assertIs(legacy['requireProtectedCapture'],False);owner.assertIsNone(legacy['idempotencyKeySha256'])
    request.update(command='check-diagnostic-admission',id=str(uuid.UUID(int=0)));request.pop('payloadSha256')
    response=subprocess.run([sys.executable,'-I','-S',f.config['ScriptPath']],input=json.dumps(request),capture_output=True,text=True,timeout=25)
    owner.assertNotEqual(0,response.returncode);owner.assertEqual('DiagnosticScope',json.loads(response.stdout)['code'])
    pilot=copy.deepcopy(old_policy);pilot.pop('WholeArticleGeneration',None)
    pilot.update(SchemaVersion=2,SourceApprovalSha256=f.scope['approvalSha256'],ExpiresAtUtc=f.scope['endUtc'],
        MaximumNewAttempts=2,MaximumAdditionalReservationMinorUnits=2,
        Articles=[dict(Proposal=proposal,ReusedAudio=reuse,AllowedSegments=[dict(CoverageOrdinal=x['coverageOrdinal'],MaximumMinorUnits=1) for x in first])])
    pilot['Budget'].update(ProjectCommittedMinorUnits=173,AllocationCommittedMinorUnits=43,ProjectCeilingMinorUnits=289,AllocationCeilingMinorUnits=159)
    path=f.new_folder/'prior-pilot.json';path.write_text(json.dumps(pilot));op=str(uuid.uuid4())
    for command in ('claim-completion','check'):
        result=f.command(command,path,op);owner.assertEqual(0,result[0],result)
    f.install_policy(path,f.config,f.pin);result=f.command('complete',path,op,{'outcome':'Succeeded'});owner.assertEqual(0,result[0],result)
    with closing(sqlite3.connect(f.f.db)) as db,db:
        db.execute('INSERT INTO chirp_article_plans VALUES(?,?,?,?)',(proposal['PlanSha256'],json.dumps(proposal),f.config['AccountId'],f.config['ProjectId']))
    f.f.proposal=proposal;failed,_=f.add_attempt(first[0]['coverageOrdinal'],pilot,False);local=uuid.uuid4().hex
    with closing(sqlite3.connect(f.f.db)) as db,db:
        db.row_factory=sqlite3.Row;row=dict(db.execute('SELECT * FROM chirp_article_attempts WHERE AuthorizationId=?',(failed,)).fetchone())
        db.execute('UPDATE chirp_article_attempts SET Consumed=1,ClaimedLocalAttemptId=? WHERE AuthorizationId=?',(local,failed))
        db.execute("UPDATE google_generation_queue_states SET SubmissionAttempted=1,UnresolvedSubmission=0,DiagnosticCode='google-http-400',ErrorStatus=1,LocalAttemptId=? WHERE IdempotencyKey=?",(local,row['ChildKey']))
    old_config=copy.deepcopy(f.config);old_path=f.config_path;old_pin=f.pin;old_manifest=copy.deepcopy(f.manifest)
    old=m.Register(old_config['RegisterPath'],old_config['ScopeSha256'])
    with old._open() as db:before=old._read(db)
    folder=f.new_folder/'diagnostic6';folder.mkdir();f.new_folder=folder
    approval=m.digest({'synthetic':'single diagnostic approval'});consent=m.digest({'synthetic':'separate protected private consent'})
    grant=dict(GrantId=str(uuid.uuid4()),RootAuthorizationId=str(uuid.UUID(failed)),ParentAuthorizationId=str(uuid.UUID(failed)),
        PlanSha256=proposal['PlanSha256'],CoverageOrdinal=first[0]['coverageOrdinal'],ParentKey=row['ChildKey'],
        ParentAttemptRowSha256=m.digest({'synthetic':'native parent pin'}),ParentQueueRowSha256=m.digest({'synthetic':'native queue pin'}),
        ParentLocalAttemptId=local,CredentialReferenceId=f.config['CredentialReferenceId'],ReviewedFailureEvidenceSha256=m.digest({'synthetic':'confirmed failure'}),
        MaximumNewAttempts=1,MaximumAdditionalReservationMinorUnits=1)
    part=next(x for x in new_parts if x['coverageOrdinal']==grant['CoverageOrdinal'])
    d=dict(proposalSha256=g.sha(json.dumps(proposal,separators=(',',':')).encode()),successorGrantSha256=g.sha(json.dumps(grant,separators=(',',':')).encode()),
        privateCaptureConsentSha256=consent,failedAuthorizationId=str(uuid.UUID(failed)),failedLocalAttemptId=local,
        planSha256=proposal['PlanSha256'],coverageOrdinal=grant['CoverageOrdinal'],payloadSha256=part['payloadSha256'],payloadBytes=part['payloadBytes'],
        financialRowsSha256=f.financial_pin(),projectCommittedMinorUnits=174,allocationCommittedMinorUnits=44)
    f.manifest=dict(old_manifest,diagnostic=d);manifest_path=folder/'manifest.json';manifest_path.write_text(json.dumps(f.manifest))
    f.scope=dict(f.scope,schema=6,approvalSha256=approval,maximumNewAttempts=1,diagnosticPurpose='unchanged-provider-evidence',diagnosticWindowMode='inherited',privateCaptureConsentSha256=consent,
        predecessorRegisterPath=old_config['RegisterPath'],predecessorScopeSha256=old_config['ScopeSha256'],transitionId=str(uuid.uuid4()),
        plansSha256=m.digest(f.manifest),registerPathSha256=m.path_digest(folder/'register.db'))
    f.scope.pop('wholeArticleGrantSha256');f.scope_path=folder/'scope.json';f.scope_path.write_text(json.dumps(f.scope))
    f.config=dict(f.config,RegisterPath=str(folder/'register.db'),ScopeSha256=m.digest(f.scope),PlanManifestPath=str(manifest_path),PlanManifestSha256=g.sha(manifest_path.read_bytes()))
    f.config_path=folder/'gate.json';f.config_path.write_text(json.dumps(f.config));f.pin=g.sha(f.config_path.read_bytes())
    failure=dict(FailedAuthorizationId=d['failedAuthorizationId'],FailedLocalAttemptId=local,FailedPlanSha256=d['planSha256'],
        FailedCoverageOrdinal=d['coverageOrdinal'],FailedPayloadSha256=d['payloadSha256'],FailedPayloadBytes=d['payloadBytes'])
    f.h=dict(SchemaVersion=5,TransitionKind='sentence-evidence-diagnostic',TransitionId=f.scope['transitionId'],SourceApprovalSha256=approval,
        PreviousConfigPath=str(old_path),PreviousGateSha256=old_pin,PreviousScopeSha256=old.scope_sha,ExpectedPreviousHeadSha256=before['head'],
        NewConfigPath=str(f.config_path),NewGateSha256=f.pin,NewScopePath=str(f.scope_path),NewScopeSha256=f.config['ScopeSha256'],
        DatabaseSha256=g.sha(f.f.db.read_bytes()),OriginalManifestSha256=m.digest(old_manifest),AcceptedEvidenceSha256=f.scope['acceptedEvidenceSha256'],
        UsedRefreshes=before['refresh'],UsedAdmissions=before['admissions'],UsedResumeAdmissions=before['resume-admission'],
        InheritedClockEpochEvidence=before['clockEpoch'],**failure)
    f.h_path=folder/'handoff.json';f.h_path.write_text(json.dumps(f.h));f.h_pin=g.sha(f.h_path.read_bytes())
    policy=copy.deepcopy(pilot);policy.update(SchemaVersion=4,SourceApprovalSha256=approval,MaximumNewAttempts=1,MaximumAdditionalReservationMinorUnits=1,
        SuccessorGrant=grant,DiagnosticEvidence=dict(Version=1,Purpose='unchanged-provider-evidence',ProposalSha256=d['proposalSha256'],
            SuccessorGrantSha256=d['successorGrantSha256'],PrivateCaptureConsentSha256=consent,RequiredCaptureMode=1))
    policy['Budget'].update(ProjectCommittedMinorUnits=174,AllocationCommittedMinorUnits=44,ProjectCeilingMinorUnits=175,AllocationCeilingMinorUnits=45)
    policy['Articles'][0]['AllowedSegments']=[dict(CoverageOrdinal=d['coverageOrdinal'],MaximumMinorUnits=1)]
    path=folder/'diagnostic-policy.json';path.write_text(json.dumps(policy,separators=(',',':')))
    if expired:
        import time
        from datetime import datetime,timedelta,timezone
        time.sleep(max(0,m.utc(f.scope['endUtc'])/1e6-time.time()+0.05))
        with old._open() as db:before=old._read(db)
        start=datetime.now(timezone.utc);observed=m.utc(start.isoformat().replace('+00:00','Z'));mono=time.monotonic_ns()//1000
        epoch=dict(ObservedUtcMicros=observed,ObservedMonoMicros=mono,TrustedUtcLowerMicros=observed-10_000,TrustedUtcUpperMicros=observed+100_000,
            PreviousLastGoodUtcMicros=before['lastGoodUtc'],WindowsBootUtc=before['scope']['startUtc'],BootReadbackSha256=m.digest({'synthetic':'boot metadata'}))
        f.scope.update(diagnosticWindowMode='fresh-10-minute',startUtc=start.isoformat().replace('+00:00','Z'),endUtc=(start+timedelta(minutes=10)).isoformat().replace('+00:00','Z'))
        f.scope_path.write_text(json.dumps(f.scope));f.config['ScopeSha256']=m.digest(f.scope)
        f.config_path.write_text(json.dumps(f.config));f.pin=g.sha(f.config_path.read_bytes())
        f.h.update(SchemaVersion=6,TransitionKind='expired-sentence-evidence-diagnostic',ClockEpochEvidence=epoch,WindowApprovalSha256=f.scope['approvalSha256'],
            NewGateSha256=f.pin,NewScopeSha256=f.config['ScopeSha256'])
        f.h_path.write_text(json.dumps(f.h));f.h_pin=g.sha(f.h_path.read_bytes())
        policy['ExpiresAtUtc']=m.effective_deadline(f.scope,epoch);path.write_text(json.dumps(policy,separators=(',',':')))
    return f,path,policy,old,before


class DiagnosticCliTests(unittest.TestCase):
    def admitted_fixture(self):
        f,path,policy,_,_=prepare_diagnostic(self)
        f.activate();operation=str(uuid.uuid4())
        for command in ('claim-completion','check'):
            result=f.command(command,path,operation);self.assertEqual(0,result[0],result)
        f.install_policy(path,f.config,f.pin)
        result=f.command('complete',path,operation,{'outcome':'Succeeded'})
        self.assertEqual(0,result[0],result)
        return f,path,policy

    def test_owned_refresh_reads_real_open_wal_and_retains_identity_financial_guards(self):
        f,path,policy=self.admitted_fixture()
        operation=str(uuid.uuid4())
        self.assertEqual(0,f.command('claim-refresh',operation=operation)[0])
        with closing(sqlite3.connect(f.f.db)) as writer:
            self.assertEqual('wal',writer.execute('PRAGMA journal_mode=WAL').fetchone()[0])
            writer.execute('CREATE TABLE synthetic_nonfinancial_startup (value INTEGER)')
            writer.execute('INSERT INTO synthetic_nonfinancial_startup VALUES(1)');writer.commit()
            self.assertGreater(Path(str(f.f.db)+'-wal').stat().st_size,1)
            with self.assertRaisesRegex(ValueError,'ClosedDatabaseRequired'):
                g.profile_database(f.config)
            # A WAL does not confer ownership or permit a new/replayed request.
            self.assertNotEqual(0,f.command('check',operation=str(uuid.uuid4()))[0])
            result=f.command('check',operation=operation);self.assertEqual(0,result[0],result)
            physical=m.physical
            for suffix in ('-wal','-shm'):
                def reject_link(value):
                    if str(value)==str(f.f.db)+suffix:raise m.Rejected('LinkedPath')
                    return physical(value)
                with patch.object(m,'physical',side_effect=reject_link):
                    with self.assertRaisesRegex(m.Rejected,'LinkedPath'):
                        g.diagnostic_acceptance(f.config,f.manifest,f.scope,m,live_readonly=True)
            parent=m.uuid_identity(policy['SuccessorGrant']['ParentAuthorizationId'])
            writer.execute('UPDATE chirp_article_attempts SET ReservedMinorUnits=ReservedMinorUnits+1 WHERE AuthorizationId=?',(parent,));writer.commit()
            result=f.command('check',operation=operation)
            self.assertNotEqual(0,result[0]);self.assertEqual('DiagnosticFinancialChanged',result[1]['code'])
            writer.execute('UPDATE chirp_article_attempts SET ReservedMinorUnits=ReservedMinorUnits-1 WHERE AuthorizationId=?',(parent,));writer.commit()
            writer.execute('UPDATE google_generation_queue_states SET UnresolvedSubmission=1 WHERE LocalAttemptId=?',(policy['SuccessorGrant']['ParentLocalAttemptId'],));writer.commit()
            self.assertNotEqual(0,f.command('check',operation=operation)[0])
            writer.execute('UPDATE google_generation_queue_states SET UnresolvedSubmission=0 WHERE LocalAttemptId=?',(policy['SuccessorGrant']['ParentLocalAttemptId'],));writer.commit()
            result=f.command('check',operation=operation);self.assertEqual(0,result[0],result)

    def test_non_refresh_admission_keeps_closed_database_requirement(self):
        f,path,policy,_,_=prepare_diagnostic(self);f.activate();operation=str(uuid.uuid4())
        self.assertEqual(0,f.command('claim-completion',path,operation)[0])
        with closing(sqlite3.connect(f.f.db)) as writer:
            writer.execute('PRAGMA journal_mode=WAL')
            writer.execute('CREATE TABLE synthetic_nonfinancial_startup (value INTEGER)');writer.commit()
            result=f.command('check',path,operation)
            self.assertNotEqual(0,result[0]);self.assertEqual('ClosedDatabaseRequired',result[1]['code'])
            self.assertEqual('SuccessorPending',result[1]['reason'])
            self.assertEqual('bridge',result[1]['failureComponent'])

    def test_expired_actual_bridge_installs_only_fresh_ten_minute_scope(self):
        f,path,policy,old,before=prepare_diagnostic(self,expired=True)
        database=f.f.db.read_bytes();f.activate();self.assertEqual(database,f.f.db.read_bytes())
        status=f.command('status');self.assertEqual(0,status[0],status)
        self.assertEqual(before['admissions'],status[1]['totalAdmissions'])
        self.assertEqual(policy['ExpiresAtUtc'],status[1]['deadlineUtc'])
        op=str(uuid.uuid4())
        for command in ('claim-completion','check'):
            result=f.command(command,path,op);self.assertEqual(0,result[0],result)
        f.install_policy(path,f.config,f.pin);result=f.command('complete',path,op,{'outcome':'Succeeded'});self.assertEqual(0,result[0],result)
        self.assertEqual(0,f.command('verify-completion',path)[0])
    def test_actual_paired_install_admit_owned_dispatch_twice_no_replay(self):
        f,path,policy,old,before=prepare_diagnostic(self)
        legacy=Path(json.loads(next((f.f.b.f.profile/'accepted-article-audio').glob('*.json')).read_text())['AudioPath'])
        prior=legacy.read_bytes();legacy.write_bytes(b'INERT TAMPER')
        self.assertNotEqual(0,f.command('inspect-successor')[0]);legacy.write_bytes(prior)
        database_before=f.f.db.read_bytes();f.activate();self.assertEqual(database_before,f.f.db.read_bytes())
        original=path.read_bytes();path.write_text(json.dumps(policy,indent=2))
        result=f.command('claim-completion',path,str(uuid.uuid4()))
        self.assertNotEqual(0,result[0]);self.assertEqual('DiagnosticNativePin',result[1]['code']);path.write_bytes(original)
        op=str(uuid.uuid4())
        for command in ('claim-completion','check'):
            result=f.command(command,path,op);self.assertEqual(0,result[0],result)
        f.install_policy(path,f.config,f.pin);result=f.command('complete',path,op,{'outcome':'Succeeded'});self.assertEqual(0,result[0],result)
        result=f.command('verify-completion',path);self.assertEqual(0,result[0],result)
        def admission_probe(identity=None):
            req=dict(command='check-diagnostic-admission',configPath=str(f.config_path),configSha256=f.pin,
                appDirectory=f.config['ApplicationDirectory'],id=str(uuid.UUID(int=0)),identitySha256=identity or g.sha(path.read_bytes()),receipt=None)
            p=subprocess.run([sys.executable,'-I','-S',f.config['ScriptPath']],input=json.dumps(req),capture_output=True,text=True,timeout=25)
            self.assertEqual('',p.stderr);return p.returncode,json.loads(p.stdout)
        result=admission_probe();self.assertEqual(0,result[0],result);self.assertEqual(g.sha(path.read_bytes()),result[1]['policySha256'])
        self.assertNotEqual(0,admission_probe(m.digest({'wrong':'policy pin'}))[0])
        grant=policy['SuccessorGrant'];auth=uuid.uuid4().hex;local=uuid.uuid4().hex;pin=g.sha(path.read_bytes())
        with closing(sqlite3.connect(f.f.db)) as db,db:
            db.row_factory=sqlite3.Row;a=dict(db.execute('SELECT * FROM chirp_article_attempts WHERE AuthorizationId=?',(m.uuid_identity(grant['ParentAuthorizationId']),)).fetchone())
            envelope=json.loads(a['EnvelopeJson']);envelope.update(ChirpAuthorizationId=str(uuid.UUID(auth)),CredentialReferenceId=f.config['CredentialReferenceId'])
            a.update(AuthorizationId=auth,PolicySha256=pin,SourceApprovalSha256=f.scope['approvalSha256'],ChildKey='synthetic-diagnostic-'+auth,
                EnvelopeJson=json.dumps(envelope),Consumed=1,ClaimedLocalAttemptId=local,EstimateMinorUnits=1,ReservedMinorUnits=1,
                RootAuthorizationId=m.uuid_identity(grant['RootAuthorizationId']),ParentAuthorizationId=m.uuid_identity(grant['ParentAuthorizationId']),
                ParentStateSha256=grant['ParentQueueRowSha256'],GrantSha256=f.manifest['diagnostic']['successorGrantSha256'],GrantId=m.uuid_identity(grant['GrantId']))
            db.execute('INSERT INTO chirp_article_successor_attempts('+','.join(a)+') VALUES('+','.join('?' for _ in a)+')',tuple(a.values()))
            d=f.manifest['diagnostic'];db.execute('INSERT INTO google_generation_queue_states VALUES(?,?,?,?,?,?,?,?,?,?,?)',
                (f.config['AccountId'],'synthesize-speech',a['ChildKey'],1,1,None,'google-dispatch-claimed',0,local,d['payloadSha256'],d['payloadBytes']))
        correlation=str(uuid.uuid4())
        def dispatch(payload=None,identity=None):
            payload=payload or f.manifest['diagnostic']['payloadSha256']
            request=dict(command='check-diagnostic-dispatch',configPath=str(f.f.b.f.app.parent/'completion-workflow-gate.json'),configSha256=f.pin,
                appDirectory=f.config['ApplicationDirectory'],id=identity or correlation,identitySha256=payload,payloadSha256=payload,receipt=None)
            p=subprocess.run([sys.executable,'-I','-S',f.config['ScriptPath']],input=json.dumps(request),capture_output=True,text=True,timeout=25)
            self.assertEqual('',p.stderr);return p.returncode,json.loads(p.stdout)
        self.assertNotEqual(0,dispatch(m.digest({'wrong':'payload'}))[0])
        for field,value in [('UnresolvedSubmission',0),('ProviderRequestId','synthetic-outcome'),('DiagnosticCode','google-http-400')]:
            with closing(sqlite3.connect(f.f.db)) as db,db:
                prior=db.execute('SELECT '+field+' FROM google_generation_queue_states WHERE IdempotencyKey=?',(a['ChildKey'],)).fetchone()[0]
                db.execute('UPDATE google_generation_queue_states SET '+field+'=? WHERE IdempotencyKey=?',(value,a['ChildKey']))
            self.assertNotEqual(0,dispatch()[0])
            with closing(sqlite3.connect(f.f.db)) as db,db:db.execute('UPDATE google_generation_queue_states SET '+field+'=? WHERE IdempotencyKey=?',(prior,a['ChildKey']))
        with closing(sqlite3.connect(f.f.db)) as db,db:
            db.execute('INSERT INTO chirp_article_output_journals VALUES(?,?,?)',(auth,'synthetic accepted-before-save',g.sha(b'synthetic journal')))
        self.assertNotEqual(0,dispatch()[0])
        with closing(sqlite3.connect(f.f.db)) as db,db:db.execute('DELETE FROM chirp_article_output_journals WHERE AuthorizationId=?',(auth,))
        for ordinal in (1,2):
            result=dispatch();self.assertEqual(0,result[0],result)
            self.assertEqual(correlation,result[1]['id'])
            self.assertIs(result[1]['requireProtectedCapture'],True)
            self.assertEqual(g.sha(a['ChildKey'].encode()),result[1]['idempotencyKeySha256'])
            status=f.command('status');self.assertEqual(0,status[0],status)
        self.assertNotEqual(0,dispatch()[0]);self.assertNotEqual(0,dispatch(identity=str(uuid.uuid4()))[0])
        self.assertEqual(before['admissions']+1,f.command('status')[1]['totalAdmissions'])
        # Read-only admission verification is still valid AFTER the owned claim; it is not a dispatch permit.
        self.assertEqual(0,admission_probe()[0])
        import time
        now=time.time_ns()//1000;mono=time.monotonic_ns()//1000;delta=m.utc(f.scope['endUtc'])-now+1_000_000
        expired=m.Register(f.config['RegisterPath'],f.config['ScopeSha256'],lambda:now+delta,lambda:mono+delta)
        with self.assertRaisesRegex(m.Rejected,'WindowExpired'):
            g.diagnostic_admission_check(expired,f.config,f.pin,f.manifest,f.scope,m,Path(f.config['ApplicationDirectory']),g.sha(path.read_bytes()))


if __name__=='__main__':unittest.main()
