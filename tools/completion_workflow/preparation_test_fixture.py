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


def prepare_clean_reserved_h9(self):
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
    for command in ('inspect-successor','claim-successor','check-successor'):
        result=f.command(command);self.assertEqual(0,result[0],(command,result))
    f.f.b.write_installed(f.config,f.pin,f.config_path)
    result=f.command('complete-successor');self.assertEqual(0,result[0],result)
    self.assertEqual(original_db,f.f.db.read_bytes())
    return f, auth
