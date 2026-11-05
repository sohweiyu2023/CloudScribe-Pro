"""Actual isolated bridge over disposable native-schema SQLite; no HTTP or credentials."""
import copy
from contextlib import closing
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import sqlite3
import subprocess
import sys
import time
import unittest
import uuid

import completion_register as m
import gate_cli as g
from test_sentence_diagnostic_cli import prepare_diagnostic


class ReservedDiagnosticBridgeTests(unittest.TestCase):
    def test_expired_existing_cent_transition_admission_mapping_and_single_dispatch(self):
        f,path,history,_,_=prepare_diagnostic(self,short_window=True)
        f.activate();operation=str(uuid.uuid4())
        for command in ('claim-completion','check'):
            result=f.command(command,path,operation);self.assertEqual(0,result[0],result)
        f.install_policy(path,f.config,f.pin)
        self.assertEqual(0,f.command('complete',path,operation,{'outcome':'Succeeded'})[0])
        old=m.Register(f.config['RegisterPath'],f.config['ScopeSha256'])
        old_config=copy.deepcopy(f.config);old_path=f.config_path;old_pin=f.pin
        old_manifest=copy.deepcopy(f.manifest);old_scope=copy.deepcopy(f.scope)
        auth=uuid.uuid4().hex;d=old_manifest['diagnostic'];grant=history['SuccessorGrant']
        approved=int(time.time()*1000)
        with closing(sqlite3.connect(f.f.db)) as db,db:
            db.row_factory=sqlite3.Row
            db.execute('ALTER TABLE google_generation_queue_states ADD COLUMN OutcomeObservedAtUnixMilliseconds INTEGER NULL')
            a=dict(db.execute('SELECT * FROM chirp_article_attempts WHERE AuthorizationId=?',(m.uuid_identity(grant['ParentAuthorizationId']),)).fetchone())
            envelope=json.loads(a['EnvelopeJson']);envelope.update(ChirpAuthorizationId=str(uuid.UUID(auth)),CredentialReferenceId=f.config['CredentialReferenceId'],CapabilityProvenanceId=str(uuid.uuid4()),PricingProvenanceId=str(uuid.uuid4()))
            a.update(AuthorizationId=auth,PolicySha256=g.sha(path.read_bytes()),SourceApprovalSha256=old_scope['approvalSha256'],
                ChildKey='synthetic-diagnostic:'+auth,EnvelopeJson=json.dumps(envelope,separators=(',',':')),
                ApprovedAtUnixMilliseconds=approved,Consumed=0,ClaimedLocalAttemptId=None,EstimateMinorUnits=1,ReservedMinorUnits=1,
                RootAuthorizationId=m.uuid_identity(grant['RootAuthorizationId']),ParentAuthorizationId=m.uuid_identity(grant['ParentAuthorizationId']),
                ParentStateSha256=grant['ParentQueueRowSha256'],GrantSha256=d['successorGrantSha256'],GrantId=m.uuid_identity(grant['GrantId']))
            db.execute('INSERT INTO chirp_article_successor_attempts('+','.join(a)+') VALUES('+','.join('?' for _ in a)+')',tuple(a.values()))
            db.execute('INSERT INTO google_generation_queue_states(AccountId,OperationStableId,IdempotencyKey,SubmissionAttempted,UnresolvedSubmission,ProviderRequestId,DiagnosticCode,ErrorStatus,LocalAttemptId,PayloadSha256,PayloadBytes) VALUES(?,?,?,?,?,?,?,?,?,?,?)',
                (f.config['AccountId'],'synthesize-speech',a['ChildKey'],0,0,None,None,0,None,None,None))
            child=dict(db.execute('SELECT * FROM chirp_article_successor_attempts WHERE AuthorizationId=?',(auth,)).fetchone())
            queue=dict(db.execute('SELECT * FROM google_generation_queue_states WHERE IdempotencyKey=?',(a['ChildKey'],)).fetchone())
            # Synthetic schema follows the exact production mapping layout.
            db.execute('CREATE TABLE IF NOT EXISTS chirp_reserved_catalog_mappings (AuthorizationId TEXT,MappingId TEXT,PolicyJson TEXT,PolicySha256 TEXT,CreatedAtUnixMilliseconds INTEGER,PriorMappingSha256 TEXT)')
            db.execute('CREATE TABLE IF NOT EXISTS chirp_reserved_dispatch_mappings (AuthorizationId TEXT,MappingId TEXT,PolicySha256 TEXT,ClaimedLocalAttemptId TEXT,ClaimedAtUnixMilliseconds INTEGER)')
        retained=dict(authorizationId=auth,historicalPolicyPath=str(path),historicalPolicySha256=g.sha(path.read_bytes()),
            historicalPolicyCanonicalSha256=m.digest(history),originalApprovalSha256=old_scope['approvalSha256'],
            attemptRowSha256=m.digest({'synthetic':'native owned row hash'}),cleanQueueRowSha256=m.digest({'synthetic':'native queue hash'}),
            financialSha256=m.digest({'synthetic':'native financial snapshot'}),cleanQueueObservedAtUnixMilliseconds=None,ledgerEvidenceSha256=m.digest(child),queueEvidenceSha256=m.digest(queue))
        # Let only this disposable predecessor naturally expire. No OS/live clock changes.
        wait=max(0,m.utc(old_scope['endUtc'])/1e6-time.time()+0.15)
        self.assertLess(wait,91)
        time.sleep(wait)
        with old._open() as db:before=old._read(db)
        folder=f.new_folder/'reserved9';folder.mkdir();f.new_folder=folder
        start=datetime.now(timezone.utc);now=m.utc(start.isoformat().replace('+00:00','Z'));mono=time.monotonic_ns()//1000
        approval=m.digest({'synthetic':'fresh existing reservation authority'})
        consent=m.digest({'synthetic':'fresh one response protected consent'})
        f.manifest=copy.deepcopy(old_manifest)
        f.manifest['diagnostic'].update(reservedResume=retained,privateCaptureConsentSha256=consent,
            projectCommittedMinorUnits=175,allocationCommittedMinorUnits=45)
        manifest_path=folder/'manifest.json';manifest_path.write_text(json.dumps(f.manifest))
        f.scope=dict(old_scope,approvalSha256=approval,privateCaptureConsentSha256=consent,
            diagnosticPurpose='reserved-provider-evidence',diagnosticWindowMode='fresh-10-minute',
            startUtc=start.isoformat().replace('+00:00','Z'),endUtc=(start+timedelta(minutes=10)).isoformat().replace('+00:00','Z'),
            plansSha256=m.digest(f.manifest),predecessorRegisterPath=str(old.path),predecessorScopeSha256=old.scope_sha,
            registerPathSha256=m.path_digest(folder/'register.db'),transitionId=str(uuid.uuid4()))
        f.scope_path=folder/'scope.json';f.scope_path.write_text(json.dumps(f.scope))
        f.config=dict(old_config,RegisterPath=str(folder/'register.db'),ScopeSha256=m.digest(f.scope),
            PlanManifestPath=str(manifest_path),PlanManifestSha256=g.sha(manifest_path.read_bytes()))
        f.config_path=folder/'gate.json';f.config_path.write_text(json.dumps(f.config));f.pin=g.sha(f.config_path.read_bytes())
        epoch=dict(ObservedUtcMicros=now,ObservedMonoMicros=mono,TrustedUtcLowerMicros=now-100_000,TrustedUtcUpperMicros=now+100_000,
            PreviousLastGoodUtcMicros=before['lastGoodUtc'],WindowsBootUtc=old_scope['startUtc'],BootReadbackSha256=consent)
        f.h.update(SchemaVersion=9,TransitionKind='expired-reserved-diagnostic-resume',TransitionId=f.scope['transitionId'],
            SourceApprovalSha256=approval,WindowApprovalSha256=approval,PreviousConfigPath=str(old_path),PreviousGateSha256=old_pin,
            PreviousScopeSha256=old.scope_sha,ExpectedPreviousHeadSha256=before['head'],NewConfigPath=str(f.config_path),NewGateSha256=f.pin,
            NewScopePath=str(f.scope_path),NewScopeSha256=f.config['ScopeSha256'],DatabaseSha256=g.sha(f.f.db.read_bytes()),
            OriginalManifestSha256=m.digest(old_manifest),InheritedClockEpochEvidence=before['clockEpoch'],ClockEpochEvidence=epoch,
            UsedRefreshes=before['refresh'],UsedAdmissions=before['admissions'],UsedResumeAdmissions=before['resume-admission'])
        f.h_path=folder/'handoff.json';f.h_path.write_text(json.dumps(f.h));f.h_pin=g.sha(f.h_path.read_bytes())
        original_db=f.f.db.read_bytes()
        import shutil
        pwsh=shutil.which('pwsh')
        self.assertIsNotNone(pwsh,'Supported PowerShell7 is required for native installer reader coverage')
        metadata=folder/'installer-test.json'
        metadata.write_text(json.dumps(dict(package=str(f.config['CandidateApplicationDirectory'] and Path(f.config['CandidateApplicationDirectory']).parent),
            target=str(Path(f.config['ApplicationDirectory']).parent),profile=str(f.f.b.f.profile),gatePath=str(f.config_path),
            gateSha256=f.pin,handoffPath=str(f.h_path),handoffSha256=f.h_pin)))
        checked=subprocess.run([pwsh,'-NoProfile','-File',str(Path(__file__).resolve().parents[2]/'scripts/Test-ReservedDiagnosticResume-SelfTests.ps1'),
            '-MetadataPath',str(metadata)],capture_output=True,text=True,timeout=30)
        self.assertEqual(0,checked.returncode,checked.stdout+checked.stderr)
        for command in ('inspect-successor','claim-successor','check-successor'):
            result=f.command(command);self.assertEqual(0,result[0],(command,result))
        f.f.b.write_installed(f.config,f.pin,f.config_path)
        result=f.command('complete-successor');self.assertEqual(0,result[0],result)
        self.assertEqual(original_db,f.f.db.read_bytes())
        policy=dict(SchemaVersion=1,MappingId=str(uuid.uuid4()),AppSha256=f.config['AppSha256'],InfrastructureSha256=f.config['InfrastructureSha256'],
            ProfileRootSha256=f.config['ProfileRootSha256'],SourceApprovalSha256=approval,ExpiresAtUtc=m.effective_deadline(f.scope,epoch),
            HistoricalPolicy=history,AuthorizationId=str(uuid.UUID(auth)),OriginalAttemptRowSha256=retained['attemptRowSha256'],
            CleanQueueRowSha256=retained['cleanQueueRowSha256'],FinancialSha256=retained['financialSha256'],
            OriginalApprovedAtUtc=datetime.fromtimestamp(approved/1000,timezone.utc).isoformat().replace('+00:00','Z'),
            OriginalEnvelope=envelope,ExecutionEnvelope=dict(envelope,RequestRevision=envelope['RequestRevision']+1),
            CatalogSnapshotId=str(uuid.uuid4()),CatalogExpiresAtUtc=f.scope['endUtc'],ProviderEvidenceSha256=m.digest({'synthetic':'provider'}),
            SelectionSha256=m.digest({'synthetic':'selection'}),RuntimeEvidenceSha256=m.digest({'synthetic':'runtime'}),
            RequestIdentitySha256=m.digest({'synthetic':'request'}),MaximumExistingDispatches=1,MaximumNewReservations=0,MaximumAdditionalReservationMinorUnits=0)
        resume_path=folder/'resume.json';resume_path.write_text(json.dumps(policy,separators=(',',':')))
        op=str(uuid.uuid4())
        for command in ('claim-resume','check'):
            result=f.command(command,resume_path,op);self.assertEqual(0,result[0],(command,result))
        marker_path=Path(f.config['ApplicationDirectory']).parent/'cloudscribe-install.json'
        marker=json.loads(marker_path.read_bytes());marker['ChirpReservationResumePolicySha256']=g.sha(resume_path.read_bytes())
        marker_path.write_text(json.dumps(marker));(marker_path.parent/'chirp-reservation-resume-admission.json').write_bytes(resume_path.read_bytes())
        result=f.command('complete',resume_path,op,{'outcome':'Succeeded'});self.assertEqual(0,result[0],result)
        self.assertEqual(original_db,f.f.db.read_bytes())
        current=m.Register(f.config['RegisterPath'],f.config['ScopeSha256'])
        with current._open() as db:state=current._read(db)
        self.assertEqual(before['admissions']+1,state['admissions']);self.assertEqual(before['resume-admission']+1,state['resume-admission'])
        self.assertNotEqual(0,f.command('claim-resume',resume_path,str(uuid.uuid4()))[0])
        # Simulate the existing native service's atomic mapping and claim. This
        # is not a substitute for its independent .NET financial/runtime tests.
        pin=g.sha(resume_path.read_bytes());mapping=uuid.UUID(policy['MappingId']).hex;local=uuid.uuid4().hex;claim_time=int(time.time()*1000)
        with closing(sqlite3.connect(f.f.db)) as db,db:
            db.execute('INSERT INTO chirp_reserved_catalog_mappings VALUES(?,?,?,?,?,?)',(auth,mapping,resume_path.read_text(),pin,claim_time-1,None))
            db.execute('INSERT INTO chirp_reserved_dispatch_mappings VALUES(?,?,?,?,?)',(auth,mapping,pin,local,int(time.time()*1000)))
            db.execute('UPDATE chirp_article_successor_attempts SET Consumed=1,ClaimedLocalAttemptId=? WHERE AuthorizationId=?',(local,auth))
            db.execute("UPDATE google_generation_queue_states SET SubmissionAttempted=1,UnresolvedSubmission=1,DiagnosticCode='google-dispatch-claimed',LocalAttemptId=?,PayloadSha256=?,PayloadBytes=?,OutcomeObservedAtUnixMilliseconds=? WHERE IdempotencyKey=?",(local,d['payloadSha256'],d['payloadBytes'],claim_time,a['ChildKey']))
        correlation=str(uuid.uuid4())
        request=dict(command='check-diagnostic-dispatch',configPath=str(f.config_path),configSha256=f.pin,appDirectory=f.config['ApplicationDirectory'],
            id=correlation,identitySha256=d['payloadSha256'],payloadSha256=d['payloadSha256'],receipt=None)
        def dispatch():
            result=subprocess.run([sys.executable,'-I','-S',f.config['ScriptPath']],input=json.dumps(request),capture_output=True,text=True,timeout=25)
            self.assertEqual('',result.stderr);return result.returncode,json.loads(result.stdout)
        for _ in range(2):
            result=dispatch();self.assertEqual(0,result[0],result);self.assertTrue(result[1]['requireProtectedCapture'])
        self.assertNotEqual(0,dispatch()[0])
        with closing(sqlite3.connect(f.f.db)) as db:
            self.assertEqual(1,db.execute('SELECT COUNT(*) FROM chirp_article_successor_attempts').fetchone()[0])
            self.assertEqual(1,db.execute('SELECT ReservedMinorUnits FROM chirp_article_successor_attempts WHERE AuthorizationId=?',(auth,)).fetchone()[0])


if __name__=='__main__':unittest.main()
