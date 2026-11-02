"""Exact synthetic two-stage permissions, tax allowance and retained provenance."""
import copy
from datetime import datetime,timedelta,timezone
import unittest
import uuid
import completion_register as m

H=m.digest({'synthetic':'identity'})
P=m.digest({'synthetic':'new plan'})
OLD=m.digest({'synthetic':'old plan'})


def fixture():
    parts=[dict(planSha256=P,coverageOrdinal=i,kind='new',payloadSha256=m.digest({'payload':i}),payloadBytes=100+i,
        voiceName='cmn-CN-Chirp3-HD-Alnilam',languageCode='cmn-CN') for i in range(1,9)]
    old_part=dict(parts[0],planSha256=OLD,coverageOrdinal=9)
    accepted=dict(planSha256=OLD,coverageOrdinal=9,authorizationId=uuid.uuid4().hex,policySha256=H,sourceApprovalSha256=H,
        receiptSha256=H,audioSha256=H,executionMappingSha256=None)
    legacy=[dict(localAttemptId=uuid.uuid4().hex,receiptSha256=m.digest({'legacy':1}),audioSha256=H)]
    failure=dict(FailedAuthorizationId=str(uuid.uuid4()),FailedLocalAttemptId=uuid.uuid4().hex,FailedPlanSha256=OLD,
        FailedCoverageOrdinal=24,FailedPayloadSha256=H,FailedPayloadBytes=1800)
    proposal=dict(PlanSha256=P,PlannerVersion='chirp3-existing-sentence-groups-v1',VoiceName=parts[0]['voiceName'],LanguageCode='cmn-CN',
        SentenceSuccessor=dict(Parent=dict(PlanSha256=OLD,Parts=[dict(CoverageOrdinal=24,Range=dict(StartScalar=0,EndScalarExclusive=5))])),
        Parts=[dict(CoverageOrdinal=i,Range=dict(StartScalar=(i-1)*3,EndScalarExclusive=i*3,TextSha256=H,ReusedReceiptSha256=None),
            PayloadSha256=parts[i-1]['payloadSha256'],PayloadBytes=parts[i-1]['payloadBytes']) for i in range(1,9)])
    reused=[]
    for i,pin in enumerate((accepted['receiptSha256'],legacy[0]['receiptSha256'])):
        rr=dict(StartScalar=24+i*3,EndScalarExclusive=27+i*3,TextSha256=H,ReusedReceiptSha256=pin)
        reused.append(dict(ReceiptSha256=pin,Range=rr));proposal['Parts'].append(dict(CoverageOrdinal=9+i,Range=rr,PayloadSha256=None,PayloadBytes=0))
    manifest=dict(schema=4,accountId='synthetic-account',projectId='synthetic-project',
        proposals=[dict(planSha256=P,proposalSha256=m.digest(proposal),reusedAudioSha256=m.digest(reused))],parts=parts,
        firstStage=[dict(planSha256=P,coverageOrdinal=i) for i in (1,2)],
        history=dict(proposals=[dict(planSha256=OLD,proposalSha256=H)],parts=[old_part],accepted=[accepted],legacy=legacy),
        replan=dict(originalManifestSha256=H,financialRowsSha256=H,failure=failure,budget=dict(projectCommittedMinorUnits=173,
            allocationCommittedMinorUnits=43,projectCeilingMinorUnits=181,allocationCeilingMinorUnits=51,overallCeilingMinorUnits=500,
            taxFeeUpperBoundMinorUnits=200,taxFeeEvidenceSha256=H)))
    now=datetime.now(timezone.utc);grant=dict(GrantId=str(uuid.uuid4()),PlanSha256s=[P],MaximumCatalogRefreshes=24)
    scope=dict(schema=5,plansSha256=m.digest(manifest),maximumNewAttempts=8,acceptedEvidenceSha256=m.digest({'native':[accepted],'legacy':legacy}),
        wholeArticleGrantSha256=m.digest(grant),approvalSha256=H,startUtc=now.isoformat().replace('+00:00','Z'),
        endUtc=(now+timedelta(hours=6)).isoformat().replace('+00:00','Z'))
    policy=dict(SchemaVersion=2,Currency='USD',Scale=2,AccountId=manifest['accountId'],ProjectId=manifest['projectId'],SourceApprovalSha256=H,
        ExpiresAtUtc=scope['endUtc'],MaximumNewAttempts=2,MaximumAdditionalReservationMinorUnits=2,
        Budget=dict(ProjectCommittedMinorUnits=173,AllocationCommittedMinorUnits=43,ProjectCeilingMinorUnits=181,AllocationCeilingMinorUnits=51),
        Articles=[dict(Proposal=proposal,ReusedAudio=reused,AllowedSegments=[dict(CoverageOrdinal=i,MaximumMinorUnits=1) for i in (1,2)])])
    remaining=copy.deepcopy(policy);remaining.update(SchemaVersion=3,WholeArticleGeneration=grant,MaximumNewAttempts=6,MaximumAdditionalReservationMinorUnits=6)
    remaining['Budget'].update(ProjectCommittedMinorUnits=175,AllocationCommittedMinorUnits=45)
    remaining['Articles'][0]['AllowedSegments']=[dict(CoverageOrdinal=i,MaximumMinorUnits=1) for i in range(3,9)]
    return manifest,scope,policy,remaining


class SentencePolicyTests(unittest.TestCase):
    def setUp(self):self.manifest,self.scope,self.pilot,self.remaining=fixture()
    def test_historical_readback_never_makes_expired_outer_authority_fresh(self):
        expired=copy.deepcopy(self.pilot);expired['ExpiresAtUtc']='2020-01-01T00:00:00Z'
        with self.assertRaisesRegex(m.Rejected,'PolicyWindow'):
            m.verify_plan_policy(self.manifest,self.scope,expired,'pilot-admission')
        m.verify_sentence_policy(self.manifest,self.scope,expired,'pilot-admission',historical_readback=True)
        bad=copy.deepcopy(expired);bad['MaximumNewAttempts']=3
        with self.assertRaises(m.Rejected):m.verify_sentence_policy(self.manifest,self.scope,bad,'pilot-admission',historical_readback=True)
        part=self.manifest['parts'][0]
        resume=dict(HistoricalPolicy=expired,ExpiresAtUtc=self.scope['endUtc'],MaximumExistingDispatches=1,MaximumNewReservations=0,
            MaximumAdditionalReservationMinorUnits=0,ExecutionEnvelope=dict(CompiledPayloadSha256=part['payloadSha256'],CompiledPayloadBytes=part['payloadBytes'],VoiceName=part['voiceName'],AudioEncoding='MP3'))
        m.verify_plan_policy(self.manifest,self.scope,resume,'resume-admission')
        with self.assertRaisesRegex(m.Rejected,'PolicyWindow'):
            m.verify_plan_policy(self.manifest,self.scope,dict(resume,ExpiresAtUtc=expired['ExpiresAtUtc']),'resume-admission')

    def test_exact_two_stage_and_whole_grant_permissions(self):
        m.verify_plan_policy(self.manifest,self.scope,self.pilot,'pilot-admission')
        m.verify_plan_policy(self.manifest,self.scope,self.remaining,'completion-admission')
        self.assertEqual('pilot-admission',m.successor_policy_kind(self.manifest,self.pilot))
        self.assertEqual('completion-admission',m.successor_policy_kind(self.manifest,self.remaining))
    def test_no_phase_mix_accepted_regeneration_or_extra_cent(self):
        for field,value in [('MaximumNewAttempts',3),('MaximumAdditionalReservationMinorUnits',3),('SourceApprovalSha256',P),('Scale',True)]:
            with self.subTest(field=field),self.assertRaises(m.Rejected):
                m.verify_plan_policy(self.manifest,self.scope,dict(self.pilot,**{field:value}),'pilot-admission')
        for field,value in [('ProjectCommittedMinorUnits',173),('AllocationCommittedMinorUnits',43),('ProjectCeilingMinorUnits',500)]:
            bad=copy.deepcopy(self.remaining);bad['Budget'][field]=value
            with self.subTest(field=field),self.assertRaises(m.Rejected):m.verify_plan_policy(self.manifest,self.scope,bad,'completion-admission')
        bad=copy.deepcopy(self.remaining);bad['Articles'][0]['AllowedSegments'].append(dict(CoverageOrdinal=1,MaximumMinorUnits=1))
        with self.assertRaises(m.Rejected):m.verify_plan_policy(self.manifest,self.scope,bad,'completion-admission')
    def test_tax_contingency_counted_once_inside_hard_cap(self):
        m.validate_sentence_manifest(self.manifest,self.scope)
        bad=copy.deepcopy(self.manifest);bad['replan']['budget']['taxFeeUpperBoundMinorUnits']=320
        with self.assertRaisesRegex(m.Rejected,'ContinuationAllInBudget'):m.validate_sentence_manifest(bad,dict(self.scope,plansSha256=m.digest(bad)))
    def test_missing_or_changed_accepted_provenance_and_reuse_rejected(self):
        for section in ('accepted','legacy'):
            bad=copy.deepcopy(self.manifest);bad['history'][section]=[]
            with self.subTest(section=section),self.assertRaises(m.Rejected):m.validate_sentence_manifest(bad,dict(self.scope,plansSha256=m.digest(bad)))
        bad=copy.deepcopy(self.pilot);bad['Articles'][0]['ReusedAudio']=[{'unverified':'receipt'}]
        with self.assertRaises(m.Rejected):m.verify_plan_policy(self.manifest,self.scope,bad,'pilot-admission')
    def test_representative_packets_cover_failure_once_with_neighbor_tail(self):
        bad=copy.deepcopy(self.pilot);bad['Articles'][0]['Proposal']['Parts'][1]['Range']['StartScalar']=2
        manifest=copy.deepcopy(self.manifest);manifest['proposals'][0]['proposalSha256']=m.digest(bad['Articles'][0]['Proposal'])
        with self.assertRaisesRegex(m.Rejected,'SentenceFirstStage'):m.verify_plan_policy(manifest,dict(self.scope,plansSha256=m.digest(manifest)),bad,'pilot-admission')
    def test_zero_fund_resume_binds_one_exact_original_permitted_payload(self):
        part=self.manifest['parts'][0]
        resume=dict(HistoricalPolicy=self.pilot,ExpiresAtUtc=self.scope['endUtc'],MaximumExistingDispatches=1,MaximumNewReservations=0,
            MaximumAdditionalReservationMinorUnits=0,ExecutionEnvelope=dict(CompiledPayloadSha256=part['payloadSha256'],CompiledPayloadBytes=part['payloadBytes'],VoiceName=part['voiceName'],AudioEncoding='MP3'))
        m.verify_plan_policy(self.manifest,self.scope,resume,'resume-admission')
        with self.assertRaises(m.Rejected):m.verify_plan_policy(self.manifest,self.scope,dict(resume,MaximumNewReservations=1),'resume-admission')
        for key,value in [('MaximumExistingDispatches',True),('MaximumNewReservations',False),('MaximumAdditionalReservationMinorUnits',False)]:
            with self.subTest(key=key),self.assertRaises(m.Rejected):m.verify_plan_policy(self.manifest,self.scope,dict(resume,**{key:value}),'resume-admission')


if __name__=='__main__':unittest.main()
