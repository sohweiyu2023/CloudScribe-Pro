"""Real -I -S bridge entrypoints with inert synthetic artifacts, no provider."""
import copy
from contextlib import closing
import json
from pathlib import Path
import sqlite3
import time
from datetime import datetime, timedelta, timezone
import unittest
import uuid

import completion_register as m
import interpreter_test_fixture as fixture

sha=fixture.fixtures.sha


def synthetic_reboot(f,old):
    """Construct ONLY a disposable test clock epoch; never edits a production journal."""
    import tempfile,time
    from datetime import datetime,timezone
    if any(p.is_symlink() or p.is_junction() for p in (old.path,*old.path.parents)) or not old.path.resolve().is_relative_to(Path(tempfile.gettempdir()).resolve()):raise ValueError('SyntheticRootRequired')
    with old._open() as db:
        db.execute('DROP TRIGGER immutable_events_update')
        previous=old.scope_sha
        for seq,body in db.execute('SELECT seq,body FROM events ORDER BY seq').fetchall():
            event=json.loads(body);event['mono']+=9_000_000_000_000
            current=m.digest({'previous':previous,'event':event})
            db.execute('UPDATE events SET body=?,sha=? WHERE seq=?',(m.canonical(event),current,seq));previous=current
        db.execute("CREATE TRIGGER immutable_events_update BEFORE UPDATE ON events BEGIN SELECT RAISE(ABORT,'immutable'); END")
        db.commit();old.anchor.write_bytes(b''.join(f'{i}:{pin}\n'.encode() for i,pin in db.execute('SELECT seq,sha FROM events ORDER BY seq')))
        state=old._read(db)
    observed=time.time_ns()//1000;mono=time.monotonic_ns()//1000
    e=dict(ObservedUtcMicros=observed,ObservedMonoMicros=mono,TrustedUtcLowerMicros=observed-100_000,
        TrustedUtcUpperMicros=observed+100_000,PreviousLastGoodUtcMicros=state['lastGoodUtc'],
        WindowsBootUtc=datetime.fromtimestamp((state['lastGoodUtc']+1)/1e6,timezone.utc).isoformat().replace('+00:00','Z'),
        BootReadbackSha256=m.digest({'synthetic':'boot fixture, not real evidence'}))
    f.h.update(TransitionKind='same-window-sentence-replan-after-reboot',ExpectedPreviousHeadSha256=state['head'],ClockEpochEvidence=e)
    f.h_path.write_text(json.dumps(f.h));f.h_pin=sha(f.h_path.read_bytes())
    return datetime.fromtimestamp(m.epoch_deadline(f.scope,e)/1e6,timezone.utc).isoformat().replace('+00:00','Z')


def synthetic_expired_window(f,old):
    """Disposable old scope expires naturally; no OS clock or live data changes."""
    import tempfile
    if any(p.is_symlink() or p.is_junction() for p in (old.path,*old.path.parents)) or not old.path.resolve().is_relative_to(Path(tempfile.gettempdir()).resolve()):raise ValueError('SyntheticRootRequired')
    with old._open() as db:before=old._read(db)
    time.sleep(max(0,m.utc(before['scope']['endUtc'])/1e6-time.time()+0.25))
    start=datetime.now(timezone.utc);observed=m.utc(start.isoformat().replace('+00:00','Z'));mono=time.monotonic_ns()//1000
    e=dict(ObservedUtcMicros=observed,ObservedMonoMicros=mono,TrustedUtcLowerMicros=observed-100_000,
        TrustedUtcUpperMicros=observed+100_000,PreviousLastGoodUtcMicros=before['lastGoodUtc'],
        WindowsBootUtc=before['scope']['startUtc'],BootReadbackSha256=m.digest({'synthetic':'expired-window session metadata'}))
    f.scope.update(startUtc=start.isoformat().replace('+00:00','Z'),endUtc=(start+timedelta(hours=6)).isoformat().replace('+00:00','Z'))
    f.scope_path.write_text(json.dumps(f.scope));f.config['ScopeSha256']=m.digest(f.scope)
    f.config_path.write_text(json.dumps(f.config));f.pin=sha(f.config_path.read_bytes())
    f.h.update(TransitionKind='expired-terminal-sentence-replan',ExpectedPreviousHeadSha256=before['head'],
        NewGateSha256=f.pin,NewScopeSha256=f.config['ScopeSha256'],ClockEpochEvidence=e,WindowApprovalSha256=f.scope['approvalSha256'])
    f.h_path.write_text(json.dumps(f.h));f.h_pin=sha(f.h_path.read_bytes())
    return m.effective_deadline(f.scope,e)


class SentenceCliTests(unittest.TestCase):
    def accept_existing(self,f,authorization):
        """Finish the SAME synthetic held attempt; never insert another reservation."""
        with closing(sqlite3.connect(f.f.db)) as db,db:
            db.row_factory=sqlite3.Row;a=dict(db.execute('SELECT * FROM chirp_article_attempts WHERE AuthorizationId=?',(authorization,)).fetchone())
            local=uuid.uuid4().hex;part=f.f.proposal['Parts'][a['CoverageOrdinal']-1]
            db.execute('UPDATE chirp_article_attempts SET Consumed=1,ClaimedLocalAttemptId=? WHERE AuthorizationId=?',(local,authorization))
            db.execute("UPDATE google_generation_queue_states SET SubmissionAttempted=1,UnresolvedSubmission=0,LocalAttemptId=?,DiagnosticCode='google-accepted' WHERE IdempotencyKey=?",(local,a['ChildKey']))
            media=f.f.b.f.profile/'accepted-chirp-article-audio'/(authorization+'.mp3');media.write_bytes(b'INERT SAME RESERVATION '+authorization.encode())
            audio=dict(DocumentId=f.f.proposal['DocumentId'],RevisionId=f.f.proposal['RevisionId'],TextSha256=part['Range']['TextSha256'],
                AccountId=a['AccountId'],OperationId='synthesize-speech',IdempotencyKey=a['ChildKey'],LocalAttemptId=local,PayloadSha256=part['PayloadSha256'],
                AudioPath=str(media),AudioBytes=media.stat().st_size,AudioSha256=sha(media.read_bytes()),VoiceName=f.config['VoiceName'],LanguageCode=f.config['LanguageCode'])
            raw=json.dumps(dict(PlanSha256=a['PlanSha256'],CoverageOrdinal=a['CoverageOrdinal'],Range=part['Range'],Audio=audio,ExecutionMappingSha256=None))
            db.execute('INSERT INTO chirp_article_receipts VALUES(?,?,?,?,?)',(a['PlanSha256'],a['CoverageOrdinal'],authorization,raw,sha(raw.encode())))
            db.execute('INSERT INTO chirp_article_output_journals VALUES(?,?,?)',(authorization,raw,sha(raw.encode())))

    def prepare(self,whole_end_after=None):
        f=fixture.prepare(self,whole_end_after=whole_end_after,mismatched_interpreter=False)
        old_scope=copy.deepcopy(f.scope);old_config=copy.deepcopy(f.config);old_path=f.config_path;old_pin=f.pin
        new_source=f.f.source;f.f.source=Path(old_config['ScriptPath']).parent
        policy=json.loads(f.policy().read_text());policy.update(SchemaVersion=3,WholeArticleGeneration=f.whole_grant,
            MaximumNewAttempts=116,MaximumAdditionalReservationMinorUnits=116)
        policy['Budget'].update(ProjectCommittedMinorUnits=163,AllocationCommittedMinorUnits=33)
        policy['Articles'][0]['AllowedSegments']=[dict(CoverageOrdinal=i,MaximumMinorUnits=1) for i in range(11,127)]
        path=f.new_folder/'old-policy.json';path.write_text(json.dumps(policy));op=str(uuid.uuid4())
        result=f.command('claim-completion',path,op);self.assertEqual(0,result[0],result);f.install_policy(path,f.config,f.pin)
        self.assertEqual(0,f.command('complete',path,op,{'outcome':'Succeeded'})[0])
        f.f.source=new_source
        accepted=f.accepted_entries();parent=copy.deepcopy(f.f.proposal);old_manifest=copy.deepcopy(f.manifest)
        failed_auth,_=f.add_attempt(11,policy,False);failed_local=uuid.uuid4().hex
        with closing(sqlite3.connect(f.f.db)) as db,db:
            key=db.execute('SELECT ChildKey FROM chirp_article_attempts WHERE AuthorizationId=?',(failed_auth,)).fetchone()[0]
            db.execute('UPDATE chirp_article_attempts SET Consumed=1,ClaimedLocalAttemptId=? WHERE AuthorizationId=?',(failed_local,failed_auth))
            db.execute("UPDATE google_generation_queue_states SET SubmissionAttempted=1,DiagnosticCode='google-http-400',ErrorStatus=1,LocalAttemptId=? WHERE IdempotencyKey=?",(failed_local,key))
        legacy_id=uuid.uuid4().hex;legacy_media=f.f.b.f.profile/'legacy-synthetic.mp3';legacy_media.write_bytes(b'INERT SYNTHETIC ACCEPTED MEDIA')
        legacy_audio=dict(DocumentId=parent['DocumentId'],RevisionId=parent['RevisionId'],TextSha256=parent['Parts'][-1]['Range']['TextSha256'],
            AccountId=f.config['AccountId'],OperationId='synthesize-speech',IdempotencyKey='synthetic-legacy-key',LocalAttemptId=legacy_id,
            PayloadSha256=parent['Parts'][-1]['PayloadSha256'],AudioPath=str(legacy_media),AudioBytes=legacy_media.stat().st_size,AudioSha256=sha(legacy_media.read_bytes()),
            VoiceName=f.config['VoiceName'],LanguageCode=f.config['LanguageCode'])
        legacy_folder=f.f.b.f.profile/'accepted-article-audio';legacy_folder.mkdir(exist_ok=True)
        legacy_raw=json.dumps(legacy_audio).encode();(legacy_folder/(legacy_id+'.json')).write_bytes(legacy_raw)
        legacy=dict(localAttemptId=legacy_id,receiptSha256=sha(legacy_raw),audioSha256=legacy_audio['AudioSha256'])
        with closing(sqlite3.connect(f.f.db)) as db,db:
            db.execute('INSERT INTO google_generation_queue_states VALUES(?,?,?,?,?,?,?,?,?,?,?)',
                (f.config['AccountId'],'synthesize-speech',legacy_audio['IdempotencyKey'],1,0,None,'google-accepted',0,legacy_id,legacy_audio['PayloadSha256'],100))
        proposal=copy.deepcopy(parent);proposal.update(PlanSha256=m.digest({'synthetic':'new sentence proposal'}),PlannerVersion='chirp3-existing-sentence-groups-v1',SentenceSuccessor=dict(Parent=parent))
        parts=[];reuse=[];by_old={x['coverageOrdinal']:x for x in accepted}
        with closing(sqlite3.connect(f.f.db)) as db:
            for part in parent['Parts']:
                ordinal=part['CoverageOrdinal'];r=copy.deepcopy(part['Range'])
                if ordinal<=10 or ordinal==126:
                    if ordinal<=10:
                        e=by_old[ordinal];rr=json.loads(db.execute('SELECT ReceiptJson FROM chirp_article_receipts WHERE AuthorizationId=?',(e['authorizationId'],)).fetchone()[0])
                        r['ReusedReceiptSha256']=e['receiptSha256'];audio=rr['Audio']
                        retained=dict(ParentDocumentId=parent['DocumentId'],ParentRevisionId=parent['RevisionId'],ReceiptSha256=e['receiptSha256'],Range=r,Audio=audio,
                            SourcePlanSha256=parent['PlanSha256'],SourceAuthorizationId=str(uuid.UUID(e['authorizationId'])))
                    else:
                        r['ReusedReceiptSha256']=legacy['receiptSha256']
                        retained=dict(ParentDocumentId=parent['DocumentId'],ParentRevisionId=parent['RevisionId'],ReceiptSha256=legacy['receiptSha256'],Range=r,Audio=legacy_audio)
                    reuse.append(retained);parts.append(dict(CoverageOrdinal=len(parts)+1,Range=r,PayloadSha256=None,PayloadBytes=0));continue
                if ordinal==11:
                    for start,end in ((r['StartScalar'],r['StartScalar']+1),(r['StartScalar']+1,r['EndScalarExclusive'])):
                        small=dict(r,StartScalar=start,EndScalarExclusive=end,TextSha256=sha(f.text[start:end].encode()))
                        parts.append(dict(CoverageOrdinal=len(parts)+1,Range=small,PayloadSha256=m.digest({'synthetic-payload':[start,end]}),PayloadBytes=100))
                else:parts.append(dict(part,CoverageOrdinal=len(parts)+1))
        proposal['Parts']=parts
        new_parts=[dict(planSha256=proposal['PlanSha256'],coverageOrdinal=x['CoverageOrdinal'],kind='new',payloadSha256=x['PayloadSha256'],payloadBytes=x['PayloadBytes'],
            voiceName=f.config['VoiceName'],languageCode=f.config['LanguageCode']) for x in parts if x['Range']['ReusedReceiptSha256'] is None]
        first=[dict(planSha256=proposal['PlanSha256'],coverageOrdinal=x['coverageOrdinal']) for x in new_parts[:2]]
        failure=dict(FailedAuthorizationId=str(uuid.UUID(failed_auth)),FailedLocalAttemptId=failed_local,FailedPlanSha256=parent['PlanSha256'],FailedCoverageOrdinal=11,
            FailedPayloadSha256=parent['Parts'][10]['PayloadSha256'],FailedPayloadBytes=parent['Parts'][10]['PayloadBytes'])
        manifest=dict(schema=4,accountId=f.config['AccountId'],projectId=f.config['ProjectId'],
            proposals=[dict(planSha256=proposal['PlanSha256'],proposalSha256=m.digest(proposal),reusedAudioSha256=m.digest(reuse))],parts=new_parts,firstStage=first,
            history=dict(proposals=[dict(planSha256=parent['PlanSha256'],proposalSha256=m.digest(parent))],
                parts=[x for x in old_manifest['parts'] if x['coverageOrdinal']<=10],accepted=accepted,legacy=[legacy]),
            replan=dict(originalManifestSha256=m.digest(old_manifest),financialRowsSha256=f.financial_pin(),failure=failure,
                budget=dict(projectCommittedMinorUnits=173,allocationCommittedMinorUnits=43,projectCeilingMinorUnits=289,allocationCeilingMinorUnits=159,
                    overallCeilingMinorUnits=500,taxFeeUpperBoundMinorUnits=200,taxFeeEvidenceSha256=m.digest({'synthetic':'GST/FX included allowance'}))))
        self.assertEqual(116,len(new_parts));old=m.Register(old_config['RegisterPath'],old_config['ScopeSha256'])
        with old._open() as db:before=old._read(db);events=db.execute('SELECT seq,body,sha FROM events ORDER BY seq').fetchall()
        folder=f.new_folder/'sentence5';folder.mkdir();f.new_folder=folder
        grant=dict(GrantId=str(uuid.uuid4()),PlanSha256s=[proposal['PlanSha256']],MaximumCatalogRefreshes=24)
        f.scope=dict(old_scope,schema=5,maximumNewAttempts=116,approvalSha256=m.digest({'synthetic':'new exact sentence permission'}),
            wholeArticleGrantSha256=m.digest(grant),predecessorRegisterPath=old_config['RegisterPath'],predecessorScopeSha256=old_config['ScopeSha256'],
            registerPathSha256=m.path_digest(folder/'register.db'),transitionId=str(uuid.uuid4()),plansSha256=m.digest(manifest),
            acceptedEvidenceSha256=m.digest({'native':accepted,'legacy':[legacy]}))
        f.scope_path=folder/'scope.json';f.scope_path.write_text(json.dumps(f.scope));manifest_path=folder/'manifest.json';manifest_path.write_text(json.dumps(manifest));f.manifest=manifest
        f.config=dict(old_config,RegisterPath=str(folder/'register.db'),ScopeSha256=m.digest(f.scope),PlanManifestPath=str(manifest_path),PlanManifestSha256=sha(manifest_path.read_bytes()),
            ScriptPath=str(f.f.source/'gate_cli.py'),ScriptSha256=sha((f.f.source/'gate_cli.py').read_bytes()),ModuleSha256=sha((f.f.source/'completion_register.py').read_bytes()))
        f.config_path=folder/'gate.json';f.config_path.write_text(json.dumps(f.config));f.pin=sha(f.config_path.read_bytes());f.f.new_app=Path(f.config['CandidateApplicationDirectory'])
        f.h=dict(SchemaVersion=4,TransitionKind='same-window-sentence-replan',TransitionId=f.scope['transitionId'],SourceApprovalSha256=f.scope['approvalSha256'],
            PreviousConfigPath=str(old_path),PreviousGateSha256=old_pin,PreviousScopeSha256=old.scope_sha,ExpectedPreviousHeadSha256=before['head'],
            NewConfigPath=str(f.config_path),NewGateSha256=f.pin,NewScopePath=str(f.scope_path),NewScopeSha256=f.config['ScopeSha256'],DatabaseSha256=sha(f.f.db.read_bytes()),
            OriginalManifestSha256=m.digest(old_manifest),AcceptedEvidenceSha256=f.scope['acceptedEvidenceSha256'],UsedRefreshes=before['refresh'],UsedAdmissions=before['admissions'],UsedResumeAdmissions=before['resume-admission'],**failure)
        f.h_path=folder/'handoff.json';f.h_path.write_text(json.dumps(f.h));f.h_pin=sha(f.h_path.read_bytes())
        return f,old,old_scope,events,proposal,new_parts,first,grant,policy,reuse,legacy_media,before,folder

    def test_same_window_native_proofs_staging_readback_and_zero_cost_resume(self):
        f,old,old_scope,events,proposal,new_parts,first,grant,policy,reuse,legacy_media,before,folder=self.prepare()
        raw=legacy_media.read_bytes();legacy_media.write_bytes(b'tamper');self.assertNotEqual(0,f.command('inspect-successor')[0]);legacy_media.write_bytes(raw)
        self.assertFalse(Path(f.config['RegisterPath']).exists());f.activate()
        status=f.command('status');self.assertEqual(0,status[0],status);self.assertEqual(before['admissions'],status[1]['totalAdmissions']);self.assertEqual(old_scope['endUtc'],status[1]['deadlineUtc'])
        article=dict(Proposal=proposal,ReusedAudio=reuse,AllowedSegments=[dict(CoverageOrdinal=x['coverageOrdinal'],MaximumMinorUnits=1) for x in first])
        pilot=copy.deepcopy(policy);pilot.pop('WholeArticleGeneration');pilot.update(SchemaVersion=2,Articles=[article],SourceApprovalSha256=f.scope['approvalSha256'],
            ExpiresAtUtc=f.scope['endUtc'],MaximumNewAttempts=2,MaximumAdditionalReservationMinorUnits=2)
        pilot['Budget'].update(ProjectCommittedMinorUnits=173,AllocationCommittedMinorUnits=43,ProjectCeilingMinorUnits=289,AllocationCeilingMinorUnits=159)
        pilot['ExpiresAtUtc']=(datetime.now(timezone.utc)+timedelta(seconds=45)).isoformat()
        pilot_path=folder/'pilot.json';pilot_path.write_text(json.dumps(pilot));op=str(uuid.uuid4())
        self.assertEqual(0,f.command('claim-completion',pilot_path,op)[0]);self.assertEqual(0,f.command('check',pilot_path,op)[0])
        f.install_policy(pilot_path,f.config,f.pin);self.assertEqual(0,f.command('complete',pilot_path,op,{'outcome':'Succeeded'})[0])
        with closing(sqlite3.connect(f.f.db)) as db,db:
            db.execute('INSERT INTO chirp_article_plans VALUES(?,?,?,?)',(proposal['PlanSha256'],json.dumps(proposal),f.config['AccountId'],f.config['ProjectId']))
        f.f.proposal=proposal
        remaining=copy.deepcopy(pilot);remaining.update(SchemaVersion=3,WholeArticleGeneration=grant,MaximumNewAttempts=114,MaximumAdditionalReservationMinorUnits=114)
        remaining['Budget'].update(ProjectCommittedMinorUnits=175,AllocationCommittedMinorUnits=45)
        remaining['Articles'][0]['AllowedSegments']=[dict(CoverageOrdinal=x['coverageOrdinal'],MaximumMinorUnits=1) for x in new_parts[2:]]
        remaining_path=folder/'remaining.json';remaining_path.write_text(json.dumps(remaining))
        f.add_attempt(first[0]['coverageOrdinal'],pilot,True)
        self.assertNotEqual(0,f.command('claim-completion',remaining_path,str(uuid.uuid4()))[0])
        stage_held,stage_envelope=f.add_attempt(first[1]['coverageOrdinal'],pilot,False)
        self.assertEqual(0,f.command('verify-completion',pilot_path)[0])
        stage_resume=dict(HistoricalPolicy=pilot,AuthorizationId=str(uuid.UUID(stage_held)),ExecutionEnvelope=stage_envelope,ExpiresAtUtc=f.scope['endUtc'],
            MaximumExistingDispatches=1,MaximumNewReservations=0,MaximumAdditionalReservationMinorUnits=0)
        stage_path=folder/'stage-resume.json';stage_path.write_text(json.dumps(stage_resume));stage_op=str(uuid.uuid4())
        time.sleep(max(0,datetime.fromisoformat(pilot['ExpiresAtUtc']).timestamp()-time.time()+0.1))
        self.assertNotEqual(0,f.command('claim-completion',pilot_path,str(uuid.uuid4()))[0])
        result=f.command('claim-resume',stage_path,stage_op);self.assertEqual(0,result[0],result)
        self.assertEqual(0,f.command('check',stage_path,stage_op)[0]);f.install_policy(stage_path,f.config,f.pin,resume=True)
        self.assertEqual(0,f.command('complete',stage_path,stage_op,{'outcome':'Succeeded'})[0])
        self.accept_existing(f,stage_held)
        remaining['ExpiresAtUtc']=(datetime.now(timezone.utc)+timedelta(seconds=45)).isoformat();remaining_path.write_text(json.dumps(remaining))
        op=str(uuid.uuid4());result=f.command('claim-completion',remaining_path,op);self.assertEqual(0,result[0],result)
        self.assertEqual(0,f.command('check',remaining_path,op)[0]);f.install_policy(remaining_path,f.config,f.pin)
        self.assertEqual(0,f.command('complete',remaining_path,op,{'outcome':'Succeeded'})[0]);self.assertEqual(0,f.command('verify-completion',remaining_path)[0])
        f.add_attempt(new_parts[2]['coverageOrdinal'],remaining,True)
        held,envelope=f.add_attempt(new_parts[3]['coverageOrdinal'],remaining,False)
        self.assertEqual(0,f.command('verify-completion',remaining_path)[0])
        resume=dict(HistoricalPolicy=remaining,AuthorizationId=str(uuid.UUID(held)),ExecutionEnvelope=envelope,ExpiresAtUtc=f.scope['endUtc'],
            MaximumExistingDispatches=1,MaximumNewReservations=0,MaximumAdditionalReservationMinorUnits=0)
        resume_path=folder/'resume.json';resume_path.write_text(json.dumps(resume));op=str(uuid.uuid4())
        time.sleep(max(0,datetime.fromisoformat(remaining['ExpiresAtUtc']).timestamp()-time.time()+0.1))
        self.assertNotEqual(0,f.command('claim-completion',remaining_path,str(uuid.uuid4()))[0])
        result=f.command('claim-resume',resume_path,op);self.assertEqual(0,result[0],result)
        self.assertEqual(0,f.command('check',resume_path,op)[0]);f.install_policy(resume_path,f.config,f.pin,resume=True)
        self.assertEqual(0,f.command('complete',resume_path,op,{'outcome':'Succeeded'})[0])
        with old._open() as db:self.assertEqual(events,db.execute('SELECT seq,body,sha FROM events ORDER BY seq LIMIT ?',(len(events),)).fetchall())
        self.assertNotEqual(0,f.command('claim-successor')[0])

    def test_reboot_cli_installed_cutoff_rejects_original_deadline(self):
        f,old,old_scope,events,proposal,new_parts,first,grant,policy,reuse,legacy_media,before,folder=self.prepare()
        cutoff=synthetic_reboot(f,old)
        self.assertEqual(0,f.command('inspect-successor')[0]);f.activate()
        self.assertEqual(cutoff,f.command('status')[1]['deadlineUtc'])
        article=dict(Proposal=proposal,ReusedAudio=reuse,AllowedSegments=[dict(CoverageOrdinal=x['coverageOrdinal'],MaximumMinorUnits=1) for x in first])
        pilot=copy.deepcopy(policy);pilot.pop('WholeArticleGeneration');pilot.update(SchemaVersion=2,Articles=[article],SourceApprovalSha256=f.scope['approvalSha256'],
            ExpiresAtUtc=f.scope['endUtc'],MaximumNewAttempts=2,MaximumAdditionalReservationMinorUnits=2)
        pilot['Budget'].update(ProjectCommittedMinorUnits=173,AllocationCommittedMinorUnits=43,ProjectCeilingMinorUnits=289,AllocationCeilingMinorUnits=159)
        path=folder/'reboot-pilot.json';path.write_text(json.dumps(pilot));op=str(uuid.uuid4())
        rejected=f.command('claim-completion',path,op)
        self.assertNotEqual(0,rejected[0]);self.assertEqual('ClockEpochPolicyDeadline',rejected[1]['code'])
        pilot['ExpiresAtUtc']=cutoff;path.write_text(json.dumps(pilot))
        result=f.command('claim-completion',path,op);self.assertEqual(0,result[0],result)
        self.assertEqual(0,f.command('check',path,op)[0]);f.install_policy(path,f.config,f.pin)
        self.assertEqual(0,f.command('complete',path,op,{'outcome':'Succeeded'})[0])


if __name__=='__main__':unittest.main()
