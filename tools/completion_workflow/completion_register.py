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


def path_digest(path):
    return hashlib.sha256(os.path.normcase(str(physical(path))).encode("utf-8")).hexdigest()


def validate_scope(scope):
    if (not isinstance(scope, dict) or type(scope.get("schema")) is not int or scope["schema"] not in (1, 2)
            or set(scope) != (SUCCESSOR_SCOPE_KEYS if scope["schema"] == 2 else SCOPE_KEYS)):
        raise Rejected("ScopeSchema")
    for key in scope:
        if key.endswith("Sha256"):
            hash_value(scope[key])
    if scope["schema"] == 2:
        if type(scope["maxAdmissions"]) is not int or scope["maxAdmissions"] != 24:
            raise Rejected("FixedAdmissionLimit")
        if not isinstance(scope["predecessorRegisterPath"], str) or not Path(scope["predecessorRegisterPath"]).is_absolute():
            raise Rejected("PredecessorPath")
        if str(uuid.UUID(scope["transitionId"])) != scope["transitionId"] or uuid.UUID(scope["transitionId"]).int == 0:
            raise Rejected("SuccessorTransitionId")
    if (type(scope["maxRefreshes"]) is not int or scope["maxRefreshes"] != 24 or
            type(scope["maxResumeAdmissions"]) is not int or scope["maxResumeAdmissions"] != 24):
        raise Rejected("FixedLimits")
    if utc(scope["endUtc"]) - utc(scope["startUtc"]) != 6 * 3600 * 1_000_000:
        raise Rejected("FixedSixHourWindow")


TRANSITION_KEYS = {"SchemaVersion", "TransitionId", "SourceApprovalSha256", "ScopeSha256", "ExpectedHeadSha256",
                   "PreviousConfigPath", "PreviousGateSha256", "NewGateSha256", "PreviousBuildSha256", "NewBuildSha256"}


def transition_kind(transition):
    return "helper-source" if transition.get("TransitionKind") == "helper-source" else "build"


def validate_transition(transition):
    if (not isinstance(transition, dict) or set(transition) not in (TRANSITION_KEYS, TRANSITION_KEYS | {"TransitionKind"})
            or ("TransitionKind" in transition and transition["TransitionKind"] != "helper-source")
            or type(transition["SchemaVersion"]) is not int or transition["SchemaVersion"] != 1):
        raise Rejected("TransitionSchema")
    for name in TRANSITION_KEYS:
        if name.endswith("Sha256"):
            hash_value(transition[name])
    if str(uuid.UUID(transition["TransitionId"])) != transition["TransitionId"] or uuid.UUID(transition["TransitionId"]).int == 0:
        raise Rejected("TransitionId")
    if not isinstance(transition["PreviousConfigPath"], str) or not Path(transition["PreviousConfigPath"]).is_absolute():
        raise Rejected("TransitionConfigPath")
    same_build = transition["PreviousBuildSha256"] == transition["NewBuildSha256"]
    if (same_build != (transition_kind(transition) == "helper-source")
            or transition["PreviousGateSha256"] == transition["NewGateSha256"]):
        raise Rejected("TransitionMustChangeBuild")


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


def validate_handoff(h):
    if not isinstance(h, dict) or set(h) != HANDOFF_KEYS or type(h["SchemaVersion"]) is not int or h["SchemaVersion"] != 1:
        raise Rejected("HandoffSchema")
    for key in h:
        if key.endswith("Sha256"):
            hash_value(h[key])
    if str(uuid.UUID(h["TransitionId"])) != h["TransitionId"] or uuid.UUID(h["TransitionId"]).int == 0:
        raise Rejected("HandoffId")
    uuid_identity(h["FailedAuthorizationId"])
    if (not re.fullmatch(r"[a-f0-9]{32}", h["FailedLocalAttemptId"]) or int(h["FailedLocalAttemptId"], 16) == 0
            or type(h["FailedCoverageOrdinal"]) is not int or h["FailedCoverageOrdinal"] < 1
            or type(h["FailedPayloadBytes"]) is not int or not 1 <= h["FailedPayloadBytes"] <= 5000
            or h["PreviousScopeSha256"] == h["NewScopeSha256"] or h["PreviousGateSha256"] == h["NewGateSha256"]):
        raise Rejected("HandoffBinding")
    for name in ("PreviousConfigPath", "NewConfigPath", "NewScopePath"):
        if not isinstance(h[name], str) or not Path(h[name]).is_absolute():
            raise Rejected("HandoffPath")


def verify_plan_policy(manifest, scope, policy, kind):
    """Bind actual immutable proposal/coverage projection, not a caller assertion."""
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
        state = {"refresh": 0, "resume-admission": 0, "completion-admission": 0, "pilot-admission": 0,
                 "admissions": 0, "pilotPolicySha256": None, "pending": None, "stopped": False, "stopReason": None,
                 "lastUtc": 0, "lastMono": 0, "startUtc": 0, "startMono": 0,
                 "ids": set(), "scope": scope, "head": previous, "buildTransitions": 0, "helperSourceRepairs": 0,
                 "currentBuild": scope["buildSha256"], "currentGate": None, "retirement": None,
                 "retired": False, "activation": None, "activated": scope["schema"] == 1}
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
            shapes["retire-claim"] = {"type", "utc", "mono", "id", "identity", "handoff"}
            shapes["retire-complete"] = {"type", "utc", "mono", "id", "identity", "receiptSha256"}
            shapes["activate"] = {"type", "utc", "mono", "id", "identity", "previousRetirementHead", "receiptSha256", "gateSha256"}
            if event.get("type") == "claim" and scope["schema"] == 2 and event.get("kind") == "completion-admission":
                shapes["claim"] = shapes["claim"] | {"pilotProofSha256"}
            if event.get("type") not in shapes or set(event) != shapes[event["type"]]:
                raise Rejected("EventSchema")
            if event["type"] in ("claim", "complete", "build-claim", "build-complete", "source-claim", "source-complete", "retire-claim", "retire-complete", "activate"):
                try:
                    if str(uuid.UUID(event["id"])) != event["id"]:
                        raise Rejected("EventId")
                except (ValueError, TypeError, AttributeError) as error:
                    raise Rejected("EventId") from error
                hash_value(event["identity"] if event["type"] in ("claim", "build-claim", "source-claim", "retire-claim") else event["receiptSha256"])
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
                kinds = ("refresh", "resume-admission", "completion-admission", "pilot-admission") if scope["schema"] == 2 else ("refresh", "resume-admission", "completion-admission")
                if (state["stopped"] or not state["activated"] or state["retirement"] or state["pending"] or kind not in kinds
                        or event["id"] in state["ids"] or state[kind] >= (1 if kind in ("completion-admission", "pilot-admission") else 24)
                        or (scope["schema"] == 2 and kind != "refresh" and state["admissions"] >= 24)):
                    raise Rejected("InvalidClaimHistory")
                if scope["schema"] == 2 and kind == "completion-admission":
                    hash_value(event["pilotProofSha256"])
                    if state["pilotPolicySha256"] is None:
                        raise Rejected("PilotNotAccepted")
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
                state["pending"] = None
                if event["outcome"] != "Succeeded":
                    state["stopped"] = True
                    state["stopReason"] = event["reason"]
            elif event["type"] in ("build-claim", "source-claim"):
                transition = event["transition"]
                validate_transition(transition)
                source = transition_kind(transition) == "helper-source"
                if (scope["schema"] != 1 or source != (event["type"] == "source-claim") or state["stopped"] or state["pending"]
                        or state["buildTransitions"] != (1 if source else 0) or (source and state["helperSourceRepairs"] != 0)
                        or event["id"] in state["ids"]
                        or state["resume-admission"] != 0 or state["completion-admission"] != 0
                        or transition["TransitionId"] != event["id"] or transition["ExpectedHeadSha256"] != previous
                        or transition["ScopeSha256"] != self.scope_sha or transition["SourceApprovalSha256"] != scope["approvalSha256"]
                        or transition["PreviousBuildSha256"] != state["currentBuild"]
                        or (state["currentGate"] is not None and transition["PreviousGateSha256"] != state["currentGate"])):
                    raise Rejected("InvalidBuildClaimHistory")
                state["helperSourceRepairs" if source else "buildTransitions"] = 1
                state["ids"].add(event["id"])
                state["pending"] = dict(event, kind="helper-source" if source else "build-transition")
                state["currentGate"] = transition["PreviousGateSha256"]
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
            elif event["type"] == "stop":
                state["stopped"] = True
                state["stopReason"] = event["reason"]
            elif event["type"] == "retire-claim":
                h = event["handoff"]
                validate_handoff(h)
                if (scope["schema"] != 1 or state["pending"] or state["retirement"] or event["id"] in state["ids"]
                        or h["TransitionId"] != event["id"] or h["ExpectedPreviousHeadSha256"] != previous
                        or h["PreviousScopeSha256"] != self.scope_sha
                        or h["PreviousGateSha256"] != state["currentGate"]):
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
                if (scope["schema"] != 2 or state["activated"] or state["pending"] or state["stopped"]
                        or event["id"] != scope["transitionId"]):
                    raise Rejected("InvalidSuccessorActivation")
                hash_value(event["identity"]); hash_value(event["previousRetirementHead"])
                hash_value(event["gateSha256"])
                state["activation"] = event
                state["activated"] = True
                state["currentGate"] = event["gateSha256"]
            elif event["type"] == "check":
                if state["stopped"] or state["retirement"]:
                    raise Rejected("CheckAfterStop")
            else:
                raise Rejected("UnknownEvent")
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
        # An unobserved backward wall-clock adjustment cannot extend six hours.
        if now - state["startUtc"] + 250000 < mono - state["startMono"]:
            return "ClockRollback"
        if now - state["startUtc"] > mono - state["startMono"] + 250000:
            return "ClockDiscontinuity"
        if (not utc(state["scope"]["startUtc"]) <= now < utc(state["scope"]["endUtc"])
                or mono - state["startMono"] >= utc(state["scope"]["endUtc"]) - state["startUtc"]):
            return "WindowExpired"
        return None

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

    def claim(self, kind, identity_sha, operation_id, pilot_proof_sha=None):
        if kind not in ("refresh", "resume-admission", "completion-admission", "pilot-admission"):
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
            if kind == "pilot-admission" and state["scope"]["schema"] != 2:
                raise Rejected("UnsupportedOperation")
            if (state[kind] >= (1 if kind in ("completion-admission", "pilot-admission") else 24)
                    or (state["scope"]["schema"] == 2 and kind != "refresh" and state["admissions"] >= 24)):
                raise Rejected("OperationLimit")
            event = {"type": "claim", "utc": now, "id": operation_id, "kind": kind, "identity": identity_sha}
            if state["scope"]["schema"] == 2 and kind == "completion-admission":
                hash_value(pilot_proof_sha)
                if state["pilotPolicySha256"] is None:
                    raise Rejected("PilotNotAccepted")
                event["pilotProofSha256"] = pilot_proof_sha
            self._append(db, self._stamp(event), state["head"])
            db.commit()  # A durable consumed slot exists BEFORE any external action.
            self._seal(db)  # Failure here returns NO permit and future mismatch stops.
            return {"id": operation_id, "kind": kind, "ordinal": state[kind] + 1,
                    "scopeSha256": self.scope_sha, "identitySha256": identity_sha,
                    "deadlineUtc": state["scope"]["endUtc"], "authority": False}

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

    def _build_ready(self, state, transition, identity_sha, operation_id, pending=False, check_clock=True):
        validate_transition(transition)
        hash_value(identity_sha)
        if state["scope"]["schema"] != 1 or state["retirement"]:
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
            claim_type = "source-claim" if transition_kind(transition) == "helper-source" else "build-claim"
            if (not item or item["type"] != claim_type or item["id"] != operation_id
                    or item["identity"] != identity_sha or item["transition"] != transition):
                raise Rejected("BuildTransitionPendingMismatch")
        elif (state["pending"] or state["buildTransitions"] != (1 if transition_kind(transition) == "helper-source" else 0)
              or (transition_kind(transition) == "helper-source" and state["helperSourceRepairs"] != 0)
              or operation_id in state["ids"]
              or state["head"] != transition["ExpectedHeadSha256"] or state["currentBuild"] != transition["PreviousBuildSha256"]
              or (state["currentGate"] is not None and state["currentGate"] != transition["PreviousGateSha256"])):
            raise Rejected("BuildTransitionHeadOrState")

    def inspect_build(self, transition, identity_sha, operation_id):
        with self._open() as db:
            state = self._read(db)
            self._build_ready(state, transition, identity_sha, operation_id)
            return {"valid": True, "headSha256": state["head"], "deadlineUtc": state["scope"]["endUtc"], "authority": False}

    def claim_build(self, transition, identity_sha, operation_id):
        with self._open() as db:
            state = self._read(db)
            # Expected head is checked under the exclusive lease before any append.
            self._build_ready(state, transition, identity_sha, operation_id, check_clock=False)
            if self._time_rejection(state):
                self._guard(db, state)  # Persist terminal stop without altering a valid expected head.
            event_type = "source-claim" if transition_kind(transition) == "helper-source" else "build-claim"
            self._append(db, self._stamp({"type": event_type, "utc": self.clock(), "id": operation_id,
                                         "identity": identity_sha, "transition": transition}), state["head"])
            db.commit()
            self._seal(db)
            return {"id": operation_id, "identitySha256": identity_sha, "scopeSha256": self.scope_sha,
                    "deadlineUtc": state["scope"]["endUtc"], "authority": False}

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
            event_type = "source-complete" if transition_kind(transition) == "helper-source" else "build-complete"
            self._append(db, self._stamp({"type": event_type, "utc": self.clock(), "id": operation_id,
                                         "identity": identity_sha, "receiptSha256": observed_sha}), state["head"])
            db.commit()
            self._seal(db)
            return {"outcome": "Succeeded", "authority": False}

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
        if (old_state["scope"]["schema"] != 1 or new_scope["schema"] != 2
                or h["PreviousScopeSha256"] != self.scope_sha or h["NewScopeSha256"] != successor.scope_sha
                or digest(new_scope) != successor.scope_sha or new_scope["predecessorScopeSha256"] != self.scope_sha
                or physical(new_scope["predecessorRegisterPath"]) != self.path
                or new_scope["registerPathSha256"] != path_digest(successor.path)
                or h["TransitionId"] != new_scope["transitionId"] or h["SourceApprovalSha256"] != new_scope["approvalSha256"]
                or new_scope["approvalSha256"] == old_state["scope"]["approvalSha256"]
                or any(new_scope[k] != old_state["scope"][k] for k in ("profileSha256", "accountSha256", "projectSha256"))):
            raise Rejected("SuccessorScopeBinding")
        # Explicit new approval may replace an expired old window, never a clock discontinuity.
        if ((old_state["stopped"] and old_state["stopReason"] != "WindowExpired")
                or check_clock and self._time_rejection(old_state) not in (None, "WindowExpired")):
            raise Rejected("RetirementClockOrStop")
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

    def _retirement_clock(self, db, state):
        reason = self._time_rejection(state)
        if reason in ("ClockRollback", "ClockDiscontinuity"):
            self._append(db, self._stamp({"type": "stop", "utc": max(self.clock(), state["lastUtc"]), "reason": reason}, state["lastMono"]), state["head"])
            db.commit(); self._seal(db)
            raise Rejected(reason)

    def inspect_successor(self, successor, new_scope, h, identity):
        with self.pair(successor), self._transaction() as db:
            state = self._read(db)
            self._handoff_ready(state, successor, new_scope, h, identity)
            if successor.path.exists() or successor.anchor.exists():
                raise Rejected("SuccessorAlreadyExists")
            return {"valid": True, "deadlineUtc": new_scope["endUtc"], "authority": False}

    def claim_successor(self, successor, new_scope, h, identity):
        with self.pair(successor), self._transaction() as db:
            state = self._read(db)
            self._handoff_ready(state, successor, new_scope, h, identity, check_clock=False)
            self._retirement_clock(db, state)
            if not utc(new_scope["startUtc"]) <= successor.clock() < utc(new_scope["endUtc"]):
                raise Rejected("OutsideWindow")
            if successor.path.exists() or successor.anchor.exists():
                raise Rejected("SuccessorAlreadyExists")
            self._append(db, self._stamp({"type": "retire-claim", "utc": max(self.clock(), state["lastUtc"]),
                                         "id": h["TransitionId"], "identity": identity, "handoff": h}, state["lastMono"]), state["head"])
            db.commit(); self._seal(db)  # Old authority stops durably before any new state/install effect.
            successor._create_locked(new_scope, successor.scope_sha, successor.clock())
            return {"id": h["TransitionId"], "identitySha256": identity, "scopeSha256": successor.scope_sha,
                    "deadlineUtc": new_scope["endUtc"], "authority": False}

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
            successor._append(new_db, successor._stamp({"type": "activate", "utc": successor.clock(), "id": h["TransitionId"],
                                                       "identity": identity, "previousRetirementHead": previous_head,
                                                       "receiptSha256": observed_sha, "gateSha256": h["NewGateSha256"]}), new_state["head"])
            new_db.commit(); successor._seal(new_db)
            return {"outcome": "Succeeded", "authority": False}

    def require_predecessor_retired(self, new_config_pin):
        # Called on EVERY operational bridge entry, never a caller success assertion.
        with self._open() as db:
            new_state = self._read(db)
        if new_state["scope"]["schema"] != 2:
            return
        scope = new_state["scope"]
        old = Register(scope["predecessorRegisterPath"], scope["predecessorScopeSha256"])
        with old.pair(self), old._transaction() as old_db, self._transaction() as db:
            state = old._read(old_db); current = self._read(db)
            a = current["activation"]; item = state["retirement"]
            if (not state["retired"] or state["pending"] or not a or not item
                    or item["identity"] != a["identity"] or state["head"] != a["previousRetirementHead"]
                    or item["id"] != scope["transitionId"] or item["handoff"]["NewScopeSha256"] != self.scope_sha
                    or item["handoff"]["NewGateSha256"] != new_config_pin or current["currentGate"] != new_config_pin):
                raise Rejected("PredecessorNotRetired")

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
                    "scopeSha256": self.scope_sha, "deadlineUtc": state["scope"]["endUtc"]}
