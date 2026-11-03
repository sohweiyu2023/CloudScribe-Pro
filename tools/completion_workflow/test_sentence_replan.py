"""Synthetic same-window transition; no real profile, provider, clock change or funding."""
import copy
from datetime import datetime, timedelta, timezone
import unittest
from unittest.mock import patch
import uuid

import completion_register as m
import test_whole_article_continuation as previous

H = m.digest({'synthetic': 'sentence replan'})
G = m.digest({'synthetic': 'new exact gate'})


def receipt(claim,maximum=None):
    result=dict(id=claim['id'],identitySha256=claim['identitySha256'],outcome='Succeeded',evidenceSha256=H,policyVerified=True)
    if claim['kind']=='resume-admission':result['zeroNewFunds']=True
    else:result.update(maximumNewAttempts=maximum,additionalReservationCents=maximum)
    return result


class SentenceRegisterTests(unittest.TestCase):
    def setUp(self):
        self.f=previous.WholeArticleTransitionTests();self.f.setUp();self.addCleanup(self.f.doCleanups);self.f.activate()
        self.old=self.f.new;self.clock=self.f.clock
        for kind in ('refresh','refresh','completion-admission','resume-admission'):
            op=self.old.claim(kind,H,str(uuid.uuid4()),accepted_proof_sha=self.f.scope['acceptedEvidenceSha256'])
            observed=receipt(op,119)
            if kind=='refresh':observed.update(catalogVerified=True,probeInputEmpty=True,probeHttpStatus=400,probeStatus='INVALID_ARGUMENT',audioAbsent=True)
            self.old.complete(op['id'],observed)
        with self.old._open() as db:self.before=self.old._read(db)
        path=self.old.path.parent/'sentence5.db'
        self.scope=dict(self.f.scope,schema=5,approvalSha256=H,maximumNewAttempts=114,plansSha256=G,
            acceptedEvidenceSha256=H,wholeArticleGrantSha256=G,predecessorRegisterPath=str(self.old.path),
            predecessorScopeSha256=self.old.scope_sha,registerPathSha256=m.path_digest(path),transitionId=str(uuid.uuid4()))
        self.new=m.Register(path,m.digest(self.scope),lambda:self.clock.now)
        self.h=dict(self.f.h,SchemaVersion=4,TransitionKind='same-window-sentence-replan',
            TransitionId=self.scope['transitionId'],SourceApprovalSha256=H,PreviousConfigPath=str(path.parent/'old4.json'),
            PreviousGateSha256=self.before['currentGate'],PreviousScopeSha256=self.old.scope_sha,ExpectedPreviousHeadSha256=self.before['head'],
            NewConfigPath=str(path.parent/'new5.json'),NewGateSha256=G,NewScopePath=str(path.parent/'scope5.json'),NewScopeSha256=self.new.scope_sha,
            OriginalManifestSha256=self.before['scope']['plansSha256'],AcceptedEvidenceSha256=H,
            FailedAuthorizationId=str(uuid.uuid4()),FailedLocalAttemptId=uuid.uuid4().hex,FailedPlanSha256=G,FailedCoverageOrdinal=24,
            FailedPayloadSha256=H,FailedPayloadBytes=1800,UsedRefreshes=2,UsedAdmissions=2,UsedResumeAdmissions=1)

    def activate(self):
        self.old.inspect_successor(self.new,self.scope,self.h,H)
        self.old.claim_successor(self.new,self.scope,self.h,H)
        self.old.check_successor(self.new,self.scope,self.h,H)
        self.old.complete_successor(self.new,self.scope,self.h,H,G)
        self.new.require_predecessor_retired(G)

    def test_same_window_counters_and_history_survive_restart(self):
        with self.old._open() as db:rows=db.execute('SELECT seq,body,sha FROM events ORDER BY seq').fetchall()
        self.activate();state=self.new.status()
        self.assertEqual((2,2,1),(state['refreshes'],state['totalAdmissions'],state['resumeAdmissions']))
        self.assertEqual(self.before['scope']['endUtc'],state['deadlineUtc'])
        self.assertEqual(self.before['scope']['startUtc'],self.scope['startUtc'])
        reopened=m.Register(self.new.path,self.new.scope_sha,lambda:self.clock.now);reopened.require_predecessor_retired(G)
        self.assertEqual(2,reopened.status()['totalAdmissions'])
        with self.old._open() as db:self.assertEqual(rows,db.execute('SELECT seq,body,sha FROM events ORDER BY seq LIMIT ?',(len(rows),)).fetchall())
        with self.assertRaises(m.Rejected):self.old.claim('refresh',H,str(uuid.uuid4()))

    def test_changed_counter_window_identity_and_pending_rejected_before_creation(self):
        for key,value in [('UsedRefreshes',0),('UsedAdmissions',0),('UsedResumeAdmissions',0),('ExpectedPreviousHeadSha256',G),('PreviousScopeSha256',G)]:
            with self.subTest(key=key),self.assertRaises(m.Rejected):self.old.inspect_successor(self.new,self.scope,dict(self.h,**{key:value}),H)
        changed=dict(self.scope,startUtc='2030-01-01T00:00:00Z',endUtc='2030-01-01T06:00:00Z')
        target=m.Register(self.new.path,m.digest(changed),lambda:self.clock.now)
        with self.assertRaises(m.Rejected):self.old.inspect_successor(target,changed,dict(self.h,NewScopeSha256=target.scope_sha),H)
        self.assertFalse(self.new.path.exists())
        op=self.old.claim('refresh',H,str(uuid.uuid4()))
        with self.old._open() as db:head=self.old._read(db)['head']
        with self.assertRaises(m.Rejected):self.old.inspect_successor(self.new,self.scope,dict(self.h,ExpectedPreviousHeadSha256=head,UsedRefreshes=3),H)
        self.assertIsNotNone(op)

    def test_clock_rollback_discontinuity_and_deadline_not_reset(self):
        self.old.monotonic=lambda:self.clock.now-1_000_000
        with self.assertRaisesRegex(m.Rejected,'ClockRollback|ClockDiscontinuity'):self.old.inspect_successor(self.new,self.scope,self.h,H)
        self.old.monotonic=lambda:self.clock.now
        self.clock.now=m.utc(self.scope['endUtc'])
        with self.assertRaisesRegex(m.Rejected,'WindowExpired|OutsideWindow'):self.old.inspect_successor(self.new,self.scope,self.h,H)
        self.assertFalse(self.new.path.exists())

    def test_stage_two_requires_stage_one_and_each_fund_limit_is_exact(self):
        self.activate()
        with self.assertRaises((m.Rejected,TypeError)):self.new.claim('completion-admission',H,str(uuid.uuid4()),pilot_proof_sha=G,accepted_proof_sha=H)
        op=self.new.claim('pilot-admission',H,str(uuid.uuid4()),accepted_proof_sha=H)
        self.assertEqual('Succeeded',self.new.complete(op['id'],receipt(op,2)))
        with self.assertRaises(m.Rejected):self.new.claim('pilot-admission',H,str(uuid.uuid4()),accepted_proof_sha=H)
        op=self.new.claim('completion-admission',G,str(uuid.uuid4()),pilot_proof_sha=H,accepted_proof_sha=H)
        self.assertEqual('Succeeded',self.new.complete(op['id'],receipt(op,112)))
        for _ in range(20):
            op=self.new.claim('resume-admission',H,str(uuid.uuid4()));self.new.complete(op['id'],receipt(op))
        with self.assertRaisesRegex(m.Rejected,'OperationLimit'):self.new.claim('resume-admission',H,str(uuid.uuid4()))
        for _ in range(22):
            op=self.new.claim('refresh',H,str(uuid.uuid4()));r=receipt(op)
            r.update(catalogVerified=True,probeInputEmpty=True,probeHttpStatus=400,probeStatus='INVALID_ARGUMENT',audioAbsent=True);self.new.complete(op['id'],r)
        with self.assertRaisesRegex(m.Rejected,'OperationLimit'):self.new.claim('refresh',H,str(uuid.uuid4()))

    def test_interrupted_activation_only_exact_same_handoff_recovers(self):
        self.old.claim_successor(self.new,self.scope,self.h,H)
        original=self.new._append
        def fail(db,event,head):
            if event['type']=='activate':raise OSError('synthetic activation interruption')
            return original(db,event,head)
        with patch.object(self.new,'_append',side_effect=fail),self.assertRaises(OSError):
            self.old.complete_successor(self.new,self.scope,self.h,H,G)
        with self.new._open() as db:self.assertFalse(self.new._read(db)['activated'])
        self.old.complete_successor(self.new,self.scope,self.h,H,G);self.new.require_predecessor_retired(G)
        self.assertEqual(2,self.new.status()['totalAdmissions'])
        with self.assertRaises(m.Rejected):self.old.complete_successor(self.new,self.scope,self.h,H,G)


if __name__=='__main__':unittest.main()
