"""Synthetic continuation coverage and gross-budget bounds; no real user evidence."""
import copy
from datetime import datetime, timedelta, timezone
import unittest
import uuid

import test_successor_run as fixtures
import completion_register as module


def fixture():
    original, proposal = fixtures.plans()
    accepted = [{"planSha256": proposal["PlanSha256"], "coverageOrdinal": i, "authorizationId": uuid.uuid4().hex,
                 "policySha256": fixtures.sha(b"synthetic historical policy"),
                 "sourceApprovalSha256": fixtures.sha(b"synthetic historical approval"),
                 "receiptSha256": fixtures.sha(("synthetic receipt" + str(i)).encode()),
                 "audioSha256": fixtures.sha(("synthetic audio" + str(i)).encode()),
                 "executionMappingSha256": None} for i in range(1, 8)]
    manifest = dict(copy.deepcopy(original), schema=3, continuation={"originalManifestSha256": module.digest(original),
        "financialRowsSha256": fixtures.sha(b"SYNTHETIC reviewed financial projection"),
        "accepted": accepted, "budget": {"projectCommittedMinorUnits": 160, "allocationCommittedMinorUnits": 30,
        "projectCeilingMinorUnits": 279, "allocationCeilingMinorUnits": 149, "overallCeilingMinorUnits": 500,
        "taxFeeUpperBoundMinorUnits": 221, "taxFeeEvidenceSha256": fixtures.sha(b"SYNTHETIC verified all-project tax/fee upper bound")}})
    expiry = (datetime.now(timezone.utc) + timedelta(hours=5)).isoformat().replace("+00:00", "Z")
    scope = {"schema": 3, "plansSha256": module.digest(manifest), "acceptedEvidenceSha256": module.digest(accepted),
             "maximumNewAttempts": 119, "approvalSha256": fixtures.sha(b"synthetic NEW human approval"), "endUtc": expiry}
    policy = {"Currency": "USD", "Scale": 2, "AccountId": original["accountId"], "ProjectId": original["projectId"],
              "SourceApprovalSha256": scope["approvalSha256"], "ExpiresAtUtc": expiry,
              "MaximumNewAttempts": 119, "MaximumAdditionalReservationMinorUnits": 119,
              "Budget": {"ProjectCommittedMinorUnits": 160, "AllocationCommittedMinorUnits": 30,
                         "ProjectCeilingMinorUnits": 279, "AllocationCeilingMinorUnits": 149},
              "Articles": [{"Proposal": proposal, "AllowedSegments": [{"CoverageOrdinal": i, "MaximumMinorUnits": 1} for i in range(8, 127)]}]}
    return manifest, scope, policy


class ContinuationPolicyTests(unittest.TestCase):
    def setUp(self):
        self.manifest, self.scope, self.policy = fixture()

    def verify(self):
        module.verify_plan_policy(self.manifest, self.scope, self.policy, "completion-admission")

    def rebind(self):
        self.scope["plansSha256"] = module.digest(self.manifest)
        self.scope["acceptedEvidenceSha256"] = module.digest(self.manifest["continuation"]["accepted"])

    def test_exact_119_no_new_pilot_and_221_all_project_tax_headroom(self):
        self.verify()
        self.assertEqual("completion-admission", module.successor_policy_kind(self.manifest, self.policy))
        self.assertEqual(279 + 221, 500)

    def test_any_accepted_member_or_pilot_readded_is_rejected(self):
        for ordinal in range(1, 8):
            with self.subTest(ordinal=ordinal):
                p = copy.deepcopy(self.policy); p["Articles"][0]["AllowedSegments"].append({"CoverageOrdinal": ordinal, "MaximumMinorUnits": 1})
                with self.assertRaises(module.Rejected): module.verify_plan_policy(self.manifest, self.scope, p, "completion-admission")

    def test_missing_duplicate_or_unknown_accepted_proofs_fail(self):
        for action in (lambda a: a.pop(0), lambda a: a.append(copy.deepcopy(a[0])), lambda a: a[0].update(coverageOrdinal=999)):
            m, s, p = fixture(); action(m["continuation"]["accepted"]); s["plansSha256"] = module.digest(m)
            with self.assertRaises(module.Rejected): module.verify_plan_policy(m, s, p, "completion-admission")

    def test_original_proposals_ranges_payload_voice_and_manifest_cannot_change(self):
        for field in ("PayloadSha256", "PayloadBytes"):
            p = copy.deepcopy(self.policy); p["Articles"][0]["Proposal"]["Parts"][8][field] = 101 if field.endswith("Bytes") else "a" * 64
            with self.assertRaises(module.Rejected): module.verify_plan_policy(self.manifest, self.scope, p, "completion-admission")
        self.manifest["parts"][8]["payloadBytes"] += 1; self.rebind()
        with self.assertRaisesRegex(module.Rejected, "OriginalManifest"): self.verify()

    def test_remaining_and_financial_exactness_not_125(self):
        for field, value in (("MaximumNewAttempts", 125), ("MaximumAdditionalReservationMinorUnits", 120), ("MaximumNewAttempts", True)):
            p = copy.deepcopy(self.policy); p[field] = value
            with self.assertRaises(module.Rejected): module.verify_plan_policy(self.manifest, self.scope, p, "completion-admission")
        self.policy["Budget"]["ProjectCommittedMinorUnits"] = 159
        with self.assertRaises(module.Rejected): self.verify()

    def test_unknown_tax_missing_evidence_or_bound_exceeding_total_fail_closed(self):
        for field, value in (("taxFeeUpperBoundMinorUnits", None), ("taxFeeUpperBoundMinorUnits", 222),
                             ("taxFeeUpperBoundMinorUnits", True), ("taxFeeEvidenceSha256", "0" * 64),
                             ("overallCeilingMinorUnits", 501)):
            m, s, p = fixture(); m["continuation"]["budget"][field] = value; s["plansSha256"] = module.digest(m)
            with self.assertRaises(module.Rejected): module.verify_plan_policy(m, s, p, "completion-admission")

    def test_expiry_utc_and_approval_mismatch_fail(self):
        for expiry in ((datetime.now(timezone.utc) - timedelta(seconds=1)).isoformat(),
                       (datetime.now(timezone.utc) + timedelta(hours=7)).isoformat(), "2030-01-01T00:00:00"):
            p = copy.deepcopy(self.policy); p["ExpiresAtUtc"] = expiry
            with self.assertRaises(module.Rejected): module.verify_plan_policy(self.manifest, self.scope, p, "completion-admission")
        self.policy["SourceApprovalSha256"] = "c" * 64
        with self.assertRaises(module.Rejected): self.verify()

    def test_zero_cost_resume_only_unaccepted_exact_request(self):
        part = self.manifest["parts"][7]
        resume = {"HistoricalPolicy": self.policy, "ExpiresAtUtc": self.policy["ExpiresAtUtc"],
                  "ExecutionEnvelope": {"CompiledPayloadSha256": part["payloadSha256"], "CompiledPayloadBytes": part["payloadBytes"],
                                        "VoiceName": part["voiceName"], "AudioEncoding": "MP3"},
                  "MaximumExistingDispatches": 1, "MaximumNewReservations": 0, "MaximumAdditionalReservationMinorUnits": 0}
        module.verify_plan_policy(self.manifest, self.scope, resume, "resume-admission")
        accepted = self.manifest["parts"][0]; resume["ExecutionEnvelope"].update(CompiledPayloadSha256=accepted["payloadSha256"], CompiledPayloadBytes=accepted["payloadBytes"])
        with self.assertRaises(module.Rejected): module.verify_plan_policy(self.manifest, self.scope, resume, "resume-admission")


if __name__ == "__main__":
    unittest.main(verbosity=2)
