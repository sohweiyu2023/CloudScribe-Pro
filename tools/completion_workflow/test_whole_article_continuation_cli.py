"""Real pinned isolated CLI3->4, with a naturally expiring synthetic-only window."""
import copy
from contextlib import closing
from datetime import datetime, timedelta, timezone
import json
import sqlite3
from pathlib import Path
import time
import unittest
import uuid

import completion_register as m
import test_continuation_cli as fixtures


class WholeArticleCliTests(unittest.TestCase):
    def test_actual_expired_transition_installer_readback_grant_and_progress(self):
        f = fixtures.ContinuationCliTests(); f.setUp(); self.addCleanup(f.doCleanups)
        # All paths belong to the existing synthetic temporary fixture. The scope
        # retains an exact6h interval; naturally wait for its near end, no OS clock change.
        end = datetime.now(timezone.utc) + timedelta(seconds=30)
        f.scope.update(startUtc=(end-timedelta(hours=6)).isoformat().replace('+00:00', 'Z'),
                       endUtc=end.isoformat().replace('+00:00', 'Z'))
        f.scope_path.write_text(json.dumps(f.scope))
        f.config['ScopeSha256'] = m.digest(f.scope); f.config_path.write_text(json.dumps(f.config))
        f.pin = fixtures.sha(f.config_path.read_bytes())
        f.h.update(NewScopeSha256=f.config['ScopeSha256'], NewGateSha256=f.pin)
        f.h_path.write_text(json.dumps(f.h)); f.h_pin = fixtures.sha(f.h_path.read_bytes())
        f.activate(); old_policy_path = f.admit(); old_policy = json.loads(old_policy_path.read_text())
        for ordinal in (8,9,10): f.add_attempt(ordinal, old_policy, accepted=True)
        old_manifest = copy.deepcopy(f.manifest); old_config = copy.deepcopy(f.config)
        old_config_path = f.config_path; old_pin = f.pin
        old_r = m.Register(old_config['RegisterPath'], old_config['ScopeSha256'])
        with old_r._open() as c:
            before_state = old_r._read(c)
            before_events = c.execute('SELECT seq,body,sha FROM events ORDER BY seq').fetchall()
        accepted = f.accepted_entries(); self.assertEqual(10,len(accepted))
        grant = {'GrantId':str(uuid.uuid4()),'PlanSha256s':[f.f.proposal['PlanSha256']],'MaximumCatalogRefreshes':24}
        root = f.new_folder / 'whole4'; root.mkdir()
        f.new_folder = root
        f.manifest = copy.deepcopy(old_manifest)
        f.manifest['continuation'].update(accepted=accepted,financialRowsSha256=f.financial_pin())
        f.manifest['continuation']['budget'].update(projectCommittedMinorUnits=163,allocationCommittedMinorUnits=33)
        manifest_path = root/'manifest.json'; manifest_path.write_text(json.dumps(f.manifest))
        time.sleep(max(0,end.timestamp()-time.time()+0.05))
        start = datetime.now(timezone.utc)
        f.scope = dict(f.scope,schema=4,wholeArticleGrantSha256=m.digest(grant),maximumNewAttempts=116,
            approvalSha256=m.digest({'synthetic':'new whole article approval'}),
            predecessorRegisterPath=old_config['RegisterPath'],predecessorScopeSha256=old_config['ScopeSha256'],
            registerPathSha256=m.path_digest(root/'register.db'),transitionId=str(uuid.uuid4()),
            plansSha256=m.digest(f.manifest),acceptedEvidenceSha256=m.digest(accepted),
            startUtc=start.isoformat().replace('+00:00','Z'),endUtc=(start+timedelta(hours=6)).isoformat().replace('+00:00','Z'))
        f.scope_path = root/'scope.json'; f.scope_path.write_text(json.dumps(f.scope))
        f.config = dict(old_config,RegisterPath=str(root/'register.db'),ScopeSha256=m.digest(f.scope),
                        PlanManifestPath=str(manifest_path),PlanManifestSha256=fixtures.sha(manifest_path.read_bytes()))
        f.config_path = root/'gate.json'; f.config_path.write_text(json.dumps(f.config)); f.pin = fixtures.sha(f.config_path.read_bytes())
        f.h = dict(f.h,SchemaVersion=3,TransitionKind='whole-article-continuation',TransitionId=f.scope['transitionId'],
            SourceApprovalSha256=f.scope['approvalSha256'],PreviousConfigPath=str(old_config_path),PreviousGateSha256=old_pin,
            PreviousScopeSha256=old_config['ScopeSha256'],ExpectedPreviousHeadSha256=before_state['head'],
            NewConfigPath=str(f.config_path),NewGateSha256=f.pin,NewScopePath=str(f.scope_path),NewScopeSha256=f.config['ScopeSha256'],
            DatabaseSha256=fixtures.sha(f.f.db.read_bytes()),OriginalManifestSha256=m.digest(old_manifest),AcceptedEvidenceSha256=m.digest(accepted))
        f.h_path = root/'handoff.json'; f.h_path.write_text(json.dumps(f.h)); f.h_pin=fixtures.sha(f.h_path.read_bytes())
        f.activate()
        policy = copy.deepcopy(old_policy)
        policy.update(SchemaVersion=3,SourceApprovalSha256=f.scope['approvalSha256'],ExpiresAtUtc=f.scope['endUtc'],
                      MaximumNewAttempts=116,MaximumAdditionalReservationMinorUnits=116,WholeArticleGeneration=grant)
        policy['Budget'].update(ProjectCommittedMinorUnits=163,AllocationCommittedMinorUnits=33)
        policy['Articles'][0]['AllowedSegments']=[{'CoverageOrdinal':i,'MaximumMinorUnits':1} for i in range(11,127)]
        path=root/'whole-policy.json'; path.write_text(json.dumps(policy)); op=str(uuid.uuid4())
        result=f.command('claim-completion',path,op);self.assertEqual(0,result[0],result)
        self.assertEqual(0,f.command('check',path,op)[0])
        f.install_policy(path,f.config,f.pin)
        result=f.command('complete',path,op,{'outcome':'Succeeded'});self.assertEqual(0,result[0],result)
        self.assertEqual(0,f.command('verify-completion',path)[0])
        f.add_attempt(11,policy,accepted=True)
        self.assertEqual(0,f.command('verify-completion',path)[0])
        self.assertNotEqual(0,f.command('claim-completion',path,str(uuid.uuid4()))[0])
        with old_r._open() as c:
            after=old_r._read(c)
            self.assertEqual(before_events,c.execute('SELECT seq,body,sha FROM events ORDER BY seq LIMIT ?',(len(before_events),)).fetchall())
            self.assertTrue(after['retired'])
        with closing(sqlite3.connect(f.f.db)) as c,c:
            c.execute("INSERT INTO google_generation_spend_authorizations VALUES('unexpected synthetic hold',1)")
        self.assertNotEqual(0,f.command('verify-completion',path)[0])


if __name__=='__main__':unittest.main()
