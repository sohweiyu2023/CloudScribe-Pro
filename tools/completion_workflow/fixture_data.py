"""Synthetic hash-only plan fixtures. Not a live admission or financial policy."""
import hashlib
from datetime import datetime, timedelta, timezone


def make_plan_fixture(digest):
    def sha(value):
        return hashlib.sha256(value.encode()).hexdigest()
    document = "11111111-1111-1111-1111-111111111111"
    held = "22222222-2222-2222-2222-222222222222"
    plan = sha("synthetic-plan")
    voice = "cmn-CN-Chirp3-HD-Alnilam"
    proposal = {"DocumentId": document, "RevisionId": document, "SourceSha256": sha("synthetic-source"),
                "SourceScalars": 9, "LanguageCode": "cmn-CN", "VoiceName": voice,
                "PlannerVersion": "synthetic", "PlanSha256": plan,
                "Parts": [{"CoverageOrdinal": i, "Range": {"StartScalar": (i - 1) * 3, "EndScalarExclusive": i * 3,
                           "TextSha256": sha("synthetic-range-" + str(i)), "ReusedReceiptSha256": None},
                           "PayloadSha256": sha("synthetic-payload-" + str(i)), "PayloadBytes": 100} for i in range(1, 4)]}
    manifest = {"schema": 1, "accountId": "synthetic-account", "projectId": "synthetic-project",
                "heldAuthorizationId": held, "proposals": [{"planSha256": plan, "proposalSha256": digest(proposal)}],
                "parts": [{"planSha256": plan, "coverageOrdinal": i, "payloadSha256": sha("synthetic-payload-" + str(i)),
                           "payloadBytes": 100, "voiceName": voice, "languageCode": "cmn-CN", "kind": "held" if i == 1 else "new"} for i in (1, 2)]}
    expiry = (datetime.now(timezone.utc) + timedelta(minutes=15)).isoformat().replace("+00:00", "Z")
    original = {"AccountId": "synthetic-account", "ProjectId": "synthetic-project", "ExpiresAtUtc": expiry,
                "Currency": "USD", "Scale": 2, "Budget": {"ProjectCeilingMinorUnits": 500, "AllocationCeilingMinorUnits": 144},
                "Articles": [{"Proposal": proposal, "AllowedSegments": [{"CoverageOrdinal": i, "MaximumMinorUnits": 1} for i in (1, 2)]}],
                "MaximumNewAttempts": 2, "MaximumAdditionalReservationMinorUnits": 2}
    resume = {"HistoricalPolicy": original, "ExpiresAtUtc": expiry, "AuthorizationId": held,
              "ExecutionEnvelope": {"CompiledPayloadSha256": sha("synthetic-payload-1"), "CompiledPayloadBytes": 100,
                                    "VoiceName": voice, "AudioEncoding": "MP3"},
              "MaximumExistingDispatches": 1, "MaximumNewReservations": 0, "MaximumAdditionalReservationMinorUnits": 0}
    completion = dict(original, Articles=[{"Proposal": proposal, "AllowedSegments": [{"CoverageOrdinal": 2, "MaximumMinorUnits": 1}]}],
                      MaximumNewAttempts=1, MaximumAdditionalReservationMinorUnits=1)
    return manifest, resume, completion
