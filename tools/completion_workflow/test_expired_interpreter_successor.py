"""Expired pre-effect whole-article successor with separate exact interpreter authority."""
from datetime import datetime,timedelta,timezone
import unittest
import uuid
import completion_register as m
import test_whole_article_continuation as fixture

H=m.digest({'synthetic':'new interpreter source evidence'})
class ExpiredInterpreterSuccessorTests(unittest.TestCase):
    def setUp(self):
        self.f=fixture.WholeArticleTransitionTests();self.f.setUp();self.addCleanup(self.f.doCleanups);self.f.activate()
        self.old=self.f.new;self.clock=self.f.clock;self.clock.now+=6*3600*1_000_000+1
        start=datetime.fromtimestamp(self.clock.now/1_000_000,timezone.utc);path=self.old.path.parent/'fresh-whole4.db'
        self.scope=dict(self.f.scope,approvalSha256=m.digest({'synthetic':'fresh paid authority'}),wholeArticleGrantSha256=m.digest({'synthetic':'fresh whole article grant'}),predecessorRegisterPath=str(self.old.path),predecessorScopeSha256=self.old.scope_sha,registerPathSha256=m.path_digest(path),transitionId=str(uuid.uuid4()),startUtc=start.isoformat().replace('+00:00','Z'),endUtc=(start+timedelta(hours=6)).isoformat().replace('+00:00','Z'))
        self.new=m.Register(path,m.digest(self.scope),lambda:self.clock.now)
        with self.old._open() as db:state=self.old._read(db)
        self.h=dict(self.f.h,TransitionKind='whole-article-interpreter-continuation',TransitionId=self.scope['transitionId'],SourceApprovalSha256=self.scope['approvalSha256'],PreviousConfigPath=str(path.parent/'historical4.json'),PreviousGateSha256=state['currentGate'],PreviousScopeSha256=self.old.scope_sha,ExpectedPreviousHeadSha256=state['head'],NewConfigPath=str(path.parent/'fresh4.json'),NewGateSha256=H,NewScopePath=str(path.parent/'fresh-scope4.json'),NewScopeSha256=self.new.scope_sha,OriginalManifestSha256=state['scope']['plansSha256'],AcceptedEvidenceSha256=self.scope['acceptedEvidenceSha256'],PreviousPythonSha256=m.digest({'synthetic':'old executable'}),NewPythonSha256=m.digest({'synthetic':'new executable'}),InterpreterEvidencePath=str(path.parent/'interpreter-proof.json'),InterpreterEvidenceSha256=H,RepairApprovalSha256=m.digest({'synthetic':'separate binding authority'}))
    def activate(self):
        self.old.inspect_successor(self.new,self.scope,self.h,H);self.old.claim_successor(self.new,self.scope,self.h,H)
        self.old.check_successor(self.new,self.scope,self.h,H);self.old.complete_successor(self.new,self.scope,self.h,H,H)
    def test_expired_whole_successor_preserves_ancestors_deadline_and_no_paid_authority(self):
        with self.old._open() as db:before=self.old._read(db);events=db.execute('SELECT body,sha FROM events ORDER BY seq').fetchall()
        self.activate();self.new.require_predecessor_retired(H)
        with self.old._open() as db:
            after=self.old._read(db);self.assertEqual(events,db.execute('SELECT body,sha FROM events ORDER BY seq LIMIT ?',(len(events),)).fetchall())
        for key in ('scope','startUtc','startMono','refresh','admissions','helperSourceRepairs'):self.assertEqual(before[key],after[key],key)
        status=self.new.status();self.assertEqual((0,0),(status['refreshes'],status['totalAdmissions']))
        self.assertEqual(self.scope['endUtc'],status['deadlineUtc'])
    def test_new_interpreter_kind_cannot_renew_active_old_run(self):
        self.clock.now=m.utc(self.f.scope['endUtc'])-1
        with self.assertRaises(m.Rejected):self.old.inspect_successor(self.new,self.scope,self.h,H)
        self.assertFalse(self.new.path.exists())
    def test_ordinary_handoff_cannot_renew_whole_scope_or_change_interpreter(self):
        ordinary={k:v for k,v in self.h.items() if k not in m.INTERPRETER_HANDOFF_KEYS-m.CONTINUATION_HANDOFF_KEYS};ordinary['TransitionKind']='whole-article-continuation'
        with self.assertRaises(m.Rejected):self.old.inspect_successor(self.new,self.scope,ordinary,H)
        with self.assertRaises(m.Rejected):self.old.inspect_successor(self.new,dict(self.scope,wholeArticleGrantSha256=self.f.scope['wholeArticleGrantSha256']),self.h,H)
    def test_postboot_retirement_is_audited_not_old_clock_repair(self):
        self.old.monotonic=lambda:1;self.activate();self.new.require_predecessor_retired(H)
        with self.old._open() as db:state=self.old._read(db)
        self.assertEqual('ExpiredMonotonicDiscontinuity',state['retirement']['clockCondition']);self.assertEqual(1,state['retirement']['observedMono'])
    def test_identity_and_separate_approval_are_required(self):
        for key,value in [('RepairApprovalSha256',self.h['SourceApprovalSha256']),('PreviousPythonSha256',self.h['NewPythonSha256']),('NewScopeSha256',H)]:
            with self.subTest(key=key),self.assertRaises(m.Rejected):self.old.inspect_successor(self.new,self.scope,dict(self.h,**{key:value}),H)
    def test_prior_effect_pending_or_uncertain_blocks_successor(self):
        # A past claim is enough; no provider outcome is fabricated.
        self.clock.now=m.utc(self.f.scope['endUtc'])-1
        self.old.claim('refresh',H,str(uuid.uuid4()))
        # The uncompleted operation remains uncertain; deadline passage is not
        # evidence of failure and cannot authorize a new scope.
        self.clock.now=m.utc(self.f.scope['endUtc'])+1
        with self.assertRaises(m.Rejected):self.old.inspect_successor(self.new,self.scope,self.h,H)

    def test_typed_activation_blocks_in_place_second_binding_and_forged_replay(self):
        self.activate()
        with self.new._open() as db:state=self.new._read(db)
        t=dict(SchemaVersion=1,TransitionKind='interpreter-binding',TransitionId=str(uuid.uuid4()),SourceApprovalSha256=self.scope['approvalSha256'],RepairApprovalSha256=m.digest({'synthetic':'another repair approval'}),ScopeSha256=self.new.scope_sha,ExpectedHeadSha256=state['head'],PreviousConfigPath=str(self.new.path.parent/'first-rebound-gate.json'),PreviousGateSha256=state['currentGate'],NewGateSha256=m.digest({'synthetic':'second gate'}),PreviousBuildSha256=state['currentBuild'],NewBuildSha256=state['currentBuild'],PreviousPythonSha256=H,NewPythonSha256=m.digest({'synthetic':'third Python'}),InterpreterEvidencePath=str(self.new.path.parent/'third-proof.json'),InterpreterEvidenceSha256=H)
        with self.assertRaises(m.Rejected):self.new.claim_build(t,H,t['TransitionId'])
        # Even an inserted, rehashed, anchor-matching event must not bypass the
        # same invariant when the historical journal is replayed.
        with self.new._open() as db:
            self.new._append(db,self.new._stamp(dict(type='source-claim',utc=self.clock.now,id=t['TransitionId'],identity=H,transition=t)),state['head']);db.commit();self.new._seal(db)
        with self.assertRaisesRegex(m.Rejected,'InvalidBuildClaimHistory'):
            with self.new._open() as db:self.new._read(db)

    def test_typed_activation_blocks_second_successor_and_forged_retirement(self):
        self.activate();self.clock.now=m.utc(self.scope['endUtc'])+1
        with self.new._open() as db:state=self.new._read(db)
        path=self.new.path.parent/'unapproved-second-successor.db'
        future=dict(self.scope,approvalSha256=m.digest({'synthetic':'another narration approval'}),wholeArticleGrantSha256=m.digest({'synthetic':'another whole grant'}),predecessorRegisterPath=str(self.new.path),predecessorScopeSha256=self.new.scope_sha,registerPathSha256=m.path_digest(path),transitionId=str(uuid.uuid4()))
        start=datetime.fromtimestamp(self.clock.now/1_000_000,timezone.utc);future.update(startUtc=start.isoformat().replace('+00:00','Z'),endUtc=(start+timedelta(hours=6)).isoformat().replace('+00:00','Z'))
        next_register=m.Register(path,m.digest(future),lambda:self.clock.now)
        h=dict(self.h,TransitionId=future['transitionId'],SourceApprovalSha256=future['approvalSha256'],PreviousScopeSha256=self.new.scope_sha,PreviousGateSha256=state['currentGate'],ExpectedPreviousHeadSha256=state['head'],NewScopeSha256=next_register.scope_sha,NewGateSha256=m.digest({'synthetic':'third gate'}))
        with self.assertRaises(m.Rejected):self.new.inspect_successor(next_register,future,h,H)
        mono=self.new.monotonic();condition,drift=self.new._retirement_observation(state,self.clock.now,mono,h)
        event=dict(type='retire-claim',utc=self.clock.now,mono=max(mono,state['lastMono']),observedMono=mono,id=h['TransitionId'],identity=H,handoff=h,clockCondition=condition,clockDriftMicros=drift)
        with self.new._open() as db:self.new._append(db,event,state['head']);db.commit();self.new._seal(db)
        with self.assertRaisesRegex(m.Rejected,'InvalidRetirementHistory'):
            with self.new._open() as db:self.new._read(db)

if __name__=='__main__':unittest.main()
