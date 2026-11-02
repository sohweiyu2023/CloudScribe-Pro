"""Synthetic expired3->whole4 transition; no live profile, credentials or network."""
import copy
import concurrent.futures
from datetime import datetime, timedelta, timezone
from pathlib import Path
import unittest
import uuid

import completion_register as m
import test_continuation_register as registers
import test_continuation_policy as policies

H = registers.H


class WholeArticleTransitionTests(unittest.TestCase):
    def setUp(self):
        self.f = registers.ContinuationRegisterTests(); self.f.setUp(); self.addCleanup(self.f.doCleanups)
        self.f.activate(); self.old = self.f.new
        self.clock = self.f.f.b.f
        self.clock.now += 6 * 3600 * 1_000_000 + 1
        start = datetime.fromtimestamp(self.clock.now / 1_000_000, timezone.utc)
        path = self.old.path.parent / 'whole4.db'
        self.scope = dict(self.f.scope, schema=4, wholeArticleGrantSha256=H,
            approvalSha256=m.digest({'synthetic': 'new explicit whole article authority'}),
            predecessorRegisterPath=str(self.old.path), predecessorScopeSha256=self.old.scope_sha,
            registerPathSha256=m.path_digest(path), transitionId=str(uuid.uuid4()),
            startUtc=start.isoformat().replace('+00:00', 'Z'),
            endUtc=(start + timedelta(hours=6)).isoformat().replace('+00:00', 'Z'))
        self.new = m.Register(path, m.digest(self.scope), lambda: self.clock.now)
        state = self.read(self.old)
        self.h = dict(self.f.h, SchemaVersion=3, TransitionKind='whole-article-continuation',
            TransitionId=self.scope['transitionId'], SourceApprovalSha256=self.scope['approvalSha256'],
            PreviousConfigPath=str(path.parent / 'old3.json'), PreviousGateSha256=registers.NEW_GATE,
            PreviousScopeSha256=self.old.scope_sha, ExpectedPreviousHeadSha256=state['head'],
            NewConfigPath=str(path.parent / 'new4.json'), NewGateSha256=H,
            NewScopePath=str(path.parent / 'scope4.json'), NewScopeSha256=self.new.scope_sha,
            OriginalManifestSha256=state['scope']['plansSha256'], AcceptedEvidenceSha256=self.scope['acceptedEvidenceSha256'])

    def read(self, r):
        with r._open() as db: return r._read(db)

    def activate(self):
        self.old.inspect_successor(self.new, self.scope, self.h, H)
        self.old.claim_successor(self.new, self.scope, self.h, H)
        self.old.check_successor(self.new, self.scope, self.h, H)
        self.old.complete_successor(self.new, self.scope, self.h, H, H)
        self.new.require_predecessor_retired(H)

    def test_expired3_to4_retains_entire_history_and_restart_chain(self):
        before = self.read(self.old)
        with self.old._open() as db: events = db.execute('SELECT seq,body,sha FROM events ORDER BY seq').fetchall()
        self.activate(); after = self.read(self.old)
        for key in ('scope', 'startUtc', 'startMono', 'refresh', 'admissions'):
            self.assertEqual(before[key], after[key])
        with self.old._open() as db:
            self.assertEqual(events, db.execute('SELECT seq,body,sha FROM events ORDER BY seq LIMIT ?', (len(events),)).fetchall())
        reopened = m.Register(self.new.path, self.new.scope_sha, lambda: self.clock.now)
        reopened.require_predecessor_retired(H)
        self.assertEqual(0, reopened.status()['totalAdmissions'])
        self.assertTrue(after['retired'])

    def test_active_predecessor_is_not_renewed(self):
        self.clock.now = m.utc(self.read(self.old)['scope']['endUtc']) - 1
        with self.assertRaisesRegex(m.Rejected, 'WholeArticlePredecessorNotExpired'):
            self.old.inspect_successor(self.new, self.scope, self.h, H)
        self.assertFalse(self.new.path.exists())

    def test_pending_or_changed_identity_or_wrong_handoff_rejected(self):
        for key, value in (('SchemaVersion', 2), ('TransitionKind', 'accepted-continuation'),
                           ('OriginalManifestSha256', H), ('AcceptedEvidenceSha256', H),
                           ('ExpectedPreviousHeadSha256', H)):
            with self.subTest(key=key), self.assertRaises((m.Rejected, ValueError)):
                self.old.inspect_successor(self.new, self.scope, dict(self.h, **{key: value}), H)
        self.assertFalse(self.new.path.exists())

    def test_same_claim_only_after_interruption_and_replay_rejected(self):
        self.old.claim_successor(self.new, self.scope, self.h, H)
        with self.assertRaises(m.Rejected): self.old.claim_successor(self.new, self.scope, self.h, H)
        self.old.check_successor(self.new, self.scope, self.h, H)
        self.old.complete_successor(self.new, self.scope, self.h, H, H)
        with self.assertRaises(m.Rejected): self.old.complete_successor(self.new, self.scope, self.h, H, H)

    def test_concurrent_successor_claim_has_one_winner(self):
        def claim():
            try:
                self.old.claim_successor(self.new,self.scope,self.h,H)
                return True
            except (m.Rejected,PermissionError): return False
        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
            futures=[pool.submit(claim) for _ in range(2)]
            self.assertEqual(1,sum(f.result(timeout=10) for f in futures))

    def test_expired_postboot_observation_does_not_change_old_clock_anchors(self):
        before=self.read(self.old)
        self.old.monotonic=lambda:1
        self.activate();after=self.read(self.old)
        self.assertEqual(before['startMono'],after['startMono'])
        self.assertEqual('ExpiredMonotonicDiscontinuity',after['retirement']['clockCondition'])
        self.assertEqual(1,after['retirement']['observedMono'])

    def test_global_refresh_and_admission_limits_and_deadline(self):
        self.activate()
        claim = self.new.claim('completion-admission', H, str(uuid.uuid4()), accepted_proof_sha=self.scope['acceptedEvidenceSha256'])
        self.new.complete(claim['id'], self.f.receipt(claim, 119))
        for _ in range(23):
            claim = self.new.claim('resume-admission', H, str(uuid.uuid4())); self.new.complete(claim['id'], self.f.receipt(claim))
        with self.assertRaisesRegex(m.Rejected, 'OperationLimit'): self.new.claim('resume-admission', H, str(uuid.uuid4()))
        for _ in range(24):
            claim = self.new.claim('refresh', H, str(uuid.uuid4()))
            self.new.complete(claim['id'], dict(self.f.receipt(claim), catalogVerified=True, probeInputEmpty=True,
                probeHttpStatus=400, probeStatus='INVALID_ARGUMENT', audioAbsent=True))
        with self.assertRaisesRegex(m.Rejected, 'OperationLimit'): self.new.claim('refresh', H, str(uuid.uuid4()))
        self.clock.now = m.utc(self.scope['endUtc'])
        with self.assertRaisesRegex(m.Rejected, 'WindowExpired'): self.new.status()


class WholeArticleGrantTests(unittest.TestCase):
    def setUp(self):
        self.manifest, self.scope, self.policy = policies.fixture()
        self.grant = {'GrantId': str(uuid.uuid4()), 'PlanSha256s': [self.policy['Articles'][0]['Proposal']['PlanSha256']], 'MaximumCatalogRefreshes': 24}
        self.scope.update(schema=4, wholeArticleGrantSha256=m.digest(self.grant))
        self.policy.update(SchemaVersion=3, WholeArticleGeneration=self.grant)

    def verify(self, policy=None):
        m.verify_plan_policy(self.manifest, self.scope, policy or self.policy, 'completion-admission')

    def test_exact_separate_whole_article_grant_is_required(self):
        self.verify()
        for change in ({'SchemaVersion': 2}, {'WholeArticleGeneration': None},
                       {'WholeArticleGeneration': dict(self.grant, GrantId=str(uuid.uuid4()))},
                       {'MaximumNewAttempts': 120}, {'MaximumAdditionalReservationMinorUnits': 120}):
            with self.subTest(change=change), self.assertRaises(m.Rejected): self.verify(dict(self.policy, **change))

    def test_legacy_request_scope_cannot_acquire_whole_article_authority(self):
        for schema in (1,2,3):
            with self.subTest(schema=schema),self.assertRaisesRegex(m.Rejected,'WholeArticleGrantBinding'):
                m.verify_plan_policy(self.manifest,dict(self.scope,schema=schema),self.policy,'completion-admission')
        with self.assertRaisesRegex(m.Rejected,'WholeArticleGrantBinding'):
            m.verify_plan_policy(self.manifest,dict(self.scope,schema=3),{'HistoricalPolicy':self.policy},'resume-admission')

    def test_malformed_duplicate_plan_expired_or_changed_grant_rejected(self):
        for change in ({'GrantId': 'invalid'}, {'GrantId': str(uuid.UUID(int=0))},
                       {'PlanSha256s': self.grant['PlanSha256s'] * 2}, {'PlanSha256s': [H]},
                       {'MaximumCatalogRefreshes': 25}, {'MaximumCatalogRefreshes': True}):
            grant = dict(self.grant, **change); scope = dict(self.scope, wholeArticleGrantSha256=m.digest(grant))
            with self.subTest(change=change), self.assertRaises(m.Rejected):
                m.verify_plan_policy(self.manifest, scope, dict(self.policy, WholeArticleGeneration=grant), 'completion-admission')


if __name__ == '__main__': unittest.main()
