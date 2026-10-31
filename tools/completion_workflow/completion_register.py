"""Private, fail-closed workflow gate. Never supplies Google or spending authority.

An admitted scope digest must come from independent review. No live scope is bundled.
Only fixed metadata/hashes are persisted. Native UI is NOT intercepted by this module.
"""
from __future__ import annotations

import hashlib
from contextlib import closing, contextmanager
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


def path_digest(path):
    return hashlib.sha256(os.path.normcase(str(physical(path))).encode("utf-8")).hexdigest()


def validate_scope(scope):
    if not isinstance(scope, dict) or set(scope) != SCOPE_KEYS or type(scope["schema"]) is not int or scope["schema"] != 1:
        raise Rejected("ScopeSchema")
    for key in SCOPE_KEYS:
        if key.endswith("Sha256"):
            hash_value(scope[key])
    if (type(scope["maxRefreshes"]) is not int or scope["maxRefreshes"] != 24 or
            type(scope["maxResumeAdmissions"]) is not int or scope["maxResumeAdmissions"] != 24):
        raise Rejected("FixedLimits")
    if utc(scope["endUtc"]) - utc(scope["startUtc"]) != 6 * 3600 * 1_000_000:
        raise Rejected("FixedSixHourWindow")


def verify_plan_policy(manifest, scope, policy, kind):
    """Bind actual immutable proposal/coverage projection, not a caller assertion."""
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
        if part["kind"] == "held" and policy["AuthorizationId"] != manifest["heldAuthorizationId"]:
            raise Rejected("OriginalHeldIdentity")
        if part["kind"] not in ("held", "new"):
            raise Rejected("AcceptedCoverageReplay")
        if policy["MaximumExistingDispatches"] != 1 or policy["MaximumNewReservations"] != 0 or policy["MaximumAdditionalReservationMinorUnits"] != 0:
            raise Rejected("ResumeFinancialBounds")


class Register:
    """Clock injection is for offline tests, never exposed by operational CLI."""

    def __init__(self, path, scope_sha, clock=None, monotonic=None):
        self.path = physical(path)
        self.scope_sha = hash_value(scope_sha)
        self.clock = clock or (lambda: time.time_ns() // 1000)
        self.monotonic = monotonic or clock or (lambda: time.monotonic_ns() // 1000)

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
            # Sentinel survives partial initialization; NEVER delete/retry/reset it.
            fd = os.open(register.anchor, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
            os.close(fd)
            fd = os.open(register.path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
            os.close(fd)
            register._initialize(scope, reviewed_sha, now)
        return register

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
        state = {"refresh": 0, "resume-admission": 0, "completion-admission": 0, "pending": None, "stopped": False,
                 "lastUtc": 0, "lastMono": 0, "startUtc": 0, "startMono": 0,
                 "ids": set(), "scope": scope, "head": previous}
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
                      "stop": {"type", "utc", "reason", "mono"}, "check": {"type", "utc", "mono"}}
            if event.get("type") not in shapes or set(event) != shapes[event["type"]]:
                raise Rejected("EventSchema")
            if event["type"] in ("claim", "complete"):
                try:
                    if str(uuid.UUID(event["id"])) != event["id"]:
                        raise Rejected("EventId")
                except (ValueError, TypeError, AttributeError) as error:
                    raise Rejected("EventId") from error
                hash_value(event["identity"] if event["type"] == "claim" else event["receiptSha256"])
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
                if (state["stopped"] or state["pending"] or kind not in ("refresh", "resume-admission", "completion-admission") or
                        event["id"] in state["ids"] or state[kind] >= (1 if kind == "completion-admission" else 24)):
                    raise Rejected("InvalidClaimHistory")
                state[kind] += 1
                state["ids"].add(event["id"])
                state["pending"] = event
            elif event["type"] == "complete":
                if not state["pending"] or event["id"] != state["pending"]["id"]:
                    raise Rejected("InvalidCompletionHistory")
                state["pending"] = None
                if event["outcome"] != "Succeeded":
                    state["stopped"] = True
            elif event["type"] == "stop":
                state["stopped"] = True
            elif event["type"] == "check":
                if state["stopped"]:
                    raise Rejected("CheckAfterStop")
            else:
                raise Rejected("UnknownEvent")
            state["lastUtc"] = event["utc"]
            state["lastMono"] = event["mono"]
            previous = sha
        state["head"] = previous
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

    def _guard(self, db, state):
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

    def claim(self, kind, identity_sha, operation_id):
        if kind not in ("refresh", "resume-admission", "completion-admission"):
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
            if state[kind] >= (1 if kind == "completion-admission" else 24):
                raise Rejected("OperationLimit")
            event = {"type": "claim", "utc": now, "id": operation_id, "kind": kind, "identity": identity_sha}
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
            if not pending or pending["id"] != operation_id or pending["identity"] != receipt["identitySha256"]:
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
                good = good and type(receipt.get("maximumNewAttempts")) is int and 0 <= receipt["maximumNewAttempts"] <= 108
                good = good and type(receipt.get("additionalReservationCents")) is int and 0 <= receipt["additionalReservationCents"] <= 108
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
            if not pending or pending["id"] != operation_id or pending["identity"] != identity_sha:
                raise Rejected("PendingEffectIdentityMismatch")
            return True

    def status(self):
        with self._open() as db:
            state = self._read(db)
            self._guard(db, state)
            return {"refreshes": state["refresh"], "resumeAdmissions": state["resume-admission"],
                    "completionAdmissions": state["completion-admission"],
                    "pendingId": state["pending"]["id"] if state["pending"] else None,
                    "requiresReconciliation": state["pending"] is not None,
                    "scopeSha256": self.scope_sha, "deadlineUtc": state["scope"]["endUtc"]}
