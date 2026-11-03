"""Private, fail-closed workflow gate. Never supplies Google or spending authority.

An admitted scope digest must come from independent review. No live scope is bundled.
Only fixed metadata/hashes are persisted. Native UI is NOT intercepted by this module.
"""
from __future__ import annotations

import hashlib
from contextlib import closing, contextmanager, ExitStack
import json
import os
from pathlib import Path
import re
import sqlite3
import time
import uuid
from datetime import datetime, timedelta, timezone


class Rejected(RuntimeError):
    pass


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True)


def digest(value):
    return hashlib.sha256(canonical(value).encode()).hexdigest()


def hash_value(value):
    if not isinstance(value, str) or not re.fullmatch(r"[0-9a-f]{64}", value) or value == "0" * 64:
        raise Rejected("InvalidHash")
    return value


def utc(value):
    if not isinstance(value, str) or not value.endswith("Z"):
        raise Rejected("ExplicitUtcRequired")
    parsed = datetime.fromisoformat(value[:-1] + "+00:00")
    return int(parsed.timestamp() * 1_000_000)


def physical(path):
    path = Path(os.path.abspath(path))
    for part in (path, *path.parents):
        if part.is_symlink() or part.is_junction():
            raise Rejected("LinkedPath")
    return path


SCOPE_KEYS = {"schema", "approvalSha256", "buildSha256", "profileSha256", "accountSha256",
              "projectSha256", "plansSha256", "registerPathSha256", "startUtc", "endUtc", "maxRefreshes", "maxResumeAdmissions"}
SUCCESSOR_SCOPE_KEYS = SCOPE_KEYS | {"maxAdmissions", "predecessorRegisterPath", "predecessorScopeSha256", "transitionId"}
CONTINUATION_SCOPE_KEYS = SUCCESSOR_SCOPE_KEYS | {"acceptedEvidenceSha256", "maximumNewAttempts"}
WHOLE_ARTICLE_SCOPE_KEYS = CONTINUATION_SCOPE_KEYS | {"wholeArticleGrantSha256"}
DIAGNOSTIC_SCOPE_KEYS = CONTINUATION_SCOPE_KEYS | {"diagnosticPurpose", "privateCaptureConsentSha256", "diagnosticWindowMode"}


def path_digest(path):
    return hashlib.sha256(os.path.normcase(str(physical(path))).encode("utf-8")).hexdigest()


def validate_scope(scope):
    if (not isinstance(scope, dict) or type(scope.get("schema")) is not int or scope["schema"] not in (1, 2, 3, 4, 5, 6)
            or set(scope) != {1: SCOPE_KEYS, 2: SUCCESSOR_SCOPE_KEYS, 3: CONTINUATION_SCOPE_KEYS, 4: WHOLE_ARTICLE_SCOPE_KEYS, 5: WHOLE_ARTICLE_SCOPE_KEYS, 6: DIAGNOSTIC_SCOPE_KEYS}[scope["schema"]]):
        raise Rejected("ScopeSchema")
    if scope["schema"] == 6 and (scope["diagnosticPurpose"] != "unchanged-provider-evidence"
            or scope["diagnosticWindowMode"] not in ("inherited", "fresh-10-minute")
            or type(scope["maximumNewAttempts"]) is not int or scope["maximumNewAttempts"] != 1
            or scope["privateCaptureConsentSha256"] == scope["approvalSha256"]):
        raise Rejected("DiagnosticScope")
    for key in scope:
        if key.endswith("Sha256"):
            hash_value(scope[key])
    if scope["schema"] in (2, 3, 4, 5, 6):
        if type(scope["maxAdmissions"]) is not int or scope["maxAdmissions"] != 24:
            raise Rejected("FixedAdmissionLimit")
        if not isinstance(scope["predecessorRegisterPath"], str) or not Path(scope["predecessorRegisterPath"]).is_absolute():
            raise Rejected("PredecessorPath")
        if str(uuid.UUID(scope["transitionId"])) != scope["transitionId"] or uuid.UUID(scope["transitionId"]).int == 0:
            raise Rejected("SuccessorTransitionId")
    if scope["schema"] in (3, 4, 5) and (type(scope["maximumNewAttempts"]) is not int or not 1 <= scope["maximumNewAttempts"] <= 125):
        raise Rejected("ContinuationAttemptLimit")
    if (type(scope["maxRefreshes"]) is not int or scope["maxRefreshes"] != 24 or
            type(scope["maxResumeAdmissions"]) is not int or scope["maxResumeAdmissions"] != 24):
        raise Rejected("FixedLimits")
    duration = 600 if scope["schema"] == 6 and scope["diagnosticWindowMode"] == "fresh-10-minute" else 6*3600
    if utc(scope["endUtc"]) - utc(scope["startUtc"]) != duration * 1_000_000:
        raise Rejected("FixedSixHourWindow")


TRANSITION_KEYS = {"SchemaVersion", "TransitionId", "SourceApprovalSha256", "ScopeSha256", "ExpectedHeadSha256",
                   "PreviousConfigPath", "PreviousGateSha256", "NewGateSha256", "PreviousBuildSha256", "NewBuildSha256"}
INTERPRETER_TRANSITION_KEYS = TRANSITION_KEYS | {"TransitionKind", "PreviousPythonSha256", "NewPythonSha256",
    "InterpreterEvidencePath", "InterpreterEvidenceSha256", "RepairApprovalSha256"}


def transition_kind(transition):
    return transition.get("TransitionKind", "build")


def source_transition(transition):
    return transition_kind(transition) in ("helper-source", "interpreter-binding")


def validate_transition(transition):
    if (not isinstance(transition, dict) or set(transition) not in (TRANSITION_KEYS, TRANSITION_KEYS | {"TransitionKind"}, INTERPRETER_TRANSITION_KEYS)
            or ("TransitionKind" in transition and transition["TransitionKind"] not in ("helper-source", "interpreter-binding"))
            or type(transition["SchemaVersion"]) is not int or transition["SchemaVersion"] != 1):
        raise Rejected("TransitionSchema")
    interpreter = transition_kind(transition) == "interpreter-binding"
    if interpreter != (set(transition) == INTERPRETER_TRANSITION_KEYS):
        raise Rejected("InterpreterTransitionSchema")
    for name in TRANSITION_KEYS:
        if name.endswith("Sha256"):
            hash_value(transition[name])
    if str(uuid.UUID(transition["TransitionId"])) != transition["TransitionId"] or uuid.UUID(transition["TransitionId"]).int == 0:
        raise Rejected("TransitionId")
    if not isinstance(transition["PreviousConfigPath"], str) or not Path(transition["PreviousConfigPath"]).is_absolute():
        raise Rejected("TransitionConfigPath")
    if interpreter:
        for key in ("PreviousPythonSha256", "NewPythonSha256", "InterpreterEvidenceSha256", "RepairApprovalSha256"):
            hash_value(transition[key])
        if (transition["PreviousPythonSha256"] == transition["NewPythonSha256"]
                or transition["RepairApprovalSha256"] == transition["SourceApprovalSha256"]
                or not isinstance(transition["InterpreterEvidencePath"], str)
                or not Path(transition["InterpreterEvidencePath"]).is_absolute()):
            raise Rejected("InterpreterTransitionBinding")
    same_build = transition["PreviousBuildSha256"] == transition["NewBuildSha256"]
    if (same_build != source_transition(transition)
            or transition["PreviousGateSha256"] == transition["NewGateSha256"]):
        raise Rejected("TransitionMustChangeBuild")


def interpreter_pre_effect(state):
    return (state["scope"]["schema"] == 4 and state["activated"] and not state["retired"]
            and not state["retirement"] and state["refresh"] == 0 and state["admissions"] == 0
            and state["buildTransitions"] == 0)


def validate_interpreter_withdrawal(record):
    keys = {"SchemaVersion", "WithdrawalId", "WithdrawalApprovalSha256", "RepairTransitionId", "RepairIdentitySha256",
            "ScopeSha256", "ExpectedHeadSha256", "PreviousGateSha256", "RestoredGateSha256", "BackupPath",
            "PreviousInstallSha256", "PreviousRequiredSha256"}
    if not isinstance(record, dict) or set(record) != keys or type(record["SchemaVersion"]) is not int or record["SchemaVersion"] != 1:
        raise Rejected("InterpreterWithdrawalSchema")
    for key in keys:
        if key.endswith("Sha256"):
            hash_value(record[key])
    for key in ("WithdrawalId", "RepairTransitionId"):
        if str(uuid.UUID(record[key])) != record[key] or uuid.UUID(record[key]).int == 0:
            raise Rejected("InterpreterWithdrawalIdentity")
    if not isinstance(record["BackupPath"], str) or not Path(record["BackupPath"]).is_absolute():
        raise Rejected("InterpreterWithdrawalIdentity")


def interpreter_binding_event(state):
    return state["interpreterRepair"] or state["interpreterActivation"]


def interpreter_withdrawal_matches(state, record):
    validate_interpreter_withdrawal(record)
    repair = interpreter_binding_event(state)
    if not interpreter_pre_effect(state) or not repair:
        raise Rejected("InterpreterWithdrawalState")
    transition = repair["transition"] if repair["type"] == "source-claim" else repair["interpreterHandoff"]
    if (record["ScopeSha256"] != digest(state["scope"]) or record["RepairTransitionId"] != repair["id"]
            or record["RepairIdentitySha256"] != repair["identity"]
            or record["PreviousGateSha256"] != transition["NewGateSha256"]
            or record["RestoredGateSha256"] != transition["PreviousGateSha256"]
            or record["WithdrawalApprovalSha256"] in (transition["RepairApprovalSha256"], state["scope"]["approvalSha256"])
            or state["currentBuild"] != transition.get("NewBuildSha256", state["scope"]["buildSha256"])):
        raise Rejected("InterpreterWithdrawalIdentity")


def uuid_identity(value):
    """Only non-nil N32/D36 UUID text; no braces, whitespace or coerced values."""
    if (not isinstance(value, str) or re.fullmatch(r"(?:[0-9a-fA-F]{32}|[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})", value) is None):
        raise Rejected("InvalidHeldIdentity")
    parsed = uuid.UUID(value)
    if parsed.int == 0:
        raise Rejected("InvalidHeldIdentity")
    return parsed.hex


HANDOFF_KEYS = {"SchemaVersion", "TransitionId", "SourceApprovalSha256", "PreviousConfigPath", "PreviousGateSha256",
                "PreviousScopeSha256", "ExpectedPreviousHeadSha256", "NewConfigPath", "NewGateSha256", "NewScopePath",
                "NewScopeSha256", "DatabaseSha256", "FailedAuthorizationId", "FailedLocalAttemptId", "FailedPlanSha256",
                "FailedCoverageOrdinal", "FailedPayloadSha256", "FailedPayloadBytes"}
CONTINUATION_HANDOFF_KEYS = HANDOFF_KEYS - {"FailedAuthorizationId", "FailedLocalAttemptId", "FailedPlanSha256",
                                          "FailedCoverageOrdinal", "FailedPayloadSha256", "FailedPayloadBytes"} | {
                                              "TransitionKind", "OriginalManifestSha256", "AcceptedEvidenceSha256"}
INTERPRETER_HANDOFF_KEYS = CONTINUATION_HANDOFF_KEYS | {"PreviousPythonSha256", "NewPythonSha256",
    "InterpreterEvidencePath", "InterpreterEvidenceSha256", "RepairApprovalSha256"}
SENTENCE_HANDOFF_KEYS = CONTINUATION_HANDOFF_KEYS | (HANDOFF_KEYS - CONTINUATION_HANDOFF_KEYS) | {
    "UsedRefreshes", "UsedAdmissions", "UsedResumeAdmissions"}


def interpreter_handoff(h):
    return isinstance(h, dict) and h.get("TransitionKind") == "whole-article-interpreter-continuation"


def diagnostic_handoff(h):
    return isinstance(h, dict) and ((h.get("SchemaVersion"),h.get("TransitionKind")) in
        ((5,"sentence-evidence-diagnostic"),(6,"expired-sentence-evidence-diagnostic")))


def expired_diagnostic_handoff(h):
    return diagnostic_handoff(h) and h["SchemaVersion"] == 6


def diagnostic_window_binding(old, scope, h):
    fresh = expired_diagnostic_handoff(h)
    allowed_stop = fresh and old["stopReason"] in ("WindowExpired", "ClockRollback", "ClockDiscontinuity")
    if (not old["activated"] or (old["stopped"] and not allowed_stop)
            or h["InheritedClockEpochEvidence"] != old["clockEpoch"]
            or scope["acceptedEvidenceSha256"] != old["scope"]["acceptedEvidenceSha256"]
            or (h["UsedRefreshes"],h["UsedAdmissions"],h["UsedResumeAdmissions"]) != (old["refresh"],old["admissions"],old["resume-admission"])):
        raise Rejected("DiagnosticCounterOrEpoch")
    if fresh:
        e = h["ClockEpochEvidence"]
        if (scope["diagnosticWindowMode"] != "fresh-10-minute" or utc(scope["startUtc"]) != e["ObservedUtcMicros"]
                or e["TrustedUtcLowerMicros"] < utc(old["scope"]["endUtc"])
                or e["ObservedUtcMicros"] < utc(old["scope"]["endUtc"])
                or e["PreviousLastGoodUtcMicros"] != old["lastGoodUtc"]):raise Rejected("DiagnosticWindow")
    elif scope["diagnosticWindowMode"] != "inherited" or any(scope[k] != old["scope"][k] for k in ("startUtc", "endUtc")):
        raise Rejected("DiagnosticWindow")


def sentence_handoff(h):
    return isinstance(h, dict) and h.get("TransitionKind") in ("same-window-sentence-replan", "same-window-sentence-replan-after-reboot", "expired-terminal-sentence-replan")


def reboot_sentence_handoff(h):
    return isinstance(h, dict) and h.get("TransitionKind") == "same-window-sentence-replan-after-reboot"


def expired_sentence_handoff(h):
    return isinstance(h,dict) and h.get('TransitionKind')=='expired-terminal-sentence-replan'


def clock_sentence_handoff(h):
    return reboot_sentence_handoff(h) or expired_sentence_handoff(h)


def validate_clock_epoch(e, *, require_reboot=True):
    keys={"ObservedUtcMicros","ObservedMonoMicros","TrustedUtcLowerMicros","TrustedUtcUpperMicros","PreviousLastGoodUtcMicros","WindowsBootUtc","BootReadbackSha256"}
    if not isinstance(e,dict) or set(e)!=keys or any(type(e[k]) is not int or e[k]<0 for k in keys-{"WindowsBootUtc","BootReadbackSha256"}):
        raise Rejected("ClockEpochEvidence")
    hash_value(e["BootReadbackSha256"])
    if (not 0 <= e["TrustedUtcUpperMicros"]-e["TrustedUtcLowerMicros"] <= 5_000_000
            or not e["TrustedUtcLowerMicros"] <= e["ObservedUtcMicros"] <= e["TrustedUtcUpperMicros"]
            or not 0 < utc(e["WindowsBootUtc"]) <= e["TrustedUtcUpperMicros"]
            or (require_reboot and utc(e["WindowsBootUtc"]) <= e["PreviousLastGoodUtcMicros"])):
        raise Rejected("ClockEpochEvidence")


def epoch_deadline(scope,e):
    # Conservative local-clock cutoff: independent UTC uncertainty can only shorten permission.
    return utc(scope["endUtc"])-max(0,e["TrustedUtcUpperMicros"]-e["ObservedUtcMicros"])-250_000


def effective_deadline(scope,e=None):
    return (datetime.fromtimestamp(epoch_deadline(scope,e)/1_000_000,timezone.utc).isoformat().replace('+00:00','Z') if e else scope['endUtc'])


def require_epoch_policy_deadline(state,expiry):
    e=state.get("clockEpoch")
    if e:
        value=datetime.fromisoformat(expiry.replace('Z','+00:00'))
        if value.utcoffset()!=timedelta(0) or int(value.timestamp()*1_000_000)>epoch_deadline(state["scope"],e):
            raise Rejected("ClockEpochPolicyDeadline")


def validate_handoff(h):
    if (not isinstance(h, dict) or type(h.get("SchemaVersion")) is not int or h["SchemaVersion"] not in (1, 2, 3, 4, 5, 6)
            or set(h) != (SENTENCE_HANDOFF_KEYS | {"InheritedClockEpochEvidence", "ClockEpochEvidence", "WindowApprovalSha256"} if expired_diagnostic_handoff(h) else SENTENCE_HANDOFF_KEYS | {"InheritedClockEpochEvidence"} if diagnostic_handoff(h) else SENTENCE_HANDOFF_KEYS | {"ClockEpochEvidence", "WindowApprovalSha256"} if expired_sentence_handoff(h) else SENTENCE_HANDOFF_KEYS | {"ClockEpochEvidence"} if reboot_sentence_handoff(h) else SENTENCE_HANDOFF_KEYS if sentence_handoff(h) else INTERPRETER_HANDOFF_KEYS if interpreter_handoff(h) else HANDOFF_KEYS if h["SchemaVersion"] == 1 else CONTINUATION_HANDOFF_KEYS)
            or (h["SchemaVersion"] == 2 and h["TransitionKind"] != "accepted-continuation")
            or (h["SchemaVersion"] == 3 and h["TransitionKind"] not in ("whole-article-continuation", "whole-article-interpreter-continuation"))
            or (interpreter_handoff(h) and h["SchemaVersion"] != 3)
            or (h["SchemaVersion"] == 4) != sentence_handoff(h)
            or (h["SchemaVersion"] in (5, 6)) != diagnostic_handoff(h)):
        raise Rejected("HandoffSchema")
    for key in h:
        if key.endswith("Sha256"):
            hash_value(h[key])
    if str(uuid.UUID(h["TransitionId"])) != h["TransitionId"] or uuid.UUID(h["TransitionId"]).int == 0:
        raise Rejected("HandoffId")
    if h["SchemaVersion"] == 1 or sentence_handoff(h) or diagnostic_handoff(h):
        uuid_identity(h["FailedAuthorizationId"])
        if (not re.fullmatch(r"[a-f0-9]{32}", h["FailedLocalAttemptId"]) or int(h["FailedLocalAttemptId"], 16) == 0
            or type(h["FailedCoverageOrdinal"]) is not int or h["FailedCoverageOrdinal"] < 1
            or type(h["FailedPayloadBytes"]) is not int or not 1 <= h["FailedPayloadBytes"] <= 5000):
            raise Rejected("HandoffBinding")
    if (sentence_handoff(h) or diagnostic_handoff(h)) and (any(type(h[k]) is not int or not 0 <= h[k] <= 24 for k in
                                    ("UsedRefreshes", "UsedAdmissions", "UsedResumeAdmissions"))
                                or h["UsedResumeAdmissions"] > h["UsedAdmissions"]):
        raise Rejected("SentenceCounterBinding")
    if clock_sentence_handoff(h):validate_clock_epoch(h["ClockEpochEvidence"],require_reboot=not expired_sentence_handoff(h))
    if diagnostic_handoff(h) and h["InheritedClockEpochEvidence"] is not None:
        validate_clock_epoch(h["InheritedClockEpochEvidence"], require_reboot=False)
    if expired_diagnostic_handoff(h):
        validate_clock_epoch(h["ClockEpochEvidence"], require_reboot=False)
        if h["WindowApprovalSha256"] != h["SourceApprovalSha256"]:raise Rejected("FreshWindowApprovalRequired")
    if expired_sentence_handoff(h) and h["WindowApprovalSha256"] != h["SourceApprovalSha256"]:
        raise Rejected("FreshWindowApprovalRequired")
    if h["PreviousScopeSha256"] == h["NewScopeSha256"] or h["PreviousGateSha256"] == h["NewGateSha256"]:
        raise Rejected("HandoffBinding")
    if interpreter_handoff(h) and (h["PreviousPythonSha256"] == h["NewPythonSha256"]
            or h["RepairApprovalSha256"] == h["SourceApprovalSha256"]
            or not isinstance(h["InterpreterEvidencePath"], str) or not Path(h["InterpreterEvidencePath"]).is_absolute()):
        raise Rejected("InterpreterTransitionBinding")
    for name in ("PreviousConfigPath", "NewConfigPath", "NewScopePath"):
        if not isinstance(h[name], str) or not Path(h[name]).is_absolute():
            raise Rejected("HandoffPath")


def verify_plan_policy(manifest, scope, policy, kind):
    """Bind actual immutable proposal/coverage projection, not a caller assertion."""
    if scope.get("schema") == 6:
        return verify_diagnostic_policy(manifest, scope, policy, kind)
    original = policy.get("HistoricalPolicy", {}) if kind == "resume-admission" else policy
    if scope.get("schema", 1) not in (4, 5) and (original.get("SchemaVersion") == 3
            or original.get("WholeArticleGeneration") is not None):
        raise Rejected("WholeArticleGrantBinding")
    if scope.get("schema", 1) == 5:
        return verify_sentence_policy(manifest, scope, policy, kind)
    if scope.get("schema", 1) in (3, 4):
        return verify_continuation_policy(manifest, scope, policy, kind)
    if scope.get("schema", 1) == 2:
        return verify_successor_policy(manifest, scope, policy, kind)
    if digest(manifest) != scope["plansSha256"]:
        raise Rejected("PlanManifestPin")
    if set(manifest) != {"schema", "accountId", "projectId", "heldAuthorizationId", "proposals", "parts"} or type(manifest["schema"]) is not int or manifest["schema"] != 1:
        raise Rejected("PlanManifestSchema")
    if not 1 <= len(manifest["proposals"]) <= 2 or not 1 <= len(manifest["parts"]) <= 109:
        raise Rejected("PlanManifestBounds")
    expiry = datetime.fromisoformat(policy["ExpiresAtUtc"].replace("Z", "+00:00"))
    if expiry.tzinfo is None or expiry.utcoffset() != timedelta(0):
        raise Rejected("PolicyUtcRequired")
    if int(expiry.timestamp() * 1_000_000) > utc(scope["endUtc"]) or expiry.timestamp() <= time.time():
        raise Rejected("PolicyWindow")
    plans = {item["planSha256"]: item["proposalSha256"] for item in manifest["proposals"]}
    if len(plans) != len(manifest["proposals"]) or not plans:
        raise Rejected("DuplicatePlan")
    parts = {(item["planSha256"], item["coverageOrdinal"]): item for item in manifest["parts"]}
    if len(parts) != len(manifest["parts"]):
        raise Rejected("DuplicateCoverage")
    original = policy["HistoricalPolicy"] if kind == "resume-admission" else policy
    if original.get("Currency") != "USD" or original.get("Scale") != 2:
        raise Rejected("CompletionCurrency")
    budget = original["Budget"]
    if kind == "completion-admission" and (type(budget["ProjectCeilingMinorUnits"]) is not int or budget["ProjectCeilingMinorUnits"] > 500):
        raise Rejected("CompletionProjectCap")
    if kind == "completion-admission" and (type(budget["AllocationCeilingMinorUnits"]) is not int or budget["AllocationCeilingMinorUnits"] > 500):
        raise Rejected("CompletionAllocationCap")
    if original["AccountId"] != manifest["accountId"] or original["ProjectId"] != manifest["projectId"]:
        raise Rejected("PlanAccountBinding")
    observed = {}
    allowed = set()
    for article in original["Articles"]:
        proposal = article["Proposal"]
        plan = proposal["PlanSha256"]
        if plan in observed or plans.get(plan) != digest(proposal):
            raise Rejected("ExactProposalBinding")
        observed[plan] = proposal
        by_ordinal = {part["CoverageOrdinal"]: part for part in proposal["Parts"]}
        for permission in article["AllowedSegments"]:
            key = (plan, permission["CoverageOrdinal"])
            if key in allowed or permission["MaximumMinorUnits"] != 1:
                raise Rejected("CoveragePermission")
            allowed.add(key)
            if kind != "resume-admission":
                expected = parts.get(key)
                actual = by_ordinal.get(key[1])
                if not expected or expected["kind"] != "new" or not actual:
                    raise Rejected("AcceptedOrUnapprovedCoverage")
                if (actual["PayloadSha256"] != expected["payloadSha256"] or actual["PayloadBytes"] != expected["payloadBytes"]
                        or proposal["VoiceName"] != expected["voiceName"] or proposal["LanguageCode"] != expected["languageCode"]):
                    raise Rejected("CoveragePayloadBinding")
    if kind == "completion-admission":
        required = {key for key, part in parts.items() if part["kind"] == "new"}
        if set(observed) != set(plans) or allowed != required or len(required) > 108:
            raise Rejected("ExactRemainingCoverage")
        if type(policy["MaximumNewAttempts"]) is not int or type(policy["MaximumAdditionalReservationMinorUnits"]) is not int or policy["MaximumNewAttempts"] != len(required) or policy["MaximumAdditionalReservationMinorUnits"] != len(required):
            raise Rejected("ExactCompletionBudget")
    else:
        envelope = policy["ExecutionEnvelope"]
        matches = [part for part in parts.values() if part["payloadSha256"] == envelope["CompiledPayloadSha256"]
                   and part["payloadBytes"] == envelope["CompiledPayloadBytes"] and part["voiceName"] == envelope["VoiceName"]]
        if len(matches) != 1:
            raise Rejected("ExactResumeCoverage")
        part = matches[0]
        key = (part["planSha256"], part["coverageOrdinal"])
        if key not in allowed or part["planSha256"] not in observed or envelope["AudioEncoding"] != "MP3":
            raise Rejected("ResumeNotOriginallyAdmitted")
        if part["kind"] == "held" and uuid_identity(policy["AuthorizationId"]) != uuid_identity(manifest["heldAuthorizationId"]):
            raise Rejected("OriginalHeldIdentity")
        if part["kind"] not in ("held", "new"):
            raise Rejected("AcceptedCoverageReplay")
        if policy["MaximumExistingDispatches"] != 1 or policy["MaximumNewReservations"] != 0 or policy["MaximumAdditionalReservationMinorUnits"] != 0:
            raise Rejected("ResumeFinancialBounds")


def successor_policy_kind(manifest, policy):
    if "diagnostic" in manifest:
        return "diagnostic-admission"
    if manifest.get("schema") == 4:
        allowed = {(a["Proposal"]["PlanSha256"], x["CoverageOrdinal"]) for a in policy["Articles"] for x in a["AllowedSegments"]}
        first = {(x["planSha256"], x["coverageOrdinal"]) for x in manifest["firstStage"]}
        return "pilot-admission" if allowed == first else "completion-admission"
    if manifest.get("schema") == 3:
        return "completion-admission"
    allowed = {(a["Proposal"]["PlanSha256"], x["CoverageOrdinal"]) for a in policy["Articles"] for x in a["AllowedSegments"]}
    pilot = (manifest["pilot"]["planSha256"], manifest["pilot"]["coverageOrdinal"])
    return "pilot-admission" if allowed == {pilot} else "completion-admission"


def validate_successor_manifest(manifest, scope):
    if (digest(manifest) != scope["plansSha256"] or set(manifest) != {"schema", "accountId", "projectId", "proposals", "parts", "pilot"}
            or type(manifest["schema"]) is not int or manifest["schema"] != 2 or len(manifest["parts"]) != 126
            or not 1 <= len(manifest["proposals"]) <= 2 or set(manifest["pilot"]) != {"planSha256", "coverageOrdinal"}):
        raise Rejected("SuccessorManifest")
    parts = {(x["planSha256"], x["coverageOrdinal"]): x for x in manifest["parts"]}
    plans = {x["planSha256"]: x["proposalSha256"] for x in manifest["proposals"]}
    pilot = (manifest["pilot"]["planSha256"], manifest["pilot"]["coverageOrdinal"])
    if (len(parts) != 126 or len(plans) != len(manifest["proposals"]) or pilot not in parts or any(x["kind"] != "new" for x in parts.values())
            or len({(x["payloadSha256"], x["payloadBytes"], x["voiceName"]) for x in parts.values()}) != 126):
        raise Rejected("SuccessorCoverage")
    return parts, plans, pilot


def verify_successor_policy(manifest, scope, policy, kind):
    parts, plans, pilot = validate_successor_manifest(manifest, scope)
    original = policy["HistoricalPolicy"] if kind == "resume-admission" else policy
    expiry = datetime.fromisoformat(policy["ExpiresAtUtc"].replace("Z", "+00:00"))
    if (expiry.utcoffset() != timedelta(0) or expiry.timestamp() <= time.time()
            or int(expiry.timestamp() * 1_000_000) > utc(scope["endUtc"])):
        raise Rejected("PolicyWindow")
    if (original["Currency"] != "USD" or type(original["Scale"]) is not int or original["Scale"] != 2
            or original["AccountId"] != manifest["accountId"] or original["ProjectId"] != manifest["projectId"]
            or original["SourceApprovalSha256"] != scope["approvalSha256"]):
        raise Rejected("SuccessorPolicyBinding")
    allowed = set()
    observed = set()
    for article in original["Articles"]:
        proposal = article["Proposal"]
        plan = proposal["PlanSha256"]
        if plan in observed or plans.get(plan) != digest(proposal):
            raise Rejected("ExactProposalBinding")
        observed.add(plan)
        by_ordinal = {x["CoverageOrdinal"]: x for x in proposal["Parts"]}
        if len(by_ordinal) != len(proposal["Parts"]):
            raise Rejected("DuplicateCoverage")
        for permission in article["AllowedSegments"]:
            key = (plan, permission["CoverageOrdinal"])
            actual = by_ordinal.get(key[1]); expected = parts.get(key)
            if (key in allowed or type(permission["MaximumMinorUnits"]) is not int or permission["MaximumMinorUnits"] != 1
                    or actual is None or expected is None or actual["Range"]["ReusedReceiptSha256"] is not None
                    or actual["PayloadSha256"] != expected["payloadSha256"] or actual["PayloadBytes"] != expected["payloadBytes"]
                    or proposal["VoiceName"] != expected["voiceName"] or proposal["LanguageCode"] != expected["languageCode"]):
                raise Rejected("CoveragePayloadBinding")
            allowed.add(key)
    original_kind = successor_policy_kind(manifest, original)
    required = {pilot} if original_kind == "pilot-admission" else set(parts) - {pilot}
    count = 1 if original_kind == "pilot-admission" else 125
    baseline = (153, 23) if count == 1 else (154, 24)
    budget = original["Budget"]
    if (allowed != required or (count == 125 and observed != set(plans))
            or type(original["MaximumNewAttempts"]) is not int or original["MaximumNewAttempts"] != count
            or type(original["MaximumAdditionalReservationMinorUnits"]) is not int or original["MaximumAdditionalReservationMinorUnits"] != count
            or any(type(budget[k]) is not int for k in ("ProjectCommittedMinorUnits", "AllocationCommittedMinorUnits", "ProjectCeilingMinorUnits", "AllocationCeilingMinorUnits"))
            or (budget["ProjectCommittedMinorUnits"], budget["AllocationCommittedMinorUnits"]) != baseline
            or budget["ProjectCeilingMinorUnits"] != 500 or budget["AllocationCeilingMinorUnits"] != 149):
        raise Rejected("SuccessorPilotOrBudget")
    if kind == "resume-admission":
        e = policy["ExecutionEnvelope"]
        matches = [key for key, x in parts.items() if x["payloadSha256"] == e["CompiledPayloadSha256"]
                   and x["payloadBytes"] == e["CompiledPayloadBytes"] and x["voiceName"] == e["VoiceName"]]
        if (len(matches) != 1 or matches[0] not in allowed or e["AudioEncoding"] != "MP3"
                or policy["MaximumExistingDispatches"] != 1 or policy["MaximumNewReservations"] != 0
                or policy["MaximumAdditionalReservationMinorUnits"] != 0):
            raise Rejected("ResumeFinancialBounds")
    elif kind != original_kind:
        raise Rejected("SuccessorAdmissionKind")


ACCEPTED_KEYS = {"planSha256", "coverageOrdinal", "authorizationId", "policySha256",
                 "sourceApprovalSha256", "receiptSha256", "audioSha256", "executionMappingSha256"}
BUDGET_KEYS = {"projectCommittedMinorUnits", "allocationCommittedMinorUnits", "projectCeilingMinorUnits",
               "allocationCeilingMinorUnits", "overallCeilingMinorUnits", "taxFeeUpperBoundMinorUnits",
               "taxFeeEvidenceSha256"}


def validate_continuation_manifest(manifest, scope):
    if scope.get("schema") == 5:
        return validate_sentence_manifest(manifest, scope)
    reject = Rejected
    original = {k: v for k, v in manifest.items() if k != "continuation"}
    original["schema"] = 2
    extension = manifest.get("continuation")
    if (manifest.get("schema") != 3 or type(manifest.get("schema")) is not int
            or not isinstance(extension, dict)
            or set(extension) != {"originalManifestSha256", "accepted", "budget", "financialRowsSha256"}
            or digest(manifest) != scope["plansSha256"]):
        raise reject("ContinuationManifest")
    hash_value(extension["originalManifestSha256"])
    hash_value(extension["financialRowsSha256"])
    if digest(original) != extension["originalManifestSha256"]:
        raise reject("ContinuationOriginalManifest")
    original_scope = dict(scope, plansSha256=digest(original))
    parts, plans, pilot = validate_successor_manifest(original, original_scope)
    accepted = extension["accepted"]
    if not isinstance(accepted, list) or not 1 <= len(accepted) < len(parts):
        raise reject("ContinuationAcceptedBounds")
    keys = set(); identities = set(); receipts = set(); ordered = []
    for entry in accepted:
        if not isinstance(entry, dict) or set(entry) != ACCEPTED_KEYS:
            raise reject("ContinuationAcceptedSchema")
        for name in ACCEPTED_KEYS - {"coverageOrdinal", "authorizationId", "executionMappingSha256"}:
            hash_value(entry[name])
        if entry["executionMappingSha256"] is not None:
            hash_value(entry["executionMappingSha256"])
        authorization = uuid_identity(entry["authorizationId"])
        if entry["authorizationId"] != authorization or type(entry["coverageOrdinal"]) is not int:
            raise reject("ContinuationAcceptedIdentity")
        key = (entry["planSha256"], entry["coverageOrdinal"])
        if key not in parts or key in keys or authorization in identities or entry["receiptSha256"] in receipts:
            raise reject("ContinuationAcceptedDuplicateOrUnknown")
        keys.add(key); identities.add(authorization); receipts.add(entry["receiptSha256"]); ordered.append(key)
    if ordered != sorted(ordered) or pilot not in keys or digest(accepted) != scope["acceptedEvidenceSha256"]:
        raise reject("ContinuationAcceptedPin")
    remaining = set(parts) - keys
    if type(scope["maximumNewAttempts"]) is not int or scope["maximumNewAttempts"] != len(remaining):
        raise reject("ContinuationRemainingCount")
    budget = extension["budget"]
    if not isinstance(budget, dict) or set(budget) != BUDGET_KEYS:
        raise reject("ContinuationBudgetSchema")
    hash_value(budget["taxFeeEvidenceSha256"])
    for name in BUDGET_KEYS - {"taxFeeEvidenceSha256"}:
        if type(budget[name]) is not int or budget[name] < 0:
            raise reject("ContinuationBudgetType")
    # This is a reviewed upper BOUND for all project taxes/fees, not an assumed
    # tax rate or an authenticated invoice. Missing evidence fails closed.
    if (budget["overallCeilingMinorUnits"] != 500
            or budget["projectCeilingMinorUnits"] + budget["taxFeeUpperBoundMinorUnits"] > 500
            or budget["projectCommittedMinorUnits"] + len(remaining) > budget["projectCeilingMinorUnits"]
            or budget["allocationCommittedMinorUnits"] + len(remaining) > budget["allocationCeilingMinorUnits"]
            or budget["allocationCommittedMinorUnits"] > budget["projectCommittedMinorUnits"]
            or budget["allocationCeilingMinorUnits"] > budget["projectCeilingMinorUnits"]):
        raise reject("ContinuationAllInBudget")
    return parts, plans, remaining, accepted, budget


def verify_continuation_policy(manifest, scope, policy, kind):
    parts, plans, remaining, accepted, budget = validate_continuation_manifest(manifest, scope)
    reject = Rejected
    if kind not in ("completion-admission", "resume-admission"):
        raise reject("ContinuationAdmissionKind")
    expiry = datetime.fromisoformat(policy["ExpiresAtUtc"].replace("Z", "+00:00"))
    if (expiry.utcoffset() != timedelta(0) or expiry.timestamp() <= time.time()
            or int(expiry.timestamp() * 1_000_000) > utc(scope["endUtc"])):
        raise reject("PolicyWindow")
    original = policy["HistoricalPolicy"] if kind == "resume-admission" else policy
    if (original["Currency"] != "USD" or type(original["Scale"]) is not int or original["Scale"] != 2
            or original["AccountId"] != manifest["accountId"] or original["ProjectId"] != manifest["projectId"]
            or original["SourceApprovalSha256"] != scope["approvalSha256"]):
        raise reject("ContinuationPolicyBinding")
    allowed = set(); observed = set()
    for article in original["Articles"]:
        proposal = article["Proposal"]; plan = proposal["PlanSha256"]
        if plan in observed or plans.get(plan) != digest(proposal):
            raise reject("ExactProposalBinding")
        observed.add(plan)
        by_ordinal = {part["CoverageOrdinal"]: part for part in proposal["Parts"]}
        if len(by_ordinal) != len(proposal["Parts"]):
            raise reject("DuplicateCoverage")
        for permission in article["AllowedSegments"]:
            key = (plan, permission["CoverageOrdinal"]); part = by_ordinal.get(key[1]); expected = parts.get(key)
            if (key not in remaining or key in allowed or part is None or expected is None
                    or type(permission["MaximumMinorUnits"]) is not int or permission["MaximumMinorUnits"] != 1
                    or part["Range"]["ReusedReceiptSha256"] is not None
                    or part["PayloadSha256"] != expected["payloadSha256"] or part["PayloadBytes"] != expected["payloadBytes"]
                    or proposal["VoiceName"] != expected["voiceName"] or proposal["LanguageCode"] != expected["languageCode"]):
                raise reject("ContinuationAcceptedOrChangedCoverage")
            allowed.add(key)
    fields = {"ProjectCommittedMinorUnits": "projectCommittedMinorUnits", "AllocationCommittedMinorUnits": "allocationCommittedMinorUnits",
              "ProjectCeilingMinorUnits": "projectCeilingMinorUnits", "AllocationCeilingMinorUnits": "allocationCeilingMinorUnits"}
    if (allowed != remaining or observed != set(plans)
            or type(original["MaximumNewAttempts"]) is not int or original["MaximumNewAttempts"] != len(remaining)
            or type(original["MaximumAdditionalReservationMinorUnits"]) is not int
            or original["MaximumAdditionalReservationMinorUnits"] != len(remaining)
            or any(type(original["Budget"][k]) is not int or original["Budget"][k] != budget[v] for k, v in fields.items())):
        raise reject("ContinuationExactBudgetOrCoverage")
    if scope["schema"] == 4:
        grant = original.get("WholeArticleGeneration")
        if (type(original.get("SchemaVersion")) is not int or original["SchemaVersion"] != 3
                or not isinstance(grant, dict) or set(grant) != {"GrantId", "PlanSha256s", "MaximumCatalogRefreshes"}
                or digest(grant) != scope["wholeArticleGrantSha256"]
                or not isinstance(grant["PlanSha256s"], list) or len(grant["PlanSha256s"]) != len(plans)
                or set(grant["PlanSha256s"]) != set(plans)
                or type(grant["MaximumCatalogRefreshes"]) is not int or not 0 <= grant["MaximumCatalogRefreshes"] <= 24):
            raise reject("WholeArticleGrantBinding")
        try:
            if grant["GrantId"] != str(uuid.UUID(grant["GrantId"])) or uuid.UUID(grant["GrantId"]).int == 0:
                raise ValueError("GrantId")
        except (ValueError, TypeError, AttributeError) as error:
            raise reject("WholeArticleGrantBinding") from error
    if kind == "resume-admission":
        envelope = policy["ExecutionEnvelope"]
        matches = [key for key in remaining if parts[key]["payloadSha256"] == envelope["CompiledPayloadSha256"]
                   and parts[key]["payloadBytes"] == envelope["CompiledPayloadBytes"]
                   and parts[key]["voiceName"] == envelope["VoiceName"]]
        if (len(matches) != 1 or envelope["AudioEncoding"] != "MP3"
                or type(policy["MaximumExistingDispatches"]) is not int or policy["MaximumExistingDispatches"] != 1
                or type(policy["MaximumNewReservations"]) is not int or policy["MaximumNewReservations"] != 0
                or type(policy["MaximumAdditionalReservationMinorUnits"]) is not int
                or policy["MaximumAdditionalReservationMinorUnits"] != 0):
            raise reject("ResumeFinancialBounds")


def validate_sentence_manifest(manifest, scope):
    """New exact manifest; never reinterpret or edit a prior admitted manifest."""
    if (scope.get("schema") != 5 or manifest.get("schema") != 4 or type(manifest.get("schema")) is not int
            or set(manifest) != {"schema", "accountId", "projectId", "proposals", "parts", "firstStage", "history", "replan"}
            or digest(manifest) != scope["plansSha256"]):
        raise Rejected("SentenceManifest")
    history, replan = manifest["history"], manifest["replan"]
    if (not isinstance(history, dict) or set(history) != {"proposals", "parts", "accepted", "legacy"}
            or not isinstance(replan, dict) or set(replan) != {"originalManifestSha256", "financialRowsSha256", "budget", "failure"}
            or not 1 <= len(manifest["proposals"]) <= 2 or not 3 <= len(manifest["parts"]) <= 125
            or not 1 <= len(history["proposals"]) <= 3 or not 1 <= len(history["accepted"]) + len(history["legacy"]) <= 200):
        raise Rejected("SentenceManifestBounds")
    for name in ("originalManifestSha256", "financialRowsSha256"):
        hash_value(replan[name])
    new_plans = {}; plans = {}; parts = {}
    for proposal in manifest["proposals"]:
        if set(proposal) != {"planSha256", "proposalSha256", "reusedAudioSha256"}:
            raise Rejected("SentenceProposal")
        for value in proposal.values(): hash_value(value)
        if proposal["planSha256"] in plans: raise Rejected("DuplicatePlan")
        plans[proposal["planSha256"]] = proposal["proposalSha256"]; new_plans.update(plans)
    for proposal in history["proposals"]:
        if set(proposal) != {"planSha256", "proposalSha256"}: raise Rejected("SentenceHistory")
        for value in proposal.values(): hash_value(value)
        if proposal["planSha256"] in plans: raise Rejected("DuplicatePlan")
        plans[proposal["planSha256"]] = proposal["proposalSha256"]
    for part in manifest["parts"] + history["parts"]:
        if (set(part) != {"planSha256", "coverageOrdinal", "kind", "payloadSha256", "payloadBytes", "voiceName", "languageCode"}
                or type(part["coverageOrdinal"]) is not int or part["coverageOrdinal"] < 1 or part["kind"] != "new"
                or type(part["payloadBytes"]) is not int or not 1 <= part["payloadBytes"] <= 5000
                or part["planSha256"] not in plans or not isinstance(part["languageCode"], str)
                or part["voiceName"] != part["languageCode"] + "-Chirp3-HD-Alnilam"):
            raise Rejected("SentenceCoverage")
        hash_value(part["payloadSha256"])
        key = (part["planSha256"], part["coverageOrdinal"])
        if key in parts: raise Rejected("DuplicateCoverage")
        parts[key] = part
    remaining = {(x["planSha256"], x["coverageOrdinal"]) for x in manifest["parts"]}
    if len(remaining) != scope["maximumNewAttempts"] or any(key[0] not in new_plans for key in remaining):
        raise Rejected("SentenceRemainingCount")
    first = manifest["firstStage"]
    if (not isinstance(first, list) or len(first) != 2 or any(set(x) != {"planSha256", "coverageOrdinal"} for x in first)
            or first[0]["planSha256"] != first[1]["planSha256"]
            or type(first[0]["coverageOrdinal"]) is not int or type(first[1]["coverageOrdinal"]) is not int
            or first[1]["coverageOrdinal"] != first[0]["coverageOrdinal"] + 1
            or any((x["planSha256"], x["coverageOrdinal"]) not in remaining for x in first)):
        raise Rejected("SentenceFirstStage")
    accepted = history["accepted"]; ids = set(); receipts = set(); accepted_keys = set()
    for entry in accepted:
        if set(entry) != ACCEPTED_KEYS: raise Rejected("ContinuationAcceptedSchema")
        for name in ACCEPTED_KEYS - {"coverageOrdinal", "authorizationId", "executionMappingSha256"}: hash_value(entry[name])
        if entry["executionMappingSha256"] is not None: hash_value(entry["executionMappingSha256"])
        auth = uuid_identity(entry["authorizationId"]); key = (entry["planSha256"], entry["coverageOrdinal"])
        if (entry["authorizationId"] != auth or key not in parts or key in remaining or key in accepted_keys
                or auth in ids or entry["receiptSha256"] in receipts):
            raise Rejected("ContinuationAcceptedDuplicateOrUnknown")
        accepted_keys.add(key); ids.add(auth); receipts.add(entry["receiptSha256"])
    if set(parts) - remaining != accepted_keys: raise Rejected("SentenceHistory")
    for entry in history["legacy"]:
        if set(entry) != {"localAttemptId", "receiptSha256", "audioSha256"}: raise Rejected("SentenceLegacy")
        if uuid_identity(entry["localAttemptId"]) != entry["localAttemptId"] or entry["localAttemptId"] in ids:
            raise Rejected("SentenceLegacy")
        hash_value(entry["receiptSha256"]); hash_value(entry["audioSha256"])
        if entry["receiptSha256"] in receipts: raise Rejected("SentenceLegacy")
        ids.add(entry["localAttemptId"]); receipts.add(entry["receiptSha256"])
    if digest({"native": accepted, "legacy": history["legacy"]}) != scope["acceptedEvidenceSha256"]:
        raise Rejected("ContinuationAcceptedPin")
    budget = replan["budget"]
    if not isinstance(budget, dict) or set(budget) != BUDGET_KEYS: raise Rejected("ContinuationBudgetSchema")
    hash_value(budget["taxFeeEvidenceSha256"])
    if any(type(budget[k]) is not int or budget[k] < 0 for k in BUDGET_KEYS - {"taxFeeEvidenceSha256"}):
        raise Rejected("ContinuationBudgetType")
    if (budget["overallCeilingMinorUnits"] != 500 or budget["projectCeilingMinorUnits"] + budget["taxFeeUpperBoundMinorUnits"] > 500
            or budget["projectCeilingMinorUnits"] != budget["projectCommittedMinorUnits"] + len(remaining)
            or budget["allocationCeilingMinorUnits"] != budget["allocationCommittedMinorUnits"] + len(remaining)
            or budget["allocationCommittedMinorUnits"] > budget["projectCommittedMinorUnits"]):
        raise Rejected("ContinuationAllInBudget")
    failure = replan["failure"]
    if set(failure) != {"FailedAuthorizationId", "FailedLocalAttemptId", "FailedPlanSha256", "FailedCoverageOrdinal", "FailedPayloadSha256", "FailedPayloadBytes"}:
        raise Rejected("SentenceFailure")
    return parts, plans, remaining, accepted, budget


def verify_sentence_policy(manifest, scope, policy, kind, *, historical_readback=False):
    parts, plans, remaining, _, budget = validate_sentence_manifest(manifest, scope)
    original = policy["HistoricalPolicy"] if kind == "resume-admission" else policy
    phase = successor_policy_kind(manifest, original)
    first = {(x["planSha256"], x["coverageOrdinal"]) for x in manifest["firstStage"]}
    required = first if phase == "pilot-admission" else remaining - first
    increment = 0 if phase == "pilot-admission" else 2
    if (kind != "resume-admission" and kind != phase) or kind not in ("pilot-admission", "completion-admission", "resume-admission"):
        raise Rejected("SentenceAdmissionKind")
    expiry = datetime.fromisoformat(policy["ExpiresAtUtc"].replace("Z", "+00:00"))
    if (expiry.utcoffset() != timedelta(0) or (not historical_readback and expiry.timestamp() <= time.time())
            or int(expiry.timestamp() * 1_000_000) > utc(scope["endUtc"])):
        raise Rejected("PolicyWindow")
    if (original["AccountId"] != manifest["accountId"] or original["ProjectId"] != manifest["projectId"]
            or original["Currency"] != "USD" or type(original["Scale"]) is not int or original["Scale"] != 2
            or original["SourceApprovalSha256"] != scope["approvalSha256"] or original.get("SuccessorGrant") is not None):
        raise Rejected("SentencePolicyBinding")
    allowed = set(); observed = set(); retained = []; specs = {x["planSha256"]: x for x in manifest["proposals"]}
    for article in original["Articles"]:
        proposal = article["Proposal"]; plan = proposal["PlanSha256"]
        if (plan in observed or plan not in specs or digest(proposal) != specs[plan]["proposalSha256"]
                or digest(article["ReusedAudio"]) != specs[plan]["reusedAudioSha256"]
                or proposal["PlannerVersion"] != "chirp3-existing-sentence-groups-v1"):
            raise Rejected("ExactProposalBinding")
        observed.add(plan); by_ordinal = {x["CoverageOrdinal"]: x for x in proposal["Parts"]}
        if len(by_ordinal) != len(proposal["Parts"]): raise Rejected("DuplicateCoverage")
        for audio in article["ReusedAudio"]:
            matching = [x for x in proposal["Parts"] if x["Range"] == audio["Range"] and x["Range"]["ReusedReceiptSha256"] == audio["ReceiptSha256"]
                        and x["PayloadSha256"] is None and x["PayloadBytes"] == 0]
            if len(matching) != 1: raise Rejected("SentenceReuse")
            retained.append(audio["ReceiptSha256"])
        if plan == manifest["firstStage"][0]["planSha256"]:
            successor = proposal.get("SentenceSuccessor"); failure = manifest["replan"]["failure"]
            if not isinstance(successor, dict) or set(successor) != {"Parent"} or successor["Parent"]["PlanSha256"] != failure["FailedPlanSha256"]:
                raise Rejected("SentenceFailure")
            parents = [x for x in successor["Parent"]["Parts"] if x["CoverageOrdinal"] == failure["FailedCoverageOrdinal"]]
            a, b = (by_ordinal[x["coverageOrdinal"]]["Range"] for x in manifest["firstStage"])
            if (len(parents) != 1 or a["StartScalar"] != parents[0]["Range"]["StartScalar"]
                    or a["EndScalarExclusive"] != b["StartScalar"] or b["EndScalarExclusive"] < parents[0]["Range"]["EndScalarExclusive"]):
                raise Rejected("SentenceFirstStage")
        for permission in article["AllowedSegments"]:
            key = (plan, permission["CoverageOrdinal"]); expected = parts.get(key); actual = by_ordinal.get(key[1])
            if (key not in required or key in allowed or expected is None or actual is None or type(permission["MaximumMinorUnits"]) is not int
                    or permission["MaximumMinorUnits"] != 1 or actual["Range"]["ReusedReceiptSha256"] is not None
                    or actual["PayloadSha256"] != expected["payloadSha256"] or actual["PayloadBytes"] != expected["payloadBytes"]
                    or proposal["VoiceName"] != expected["voiceName"] or proposal["LanguageCode"] != expected["languageCode"]):
                raise Rejected("SentenceCoverage")
            allowed.add(key)
    expected_plans = {key[0] for key in required}
    expected_receipts = {x["receiptSha256"] for x in manifest["history"]["accepted"] + manifest["history"]["legacy"]}
    if len(retained) != len(set(retained)) or set(retained) != expected_receipts:
        raise Rejected("SentenceReuse")
    fields = {"ProjectCommittedMinorUnits": budget["projectCommittedMinorUnits"] + increment,
              "AllocationCommittedMinorUnits": budget["allocationCommittedMinorUnits"] + increment,
              "ProjectCeilingMinorUnits": budget["projectCeilingMinorUnits"], "AllocationCeilingMinorUnits": budget["allocationCeilingMinorUnits"]}
    if (allowed != required or observed != expected_plans or type(original["MaximumNewAttempts"]) is not int
            or original["MaximumNewAttempts"] != len(required) or type(original["MaximumAdditionalReservationMinorUnits"]) is not int
            or original["MaximumAdditionalReservationMinorUnits"] != len(required)
            or any(type(original["Budget"][k]) is not int or original["Budget"][k] != value for k, value in fields.items())):
        raise Rejected("ContinuationExactBudgetOrCoverage")
    if phase == "pilot-admission":
        if original.get("SchemaVersion") != 2 or original.get("WholeArticleGeneration") is not None:
            raise Rejected("SentenceFirstStage")
    else:
        grant = original.get("WholeArticleGeneration")
        if (original.get("SchemaVersion") != 3 or not isinstance(grant, dict) or digest(grant) != scope["wholeArticleGrantSha256"]
                or set(grant) != {"GrantId", "PlanSha256s", "MaximumCatalogRefreshes"} or set(grant["PlanSha256s"]) != expected_plans
                or len(grant["PlanSha256s"]) != len(expected_plans) or type(grant["MaximumCatalogRefreshes"]) is not int
                or not 0 <= grant["MaximumCatalogRefreshes"] <= 24 or uuid_identity(grant["GrantId"]) != grant["GrantId"].replace("-", "")):
            raise Rejected("WholeArticleGrantBinding")
    if kind == "resume-admission":
        e = policy["ExecutionEnvelope"]
        matches = [key for key in required if parts[key]["payloadSha256"] == e["CompiledPayloadSha256"]
                   and parts[key]["payloadBytes"] == e["CompiledPayloadBytes"] and parts[key]["voiceName"] == e["VoiceName"]]
        if (len(matches) != 1 or e["AudioEncoding"] != "MP3"
                or any(type(policy[k]) is not int for k in ("MaximumExistingDispatches", "MaximumNewReservations", "MaximumAdditionalReservationMinorUnits"))
                or policy["MaximumExistingDispatches"] != 1
                or policy["MaximumNewReservations"] != 0 or policy["MaximumAdditionalReservationMinorUnits"] != 0):
            raise Rejected("ResumeFinancialBounds")


DIAGNOSTIC_KEYS = {"proposalSha256", "successorGrantSha256", "privateCaptureConsentSha256", "failedAuthorizationId",
                   "failedLocalAttemptId", "planSha256", "coverageOrdinal", "payloadSha256", "payloadBytes",
                   "financialRowsSha256", "projectCommittedMinorUnits", "allocationCommittedMinorUnits"}


def validate_diagnostic_manifest(manifest, scope):
    """The original sentence manifest is immutable data, not renewed batch authority."""
    validate_scope(scope)
    if scope["schema"] != 6 or not isinstance(manifest, dict) or "diagnostic" not in manifest or digest(manifest) != scope["plansSha256"]:
        raise Rejected("DiagnosticManifest")
    d = manifest["diagnostic"]
    if not isinstance(d, dict) or set(d) != DIAGNOSTIC_KEYS:
        raise Rejected("DiagnosticManifest")
    for key in DIAGNOSTIC_KEYS:
        if key.endswith("Sha256"):hash_value(d[key])
    uuid_identity(d["failedAuthorizationId"])
    uuid_identity(d["failedLocalAttemptId"])
    if (d["privateCaptureConsentSha256"] != scope["privateCaptureConsentSha256"]
            or type(d["coverageOrdinal"]) is not int or d["coverageOrdinal"] < 1
            or type(d["payloadBytes"]) is not int or not 1 <= d["payloadBytes"] <= 5000
            or any(type(d[k]) is not int or d[k] < 0 for k in ("projectCommittedMinorUnits", "allocationCommittedMinorUnits"))
            or d["allocationCommittedMinorUnits"] > d["projectCommittedMinorUnits"]):
        raise Rejected("DiagnosticManifest")
    original = {k:v for k,v in manifest.items() if k != "diagnostic"}
    old_scope = dict(scope, schema=5, maximumNewAttempts=len(original.get("parts", [])), plansSha256=digest(original), wholeArticleGrantSha256=digest({"unused": "validation only"}))
    for key in ("diagnosticPurpose", "privateCaptureConsentSha256", "diagnosticWindowMode"):old_scope.pop(key)
    parts, plans, remaining, accepted, budget = validate_sentence_manifest(original, old_scope)
    part = parts.get((d["planSha256"], d["coverageOrdinal"]))
    if ((d["planSha256"], d["coverageOrdinal"]) not in remaining or part is None
            or part["payloadSha256"] != d["payloadSha256"] or part["payloadBytes"] != d["payloadBytes"]
            or d["projectCommittedMinorUnits"] < budget["projectCommittedMinorUnits"]
            or d["allocationCommittedMinorUnits"] < budget["allocationCommittedMinorUnits"]
            or d["projectCommittedMinorUnits"] + 1 + budget["taxFeeUpperBoundMinorUnits"] > budget["overallCeilingMinorUnits"]):
        raise Rejected("DiagnosticCoverageOrBudget")
    return parts, plans, accepted, budget


def verify_diagnostic_policy(manifest, scope, policy, kind):
    parts, plans, _, budget = validate_diagnostic_manifest(manifest, scope)
    d = manifest["diagnostic"]
    if (kind != "diagnostic-admission" or type(policy.get("SchemaVersion")) is not int or policy["SchemaVersion"] != 4
            or policy.get("WholeArticleGeneration") is not None or policy.get("SourceApprovalSha256") != scope["approvalSha256"]
            or policy.get("AccountId") != manifest["accountId"] or policy.get("ProjectId") != manifest["projectId"]
            or policy.get("Currency") != "USD" or type(policy.get("Scale")) is not int or policy["Scale"] != 2
            or any(type(policy.get(k)) is not int or policy[k] != 1 for k in ("MaximumNewAttempts", "MaximumAdditionalReservationMinorUnits"))
            or not isinstance(policy.get("Articles"), list) or len(policy["Articles"]) != 1):
        raise Rejected("DiagnosticPolicy")
    if (hashlib.sha256((policy["AppSha256"]+"|"+policy["InfrastructureSha256"]).encode()).hexdigest()!=scope["buildSha256"]
            or policy["ProfileRootSha256"]!=scope["profileSha256"]):raise Rejected("DiagnosticPolicy")
    expiry = datetime.fromisoformat(policy["ExpiresAtUtc"].replace("Z", "+00:00"))
    if expiry.utcoffset() != timedelta(0) or expiry.timestamp() <= time.time() or int(expiry.timestamp()*1e6) > utc(scope["endUtc"]):
        raise Rejected("PolicyWindow")
    article = policy["Articles"][0]; proposal = article["Proposal"]
    spec = next((x for x in manifest["proposals"] if x["planSha256"] == d["planSha256"]), None)
    part = parts[(d["planSha256"], d["coverageOrdinal"])]
    chosen = [x for x in proposal["Parts"] if x["CoverageOrdinal"] == d["coverageOrdinal"]]
    if (spec is None or digest(proposal) != spec["proposalSha256"] or digest(article["ReusedAudio"]) != spec["reusedAudioSha256"]
            or proposal.get("SentenceSuccessor") is None or proposal.get("Refinement") is not None
            or len(chosen) != 1 or chosen[0]["Range"]["ReusedReceiptSha256"] is not None
            or chosen[0]["PayloadSha256"] != d["payloadSha256"] or chosen[0]["PayloadBytes"] != d["payloadBytes"]
            or proposal["VoiceName"] != part["voiceName"] or proposal["LanguageCode"] != part["languageCode"]
            or article["AllowedSegments"] != [{"CoverageOrdinal": d["coverageOrdinal"], "MaximumMinorUnits": 1}]):
        raise Rejected("DiagnosticPolicy")
    expected = {"Version":1, "Purpose":"unchanged-provider-evidence", "ProposalSha256":d["proposalSha256"],
                "SuccessorGrantSha256":d["successorGrantSha256"], "PrivateCaptureConsentSha256":d["privateCaptureConsentSha256"], "RequiredCaptureMode":1}
    grant = policy.get("SuccessorGrant")
    if (policy.get("DiagnosticEvidence") != expected or not isinstance(grant, dict)
            or any(type(policy["DiagnosticEvidence"][k]) is not int for k in ("Version", "RequiredCaptureMode"))
            or type(grant.get("CoverageOrdinal")) is not int
            or type(article["AllowedSegments"][0]["CoverageOrdinal"]) is not int
            or uuid_identity(grant["ParentAuthorizationId"]) != uuid_identity(d["failedAuthorizationId"])
            or uuid_identity(grant["ParentLocalAttemptId"]) != uuid_identity(d["failedLocalAttemptId"])
            or grant["PlanSha256"] != d["planSha256"] or grant["CoverageOrdinal"] != d["coverageOrdinal"]
            or any(type(grant.get(k)) is not int or grant[k] != 1 for k in ("MaximumNewAttempts", "MaximumAdditionalReservationMinorUnits"))):
        raise Rejected("DiagnosticPolicy")
    b = policy["Budget"]
    for key,value in {"ProjectCommittedMinorUnits":d["projectCommittedMinorUnits"], "AllocationCommittedMinorUnits":d["allocationCommittedMinorUnits"],
                      "ProjectCeilingMinorUnits":d["projectCommittedMinorUnits"]+1, "AllocationCeilingMinorUnits":d["allocationCommittedMinorUnits"]+1}.items():
        if type(b.get(key)) is not int or b[key] != value:raise Rejected("DiagnosticBudget")


class Register:
    """Clock injection is for offline tests, never exposed by operational CLI."""

    def __init__(self, path, scope_sha, clock=None, monotonic=None, expected_build=None, expected_gate=None):
        self.path = physical(path)
        self.scope_sha = hash_value(scope_sha)
        self.clock = clock or (lambda: time.time_ns() // 1000)
        self.monotonic = monotonic or clock or (lambda: time.monotonic_ns() // 1000)
        self.expected_build = expected_build
        self.expected_gate = expected_gate

    def _stamp(self, event, minimum_mono=0):
        return dict(event, mono=max(self.monotonic(), minimum_mono))

    @property
    def anchor(self):
        return physical(str(self.path) + ".anchor")

    @contextmanager
    def _lease(self):
        lease_path = physical(str(self.path) + ".lease")
        with open(lease_path, "a+b") as lease:
            lease.seek(0)
            if os.name == "nt":
                import msvcrt
                msvcrt.locking(lease.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(lease.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
            try:
                yield
            finally:
                lease.seek(0)
                if os.name == "nt":
                    msvcrt.locking(lease.fileno(), msvcrt.LK_UNLCK, 1)
                else:
                    fcntl.flock(lease.fileno(), fcntl.LOCK_UN)

    def _seal(self, db):
        rows = db.execute("SELECT seq,sha FROM events ORDER BY seq DESC LIMIT 1").fetchone()
        record = f"{rows[0]}:{rows[1]}\n".encode("ascii")
        with open(self.anchor, "ab", buffering=0) as anchor:
            anchor.write(record)
            os.fsync(anchor.fileno())

    def _verify_anchor(self, rows):
        if not self.anchor.is_file() or self.anchor.stat().st_size > 400000:
            raise Rejected("AnchorUnavailable")
        records = self.anchor.read_bytes().splitlines()
        if not records or len(records) > 4096:
            raise Rejected("AnchorInvalid")
        for expected, line in enumerate(records, 1):
            if not re.fullmatch(rb"[1-9][0-9]*:[0-9a-f]{64}", line) or line.split(b":")[0] != str(expected).encode():
                raise Rejected("AnchorInvalid")
        if len(records) != len(rows) or any(line != f"{row[0]}:{row[2]}".encode() for line, row in zip(records, rows)):
            raise Rejected("AnchorDatabaseMismatch")

    @classmethod
    def create(cls, path, scope, reviewed_sha, clock=None, monotonic=None):
        validate_scope(scope)
        if digest(scope) != hash_value(reviewed_sha):
            raise Rejected("ReviewPinMismatch")
        register = cls(path, reviewed_sha, clock, monotonic)
        if scope["registerPathSha256"] != path_digest(register.path):
            raise Rejected("RegisterPathPinMismatch")
        now = register.clock()
        if not utc(scope["startUtc"]) <= now < utc(scope["endUtc"]):
            raise Rejected("OutsideWindow")
        # Exclusive creation: an existing/corrupt journal is NEVER replaced.
        with register._lease():
            register._create_locked(scope, reviewed_sha, now)
        return register

    def _create_locked(self, scope, reviewed_sha, now):
        # Sentinel survives partial initialization; NEVER delete/retry/reset it.
        fd = os.open(self.anchor, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        os.close(fd)
        fd = os.open(self.path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        os.close(fd)
        self._initialize(scope, reviewed_sha, now)

    def _initialize(self, scope, reviewed_sha, now):
        with closing(sqlite3.connect(self.path)) as db:
            db.execute("PRAGMA journal_mode=DELETE")
            db.execute("PRAGMA synchronous=FULL")
            db.executescript("""
                CREATE TABLE scope (id INTEGER PRIMARY KEY CHECK(id=1), body TEXT NOT NULL);
                CREATE TABLE events (seq INTEGER PRIMARY KEY, body TEXT NOT NULL, sha TEXT NOT NULL);
                CREATE TRIGGER immutable_scope_update BEFORE UPDATE ON scope BEGIN SELECT RAISE(ABORT,'immutable'); END;
                CREATE TRIGGER immutable_scope_delete BEFORE DELETE ON scope BEGIN SELECT RAISE(ABORT,'immutable'); END;
                CREATE TRIGGER immutable_events_update BEFORE UPDATE ON events BEGIN SELECT RAISE(ABORT,'immutable'); END;
                CREATE TRIGGER immutable_events_delete BEFORE DELETE ON events BEGIN SELECT RAISE(ABORT,'immutable'); END;
            """)
            db.execute("INSERT INTO scope VALUES(1,?)", (canonical(scope),))
            self._append(db, self._stamp({"type": "start", "utc": self.clock(), "scope": reviewed_sha}), reviewed_sha)
            db.commit()
            self._seal(db)

    @contextmanager
    def _open(self):
        with self._lease():
            with self._transaction() as db:
                yield db

    @contextmanager
    def _transaction(self):
        physical(self.path)
        if not self.path.is_file() or self.path.stat().st_size > 2_000_000:
            raise Rejected("RegisterUnavailableOrOversized")
        db = sqlite3.connect(self.path.as_uri() + "?mode=rw", uri=True, timeout=0)
        try:
            db.execute("PRAGMA synchronous=FULL")
            db.execute("BEGIN IMMEDIATE")
            yield db
            db.commit()
        except BaseException:
            db.rollback()
            raise
        finally:
            db.close()

    @staticmethod
    def _append(db, event, previous):
        body = canonical(event)
        sha = digest({"previous": previous, "event": event})
        db.execute("INSERT INTO events(body,sha) VALUES(?,?)", (body, sha))
        return sha

    def _read(self, db):
        row = db.execute("SELECT body FROM scope WHERE id=1").fetchone()
        if not row:
            raise Rejected("IncompleteRegister")
        scope = json.loads(row[0])
        validate_scope(scope)
        if digest(scope) != self.scope_sha:
            raise Rejected("ScopePinMismatch")
        if scope["registerPathSha256"] != path_digest(self.path):
            raise Rejected("RegisterPathPinMismatch")
        previous = self.scope_sha
        state = {"refresh": 0, "resume-admission": 0, "completion-admission": 0, "pilot-admission": 0, "diagnostic-admission": 0,
                 "admissions": 0, "pilotPolicySha256": None, "diagnosticPolicySha256": None, "diagnosticDispatch": None,
                 "pending": None, "stopped": False, "stopReason": None,
                 "lastUtc": 0, "lastMono": 0, "startUtc": 0, "startMono": 0, "lastGoodUtc":0,"clockEpoch":None,
                 "ids": set(), "scope": scope, "head": previous, "buildTransitions": 0, "helperSourceRepairs": 0,
                 "currentBuild": scope["buildSha256"], "currentGate": None, "retirement": None,
                 "retired": False, "activation": None, "activated": scope["schema"] == 1,
                 "interpreterRepair": None, "interpreterActivation": None, "interpreterWithdrawal": None}
        rows = db.execute("SELECT seq,body,sha FROM events ORDER BY seq").fetchall()
        if not rows or len(rows) > 4096:
            raise Rejected("InvalidEventCount")
        self._verify_anchor(rows)
        for expected, (seq, body, sha) in enumerate(rows, 1):
            event = json.loads(body)
            if not isinstance(event, dict):
                raise Rejected("EventSchema")
            shapes = {"start": {"type", "utc", "scope", "mono"},
                      "claim": {"type", "utc", "id", "kind", "identity", "mono"},
                      "complete": {"type", "utc", "id", "outcome", "receiptSha256", "mono", "reason"},
                      "stop": {"type", "utc", "reason", "mono"}, "check": {"type", "utc", "mono"},
                      "build-claim": {"type", "utc", "mono", "id", "identity", "transition"},
                      "build-complete": {"type", "utc", "mono", "id", "identity", "receiptSha256"}}
            shapes["source-claim"] = shapes["build-claim"]
            shapes["source-complete"] = shapes["build-complete"]
            shapes["diagnostic-dispatch"] = {"type", "utc", "mono", "id", "policySha256", "payloadSha256", "authorizationId", "localAttemptId", "ordinal"}
            shapes["interpreter-withdraw-claim"] = {"type", "utc", "mono", "id", "identity", "withdrawal"}
            shapes["interpreter-withdraw-complete"] = shapes["build-complete"]
            shapes["retire-claim"] = {"type", "utc", "mono", "id", "identity", "handoff", "clockCondition", "clockDriftMicros"}
            if (event.get("type") == "retire-claim" and "observedMono" in event
                    and scope["schema"] in (2, 3, 4, 5) and isinstance(event.get("handoff"), dict)
                    and (event["handoff"].get("SchemaVersion") in (2, 3) or clock_sentence_handoff(event["handoff"]) or expired_diagnostic_handoff(event["handoff"]))):
                shapes["retire-claim"] |= {"observedMono"}
            shapes["retire-complete"] = {"type", "utc", "mono", "id", "identity", "receiptSha256"}
            shapes["activate"] = {"type", "utc", "mono", "id", "identity", "previousRetirementHead", "receiptSha256", "gateSha256"}
            if event.get("type") == "activate" and "interpreterHandoff" in event and scope["schema"] == 4:
                shapes["activate"] |= {"interpreterHandoff"}
            if event.get("type") == "activate" and "sentenceHandoff" in event and scope["schema"] in (5, 6):
                shapes["activate"] |= {"sentenceHandoff"}
            if event.get("type") == "claim" and scope["schema"] in (2, 5) and event.get("kind") == "completion-admission":
                shapes["claim"] = shapes["claim"] | {"pilotProofSha256"}
            if event.get("type") == "claim" and ((scope["schema"] in (3, 4) and event.get("kind") == "completion-admission")
                    or (scope["schema"] == 5 and event.get("kind") in ("pilot-admission", "completion-admission"))):
                shapes["claim"] = shapes["claim"] | {"acceptedProofSha256"}
            if event.get("type") not in shapes or set(event) != shapes[event["type"]]:
                raise Rejected("EventSchema")
            if event["type"] in ("claim", "complete", "build-claim", "build-complete", "source-claim", "source-complete", "retire-claim", "retire-complete", "activate", "interpreter-withdraw-claim", "interpreter-withdraw-complete"):
                try:
                    if str(uuid.UUID(event["id"])) != event["id"]:
                        raise Rejected("EventId")
                except (ValueError, TypeError, AttributeError) as error:
                    raise Rejected("EventId") from error
                hash_value(event["identity"] if event["type"] in ("claim", "build-claim", "source-claim", "retire-claim", "interpreter-withdraw-claim") else event["receiptSha256"])
            if event["type"] == "complete" and event["outcome"] not in ("Succeeded", "Stopped"):
                raise Rejected("EventOutcome")
            if event["type"] == "complete" and event["reason"] not in ("None", "ObservedFailure", "ReceiptValidation", "ClockRollback", "ClockDiscontinuity", "WindowExpired"):
                raise Rejected("EventOutcomeReason")
            if event["type"] == "stop" and event["reason"] not in ("ClockRollback", "ClockDiscontinuity", "WindowExpired"):
                raise Rejected("EventStopReason")
            if seq != expected or body != canonical(event) or digest({"previous": previous, "event": event}) != sha:
                raise Rejected("JournalIntegrity")
            if type(event.get("utc")) is not int or event["utc"] < state["lastUtc"]:
                raise Rejected("JournalClockRollback")
            if type(event["mono"]) is not int or event["mono"] < state["lastMono"]:
                raise Rejected("JournalClockRollback")
            if expected == 1:
                if event != {"type": "start", "utc": event["utc"], "scope": self.scope_sha, "mono": event["mono"]}:
                    raise Rejected("InvalidStart")
                state["startUtc"] = event["utc"]
                state["startMono"] = event["mono"]
            elif event["type"] == "claim":
                kind = event["kind"]
                kinds = ("refresh", "resume-admission", "completion-admission", "pilot-admission") if scope["schema"] in (2, 5) else ("refresh", "resume-admission", "completion-admission")
                if scope["schema"] == 6:kinds = ("refresh", "diagnostic-admission")
                if (state["stopped"] or not state["activated"] or state["retirement"] or state["pending"] or kind not in kinds
                        or (scope["schema"] == 6 and kind == "refresh" and state["diagnosticDispatch"] is not None)
                        or event["id"] in state["ids"] or state[kind] >= (1 if kind in ("completion-admission", "pilot-admission", "diagnostic-admission") else 24)
                        or (scope["schema"] in (2, 3, 4, 5, 6) and kind != "refresh" and state["admissions"] >= 24)):
                    raise Rejected("InvalidClaimHistory")
                if scope["schema"] in (2, 5) and kind == "completion-admission":
                    hash_value(event["pilotProofSha256"])
                    if state["pilotPolicySha256"] is None:
                        raise Rejected("PilotNotAccepted")
                if (scope["schema"] in (3, 4) and kind == "completion-admission") or (scope["schema"] == 5 and kind in ("pilot-admission", "completion-admission")):
                    if hash_value(event["acceptedProofSha256"]) != scope["acceptedEvidenceSha256"]:
                        raise Rejected("ContinuationAcceptedProof")
                state[kind] += 1
                if kind != "refresh":
                    state["admissions"] += 1
                state["ids"].add(event["id"])
                state["pending"] = event
            elif event["type"] == "complete":
                if not state["pending"] or state["pending"]["type"] != "claim" or event["id"] != state["pending"]["id"]:
                    raise Rejected("InvalidCompletionHistory")
                if state["pending"]["kind"] == "pilot-admission" and event["outcome"] == "Succeeded":
                    state["pilotPolicySha256"] = state["pending"]["identity"]
                if state["pending"]["kind"] == "diagnostic-admission" and event["outcome"] == "Succeeded":
                    state["diagnosticPolicySha256"] = state["pending"]["identity"]
                state["pending"] = None
                if event["outcome"] != "Succeeded":
                    state["stopped"] = True
                    state["stopReason"] = event["reason"]
            elif event["type"] == "diagnostic-dispatch":
                if (scope["schema"] != 6 or state["stopped"] or not state["activated"] or state["pending"] or state["retirement"]
                        or state["diagnosticPolicySha256"] != event["policySha256"]):
                    raise Rejected("DiagnosticDispatch")
                for name in ("policySha256", "payloadSha256"):hash_value(event[name])
                for name in ("id", "authorizationId", "localAttemptId"):uuid_identity(event[name])
                prior = state["diagnosticDispatch"]
                if type(event["ordinal"]) is not int or (not prior and (event["ordinal"] != 1 or event["id"] in state["ids"])) or (prior and
                        (prior["ordinal"] != 1 or event["ordinal"] != 2 or any(event[k] != prior[k] for k in
                         ("id", "policySha256", "payloadSha256", "authorizationId", "localAttemptId")))):
                    raise Rejected("DiagnosticDispatchReplay")
                state["ids"].add(event["id"])
                state["diagnosticDispatch"] = event
            elif event["type"] in ("build-claim", "source-claim"):
                transition = event["transition"]
                validate_transition(transition)
                source = source_transition(transition)
                interpreter = transition_kind(transition) == "interpreter-binding"
                if ((not interpreter_pre_effect(state) if interpreter else scope["schema"] != 1)
                        or (interpreter and state["interpreterActivation"] is not None)
                        or source != (event["type"] == "source-claim") or state["stopped"] or state["pending"]
                        or (not interpreter and state["buildTransitions"] != (1 if source else 0)) or (source and state["helperSourceRepairs"] != 0)
                        or event["id"] in state["ids"]
                        or state["resume-admission"] != 0 or state["completion-admission"] != 0
                        or transition["TransitionId"] != event["id"] or transition["ExpectedHeadSha256"] != previous
                        or transition["ScopeSha256"] != self.scope_sha or transition["SourceApprovalSha256"] != scope["approvalSha256"]
                        or transition["PreviousBuildSha256"] != state["currentBuild"]
                        or (state["currentGate"] is not None and transition["PreviousGateSha256"] != state["currentGate"])):
                    raise Rejected("InvalidBuildClaimHistory")
                state["helperSourceRepairs" if source else "buildTransitions"] = 1
                state["ids"].add(event["id"])
                state["pending"] = dict(event, kind=transition_kind(transition) if source else "build-transition")
                state["currentGate"] = transition["PreviousGateSha256"]
                if interpreter:
                    state["interpreterRepair"] = event
            elif event["type"] in ("build-complete", "source-complete"):
                pending = state["pending"]
                if (state["stopped"] or not pending or pending["type"] not in ("build-claim", "source-claim")
                        or event["id"] != pending["id"] or event["identity"] != pending["identity"]):
                    raise Rejected("InvalidBuildCompletionHistory")
                if event["type"] != ("source-complete" if pending["type"] == "source-claim" else "build-complete"):
                    raise Rejected("InvalidBuildCompletionHistory")
                hash_value(event["identity"])
                state["currentBuild"] = pending["transition"]["NewBuildSha256"]
                state["currentGate"] = pending["transition"]["NewGateSha256"]
                state["pending"] = None
            elif event["type"] == "interpreter-withdraw-claim":
                record = event["withdrawal"]
                interpreter_withdrawal_matches(state, record)
                if (state["interpreterWithdrawal"] or event["id"] != record["WithdrawalId"]
                        or event["id"] in state["ids"] or record["ExpectedHeadSha256"] != previous
                        or (state["pending"] and state["pending"]["id"] != interpreter_binding_event(state)["id"])
                        or (state["stopped"] and state["stopReason"] != "WindowExpired")):
                    raise Rejected("InterpreterWithdrawalState")
                state["interpreterWithdrawal"] = event
                state["pending"] = dict(event, kind="interpreter-withdrawal")
                state["ids"].add(event["id"])
                state["stopped"] = True
                state["stopReason"] = "InterpreterBindingWithdrawn"
            elif event["type"] == "interpreter-withdraw-complete":
                pending = state["pending"]
                if (not pending or pending["type"] != "interpreter-withdraw-claim"
                        or event["id"] != pending["id"] or event["identity"] != pending["identity"]
                        or not state["stopped"] or state["stopReason"] != "InterpreterBindingWithdrawn"):
                    raise Rejected("InterpreterWithdrawalState")
                state["currentGate"] = pending["withdrawal"]["RestoredGateSha256"]
                state["pending"] = None
            elif event["type"] == "stop":
                state["stopped"] = True
                state["stopReason"] = event["reason"]
            elif event["type"] == "retire-claim":
                h = event["handoff"]
                validate_handoff(h)
                observed_mono = event.get("observedMono", event["mono"])
                condition, drift = self._retirement_observation(state, event["utc"], observed_mono, h)
                allowed_interpreter = scope["schema"] == 4 and interpreter_handoff(h)
                allowed_sentence = scope["schema"] == 4 and sentence_handoff(h)
                allowed_diagnostic = scope["schema"] == 5 and diagnostic_handoff(h)
                if allowed_diagnostic and ((state["stopped"] and not (expired_diagnostic_handoff(h) and state["stopReason"] in ("ClockRollback","ClockDiscontinuity","WindowExpired"))) or not state["activated"] or h["InheritedClockEpochEvidence"] != state["clockEpoch"] or (h["UsedRefreshes"], h["UsedAdmissions"], h["UsedResumeAdmissions"]) != (state["refresh"], state["admissions"], state["resume-admission"])):raise Rejected("DiagnosticCounterOrEpoch")
                reboot = clock_sentence_handoff(h) or expired_diagnostic_handoff(h)
                clock_stop = (reboot and state["stopReason"] in ("ClockRollback","ClockDiscontinuity")) or (expired_sentence_handoff(h) and state["stopReason"] == "WindowExpired")
                if allowed_sentence and (not state["activated"] or (state["stopped"] and not clock_stop)
                        or (h["UsedRefreshes"], h["UsedAdmissions"], h["UsedResumeAdmissions"]) != (state["refresh"], state["admissions"], state["resume-admission"])):
                    raise Rejected("SentenceCounterBinding")
                if allowed_interpreter and (not interpreter_pre_effect(state) or state["helperSourceRepairs"] != 0
                        or state["interpreterActivation"] is not None
                        or h["RepairApprovalSha256"] == scope["approvalSha256"]):
                    raise Rejected("InvalidRetirementHistory")
                if (((scope["schema"], h["SchemaVersion"]) not in ((1, 1), (2, 2), (3, 3)) and not allowed_interpreter and not allowed_sentence and not allowed_diagnostic) or state["pending"] or state["retirement"] or event["id"] in state["ids"]
                        or h["TransitionId"] != event["id"] or h["ExpectedPreviousHeadSha256"] != previous
                        or h["PreviousScopeSha256"] != self.scope_sha
                        or h["PreviousGateSha256"] != state["currentGate"]
                        or type(event["clockDriftMicros"]) is not int or event["clockDriftMicros"] != drift
                        or event["clockCondition"] != condition
                        or ("observedMono" in event and event["mono"] != max(state["lastMono"], observed_mono))
                        or (state["stopped"] and state["stopReason"] != "WindowExpired" and not clock_stop)
                        or (h["SchemaVersion"] in (2, 3, 4, 5, 6) and h["OriginalManifestSha256"] != scope["plansSha256"])
                        or (h["SchemaVersion"] == 3 and event["utc"] < utc(scope["endUtc"]))):
                    raise Rejected("InvalidRetirementHistory")
                state["retirement"] = event
                state["pending"] = dict(event, kind="retirement")
                state["ids"].add(event["id"])
            elif event["type"] == "retire-complete":
                if (not state["retirement"] or state["retired"] or state["pending"] is None
                        or event["id"] != state["retirement"]["id"] or event["identity"] != state["retirement"]["identity"]):
                    raise Rejected("InvalidRetirementCompletion")
                state["retired"] = True
                state["pending"] = None
            elif event["type"] == "activate":
                if (scope["schema"] not in (2, 3, 4, 5, 6) or state["activated"] or state["pending"] or state["stopped"]
                        or event["id"] != scope["transitionId"]):
                    raise Rejected("InvalidSuccessorActivation")
                hash_value(event["identity"]); hash_value(event["previousRetirementHead"])
                hash_value(event["gateSha256"])
                state["activation"] = event
                if scope["schema"] in (5, 6):
                    h = event.get("sentenceHandoff"); validate_handoff(h)
                    if ((not diagnostic_handoff(h) if scope["schema"] == 6 else not sentence_handoff(h)) or h["NewScopeSha256"] != self.scope_sha or h["NewGateSha256"] != event["gateSha256"]
                            or h["TransitionId"] != event["id"] or h["SourceApprovalSha256"] != scope["approvalSha256"]):
                        raise Rejected("InvalidSuccessorActivation")
                    if scope["schema"] == 6 and (scope["diagnosticWindowMode"] != ("fresh-10-minute" if expired_diagnostic_handoff(h) else "inherited")
                            or (expired_diagnostic_handoff(h) and utc(scope["startUtc"]) != h["ClockEpochEvidence"]["ObservedUtcMicros"])):
                        raise Rejected("DiagnosticWindow")
                    state["refresh"], state["admissions"], state["resume-admission"] = h["UsedRefreshes"], h["UsedAdmissions"], h["UsedResumeAdmissions"]
                    if clock_sentence_handoff(h):state["clockEpoch"]=h["ClockEpochEvidence"]
                    if scope["schema"] == 6:state["clockEpoch"]=h.get("ClockEpochEvidence",h["InheritedClockEpochEvidence"])
                if "interpreterHandoff" in event:
                    h = event["interpreterHandoff"]
                    validate_handoff(h)
                    if (not interpreter_handoff(h) or h["NewScopeSha256"] != self.scope_sha or h["NewGateSha256"] != event["gateSha256"]
                            or h["TransitionId"] != event["id"] or h["SourceApprovalSha256"] != scope["approvalSha256"]):
                        raise Rejected("InvalidSuccessorActivation")
                    state["interpreterActivation"] = event
                state["activated"] = True
                state["currentGate"] = event["gateSha256"]
            elif event["type"] == "check":
                if state["stopped"] or state["retirement"]:
                    raise Rejected("CheckAfterStop")
            else:
                raise Rejected("UnknownEvent")
            old_clock_retirement = event["type"] in ("retire-claim","retire-complete") and state["retirement"] and (clock_sentence_handoff(state["retirement"]["handoff"]) or expired_diagnostic_handoff(state["retirement"]["handoff"]))
            if not (event["type"]=="stop" and event["reason"] in ("ClockRollback","ClockDiscontinuity")) and not old_clock_retirement and not (state["stopped"] and state["stopReason"] in ("ClockRollback","ClockDiscontinuity")):
                state["lastGoodUtc"]=event["utc"]
            state["lastUtc"] = event["utc"]
            state["lastMono"] = event["mono"]
            previous = sha
        state["head"] = previous
        if self.expected_build is not None and (state["currentBuild"] != self.expected_build
                or (state["currentGate"] is not None and state["currentGate"] != self.expected_gate)):
            raise Rejected("CurrentBuildBinding")
        return state

    def _time_rejection(self, state):
        now = self.clock()
        mono = self.monotonic()
        if now < state["lastUtc"] or mono < state["lastMono"]:
            return "ClockRollback"
        if state.get("clockEpoch"):
            e=state["clockEpoch"];delta=mono-e["ObservedMonoMicros"]
            if delta<0 or abs(now-e["ObservedUtcMicros"]-delta)>250_000:return "ClockRollback"
            if e["TrustedUtcUpperMicros"]+delta>=utc(state["scope"]["endUtc"]):return "WindowExpired"
        # An unobserved backward wall-clock adjustment cannot extend six hours.
        if now - state["startUtc"] + 250000 < mono - state["startMono"]:
            return "ClockRollback"
        if now - state["startUtc"] > mono - state["startMono"] + 250000:
            return "ClockDiscontinuity"
        if (not utc(state["scope"]["startUtc"]) <= now < utc(state["scope"]["endUtc"])
                or mono - state["startMono"] >= utc(state["scope"]["endUtc"]) - state["startUtc"]):
            return "WindowExpired"
        return None

    @staticmethod
    def _retirement_observation(state, now, mono, handoff=None):
        # Retirement creates no old-run permission. A separately approved new
        # scope supplies its own dual deadline; never repair old clock anchors.
        if type(now) is not int or type(mono) is not int or mono < 0:
            raise Rejected("ClockRollback")
        drift = now - state["startUtc"] - (mono - state["startMono"])
        if now < state["lastUtc"]:
            raise Rejected("ClockRollback")
        retirement = state["retirement"]
        typed = handoff or (retirement["handoff"] if retirement else None)
        if diagnostic_handoff(typed):
            e = typed["InheritedClockEpochEvidence"]
            if e != state["clockEpoch"]:raise Rejected("DiagnosticCounterOrEpoch")
            if e and not expired_diagnostic_handoff(typed):
                delta = mono - e["ObservedMonoMicros"]
                if delta < 0 or abs(now-e["ObservedUtcMicros"]-delta) > 250_000:raise Rejected("ClockRollback")
                if e["TrustedUtcUpperMicros"]+delta >= utc(state["scope"]["endUtc"]):raise Rejected("WindowExpired")
        if clock_sentence_handoff(typed) or expired_diagnostic_handoff(typed):
            e=typed["ClockEpochEvidence"];validate_clock_epoch(e,require_reboot=not (expired_sentence_handoff(typed) or expired_diagnostic_handoff(typed)))
            if e["PreviousLastGoodUtcMicros"]!=state["lastGoodUtc"] or now<e["ObservedUtcMicros"] or mono<e["ObservedMonoMicros"]:
                raise Rejected("ClockEpochEvidence")
            delta=mono-e["ObservedMonoMicros"]
            if abs(now-e["ObservedUtcMicros"]-delta)>250_000:raise Rejected("ClockRollback")
            if expired_sentence_handoff(typed) or expired_diagnostic_handoff(typed):
                if e["TrustedUtcLowerMicros"]+delta<utc(state["scope"]["endUtc"]):raise Rejected("ExpiredPredecessorRequired")
                # Immutable evidence pins the ONLY proposed new six-hour window;
                # retirement/reconciliation cannot outlive its conservative cutoff.
                if e["TrustedUtcUpperMicros"]+delta>=e["ObservedUtcMicros"]+(600 if expired_diagnostic_handoff(typed) else 6*3600)*1_000_000:raise Rejected("WindowExpired")
            elif e["TrustedUtcUpperMicros"]+delta>=utc(state["scope"]["endUtc"]):raise Rejected("WindowExpired")
            if not retirement and now-e["ObservedUtcMicros"]>600*1_000_000:raise Rejected("ClockEpochEvidence")
            return ("ApprovedExpiredDiagnosticWindow" if expired_diagnostic_handoff(typed) else "ApprovedExpiredTerminalWindow" if expired_sentence_handoff(typed) else "ApprovedRebootEpoch"),drift
        if retirement and mono < retirement.get("observedMono", retirement["mono"]):
            raise Rejected("ClockRollback")
        if mono < state["lastMono"]:
            # This retires expired authority, never resumes it or proves a reboot.
            # The exact typed handoff and fresh approval are checked separately.
            pending = state["pending"]
            typed = handoff or (retirement["handoff"] if retirement else None)
            if ((state["scope"]["schema"] in (2, 3) or (state["scope"]["schema"] == 4 and interpreter_handoff(typed))) and now >= utc(state["scope"]["endUtc"])
                    and (not state["stopped"] or state["stopReason"] == "WindowExpired")
                    and (not pending or (pending["type"] == "retire-claim"
                                         and pending["handoff"]["SchemaVersion"] in (2, 3)))):
                return "ExpiredMonotonicDiscontinuity", drift
            raise Rejected("ClockRollback")
        if drift < -250000:
            raise Rejected("ClockRollback")
        typed = handoff or (retirement["handoff"] if retirement else None)
        if sentence_handoff(typed) or diagnostic_handoff(typed):
            if now >= utc(state["scope"]["endUtc"]) or mono - state["startMono"] >= utc(state["scope"]["endUtc"]) - state["startUtc"]:
                raise Rejected("WindowExpired")
            if drift > 250000:
                raise Rejected("ClockDiscontinuity")
        return ("ForwardDiscontinuity" if drift > 250000 else "Continuous", drift)

    def _guard(self, db, state, allow_inactive=False):
        if state["retirement"]:
            raise Rejected("RunRetired")
        if not state["activated"] and not allow_inactive:
            raise Rejected("SuccessorNotActivated")
        if state["stopped"]:
            raise Rejected("RunStopped")
        now = self.clock()
        reason = self._time_rejection(state)
        if reason:
            # Commit the stop BEFORE raising; clock recovery cannot renew the window.
            self._append(db, self._stamp({"type": "stop", "utc": max(now, state["lastUtc"]), "reason": reason}, state["lastMono"]), state["head"])
            db.commit()
            self._seal(db)
            raise Rejected(reason)
        # Persist the UTC/monotonic high-water marks on EVERY pre-effect check.
        event = self._stamp({"type": "check", "utc": now})
        state["head"] = self._append(db, event, state["head"])
        state["lastUtc"] = now
        state["lastMono"] = event["mono"]
        db.commit()
        self._seal(db)
        return now

    def claim(self, kind, identity_sha, operation_id, pilot_proof_sha=None, accepted_proof_sha=None):
        if kind not in ("refresh", "resume-admission", "completion-admission", "pilot-admission", "diagnostic-admission"):
            raise Rejected("UnsupportedOperation")
        hash_value(identity_sha)
        if str(uuid.UUID(operation_id)) != operation_id:
            raise Rejected("InvalidOperationId")
        with self._open() as db:
            state = self._read(db)
            now = self._guard(db, state)
            if state["pending"]:
                raise Rejected("PendingOutcomeRequiresReconciliation")
            if operation_id in state["ids"]:
                raise Rejected("Replay")
            if (state["scope"]["schema"] == 6 and kind not in ("refresh", "diagnostic-admission")) or (kind == "diagnostic-admission" and state["scope"]["schema"] != 6):raise Rejected("DiagnosticOperation")
            if state["scope"]["schema"] == 6 and kind == "refresh" and state["diagnosticDispatch"] is not None:raise Rejected("DiagnosticOperation")
            if kind == "pilot-admission" and state["scope"]["schema"] not in (2, 5):
                raise Rejected("UnsupportedOperation")
            if (state[kind] >= (1 if kind in ("completion-admission", "pilot-admission", "diagnostic-admission") else 24)
                    or (state["scope"]["schema"] in (2, 3, 4, 5, 6) and kind != "refresh" and state["admissions"] >= 24)):
                raise Rejected("OperationLimit")
            event = {"type": "claim", "utc": now, "id": operation_id, "kind": kind, "identity": identity_sha}
            if state["scope"]["schema"] in (2, 5) and kind == "completion-admission":
                hash_value(pilot_proof_sha)
                if state["pilotPolicySha256"] is None:
                    raise Rejected("PilotNotAccepted")
                event["pilotProofSha256"] = pilot_proof_sha
            if (state["scope"]["schema"] in (3, 4) and kind == "completion-admission") or (state["scope"]["schema"] == 5 and kind in ("pilot-admission", "completion-admission")):
                if hash_value(accepted_proof_sha) != state["scope"]["acceptedEvidenceSha256"]:
                    raise Rejected("ContinuationAcceptedProof")
                event["acceptedProofSha256"] = accepted_proof_sha
            self._append(db, self._stamp(event), state["head"])
            db.commit()  # A durable consumed slot exists BEFORE any external action.
            self._seal(db)  # Failure here returns NO permit and future mismatch stops.
            return {"id": operation_id, "kind": kind, "ordinal": state[kind] + 1,
                    "scopeSha256": self.scope_sha, "identitySha256": identity_sha,
                    "deadlineUtc": effective_deadline(state["scope"],state.get("clockEpoch")), "authority": False}

    def complete(self, operation_id, receipt):
        # Receipts are observed evidence from separately reviewed adapters, never authority.
        allowed = {"id", "identitySha256", "outcome", "evidenceSha256", "catalogVerified", "probeInputEmpty",
                   "probeHttpStatus", "probeStatus", "audioAbsent", "policyVerified", "zeroNewFunds",
                   "maximumNewAttempts", "additionalReservationCents"}
        if set(receipt) - allowed or receipt.get("id") != operation_id:
            raise Rejected("ReceiptSchema")
        for key in ("identitySha256", "evidenceSha256"):
            hash_value(receipt.get(key))
        with self._open() as db:
            state = self._read(db)
            pending = state["pending"]
            if not pending or pending["type"] != "claim" or pending["id"] != operation_id or pending["identity"] != receipt["identitySha256"]:
                raise Rejected("ReceiptMismatchOrReplay")
            now = max(self.clock(), state["lastUtc"])
            good = receipt.get("outcome") == "Succeeded"
            if pending["kind"] == "refresh":
                good = good and all(receipt.get(k) is True for k in ("catalogVerified", "probeInputEmpty", "audioAbsent"))
                good = good and receipt.get("probeHttpStatus") == 400 and receipt.get("probeStatus") == "INVALID_ARGUMENT"
            elif pending["kind"] == "resume-admission":
                good = good and receipt.get("policyVerified") is True and receipt.get("zeroNewFunds") is True
            else:
                good = good and receipt.get("policyVerified") is True
                maximum = (1 if pending["kind"] == "pilot-admission" else 125) if state["scope"]["schema"] == 2 else 108
                if state["scope"]["schema"] in (3, 4):
                    maximum = state["scope"]["maximumNewAttempts"]
                if state["scope"]["schema"] == 5:
                    maximum = 2 if pending["kind"] == "pilot-admission" else state["scope"]["maximumNewAttempts"] - 2
                if state["scope"]["schema"] == 6:
                    maximum = 1
                    good = good and receipt.get("maximumNewAttempts") == 1 and receipt.get("additionalReservationCents") == 1
                good = good and type(receipt.get("maximumNewAttempts")) is int and 0 <= receipt["maximumNewAttempts"] <= maximum
                good = good and type(receipt.get("additionalReservationCents")) is int and 0 <= receipt["additionalReservationCents"] <= maximum
            # Expiry/rollback cannot be hidden by completing a successful older claim.
            time_reason = self._time_rejection(state)
            reason = time_reason or ("ObservedFailure" if receipt.get("outcome") != "Succeeded" else "ReceiptValidation")
            good = good and time_reason is None
            outcome = "Succeeded" if good else "Stopped"
            self._append(db, self._stamp({"type": "complete", "utc": now, "id": operation_id, "outcome": outcome,
                              "receiptSha256": digest(receipt), "reason": "None" if good else reason}, state["lastMono"]), state["head"])
            db.commit()
            self._seal(db)
            return outcome

    def require_pending_before_effect(self, operation_id, identity_sha):
        """Owning adapters MUST call again at actual I/O after any approval/wait."""
        hash_value(identity_sha)
        with self._open() as db:
            state = self._read(db)
            self._guard(db, state)
            pending = state["pending"]
            if not pending or pending["type"] != "claim" or pending["id"] != operation_id or pending["identity"] != identity_sha:
                raise Rejected("PendingEffectIdentityMismatch")
            return True

    def diagnostic_dispatch(self, operation_id, policy_sha, payload_sha, authorization, local_attempt):
        """Two irreversible pre-effect observations; caller fields require actual bridge readback."""
        for pin in (policy_sha, payload_sha):hash_value(pin)
        for value in (operation_id, authorization, local_attempt):uuid_identity(value)
        with self._open() as db:
            state = self._read(db);now = self._guard(db, state)
            if (state["scope"]["schema"] != 6 or state["pending"] or state["diagnosticPolicySha256"] != policy_sha):
                raise Rejected("DiagnosticDispatch")
            prior = state["diagnosticDispatch"]
            fields = dict(id=operation_id, policySha256=policy_sha, payloadSha256=payload_sha, authorizationId=authorization, localAttemptId=local_attempt)
            if prior is None and operation_id in state["ids"]:raise Rejected("DiagnosticDispatchReplay")
            if prior and (prior["ordinal"] != 1 or any(prior[k] != v for k,v in fields.items())):
                raise Rejected("DiagnosticDispatchReplay")
            event = dict(type="diagnostic-dispatch", utc=now, ordinal=2 if prior else 1, **fields)
            self._append(db, self._stamp(event), state["head"]);db.commit();self._seal(db)
            return {"valid":True, "id":operation_id, "payloadSha256":payload_sha, "authority":False}

    def _build_ready(self, state, transition, identity_sha, operation_id, pending=False, check_clock=True):
        validate_transition(transition)
        hash_value(identity_sha)
        interpreter = transition_kind(transition) == "interpreter-binding"
        source = source_transition(transition)
        if ((not interpreter_pre_effect(state) if interpreter else state["scope"]["schema"] != 1) or state["retirement"]
                or (interpreter and state["interpreterActivation"] is not None)):
            raise Rejected("BuildTransitionNotReady")
        if (operation_id != transition["TransitionId"] or transition["ScopeSha256"] != self.scope_sha
                or transition["SourceApprovalSha256"] != state["scope"]["approvalSha256"]):
            raise Rejected("TransitionBinding")
        if state["resume-admission"] != 0 or state["completion-admission"] != 0:
            raise Rejected("TransitionFinancialAdmissionExists")
        if state["stopped"] or (check_clock and self._time_rejection(state)):
            raise Rejected("BuildTransitionNotReady")
        if pending:
            item = state["pending"]
            claim_type = "source-claim" if source else "build-claim"
            if (not item or item["type"] != claim_type or item["id"] != operation_id
                    or item["identity"] != identity_sha or item["transition"] != transition):
                raise Rejected("BuildTransitionPendingMismatch")
        elif (state["pending"] or (not interpreter and state["buildTransitions"] != (1 if source else 0))
              or (source and state["helperSourceRepairs"] != 0)
              or operation_id in state["ids"]
              or state["head"] != transition["ExpectedHeadSha256"] or state["currentBuild"] != transition["PreviousBuildSha256"]
              or (state["currentGate"] is not None and state["currentGate"] != transition["PreviousGateSha256"])):
            raise Rejected("BuildTransitionHeadOrState")

    def inspect_build(self, transition, identity_sha, operation_id):
        with self._open() as db:
            state = self._read(db)
            self._build_ready(state, transition, identity_sha, operation_id)
            return {"valid": True, "headSha256": state["head"], "deadlineUtc": effective_deadline(state["scope"],state.get("clockEpoch")), "authority": False}

    def claim_build(self, transition, identity_sha, operation_id):
        with self._open() as db:
            state = self._read(db)
            # Expected head is checked under the exclusive lease before any append.
            self._build_ready(state, transition, identity_sha, operation_id, check_clock=False)
            if self._time_rejection(state):
                self._guard(db, state)  # Persist terminal stop without altering a valid expected head.
            event_type = "source-claim" if source_transition(transition) else "build-claim"
            self._append(db, self._stamp({"type": event_type, "utc": self.clock(), "id": operation_id,
                                         "identity": identity_sha, "transition": transition}), state["head"])
            db.commit()
            self._seal(db)
            return {"id": operation_id, "identitySha256": identity_sha, "scopeSha256": self.scope_sha,
                    "deadlineUtc": effective_deadline(state["scope"],state.get("clockEpoch")), "authority": False}

    def check_build(self, transition, identity_sha, operation_id):
        with self._open() as db:
            state = self._read(db)
            self._build_ready(state, transition, identity_sha, operation_id, pending=True, check_clock=False)
            self._guard(db, state)
            return {"valid": True}

    def complete_build(self, transition, identity_sha, operation_id, observed_sha):
        # Only the independently reviewed CLI adapter supplies actual readback.
        hash_value(observed_sha)
        with self._open() as db:
            state = self._read(db)
            self._build_ready(state, transition, identity_sha, operation_id, pending=True, check_clock=False)
            self._guard(db, state)
            event_type = "source-complete" if source_transition(transition) else "build-complete"
            self._append(db, self._stamp({"type": event_type, "utc": self.clock(), "id": operation_id,
                                         "identity": identity_sha, "receiptSha256": observed_sha}), state["head"])
            db.commit()
            self._seal(db)
            return {"outcome": "Succeeded", "authority": False}

    def _interpreter_withdrawal_ready(self, state, record, identity_sha, operation_id, pending=False):
        interpreter_withdrawal_matches(state, record)
        hash_value(identity_sha)
        if operation_id != record["WithdrawalId"]:
            raise Rejected("InterpreterWithdrawalIdentity")
        if self._time_rejection(state) not in (None, "WindowExpired"):
            raise Rejected("InterpreterWithdrawalClock")
        if pending:
            item = state["pending"]
            if (not item or item["type"] != "interpreter-withdraw-claim" or item["id"] != operation_id
                    or item["identity"] != identity_sha or item["withdrawal"] != record
                    or not state["stopped"] or state["stopReason"] != "InterpreterBindingWithdrawn"):
                raise Rejected("InterpreterWithdrawalState")
        elif (state["interpreterWithdrawal"] or operation_id in state["ids"] or record["ExpectedHeadSha256"] != state["head"]
              or (state["pending"] and state["pending"]["id"] != interpreter_binding_event(state)["id"])
              or (state["stopped"] and state["stopReason"] != "WindowExpired")):
            raise Rejected("InterpreterWithdrawalState")

    def inspect_interpreter_withdrawal(self, record, identity_sha, operation_id):
        with self._open() as db:
            self._interpreter_withdrawal_ready(self._read(db), record, identity_sha, operation_id)
            return {"valid": True, "authority": False}

    def claim_interpreter_withdrawal(self, record, identity_sha, operation_id):
        """Safety-only terminal withdrawal; never resumes old or new permission."""
        with self._open() as db:
            state = self._read(db)
            self._interpreter_withdrawal_ready(state, record, identity_sha, operation_id)
            self._append(db, self._stamp({"type": "interpreter-withdraw-claim", "utc": self.clock(), "id": operation_id,
                                         "identity": identity_sha, "withdrawal": record}), state["head"])
            db.commit(); self._seal(db)
            return {"id": operation_id, "identitySha256": identity_sha, "scopeSha256": self.scope_sha, "authority": False}

    def check_interpreter_withdrawal(self, record, identity_sha, operation_id):
        with self._open() as db:
            self._interpreter_withdrawal_ready(self._read(db), record, identity_sha, operation_id, pending=True)
            return {"valid": True, "authority": False}

    def complete_interpreter_withdrawal(self, record, identity_sha, operation_id, observed_sha):
        hash_value(observed_sha)
        with self._open() as db:
            state = self._read(db)
            self._interpreter_withdrawal_ready(state, record, identity_sha, operation_id, pending=True)
            self._append(db, self._stamp({"type": "interpreter-withdraw-complete", "utc": self.clock(), "id": operation_id,
                                         "identity": identity_sha, "receiptSha256": observed_sha}), state["head"])
            db.commit(); self._seal(db)
            return {"outcome": "Succeeded", "authority": False, "runStopped": True}

    @contextmanager
    def pair(self, successor):
        old_paths = {os.path.normcase(str(x)) for x in (self.path, self.anchor, physical(str(self.path) + ".lease"))}
        new_paths = {os.path.normcase(str(x)) for x in (successor.path, successor.anchor, physical(str(successor.path) + ".lease"))}
        if old_paths & new_paths:
            raise Rejected("OverlappingRunPaths")
        with ExitStack() as stack:
            for register in sorted((self, successor), key=lambda x: os.path.normcase(str(x.path))):
                stack.enter_context(register._lease())
            yield

    def _handoff_ready(self, old_state, successor, new_scope, h, identity, pending=False, check_clock=True):
        validate_handoff(h); hash_value(identity); validate_scope(new_scope)
        typed_interpreter = interpreter_handoff(h)
        typed_pair = typed_interpreter and (old_state["scope"]["schema"], new_scope["schema"], h["SchemaVersion"]) == (4, 4, 3)
        sentence_pair = sentence_handoff(h) and (old_state["scope"]["schema"], new_scope["schema"], h["SchemaVersion"]) == (4, 5, 4)
        diagnostic_pair = diagnostic_handoff(h) and (old_state["scope"]["schema"], new_scope["schema"], h["SchemaVersion"]) in ((5, 6, 5),(5, 6, 6))
        if (((old_state["scope"]["schema"], new_scope["schema"], h["SchemaVersion"]) not in ((1, 2, 1), (2, 3, 2), (3, 4, 3)) and not typed_pair and not sentence_pair and not diagnostic_pair)
                or (typed_interpreter and not typed_pair)
                or (sentence_handoff(h) and not sentence_pair)
                or h["PreviousScopeSha256"] != self.scope_sha or h["NewScopeSha256"] != successor.scope_sha
                or digest(new_scope) != successor.scope_sha or new_scope["predecessorScopeSha256"] != self.scope_sha
                or physical(new_scope["predecessorRegisterPath"]) != self.path
                or new_scope["registerPathSha256"] != path_digest(successor.path)
                or h["TransitionId"] != new_scope["transitionId"] or h["SourceApprovalSha256"] != new_scope["approvalSha256"]
                or new_scope["approvalSha256"] == old_state["scope"]["approvalSha256"]
                or any(new_scope[k] != old_state["scope"][k] for k in ("profileSha256", "accountSha256", "projectSha256"))):
            raise Rejected("SuccessorScopeBinding")
        if diagnostic_handoff(h):
            if not diagnostic_pair:raise Rejected("DiagnosticCounterOrEpoch")
            diagnostic_window_binding(old_state,new_scope,h)
            if not expired_diagnostic_handoff(h) and self._time_rejection(old_state):raise Rejected(self._time_rejection(old_state))
        if sentence_pair:
            clock_stop = (clock_sentence_handoff(h) and old_state["stopReason"] in ("ClockRollback","ClockDiscontinuity")) or (expired_sentence_handoff(h) and old_state["stopReason"] == "WindowExpired")
            changed_window=any(new_scope[k] != old_state["scope"][k] for k in ("startUtc", "endUtc"))
            if expired_sentence_handoff(h):
                e=h["ClockEpochEvidence"]
                if (not changed_window or utc(new_scope["startUtc"]) != e["ObservedUtcMicros"]
                        or e["TrustedUtcLowerMicros"] < utc(old_state["scope"]["endUtc"])
                        or utc(new_scope["startUtc"]) < utc(old_state["scope"]["endUtc"])):
                    raise Rejected("ExpiredWindowBinding")
            if ((changed_window and not expired_sentence_handoff(h))
                    or not old_state["activated"] or (old_state["stopped"] and not clock_stop)
                    or (h["UsedRefreshes"], h["UsedAdmissions"], h["UsedResumeAdmissions"]) != (old_state["refresh"], old_state["admissions"], old_state["resume-admission"])):
                raise Rejected("SentenceCounterOrWindowBinding")
        typed_zero_effect = (old_state["scope"]["schema"] == 4 and old_state["activated"]
                             and old_state["refresh"] == 0 and old_state["admissions"] == 0
                             and old_state["buildTransitions"] == 0 and old_state["helperSourceRepairs"] == 0
                             and old_state["interpreterActivation"] is None)
        if typed_interpreter and (not typed_zero_effect or (not pending and not interpreter_pre_effect(old_state))
                or new_scope["wholeArticleGrantSha256"] == old_state["scope"]["wholeArticleGrantSha256"]
                or h["RepairApprovalSha256"] == old_state["scope"]["approvalSha256"]):
            raise Rejected("SuccessorScopeBinding")
        if h["SchemaVersion"] == 3 and self.clock() < utc(old_state["scope"]["endUtc"]):
            raise Rejected("WholeArticlePredecessorNotExpired")
        if h["SchemaVersion"] in (2, 3, 4, 5, 6):
            if (h["OriginalManifestSha256"] != old_state["scope"]["plansSha256"]
                    or h["AcceptedEvidenceSha256"] != new_scope["acceptedEvidenceSha256"]):
                raise Rejected("ContinuationAcceptedProof")
            # Current pair leases already own this old2 register. Its root1
            # ancestor is terminal/immutable; read it without re-entering leases.
            self._require_ancestor_snapshot(old_state, h["PreviousGateSha256"])
        # A distinct approved window may retire forward-discontinuous old time;
        # normal old permissions, backward UTC and committed failure stops stay blocked.
        if old_state["stopped"] and old_state["stopReason"] != "WindowExpired" and not ((clock_sentence_handoff(h) or expired_diagnostic_handoff(h)) and old_state["stopReason"] in ("ClockRollback","ClockDiscontinuity")):
            raise Rejected("RetirementClockOrStop")
        if check_clock:
            self._retirement_observation(old_state, self.clock(), self.monotonic(), h)
        if pending:
            item = old_state["retirement"]
            if not item or item["identity"] != identity or item["handoff"] != h:
                raise Rejected("RetirementIdentityMismatch")
        else:
            if old_state["retirement"] or old_state["pending"] or h["TransitionId"] in old_state["ids"]:
                raise Rejected("SuccessorPending")
            if old_state["head"] != h["ExpectedPreviousHeadSha256"]:
                raise Rejected("SuccessorHeadMismatch")
            if old_state["currentGate"] != h["PreviousGateSha256"]:
                raise Rejected("SuccessorScopeBinding")
        now = successor.clock()
        if check_clock and not utc(new_scope["startUtc"]) <= now < utc(new_scope["endUtc"]):
            raise Rejected("OutsideWindow")

    def _retirement_clock(self, db, state, handoff=None):
        try:
            return self._retirement_observation(state, self.clock(), self.monotonic(), handoff)
        except Rejected as error:
            typed = handoff or (state["retirement"]["handoff"] if state["retirement"] else None)
            reason = str(error) if sentence_handoff(typed) and str(error) in ("WindowExpired", "ClockDiscontinuity") else "ClockRollback"
            self._append(db, self._stamp({"type": "stop", "utc": max(self.clock(), state["lastUtc"]), "reason": reason}, state["lastMono"]), state["head"])
            db.commit(); self._seal(db)
            raise Rejected(reason)

    def inspect_successor(self, successor, new_scope, h, identity):
        with self.pair(successor), self._transaction() as db:
            state = self._read(db)
            self._handoff_ready(state, successor, new_scope, h, identity)
            if successor.path.exists() or successor.anchor.exists():
                raise Rejected("SuccessorAlreadyExists")
            deadline=effective_deadline(new_scope,h.get("ClockEpochEvidence", h.get("InheritedClockEpochEvidence")))
            return {"valid": True, "deadlineUtc": deadline, "authority": False}

    def claim_successor(self, successor, new_scope, h, identity):
        with self.pair(successor), self._transaction() as db:
            state = self._read(db)
            self._handoff_ready(state, successor, new_scope, h, identity, check_clock=False)
            self._retirement_clock(db, state, h)
            if not utc(new_scope["startUtc"]) <= successor.clock() < utc(new_scope["endUtc"]):
                raise Rejected("OutsideWindow")
            if successor.path.exists() or successor.anchor.exists():
                raise Rejected("SuccessorAlreadyExists")
            event = {"type": "retire-claim", "utc": self.clock(), "mono": self.monotonic(),
                     "id": h["TransitionId"], "identity": identity, "handoff": h}
            condition, drift = self._retirement_observation(state, event["utc"], event["mono"], h)
            if h["SchemaVersion"] in (2, 3) or clock_sentence_handoff(h) or expired_diagnostic_handoff(h):
                event["observedMono"] = event["mono"]
                event["mono"] = max(state["lastMono"], event["mono"])
            event.update(clockCondition=condition, clockDriftMicros=drift)
            self._append(db, event, state["head"])
            db.commit(); self._seal(db)  # Old authority stops durably before any new state/install effect.
            successor._create_locked(new_scope, successor.scope_sha, successor.clock())
            return {"id": h["TransitionId"], "identitySha256": identity, "scopeSha256": successor.scope_sha,
                    "deadlineUtc": effective_deadline(new_scope,h.get("ClockEpochEvidence", h.get("InheritedClockEpochEvidence"))), "authority": False}

    def check_successor(self, successor, new_scope, h, identity):
        with self.pair(successor), self._transaction() as db, successor._transaction() as new_db:
            state = self._read(db); new_state = successor._read(new_db)
            self._handoff_ready(state, successor, new_scope, h, identity, pending=True, check_clock=False)
            self._retirement_clock(db, state)
            if state["retired"] or new_state["activated"] or new_state["pending"]:
                raise Rejected("SuccessorAlreadyActivated")
            successor._guard(new_db, new_state, allow_inactive=True)
            return {"valid": True}

    def complete_successor(self, successor, new_scope, h, identity, observed_sha):
        hash_value(observed_sha)
        with self.pair(successor), self._transaction() as db, successor._transaction() as new_db:
            state = self._read(db); new_state = successor._read(new_db)
            self._handoff_ready(state, successor, new_scope, h, identity, pending=True, check_clock=False)
            self._retirement_clock(db, state)
            if new_state["activated"] or new_state["pending"]:
                raise Rejected("SuccessorActivationReplay")
            successor._guard(new_db, new_state, allow_inactive=True)
            if not state["retired"]:
                self._append(db, self._stamp({"type": "retire-complete", "utc": max(self.clock(), state["lastUtc"]),
                                             "id": h["TransitionId"], "identity": identity, "receiptSha256": observed_sha}, state["lastMono"]), state["head"])
                db.commit(); self._seal(db)
            # If a crash followed old completion, only exact readback of this SAME handoff can finish.
            previous_head = self._read(db)["head"]
            activation = {"type": "activate", "utc": successor.clock(), "id": h["TransitionId"],
                                                       "identity": identity, "previousRetirementHead": previous_head,
                                                       "receiptSha256": observed_sha, "gateSha256": h["NewGateSha256"]}
            if interpreter_handoff(h):
                activation["interpreterHandoff"] = h
            if sentence_handoff(h) or diagnostic_handoff(h):
                activation["sentenceHandoff"] = h
            successor._append(new_db, successor._stamp(activation), new_state["head"])
            new_db.commit(); successor._seal(new_db)
            return {"outcome": "Succeeded", "authority": False}

    def require_predecessor_retired(self, new_config_pin):
        # Called on EVERY operational bridge entry, never a caller success assertion.
        with self._open() as db:
            new_state = self._read(db)
        if new_state["scope"]["schema"] not in (2, 3, 4, 5, 6):
            return
        scope = new_state["scope"]
        old = Register(scope["predecessorRegisterPath"], scope["predecessorScopeSha256"])
        with old.pair(self), old._transaction() as old_db, self._transaction() as db:
            old._read(old_db)  # Hold canonical current/predecessor leases throughout validation.
            self._require_ancestor_snapshot(self._read(db), new_config_pin)

    def _require_ancestor_snapshot(self, current, config_pin, seen=None, maximum_depth=None):
        """Read only terminal ancestors, never recursively acquire an owned lease."""
        seen = set() if seen is None else set(seen)
        maximum_depth = (7 if current["scope"]["schema"] == 6 else 6 if current["scope"]["schema"] == 5 else 5) if maximum_depth is None else maximum_depth
        identity = os.path.normcase(str(self.path))
        if identity in seen or len(seen) >= maximum_depth:
            raise Rejected("PredecessorNotRetired")
        seen.add(identity)
        schema = current["scope"]["schema"]
        if schema == 1:
            return
        scope = current["scope"]
        old = Register(scope["predecessorRegisterPath"], scope["predecessorScopeSha256"])
        if not old.path.is_file() or old.path.stat().st_size > 2_000_000:
            raise Rejected("RegisterUnavailableOrOversized")
        with closing(sqlite3.connect(old.path.as_uri() + "?mode=ro", uri=True, timeout=0)) as db:
            db.execute("PRAGMA query_only=ON"); db.execute("BEGIN")
            state = old._read(db)
            a = current["activation"]; item = state["retirement"]
            # A single validated pre-effect interpreter repair may change only
            # the current gate pin. The immutable retirement still binds the
            # ORIGINAL activation pin; never rewrite an ancestor to match it.
            activation_pin = (a["gateSha256"] if a and schema == 4 and current["helperSourceRepairs"] == 1 else config_pin)
            typed_pair = schema == 4 and state["scope"]["schema"] == 4 and item and interpreter_handoff(item["handoff"])
            if ((state["scope"]["schema"] != schema - 1 and not typed_pair) or not state["retired"] or state["pending"] or not a or not item
                    or item["identity"] != a["identity"] or state["head"] != a["previousRetirementHead"]
                    or item["id"] != scope["transitionId"] or item["handoff"]["NewScopeSha256"] != self.scope_sha
                    or item["handoff"]["NewGateSha256"] != activation_pin or current["currentGate"] != config_pin
                    or item["handoff"]["SourceApprovalSha256"] != scope["approvalSha256"]
                    or item["handoff"]["PreviousGateSha256"] != state["currentGate"]
                    or ("interpreterHandoff" in a and a["interpreterHandoff"] != item["handoff"])
                    or (schema in (3, 4, 5, 6) and ((item["handoff"]["SchemaVersion"] != schema - 1 and not (schema==6 and expired_diagnostic_handoff(item["handoff"])))
                        or item["handoff"]["OriginalManifestSha256"] != state["scope"]["plansSha256"]
                        or item["handoff"]["AcceptedEvidenceSha256"] != scope["acceptedEvidenceSha256"]))):
                raise Rejected("PredecessorNotRetired")
            if schema == 6:
                h = item["handoff"]
                if not diagnostic_handoff(h) or a.get("sentenceHandoff") != h:raise Rejected("DiagnosticCounterOrEpoch")
                diagnostic_window_binding(state,scope,h)
                if current["clockEpoch"] != h.get("ClockEpochEvidence",state["clockEpoch"]):raise Rejected("DiagnosticCounterOrEpoch")
            if schema == 5:
                h = item["handoff"]
                if (not sentence_handoff(h) or a.get("sentenceHandoff") != h
                        or (not expired_sentence_handoff(h) and any(scope[k] != state["scope"][k] for k in ("startUtc", "endUtc")))
                        or (expired_sentence_handoff(h) and (utc(scope["startUtc"]) != h["ClockEpochEvidence"]["ObservedUtcMicros"] or h["ClockEpochEvidence"]["TrustedUtcLowerMicros"] < utc(state["scope"]["endUtc"])))
                        or (h["UsedRefreshes"], h["UsedAdmissions"], h["UsedResumeAdmissions"]) != (state["refresh"], state["admissions"], state["resume-admission"])):
                    raise Rejected("SentenceCounterOrWindowBinding")
            old._require_ancestor_snapshot(state, state["currentGate"], seen, maximum_depth)

    def status(self):
        with self._open() as db:
            state = self._read(db)
            self._guard(db, state)
            return {"refreshes": state["refresh"], "resumeAdmissions": state["resume-admission"],
                    "completionAdmissions": state["completion-admission"],
                    "buildTransitions": state["buildTransitions"], "currentBuildSha256": state["currentBuild"],
                    "helperSourceRepairs": state["helperSourceRepairs"],
                    "pilotAdmissions": state["pilot-admission"], "totalAdmissions": state["admissions"],
                    "currentGateSha256": state["currentGate"],
                    "pendingId": state["pending"]["id"] if state["pending"] else None,
                    "requiresReconciliation": state["pending"] is not None,
                    "scopeSha256": self.scope_sha, "deadlineUtc": effective_deadline(state["scope"],state.get("clockEpoch"))}
