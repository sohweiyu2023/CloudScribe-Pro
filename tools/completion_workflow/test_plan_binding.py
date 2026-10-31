import copy
import unittest
from datetime import datetime, timedelta, timezone
from completion_register import Rejected, digest, verify_plan_policy
from fixture_data import make_plan_fixture


class PlanBindingTests(unittest.TestCase):
    def setUp(self):
        self.manifest, self.resume, self.completion = make_plan_fixture(digest)
        self.scope = {"plansSha256": digest(self.manifest), "endUtc": (datetime.now(timezone.utc) + timedelta(hours=6)).isoformat().replace("+00:00", "Z")}

    def test_exact_held_and_remaining_policy(self):
        verify_plan_policy(self.manifest, self.scope, self.resume, "resume-admission")
        verify_plan_policy(self.manifest, self.scope, self.completion, "completion-admission")

    def test_changed_voice_text_range_source_document_or_payload_rejected(self):
        for field, value in (("VoiceName", "en-US-Standard-C"), ("LanguageCode", "en-US"), ("SourceSha256", "a" * 64),
                             ("DocumentId", "other-document"), ("RevisionId", "other-revision")):
            with self.subTest(field=field):
                policy = copy.deepcopy(self.completion)
                policy["Articles"][0]["Proposal"][field] = value
                with self.assertRaises(Rejected):
                    verify_plan_policy(self.manifest, self.scope, policy, "completion-admission")

    def test_accepted_coverage_cannot_be_readded(self):
        self.completion["Articles"][0]["AllowedSegments"].append({"CoverageOrdinal": 3, "MaximumMinorUnits": 1})
        with self.assertRaisesRegex(Rejected, "AcceptedOrUnapprovedCoverage"):
            verify_plan_policy(self.manifest, self.scope, self.completion, "completion-admission")

    def test_held_coverage_cannot_be_readded_as_new(self):
        self.completion["Articles"][0]["AllowedSegments"].append({"CoverageOrdinal": 1, "MaximumMinorUnits": 1})
        with self.assertRaisesRegex(Rejected, "AcceptedOrUnapprovedCoverage"):
            verify_plan_policy(self.manifest, self.scope, self.completion, "completion-admission")

    def test_different_held_authorization_payload_or_voice_rejected(self):
        for field, value in (("AuthorizationId", "other-identity"), ("payload", "b" * 64), ("voice", "other-voice")):
            policy = copy.deepcopy(self.resume)
            if field == "AuthorizationId":
                policy[field] = value
            elif field == "payload":
                policy["ExecutionEnvelope"]["CompiledPayloadSha256"] = value
            else:
                policy["ExecutionEnvelope"]["VoiceName"] = value
            with self.assertRaises(Rejected):
                verify_plan_policy(self.manifest, self.scope, policy, "resume-admission")

    def test_budget_overstatement_rejected_before_any_admission(self):
        for field in ("MaximumNewAttempts", "MaximumAdditionalReservationMinorUnits"):
            policy = copy.deepcopy(self.completion)
            policy[field] = 109
            with self.assertRaisesRegex(Rejected, "ExactCompletionBudget"):
                verify_plan_policy(self.manifest, self.scope, policy, "completion-admission")

    def test_manifest_pin_cannot_be_substituted(self):
        self.manifest["heldAuthorizationId"] = "other-identity"
        with self.assertRaisesRegex(Rejected, "PlanManifestPin"):
            verify_plan_policy(self.manifest, self.scope, self.resume, "resume-admission")

    def test_policy_expiry_cannot_extend_parent_window(self):
        self.completion["ExpiresAtUtc"] = (datetime.now(timezone.utc) + timedelta(hours=7)).isoformat()
        with self.assertRaisesRegex(Rejected, "PolicyWindow"):
            verify_plan_policy(self.manifest, self.scope, self.completion, "completion-admission")

    def test_currency_scale_and_project_cap_are_bound(self):
        for field, value in (("Currency", "EUR"), ("Scale", 0)):
            policy = copy.deepcopy(self.completion)
            policy[field] = value
            with self.assertRaisesRegex(Rejected, "CompletionCurrency"):
                verify_plan_policy(self.manifest, self.scope, policy, "completion-admission")
        self.completion["Budget"]["ProjectCeilingMinorUnits"] = 501
        with self.assertRaisesRegex(Rejected, "CompletionProjectCap"):
            verify_plan_policy(self.manifest, self.scope, self.completion, "completion-admission")

    def test_duplicate_coverage_and_missing_remaining_coverage_rejected(self):
        policy = copy.deepcopy(self.completion)
        policy["Articles"][0]["AllowedSegments"] *= 2
        with self.assertRaisesRegex(Rejected, "CoveragePermission"):
            verify_plan_policy(self.manifest, self.scope, policy, "completion-admission")
        self.completion["Articles"][0]["AllowedSegments"] = []
        with self.assertRaisesRegex(Rejected, "ExactRemainingCoverage"):
            verify_plan_policy(self.manifest, self.scope, self.completion, "completion-admission")


if __name__ == "__main__":
    unittest.main(verbosity=2)
