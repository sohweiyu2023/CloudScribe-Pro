"""Pinned private workflow bridge. -I -S; no user modules, UI or provider requests."""
import hashlib
from contextlib import closing
from datetime import datetime, timezone
import json
import os
import re
from pathlib import Path
import sqlite3
import sys
import types
import uuid


def sha(data):
    return hashlib.sha256(data).hexdigest()


def safe_exception_name(error):
    name=type(error).__name__
    return name if name in {"Rejected","KeyError","TypeError","ValueError","AttributeError","IndexError","OverflowError",
        "UnicodeDecodeError","UnicodeEncodeError","JSONDecodeError","OperationalError","DatabaseError","IntegrityError",
        "FileNotFoundError","PermissionError","OSError","TimeoutError","RuntimeError","AssertionError","MemoryError","EOFError"} else "Other"


def safe_failure_evidence(error):
    """Only bounded owned-source locations, never exception prose, paths, locals or traceback text."""
    component="unavailable";line=None
    frame=BaseException.__traceback__.__get__(error)
    for _ in range(128):
        if frame is None:break
        name=os.path.basename(frame.tb_frame.f_code.co_filename)
        if name in ("gate_cli.py","completion_register.py") and 1 <= frame.tb_lineno <= 10000:
            component="bridge" if name=="gate_cli.py" else "register";line=frame.tb_lineno
        frame=frame.tb_next
    else:component="unavailable";line=None
    return {"failureExceptionType":safe_exception_name(error),"failureComponent":component,"failureSourceLine":line}


def rejection_packet(error,reasons):
    args=BaseException.args.__get__(error)
    coded=isinstance(error,ValueError) or (isinstance(error,RuntimeError) and type(error).__name__=="Rejected")
    detail=args[0] if coded and len(args)==1 and type(args[0]) is str and len(args[0])<=128 else ""
    reason=reasons.get(detail,"Storage" if isinstance(error,(OSError,sqlite3.Error)) else "Unknown")
    result={"rejected":True,"category":safe_exception_name(error),"reason":reason,
            "code":detail if detail in reasons else "Unclassified","requiresReconciliation":True}
    if result["code"]=="Unclassified":result.update(safe_failure_evidence(error))
    return result


def bounded(path, limit):
    path = Path(os.path.abspath(path))
    for part in (path, *path.parents):
        if part.is_symlink() or part.is_junction():
            raise ValueError("LinkedPath")
    with path.open("rb") as stream:
        data = stream.read(limit + 1)
    if len(data) > limit:
        raise ValueError("Oversized")
    return data


def unique(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("DuplicateField")
        result[key] = value
    return result


def exact_object_property(raw, name):
    """Hash native serialized bytes, not a different Python JSON canonicalization."""
    decoder=json.JSONDecoder(object_pairs_hook=unique);index=0
    def skip(pos):
        while pos<len(raw) and raw[pos].isspace():pos+=1
        return pos
    index=skip(index)
    if index>=len(raw) or raw[index]!='{':raise ValueError("ContinuationHistoricalPolicy")
    index+=1;found=None
    while True:
        index=skip(index)
        if index<len(raw) and raw[index]=='}':break
        key,end=decoder.raw_decode(raw,index);index=skip(end)
        if not isinstance(key,str) or index>=len(raw) or raw[index]!=':':raise ValueError("ContinuationHistoricalPolicy")
        start=skip(index+1);_,end=decoder.raw_decode(raw,start)
        if key==name:
            if found is not None:raise ValueError("DuplicateField")
            found=raw[start:end].encode('utf-8')
        index=skip(end)
        if index<len(raw) and raw[index]==',':index+=1;continue
        if index<len(raw) and raw[index]=='}':break
        raise ValueError("ContinuationHistoricalPolicy")
    if found is None:raise ValueError("ContinuationHistoricalPolicy")
    return found


def build_digest(config):
    return sha((config["AppSha256"] + "|" + config["InfrastructureSha256"]).encode())


def installed_readback(config, pin, allow_financial=False):
    """Read actual installation, never caller success flags; no credential access."""
    app = Path(os.path.abspath(config["ApplicationDirectory"]))
    for name, field in (("CloudScribe.dll", "AppSha256"), ("CloudScribe.Infrastructure.dll", "InfrastructureSha256")):
        if sha(bounded(app / name, 512 * 1024 * 1024)) != config[field]:
            raise ValueError("InstalledBuildBinding")
    marker_bytes = bounded(app.parent / "cloudscribe-install.json", 32768)
    marker = json.loads(marker_bytes, object_pairs_hook=unique)
    if not allow_financial and any(field in marker for field in ("RecoveryPolicySha256", "ChirpArticlePolicySha256", "ChirpReservationResumePolicySha256")):
        raise ValueError("TransitionFinancialAdmissionExists")
    if (marker.get("Product") != "CloudScribe Pro" or type(marker.get("CompletionGateProtocolVersion")) is not int
            or marker["CompletionGateProtocolVersion"] != 1 or marker.get("CompletionWorkflowGateSha256") != pin
            or str(marker.get("AppSha256", "")).lower() != config["AppSha256"]):
        raise ValueError("InstalledGatePin")
    if sha(bounded(app.parent / "completion-workflow-gate.json", 32768)) != pin:
        raise ValueError("InstalledGateBytes")
    profile = Path(os.path.abspath(marker["Profile"]))
    if sha(str(profile).rstrip("/\\").upper().encode()) != config["ProfileRootSha256"]:
        raise ValueError("InstalledProfileBinding")
    required_bytes = bounded(profile / ".completion-workflow-required.json", 4096)
    required = json.loads(required_bytes, object_pairs_hook=unique)
    if required != {"GateSha256": pin, "ScopeSha256": config["ScopeSha256"]}:
        raise ValueError("RequiredGatePin")
    return sha(marker_bytes + b"\n" + required_bytes + b"\n" + pin.encode())


def profile_database(config):
    marker = json.loads(bounded(Path(config["ApplicationDirectory"]).parent / "cloudscribe-install.json", 32768), object_pairs_hook=unique)
    profile = Path(os.path.abspath(marker["Profile"]))
    if sha(str(profile).rstrip("/\\").upper().encode()) != config["ProfileRootSha256"]:
        raise ValueError("InstalledProfileBinding")
    db = profile / "data/cloudscribe.db"
    bounded(db, 512 * 1024 * 1024)  # Refuse linked/oversized inputs before SQLite opens.
    wal = Path(str(db) + "-wal")
    if wal.exists() and bounded(wal, 1):
        raise ValueError("ClosedDatabaseRequired")
    return profile, db


def terminal_failure(config, h):
    _, db = profile_database(config)
    if sha(bounded(db, 512 * 1024 * 1024)) != h["DatabaseSha256"]:
        raise ValueError("TerminalDatabasePin")
    with closing(sqlite3.connect(db.as_uri() + "?mode=ro&immutable=1", uri=True)) as connection:
        connection.row_factory = sqlite3.Row
        connection.execute("BEGIN")
        rows = connection.execute("SELECT * FROM chirp_article_owned_attempts WHERE AuthorizationId=?", (uuid.UUID(h["FailedAuthorizationId"]).hex,)).fetchall()
        if len(rows) != 1:
            raise ValueError("TerminalAttempt")
        a = rows[0]
        queues = connection.execute("SELECT * FROM google_generation_queue_states WHERE AccountId=? AND OperationStableId='synthesize-speech' AND IdempotencyKey=?", (config["AccountId"], a["ChildKey"])).fetchall()
        if len(queues) != 1:
            raise ValueError("TerminalQueue")
        q = queues[0]
        if (a["AccountId"] != config["AccountId"] or a["ProjectId"] != config["ProjectId"] or a["PlanSha256"] != h["FailedPlanSha256"]
                or a["CoverageOrdinal"] != h["FailedCoverageOrdinal"] or a["Consumed"] != 1 or a["ClaimedLocalAttemptId"] != h["FailedLocalAttemptId"]
                or q["SubmissionAttempted"] != 1 or q["UnresolvedSubmission"] != 0 or q["ProviderRequestId"] is not None
                or q["DiagnosticCode"] != "google-http-400" or q["ErrorStatus"] != 1
                or q["LocalAttemptId"] != h["FailedLocalAttemptId"] or q["PayloadSha256"] != h["FailedPayloadSha256"] or q["PayloadBytes"] != h["FailedPayloadBytes"]):
            raise ValueError("TerminalFailureEvidence")
        if connection.execute("SELECT COUNT(*) FROM chirp_article_receipts WHERE PlanSha256=? AND CoverageOrdinal=?", (h["FailedPlanSha256"], h["FailedCoverageOrdinal"])).fetchone()[0]:
            raise ValueError("AcceptedWorkCannotRetire")
        if connection.execute("SELECT COUNT(*) FROM chirp_article_output_journals WHERE AuthorizationId=?", (a["AuthorizationId"],)).fetchone()[0]:
            raise ValueError("RecoverableOutputCannotRetire")
        if (connection.execute("SELECT COUNT(*) FROM chirp_article_owned_attempts WHERE AccountId=? AND ProjectId=? AND Consumed=0", (config["AccountId"], config["ProjectId"])).fetchone()[0]
                or connection.execute("SELECT COUNT(*) FROM google_generation_queue_states WHERE AccountId=? AND UnresolvedSubmission<>0", (config["AccountId"],)).fetchone()[0]):
            raise ValueError("PendingOrUncertainWork")
    if sha(bounded(db, 512 * 1024 * 1024)) != h["DatabaseSha256"]:
        raise ValueError("TerminalDatabaseChanged")


def pilot_acceptance(config, manifest, state, module):
    """Actual local output proof, not request JSON booleans or a private receipt assertion."""
    if state["pilotPolicySha256"] is None or state["pending"] or state["stopped"]:
        raise ValueError("PilotNotAccepted")
    profile, db = profile_database(config)
    app = Path(config["ApplicationDirectory"])
    pilot_policy_bytes = bounded(app.parent / "chirp-article-admission-policy.json", 8 * 1024 * 1024)
    if sha(pilot_policy_bytes) != state["pilotPolicySha256"]:
        raise ValueError("PilotPolicyPin")
    pilot_policy = json.loads(pilot_policy_bytes, object_pairs_hook=unique)
    module.verify_plan_policy(manifest, state["scope"], pilot_policy, "pilot-admission")
    plan, ordinal = manifest["pilot"]["planSha256"], manifest["pilot"]["coverageOrdinal"]
    part = next(x for x in manifest["parts"] if (x["planSha256"], x["coverageOrdinal"]) == (plan, ordinal))
    with closing(sqlite3.connect(db.as_uri() + "?mode=ro&immutable=1", uri=True)) as connection:
        connection.row_factory = sqlite3.Row; connection.execute("BEGIN")
        rows = connection.execute("SELECT * FROM chirp_article_owned_attempts WHERE PlanSha256=? AND CoverageOrdinal=? AND PolicySha256=?", (plan, ordinal, state["pilotPolicySha256"])).fetchall()
        if len(rows) != 1:
            raise ValueError("PilotAttempt")
        a = rows[0]
        if (a["AccountId"] != config["AccountId"] or a["ProjectId"] != config["ProjectId"] or a["Consumed"] != 1
                or a["SourceApprovalSha256"] != state["scope"]["approvalSha256"] or a["ReservedMinorUnits"] != 1):
            raise ValueError("PilotAttemptIdentity")
        receipts = connection.execute("SELECT * FROM chirp_article_receipts WHERE AuthorizationId=?", (a["AuthorizationId"],)).fetchall()
        journals = connection.execute("SELECT * FROM chirp_article_output_journals WHERE AuthorizationId=?", (a["AuthorizationId"],)).fetchall()
        if len(receipts) != 1 or len(journals) != 1:
            raise ValueError("PilotOutputIncomplete")
        rr, journal = receipts[0], journals[0]
        raw = rr["ReceiptJson"]
        if (len(raw.encode()) > 32768 or sha(raw.encode()) != rr["ReceiptSha256"] or journal["ReceiptJson"] != raw
                or journal["ReceiptSha256"] != rr["ReceiptSha256"] or rr["PlanSha256"] != plan or rr["CoverageOrdinal"] != ordinal):
            raise ValueError("PilotOutputPin")
        receipt = json.loads(raw, object_pairs_hook=unique); audio = receipt["Audio"]
        envelope = json.loads(a["EnvelopeJson"], object_pairs_hook=unique)
        stored = connection.execute("SELECT ProposalJson,AccountId,ProjectId FROM chirp_article_plans WHERE PlanSha256=?", (plan,)).fetchone()
        if stored is None or len(stored[0].encode()) > 8 * 1024 * 1024:
            raise ValueError("PilotStoredPlan")
        proposal = json.loads(stored[0], object_pairs_hook=unique)
        expected_proposal = next(x["proposalSha256"] for x in manifest["proposals"] if x["planSha256"] == plan)
        original = next(x for x in proposal["Parts"] if x["CoverageOrdinal"] == ordinal)
        q = connection.execute("SELECT * FROM google_generation_queue_states WHERE AccountId=? AND OperationStableId='synthesize-speech' AND IdempotencyKey=?", (config["AccountId"], a["ChildKey"])).fetchone()
        if (module.digest(proposal) != expected_proposal or stored[1] != config["AccountId"] or stored[2] != config["ProjectId"]
                or receipt["PlanSha256"] != plan or receipt["CoverageOrdinal"] != ordinal or receipt["Range"] != original["Range"]
                or uuid.UUID(audio["DocumentId"]) != uuid.UUID(proposal["DocumentId"]) or uuid.UUID(audio["RevisionId"]) != uuid.UUID(proposal["RevisionId"])
                or audio["TextSha256"] != original["Range"]["TextSha256"] or audio["VoiceName"] != config["VoiceName"] or audio["LanguageCode"] != config["LanguageCode"]
                or audio["PayloadSha256"] != part["payloadSha256"] or audio["AccountId"] != config["AccountId"] or audio["OperationId"] != "synthesize-speech"
                or audio["IdempotencyKey"] != a["ChildKey"] or audio["LocalAttemptId"] != a["ClaimedLocalAttemptId"]
                or envelope["AccountId"] != config["AccountId"] or envelope["CredentialReferenceId"] != config["CredentialReferenceId"]
                or envelope["VoiceName"] != config["VoiceName"] or envelope["AudioEncoding"] != "MP3" or envelope["RecoveryAuthorizationId"] is not None
                or uuid.UUID(envelope["ChirpAuthorizationId"]).hex != a["AuthorizationId"]
                or envelope["CompiledPayloadSha256"] != part["payloadSha256"] or envelope["CompiledPayloadBytes"] != part["payloadBytes"]
                or q is None or q["UnresolvedSubmission"] != 0 or q["SubmissionAttempted"] != 1 or q["DiagnosticCode"] != "google-accepted"
                or q["LocalAttemptId"] != a["ClaimedLocalAttemptId"] or q["PayloadSha256"] != part["payloadSha256"] or q["PayloadBytes"] != part["payloadBytes"]):
            raise ValueError("PilotAcceptedIdentity")
        mapping = verify_pilot_mapping(connection, a, envelope, receipt, config, pilot_policy)
        media = profile / "accepted-chirp-article-audio" / (a["AuthorizationId"] + ".mp3")
        if os.path.normcase(os.path.abspath(audio["AudioPath"])) != os.path.normcase(str(media)):
            raise ValueError("PilotMediaLocation")
        data = bounded(media, 256 * 1024 * 1024)
        if len(data) < 12 or len(data) != audio["AudioBytes"] or sha(data) != audio["AudioSha256"]:
            raise ValueError("PilotMediaPin")
        return module.digest({"authorization": a["AuthorizationId"], "receipt": rr["ReceiptSha256"], "audio": sha(data), "mapping": mapping})


def verify_pilot_mapping(connection, attempt, envelope, receipt, config, historical, historical_readback=False):
    rows = connection.execute("SELECT * FROM chirp_reserved_catalog_mappings WHERE AuthorizationId=? ORDER BY CreatedAtUnixMilliseconds,MappingId", (attempt["AuthorizationId"],)).fetchall()
    dispatch = connection.execute("SELECT * FROM chirp_reserved_dispatch_mappings WHERE AuthorizationId=?", (attempt["AuthorizationId"],)).fetchall()
    if not rows:
        if dispatch or receipt.get("ExecutionMappingSha256") is not None:
            raise ValueError("PilotMappingAbsent")
        return None
    if len(rows) > 64 or len(dispatch) != 1:
        raise ValueError("PilotMappingBound")
    previous = None; approvals = set(); used = None; ordered = []; remaining = list(rows); last_created = 0
    while remaining:
        children = [x for x in remaining if x["PriorMappingSha256"] == previous]
        if len(children) != 1:
            raise ValueError("PilotMappingChain")
        row = children[0]; ordered.append(row); remaining.remove(row); previous = row["PolicySha256"]
    previous = None
    for row in ordered:
        raw = row["PolicyJson"]
        if len(raw.encode()) > 8 * 1024 * 1024 or sha(raw.encode()) != row["PolicySha256"] or row["PriorMappingSha256"] != previous:
            raise ValueError("PilotMappingChain")
        p = json.loads(raw, object_pairs_hook=unique)
        if (p.get("PriorMappingSha256") != previous or p["SourceApprovalSha256"] in approvals or p["SchemaVersion"] != 1
                or row["CreatedAtUnixMilliseconds"] < last_created
                or uuid.UUID(p["AuthorizationId"]).hex != attempt["AuthorizationId"] or uuid.UUID(p["MappingId"]).hex != row["MappingId"]
                or p["OriginalEnvelope"] != envelope or dict(p["ExecutionEnvelope"], RequestRevision=envelope["RequestRevision"]) != envelope
                or p["HistoricalPolicy"] != historical or p["ProfileRootSha256"] != config["ProfileRootSha256"]
                or (not historical_readback and (p["AppSha256"] != config["AppSha256"] or p["InfrastructureSha256"] != config["InfrastructureSha256"]))
                or any(not isinstance(p[k],str) or re.fullmatch('[0-9a-f]{64}',p[k]) is None or p[k]=='0'*64 for k in ('AppSha256','InfrastructureSha256'))
                or p["MaximumExistingDispatches"] != 1 or p["MaximumNewReservations"] != 0 or p["MaximumAdditionalReservationMinorUnits"] != 0):
            raise ValueError("PilotMappingIdentity")
        approvals.add(p["SourceApprovalSha256"]); previous = row["PolicySha256"]
        last_created = row["CreatedAtUnixMilliseconds"]
        d = dispatch[0]
        if d["MappingId"] == row["MappingId"]:
            from datetime import datetime, timezone
            expiry = datetime.fromisoformat(p["ExpiresAtUtc"].replace("Z", "+00:00")).timestamp() * 1000
            catalog_expiry = datetime.fromisoformat(p["CatalogExpiresAtUtc"].replace("Z", "+00:00")).timestamp() * 1000
            if (d["PolicySha256"] != row["PolicySha256"] or d["ClaimedLocalAttemptId"] != attempt["ClaimedLocalAttemptId"]
                    or not row["CreatedAtUnixMilliseconds"] <= d["ClaimedAtUnixMilliseconds"] < min(expiry, catalog_expiry)):
                raise ValueError("PilotMappingDispatch")
            used = row["PolicySha256"]
    if used is None or used != previous or used != receipt.get("ExecutionMappingSha256"):
        raise ValueError("PilotMappingReceipt")
    return used


def _continuation_member(connection, config, module, profile, parts, plans, entry, historical_policy=None, historical_readback=False):
    key = (entry["planSha256"], entry["coverageOrdinal"]); part = parts[key]
    attempts = connection.execute("SELECT * FROM chirp_article_owned_attempts WHERE AuthorizationId=?",
                                  (entry["authorizationId"],)).fetchall()
    if len(attempts) != 1:
        raise ValueError("ContinuationAcceptedAttempt")
    a = attempts[0]
    if (a["PlanSha256"] != key[0] or a["CoverageOrdinal"] != key[1] or a["PolicySha256"] != entry["policySha256"]
            or a["SourceApprovalSha256"] != entry["sourceApprovalSha256"] or a["AccountId"] != config["AccountId"]
            or a["ProjectId"] != config["ProjectId"] or a["Currency"] != "USD" or a["Scale"] != 2
            or a["ReservedMinorUnits"] != 1 or a["Consumed"] != 1
            or not isinstance(a["ClaimedLocalAttemptId"], str)
            or module.uuid_identity(a["ClaimedLocalAttemptId"]) != a["ClaimedLocalAttemptId"]):
        raise ValueError("ContinuationAcceptedIdentity")
    receipts = connection.execute("SELECT * FROM chirp_article_receipts WHERE AuthorizationId=?", (a["AuthorizationId"],)).fetchall()
    journals = connection.execute("SELECT * FROM chirp_article_output_journals WHERE AuthorizationId=?", (a["AuthorizationId"],)).fetchall()
    if len(receipts) != 1 or len(journals) != 1:
        raise ValueError("ContinuationOutputIncomplete")
    rr, journal = receipts[0], journals[0]; raw = rr["ReceiptJson"]
    if (len(raw.encode()) > 32768 or sha(raw.encode()) != entry["receiptSha256"]
            or rr["ReceiptSha256"] != entry["receiptSha256"] or rr["PlanSha256"] != key[0]
            or rr["CoverageOrdinal"] != key[1] or journal["ReceiptJson"] != raw
            or journal["ReceiptSha256"] != entry["receiptSha256"]):
        raise ValueError("ContinuationOutputPin")
    receipt = json.loads(raw, object_pairs_hook=unique); audio = receipt["Audio"]
    if len(a["EnvelopeJson"].encode()) > 32768:
        raise ValueError("ContinuationEnvelopeBound")
    envelope = json.loads(a["EnvelopeJson"], object_pairs_hook=unique)
    stored = connection.execute("SELECT ProposalJson,AccountId,ProjectId FROM chirp_article_plans WHERE PlanSha256=?", (key[0],)).fetchone()
    if stored is None or len(stored[0].encode()) > 8 * 1024 * 1024:
        raise ValueError("ContinuationStoredPlan")
    proposal = json.loads(stored[0], object_pairs_hook=unique)
    original = next(x for x in proposal["Parts"] if x["CoverageOrdinal"] == key[1])
    source = connection.execute("SELECT d.DraftText,r.ContentText,d.CurrentRevisionId FROM documents d JOIN document_revisions r ON r.Id=d.CurrentRevisionId WHERE lower(d.Id)=?",
                                (str(uuid.UUID(proposal["DocumentId"])),)).fetchone()
    q = connection.execute("SELECT * FROM google_generation_queue_states WHERE AccountId=? AND OperationStableId='synthesize-speech' AND IdempotencyKey=?",
                           (config["AccountId"], a["ChildKey"])).fetchone()
    if (module.digest(proposal) != plans[key[0]] or stored[1] != config["AccountId"] or stored[2] != config["ProjectId"]
            or source is None or source[0] != source[1] or sha(source[0].encode()) != proposal["SourceSha256"]
            or uuid.UUID(source[2]) != uuid.UUID(proposal["RevisionId"])
            or receipt["PlanSha256"] != key[0] or receipt["CoverageOrdinal"] != key[1] or receipt["Range"] != original["Range"]
            or uuid.UUID(audio["DocumentId"]) != uuid.UUID(proposal["DocumentId"])
            or uuid.UUID(audio["RevisionId"]) != uuid.UUID(proposal["RevisionId"])
            or audio["TextSha256"] != original["Range"]["TextSha256"] or audio["VoiceName"] != config["VoiceName"]
            or audio["LanguageCode"] != config["LanguageCode"] or audio["PayloadSha256"] != part["payloadSha256"]
            or audio["AccountId"] != config["AccountId"] or audio["OperationId"] != "synthesize-speech"
            or audio["IdempotencyKey"] != a["ChildKey"] or audio["LocalAttemptId"] != a["ClaimedLocalAttemptId"]
            or envelope["AccountId"] != config["AccountId"]
            or (not historical_readback and envelope["CredentialReferenceId"] != config["CredentialReferenceId"])
            or not isinstance(envelope["CredentialReferenceId"],str) or not 1 <= len(envelope["CredentialReferenceId"]) <= 128
            or any(ord(c)<32 or 127<=ord(c)<=159 for c in envelope["CredentialReferenceId"])
            or envelope["VoiceName"] != config["VoiceName"] or envelope["AudioEncoding"] != "MP3"
            or envelope["RecoveryAuthorizationId"] is not None
            or uuid.UUID(envelope["ChirpAuthorizationId"]).hex != a["AuthorizationId"]
            or envelope["CompiledPayloadSha256"] != part["payloadSha256"] or envelope["CompiledPayloadBytes"] != part["payloadBytes"]
            or q is None or q["UnresolvedSubmission"] != 0 or q["SubmissionAttempted"] != 1
            or q["DiagnosticCode"] != "google-accepted" or q["LocalAttemptId"] != a["ClaimedLocalAttemptId"]
            or q["PayloadSha256"] != part["payloadSha256"] or q["PayloadBytes"] != part["payloadBytes"]):
        raise ValueError("ContinuationAcceptedProvenance")
    mapping = connection.execute("SELECT PolicyJson FROM chirp_reserved_catalog_mappings WHERE AuthorizationId=? ORDER BY CreatedAtUnixMilliseconds,MappingId LIMIT 1",
                                 (a["AuthorizationId"],)).fetchone()
    historical = None
    if mapping:
        if len(mapping[0].encode()) > 8 * 1024 * 1024:
            raise ValueError("ContinuationMappingBound")
        historical = json.loads(mapping[0], object_pairs_hook=unique)["HistoricalPolicy"]
        if ((historical_policy is not None and module.canonical(historical) != module.canonical(historical_policy))
                    or (historical_policy is None and sha(exact_object_property(mapping[0],"HistoricalPolicy")) != a["PolicySha256"])):
            raise ValueError("ContinuationHistoricalPolicy")
    mapped = verify_pilot_mapping(connection, a, envelope, receipt, config, historical, historical_readback=historical_readback)
    if mapped != entry["executionMappingSha256"]:
        raise ValueError("ContinuationMappingPin")
    media = profile / "accepted-chirp-article-audio" / (a["AuthorizationId"] + ".mp3")
    if os.path.normcase(os.path.abspath(audio["AudioPath"])) != os.path.normcase(str(media)):
        raise ValueError("ContinuationMediaLocation")
    data = bounded(media, 256 * 1024 * 1024)
    if len(data) < 12 or len(data) != audio["AudioBytes"] or sha(data) != audio["AudioSha256"] or sha(data) != entry["audioSha256"]:
        raise ValueError("ContinuationMediaPin")

FINANCIAL_TABLES = ("google_generation_spend_authorizations", "google_recovery_attempts", "google_recovery_scopes",
                    "google_recovery_grant_transitions", "chirp_article_owned_attempts", "chirp_article_legacy_associations")


def continuation_financial_projection(connection, module, excluded=()):
    result = {}
    for table in FINANCIAL_TABLES:
        rows = []
        for index, row in enumerate(connection.execute("SELECT * FROM " + table), 1):
            if index > 10000:
                raise ValueError("ContinuationFinancialBound")
            if table == "chirp_article_owned_attempts" and row["AuthorizationId"] in excluded:
                continue
            rows.append({key: {"blobSha256": sha(value)} if isinstance(value, bytes) else value
                         for key, value in dict(row).items()})
        result[table] = sorted(rows, key=module.canonical)
    return module.digest(result)


def retained_continuation_policy(config, manifest, scope, module, register, expected=None):
    """Derive retained authority only from installed bytes AND a prior successful journal event."""
    app = Path(config["ApplicationDirectory"])
    marker = json.loads(bounded(app.parent / "cloudscribe-install.json", 32768), object_pairs_hook=unique)
    raw = bounded(app.parent / "chirp-article-admission-policy.json", 8 * 1024 * 1024)
    pin = sha(raw)
    if (marker.get("Product") != "CloudScribe Pro" or marker.get("ChirpArticlePolicySha256") != pin
            or (expected is not None and expected != pin)):
        raise ValueError("ContinuationRetainedPolicyPin")
    policy = json.loads(raw, object_pairs_hook=unique)
    kind = module.successor_policy_kind(manifest, policy) if scope["schema"] == 5 else "completion-admission"
    with register._open() as db:
        state = register._read(db)
        module.require_epoch_policy_deadline(state, policy["ExpiresAtUtc"])
        events = [json.loads(row[0]) for row in db.execute("SELECT body FROM events ORDER BY seq")]
        claims = {e["id"] for e in events if e["type"] == "claim" and e["kind"] == kind and e["identity"] == pin}
        if (state["stopped"] or not any(e["type"] == "complete" and e["id"] in claims and e["outcome"] == "Succeeded" for e in events)):
            raise ValueError("ContinuationRetentionNotAdmitted")
    if scope["schema"] == 5:
        # Immutable, already-admitted provenance ONLY. The fresh OUTER resume
        # policy and current register clock are independently checked before any effect.
        module.verify_sentence_policy(manifest, scope, policy, kind, historical_readback=True)
    else:
        module.verify_plan_policy(manifest, scope, policy, kind)
    return policy, pin


def _continuation_clean_held(connection, config, module, scope, parts, a, policy_sha):
    key = (a["PlanSha256"], a["CoverageOrdinal"]); part = parts[key]
    q = connection.execute("SELECT * FROM google_generation_queue_states WHERE AccountId=? AND OperationStableId='synthesize-speech' AND IdempotencyKey=?",
                           (config["AccountId"], a["ChildKey"])).fetchone()
    if len(a["EnvelopeJson"].encode()) > 32768:
        raise ValueError("ContinuationEnvelopeBound")
    e = json.loads(a["EnvelopeJson"], object_pairs_hook=unique)
    if (a["PolicySha256"] != policy_sha or a["SourceApprovalSha256"] != scope["approvalSha256"]
            or a["AccountId"] != config["AccountId"] or a["ProjectId"] != config["ProjectId"]
            or a["Currency"] != "USD" or a["Scale"] != 2 or a["EstimateMinorUnits"] != 1 or a["ReservedMinorUnits"] != 1
            or a["Consumed"] != 0 or a["ClaimedLocalAttemptId"] is not None
            or q is None or q["SubmissionAttempted"] != 0 or q["UnresolvedSubmission"] != 0
            or q["LocalAttemptId"] is not None or q["ProviderRequestId"] is not None or q["DiagnosticCode"] is not None
            or q["PayloadSha256"] not in (None, part["payloadSha256"]) or q["PayloadBytes"] not in (None, part["payloadBytes"])
            or q["ErrorStatus"] != 0 or e["AccountId"] != config["AccountId"]
            or e["CredentialReferenceId"] != config["CredentialReferenceId"] or e["VoiceName"] != config["VoiceName"]
            or e["AudioEncoding"] != "MP3" or e["RecoveryAuthorizationId"] is not None
            or module.uuid_identity(e["ChirpAuthorizationId"]) != a["AuthorizationId"]
            or e["CompiledPayloadSha256"] != part["payloadSha256"] or e["CompiledPayloadBytes"] != part["payloadBytes"]
            or connection.execute("SELECT COUNT(*) FROM chirp_article_receipts WHERE AuthorizationId=?", (a["AuthorizationId"],)).fetchone()[0]
            or connection.execute("SELECT COUNT(*) FROM chirp_article_output_journals WHERE AuthorizationId=?", (a["AuthorizationId"],)).fetchone()[0]
            or connection.execute("SELECT COUNT(*) FROM chirp_reserved_dispatch_mappings WHERE AuthorizationId=?", (a["AuthorizationId"],)).fetchone()[0]):
        raise ValueError("ContinuationHeldNotClean")


def sentence_legacy_acceptance(connection, config, module, profile, entries):
    for entry in entries:
        raw = bounded(profile / "accepted-article-audio" / (entry["localAttemptId"] + ".json"), 16384)
        if sha(raw) != entry["receiptSha256"]: raise ValueError("SentenceLegacyPin")
        audio = json.loads(raw, object_pairs_hook=unique)
        q = connection.execute("SELECT * FROM google_generation_queue_states WHERE AccountId=? AND OperationStableId='synthesize-speech' AND IdempotencyKey=?",
                               (config["AccountId"], audio["IdempotencyKey"])).fetchone()
        media = bounded(audio["AudioPath"], 256 * 1024 * 1024)
        if (audio["AccountId"] != config["AccountId"] or audio["OperationId"] != "synthesize-speech"
                or audio["VoiceName"] != config["VoiceName"] or audio["LanguageCode"] != config["LanguageCode"]
                or audio["LocalAttemptId"] != entry["localAttemptId"] or audio["AudioBytes"] != len(media)
                or audio["AudioSha256"] != entry["audioSha256"] or sha(media) != entry["audioSha256"] or len(media) < 12
                or q is None or q["SubmissionAttempted"] != 1 or q["UnresolvedSubmission"] != 0 or q["DiagnosticCode"] != "google-accepted"
                or q["LocalAttemptId"] != audio["LocalAttemptId"] or q["PayloadSha256"] != audio["PayloadSha256"]):
            raise ValueError("SentenceLegacyPin")


def sentence_stage_acceptance(config, manifest, scope, module, state):
    """Both actual new outputs, derived from the journaled first-stage policy and native receipts."""
    if state["pilotPolicySha256"] is None or state["stopped"]: raise ValueError("PilotNotAccepted")
    parts, plans, _, _, _ = module.validate_sentence_manifest(manifest, scope)
    profile, db = profile_database(config); before = sha(bounded(db, 512 * 1024 * 1024)); proof = []
    with closing(sqlite3.connect(db.as_uri()+"?mode=ro&immutable=1", uri=True)) as c:
        c.row_factory=sqlite3.Row; c.execute("BEGIN")
        for part in manifest["firstStage"]:
            row=c.execute("SELECT * FROM chirp_article_owned_attempts WHERE PlanSha256=? AND CoverageOrdinal=?", (part["planSha256"],part["coverageOrdinal"])).fetchall()
            if len(row)!=1 or row[0]["PolicySha256"]!=state["pilotPolicySha256"]: raise ValueError("PilotAttemptIdentity")
            a=row[0]; rr=c.execute("SELECT * FROM chirp_article_receipts WHERE AuthorizationId=?",(a["AuthorizationId"],)).fetchone()
            if rr is None or len(rr["ReceiptJson"].encode())>32768: raise ValueError("PilotOutputIncomplete")
            receipt=json.loads(rr["ReceiptJson"],object_pairs_hook=unique)
            entry=dict(part,authorizationId=a["AuthorizationId"],policySha256=state["pilotPolicySha256"],sourceApprovalSha256=scope["approvalSha256"],
                receiptSha256=rr["ReceiptSha256"],audioSha256=receipt["Audio"]["AudioSha256"],executionMappingSha256=receipt.get("ExecutionMappingSha256"))
            _continuation_member(c,config,module,profile,parts,plans,entry);proof.append(entry)
    if sha(bounded(db,512*1024*1024))!=before:raise ValueError("ContinuationDatabaseChanged")
    return module.digest(proof)


def continuation_acceptance(config, manifest, scope, module, database_sha=None, retained=None, resume_authorization=None, pilot_policy_sha=None):
    """Initial mode requires untouched remaining coverage. Retention is derived from journal+installed policy."""
    parts, plans, remaining, accepted, _ = module.validate_continuation_manifest(manifest, scope)
    profile, db = profile_database(config); before = sha(bounded(db, 512 * 1024 * 1024))
    if database_sha is not None and before != database_sha:
        raise ValueError("ContinuationDatabasePin")
    with closing(sqlite3.connect(db.as_uri() + "?mode=ro&immutable=1", uri=True)) as connection:
        connection.row_factory = sqlite3.Row; connection.execute("BEGIN")
        for entry in accepted:
            # Completed historical outputs retain their immutable mapping/build/credential reference,
            # not the context now reading local audio. Current/new outputs retain current-context checks.
            _continuation_member(connection, config, module, profile, parts, plans, entry, historical_readback=True)
        sentence = scope["schema"] == 5
        first = {(x["planSha256"],x["coverageOrdinal"]) for x in manifest["firstStage"]} if sentence else set()
        if sentence: sentence_legacy_acceptance(connection,config,module,profile,manifest["history"]["legacy"])
        if connection.execute("SELECT COUNT(*) FROM google_generation_queue_states WHERE AccountId=? AND UnresolvedSubmission<>0", (config["AccountId"],)).fetchone()[0]:
            raise ValueError("PendingOrUncertainWork")
        excluded = set(); held = []; coverage = set()
        candidates = connection.execute("SELECT * FROM chirp_article_owned_attempts WHERE AccountId=? AND ProjectId=?", (config["AccountId"], config["ProjectId"])).fetchall()
        for a in candidates:
            key = (a["PlanSha256"], a["CoverageOrdinal"])
            if key not in remaining:
                if a["Consumed"] == 0:
                    raise ValueError("PendingOrUncertainWork")
                continue
            stage_accepted = sentence and key in first and a["Consumed"] == 1 and pilot_policy_sha is not None and a["PolicySha256"] == pilot_policy_sha
            if retained is None and not stage_accepted:
                raise ValueError("ContinuationRemainingNotUntouched")
            policy, policy_sha = (None, pilot_policy_sha) if stage_accepted else retained
            if sentence and not stage_accepted and key not in {(x["Proposal"]["PlanSha256"],p["CoverageOrdinal"]) for x in policy["Articles"] for p in x["AllowedSegments"]}:
                raise ValueError("ContinuationProgressIdentity")
            if (key in coverage or a["PolicySha256"] != policy_sha or a["SourceApprovalSha256"] != scope["approvalSha256"]
                    or a["Currency"] != "USD" or a["Scale"] != 2 or a["EstimateMinorUnits"] != 1 or a["ReservedMinorUnits"] != 1):
                raise ValueError("ContinuationProgressIdentity")
            coverage.add(key); excluded.add(a["AuthorizationId"])
            if a["Consumed"] == 0:
                _continuation_clean_held(connection, config, module, scope, parts, a, policy_sha); held.append(a["AuthorizationId"])
            else:
                rr = connection.execute("SELECT * FROM chirp_article_receipts WHERE AuthorizationId=?", (a["AuthorizationId"],)).fetchone()
                if rr is None:
                    raise ValueError("ContinuationOutputIncomplete")
                if len(rr["ReceiptJson"].encode()) > 32768:
                    raise ValueError("ContinuationOutputPin")
                receipt = json.loads(rr["ReceiptJson"], object_pairs_hook=unique)
                entry = {"planSha256": key[0], "coverageOrdinal": key[1], "authorizationId": a["AuthorizationId"],
                         "policySha256": policy_sha, "sourceApprovalSha256": scope["approvalSha256"],
                         "receiptSha256": rr["ReceiptSha256"], "audioSha256": receipt["Audio"]["AudioSha256"],
                         "executionMappingSha256": receipt.get("ExecutionMappingSha256")}
                _continuation_member(connection, config, module, profile, parts, plans, entry, historical_policy=policy)
        if len(held) > 1 or (resume_authorization is not None and held != [module.uuid_identity(resume_authorization)]):
            raise ValueError("ContinuationResumeTarget")
        # Orphan success/journal evidence is never permission to regenerate.
        for plan, ordinal in remaining:
            if (plan, ordinal) not in coverage:
                if (connection.execute("SELECT COUNT(*) FROM chirp_article_receipts WHERE PlanSha256=? AND CoverageOrdinal=?", (plan, ordinal)).fetchone()[0]
                        or connection.execute("SELECT COUNT(*) FROM chirp_article_output_journals j JOIN chirp_article_owned_attempts a ON a.AuthorizationId=j.AuthorizationId WHERE a.PlanSha256=? AND a.CoverageOrdinal=?", (plan, ordinal)).fetchone()[0]):
                    raise ValueError("ContinuationAcceptedOmitted")
        if continuation_financial_projection(connection, module, excluded) != manifest["replan" if sentence else "continuation"]["financialRowsSha256"]:
            # No automatic exclusion of legacy/recovery changes: their legitimacy
            # requires a separately reviewed production resolver, or stop safely.
            raise ValueError("ContinuationFinancialChanged")
    if sha(bounded(db, 512 * 1024 * 1024)) != before:
        raise ValueError("ContinuationDatabaseChanged")
    return scope["acceptedEvidenceSha256"] if sentence else module.digest(accepted)


def diagnostic_policy_bytes(raw, manifest, scope, module):
    policy = json.loads(raw, object_pairs_hook=unique)
    module.verify_diagnostic_policy(manifest, scope, policy, "diagnostic-admission")
    text = raw.decode("utf-8")
    grant = exact_object_property(text, "SuccessorGrant")
    articles = exact_object_property(text, "Articles").decode("utf-8")
    decoder = json.JSONDecoder(object_pairs_hook=unique)
    _, end = decoder.raw_decode(articles, 1 + len(articles[1:]) - len(articles[1:].lstrip()))
    article = articles[1:end].strip()
    d = manifest["diagnostic"]
    if sha(grant) != d["successorGrantSha256"] or sha(exact_object_property(article, "Proposal")) != d["proposalSha256"]:
        raise ValueError("DiagnosticNativePin")
    return policy


def diagnostic_admission_check(register, config, config_pin, manifest, scope, module, app, identity):
    if scope["schema"]!=6:raise ValueError("DiagnosticScope")
    if os.path.normcase(str(app))!=os.path.normcase(os.path.abspath(config["ApplicationDirectory"])):raise ValueError("InstalledReadbackRequired")
    status=register.status()
    if status["requiresReconciliation"]:raise ValueError("DiagnosticDispatch")
    with register._open() as db:state=register._read(db)
    installed_readback(config,config_pin,allow_financial=True)
    marker=json.loads(bounded(app.parent/"cloudscribe-install.json",32768),object_pairs_hook=unique)
    raw=bounded(app.parent/"chirp-article-admission-policy.json",8*1024*1024);pin=sha(raw)
    if identity!=pin or marker.get("ChirpArticlePolicySha256")!=pin or state["diagnosticPolicySha256"]!=pin:raise ValueError("DiagnosticInstalledPolicy")
    diagnostic_policy_bytes(raw,manifest,scope,module)
    module.require_epoch_policy_deadline(state,json.loads(raw,object_pairs_hook=unique)["ExpiresAtUtc"])
    return {"valid":True,"policySha256":pin,"authority":False}


def diagnostic_acceptance(config, manifest, scope, module, *, database_sha=None, policy=None, policy_sha=None, dispatch=False):
    """Read actual native ledger. No provider, reservation, writes or caller-success assertions."""
    parts, plans, accepted, _ = module.validate_diagnostic_manifest(manifest, scope)
    d = manifest["diagnostic"]
    if dispatch:
        marker = json.loads(bounded(Path(config["ApplicationDirectory"]).parent / "cloudscribe-install.json", 32768), object_pairs_hook=unique)
        profile = Path(os.path.abspath(marker["Profile"]));db = profile / "data/cloudscribe.db"
        if sha(str(profile).rstrip("/\\").upper().encode()) != config["ProfileRootSha256"]:raise ValueError("InstalledProfileBinding")
        physical = module.physical(db)
        if physical.stat().st_size > 512*1024*1024:raise ValueError("ContinuationFinancialBound")
        uri = physical.as_uri()+"?mode=ro"
    else:
        profile, db = profile_database(config)
        before = sha(bounded(db,512*1024*1024))
        if database_sha is not None and before != database_sha:raise ValueError("DiagnosticDatabasePin")
        uri = db.as_uri()+"?mode=ro&immutable=1"
    with closing(sqlite3.connect(uri,uri=True,timeout=0)) as connection:
        connection.row_factory=sqlite3.Row;connection.execute("PRAGMA query_only=ON");connection.execute("BEGIN")
        for entry in accepted:_continuation_member(connection,config,module,profile,parts,plans,entry,historical_readback=True)
        sentence_legacy_acceptance(connection,config,module,profile,manifest["history"]["legacy"])
        parent = connection.execute("SELECT * FROM chirp_article_owned_attempts WHERE AuthorizationId=?",(module.uuid_identity(d["failedAuthorizationId"]),)).fetchone()
        if parent is None:raise ValueError("DiagnosticParent")
        pq=connection.execute("SELECT * FROM google_generation_queue_states WHERE AccountId=? AND OperationStableId='synthesize-speech' AND IdempotencyKey=?",(config["AccountId"],parent["ChildKey"])).fetchone()
        if (parent["PlanSha256"]!=d["planSha256"] or parent["CoverageOrdinal"]!=d["coverageOrdinal"] or parent["Consumed"]!=1
                or parent["AccountId"]!=config["AccountId"] or parent["ProjectId"]!=config["ProjectId"]
                or parent["ClaimedLocalAttemptId"]!=module.uuid_identity(d["failedLocalAttemptId"]) or pq is None
                or pq["SubmissionAttempted"]!=1 or pq["UnresolvedSubmission"]!=0 or pq["ProviderRequestId"] is not None
                or pq["DiagnosticCode"]!='google-http-400' or pq["ErrorStatus"]!=1
                or pq["LocalAttemptId"]!=parent["ClaimedLocalAttemptId"] or pq["PayloadSha256"]!=d["payloadSha256"] or pq["PayloadBytes"]!=d["payloadBytes"]):
            raise ValueError("DiagnosticParent")
        if (connection.execute("SELECT COUNT(*) FROM chirp_article_receipts WHERE PlanSha256=? AND CoverageOrdinal=?",(d["planSha256"],d["coverageOrdinal"])).fetchone()[0]
                or connection.execute("SELECT COUNT(*) FROM chirp_article_output_journals j JOIN chirp_article_owned_attempts a ON a.AuthorizationId=j.AuthorizationId WHERE a.PlanSha256=? AND a.CoverageOrdinal=?",(d["planSha256"],d["coverageOrdinal"])).fetchone()[0]):
            raise ValueError("DiagnosticAcceptedCoverage")
        children=connection.execute("SELECT * FROM chirp_article_successor_attempts WHERE ParentAuthorizationId=?",(parent["AuthorizationId"],)).fetchall()
        child=None
        if dispatch:
            if len(children)!=1 or policy is None or policy_sha is None:raise ValueError("DiagnosticChild")
            child=children[0];grant=policy["SuccessorGrant"]
            if (child["PolicySha256"]!=policy_sha or child["SourceApprovalSha256"]!=scope["approvalSha256"]
                    or child["PlanSha256"]!=d["planSha256"] or child["CoverageOrdinal"]!=d["coverageOrdinal"]
                    or child["RootAuthorizationId"]!=module.uuid_identity(grant["RootAuthorizationId"])
                    or child["GrantId"]!=module.uuid_identity(grant["GrantId"]) or child["GrantSha256"]!=d["successorGrantSha256"]
                    or child["ParentStateSha256"]!=grant["ParentQueueRowSha256"] or child["Consumed"]!=1
                    or child["AccountId"]!=config["AccountId"] or child["ProjectId"]!=config["ProjectId"]
                    or child["Currency"]!='USD' or child["Scale"]!=2 or child["EstimateMinorUnits"]!=1 or child["ReservedMinorUnits"]!=1):
                raise ValueError("DiagnosticChild")
            module.uuid_identity(child["ClaimedLocalAttemptId"])
            q=connection.execute("SELECT * FROM google_generation_queue_states WHERE AccountId=? AND OperationStableId='synthesize-speech' AND IdempotencyKey=?",(config["AccountId"],child["ChildKey"])).fetchone()
            if (q is None or q["SubmissionAttempted"]!=1 or q["UnresolvedSubmission"]!=1 or q["ProviderRequestId"] is not None
                    or q["DiagnosticCode"]!='google-dispatch-claimed' or q["LocalAttemptId"]!=child["ClaimedLocalAttemptId"]
                    or q["PayloadSha256"]!=d["payloadSha256"] or q["PayloadBytes"]!=d["payloadBytes"]):raise ValueError("DiagnosticOwnedClaim")
            envelope=json.loads(child["EnvelopeJson"],object_pairs_hook=unique)
            if (module.uuid_identity(envelope["ChirpAuthorizationId"])!=child["AuthorizationId"] or envelope.get("RecoveryAuthorizationId") is not None
                    or envelope["CredentialReferenceId"]!=config["CredentialReferenceId"] or envelope["AccountId"]!=config["AccountId"]
                    or envelope["VoiceName"]!=config["VoiceName"] or envelope["AudioEncoding"]!='MP3'
                    or envelope["CompiledPayloadSha256"]!=d["payloadSha256"] or envelope["CompiledPayloadBytes"]!=d["payloadBytes"]):raise ValueError("DiagnosticOwnedClaim")
        elif children:raise ValueError("DiagnosticChildExists")
        for a in connection.execute("SELECT * FROM chirp_article_owned_attempts WHERE AccountId=? AND ProjectId=?",(config["AccountId"],config["ProjectId"])):
            if a["Consumed"]==0:raise ValueError("PendingOrUncertainWork")
        for q in connection.execute("SELECT * FROM google_generation_queue_states WHERE AccountId=? AND UnresolvedSubmission<>0",(config["AccountId"],)):
            if child is None or q["IdempotencyKey"]!=child["ChildKey"] or q["OperationStableId"]!='synthesize-speech':raise ValueError("PendingOrUncertainWork")
        if continuation_financial_projection(connection,module,() if child is None else (child["AuthorizationId"],))!=d["financialRowsSha256"]:
            raise ValueError("DiagnosticFinancialChanged")
    if not dispatch and sha(bounded(db,512*1024*1024))!=before:raise ValueError("ContinuationDatabaseChanged")
    return child


def interpreter_identity(transition, old, config, module):
    data = bounded(transition["InterpreterEvidencePath"], 32768)
    if sha(data) != transition["InterpreterEvidenceSha256"]:
        raise ValueError("InterpreterEvidencePin")
    evidence = json.loads(data, object_pairs_hook=unique)
    fields = {"SchemaVersion", "RepairApprovalSha256", "PreviousPythonSha256", "NewPythonSha256", "PythonPathSha256",
              "FileBytes", "PythonVersion", "PublisherName", "SignerThumbprint"}
    if (not isinstance(evidence, dict) or set(evidence) != fields or type(evidence["SchemaVersion"]) is not int or evidence["SchemaVersion"] != 1
            or evidence["RepairApprovalSha256"] != transition["RepairApprovalSha256"]
            or evidence["PreviousPythonSha256"] != transition["PreviousPythonSha256"]
            or evidence["NewPythonSha256"] != transition["NewPythonSha256"]
            or old["PythonSha256"] != evidence["PreviousPythonSha256"] or config["PythonSha256"] != evidence["NewPythonSha256"]
            or old["PythonPath"] != config["PythonPath"] or module.path_digest(config["PythonPath"]) != evidence["PythonPathSha256"]
            or sys.version_info[:2] != (3, 13) or evidence["PythonVersion"] != sys.version.split()[0]
            or evidence["PublisherName"] != "Python Software Foundation"
            or not isinstance(evidence["SignerThumbprint"], str) or re.fullmatch(r"[A-F0-9]{40}", evidence["SignerThumbprint"]) is None
            or type(evidence["FileBytes"]) is not int or not 1 <= evidence["FileBytes"] <= 32 * 1024 * 1024):
        raise ValueError("InterpreterEvidenceBinding")
    current = bounded(config["PythonPath"], 32 * 1024 * 1024)
    if len(current) != evidence["FileBytes"] or sha(current) != evidence["NewPythonSha256"]:
        raise ValueError("InterpreterHash")


def interpreter_pending_installation(config, old, transition):
    """Only an already-owned exact pending repair may reconcile these pins."""
    app = Path(config["ApplicationDirectory"])
    marker = json.loads(bounded(app.parent / "cloudscribe-install.json", 32768), object_pairs_hook=unique)
    allowed = {transition["PreviousGateSha256"], transition["NewGateSha256"]}
    if (marker.get("Product") != "CloudScribe Pro" or marker.get("CompletionGateProtocolVersion") != 1
            or marker.get("CompletionWorkflowGateSha256") not in allowed
            or str(marker.get("AppSha256", "")).lower() != config["AppSha256"]
            or any(key in marker for key in ("RecoveryPolicySha256", "ChirpArticlePolicySha256", "ChirpReservationResumePolicySha256"))
            or sha(bounded(app.parent / "completion-workflow-gate.json", 32768)) not in allowed):
        raise ValueError("InterpreterPendingReadback")
    profile = Path(marker["Profile"])
    if sha(str(profile).rstrip("/\\").upper().encode()) != config["ProfileRootSha256"]:
        raise ValueError("InterpreterPendingReadback")
    required = json.loads(bounded(profile / ".completion-workflow-required.json", 4096), object_pairs_hook=unique)
    required_scope = (transition["PreviousScopeSha256"] if transition.get("TransitionKind") == "whole-article-interpreter-continuation"
                      and required.get("GateSha256") == transition["PreviousGateSha256"] else config["ScopeSha256"])
    if (set(required) != {"GateSha256", "ScopeSha256"} or required["GateSha256"] not in allowed
            or required["ScopeSha256"] != required_scope):
        raise ValueError("InterpreterPendingReadback")
    for name, key in (("CloudScribe.dll", "AppSha256"), ("CloudScribe.Infrastructure.dll", "InfrastructureSha256")):
        if sha(bounded(app / name, 512 * 1024 * 1024)) != config[key] or config[key] != old[key]:
            raise ValueError("InterpreterPendingReadback")


def transition_materials(request, config, config_keys, register, module, scope, app, withdrawal=False):
    data = bounded(request["transitionPath"], 32768)
    pin = sha(data)
    if pin != request["transitionSha256"] or (not withdrawal and request["identitySha256"] != pin):
        raise ValueError("TransitionPin")
    transition = json.loads(data, object_pairs_hook=unique)
    module.validate_transition(transition)
    if (transition["NewGateSha256"] != request["configSha256"] or transition["NewBuildSha256"] != build_digest(config)
            or (not withdrawal and transition["TransitionId"] != request["id"]) or transition["ScopeSha256"] != config["ScopeSha256"]
            or transition["SourceApprovalSha256"] != scope["approvalSha256"]):
        raise ValueError("TransitionBinding")
    old_bytes = bounded(transition["PreviousConfigPath"], 32768)
    if sha(old_bytes) != transition["PreviousGateSha256"]:
        raise ValueError("PreviousGatePin")
    old = json.loads(old_bytes, object_pairs_hook=unique)
    interpreter = module.transition_kind(transition) == "interpreter-binding"
    source_repair = module.source_transition(transition)
    if scope["schema"] != 1 and not (scope["schema"] == 4 and interpreter):
        raise ValueError("SuccessorBuildChangeNotAdmitted")
    changes = {"ScriptPath", "ScriptSha256", "ModuleSha256"}
    if not source_repair:
        changes |= {"AppSha256", "InfrastructureSha256", "CandidateApplicationDirectory"}
    if interpreter:
        changes.add("PythonSha256")
    if (set(old) != config_keys or type(old["SchemaVersion"]) is not int or old["SchemaVersion"] != 1
            or any(old[key] != config[key] for key in config_keys - changes)
            or build_digest(old) != transition["PreviousBuildSha256"]):
        raise ValueError("TransitionConfigBinding")
    if source_repair and all(old[key] == config[key] for key in ("ScriptSha256", "ModuleSha256")):
        raise ValueError("SourceRepairMustChangeSource")
    if interpreter:
        interpreter_identity(transition, old, config, module)
        ancestor_gate = transition["PreviousGateSha256"]
        if withdrawal:
            with register._open() as db:
                ancestor_gate = register._read(db)["currentGate"]
        register.require_predecessor_retired(ancestor_gate)
        continuation_acceptance(config, json.loads(bounded(config["PlanManifestPath"], 1024 * 1024), object_pairs_hook=unique), scope, module)
    elif withdrawal:
        raise ValueError("InterpreterWithdrawalIdentity")
    # The predecessor source remains preserved; it is verified, never executed.
    if (sha(bounded(old["ScriptPath"], 131072)) != old["ScriptSha256"]
            or sha(bounded(Path(old["ScriptPath"]).with_name("completion_register.py"), 131072)) != old["ModuleSha256"]):
        raise ValueError("PreviousSourcePin")
    return transition, pin, old, interpreter


def transition_command(request, config, config_keys, register, module, scope, app):
    transition, pin, old, interpreter = transition_materials(request, config, config_keys, register, module, scope, app)
    command = request["command"]
    expected_app = config["ApplicationDirectory"] if command == "complete-build" else config["CandidateApplicationDirectory"]
    if os.path.normcase(str(app)) != os.path.normcase(os.path.abspath(expected_app)):
        raise ValueError("TransitionApplicationLocation")
    if command == "complete-build":
        evidence = installed_readback(config, request["configSha256"])
        return register.complete_build(transition, pin, request["id"], evidence)
    if interpreter and command == "check-build":
        result = register.check_build(transition, pin, request["id"])
        interpreter_pending_installation(config, old, transition)
        return dict(result, deadlineUtc=scope["endUtc"])
    installed_readback(old, transition["PreviousGateSha256"])
    if command == "inspect-build":
        return register.inspect_build(transition, pin, request["id"])
    if command == "claim-build":
        return register.claim_build(transition, pin, request["id"])
    return register.check_build(transition, pin, request["id"])


def interpreter_withdrawal_command(request, config, config_keys, register, module, scope, app):
    transition_raw = bounded(request["transitionPath"], 32768)
    transition = json.loads(transition_raw, object_pairs_hook=unique)
    if module.interpreter_handoff(transition):
        repair_pin = sha(transition_raw)
        if repair_pin != request["transitionSha256"]:
            raise ValueError("TransitionPin")
        binding_request = dict(request, handoffPath=request["transitionPath"], handoffSha256=repair_pin,
                               identitySha256=repair_pin, id=transition["TransitionId"])
        transition, _, old, _, _, _, _, _ = successor_materials(binding_request, config, config_keys, module)
        with register._open() as db:
            current_gate = register._read(db)["currentGate"]
        register.require_predecessor_retired(current_gate)
        continuation_acceptance(config, json.loads(bounded(config["PlanManifestPath"], 1024 * 1024), object_pairs_hook=unique), scope, module)
    else:
        transition, repair_pin, old, _ = transition_materials(request, config, config_keys, register, module, scope, app, withdrawal=True)
    data = bounded(request["withdrawalPath"], 32768)
    pin = sha(data)
    if pin != request["withdrawalSha256"] or pin != request["identitySha256"]:
        raise ValueError("InterpreterWithdrawalPin")
    record = json.loads(data, object_pairs_hook=unique)
    module.validate_interpreter_withdrawal(record)
    if (record["WithdrawalId"] != request["id"] or record["RepairIdentitySha256"] != repair_pin
            or record["RepairTransitionId"] != transition["TransitionId"]
            or os.path.normcase(str(app)) != os.path.normcase(os.path.abspath(config["ApplicationDirectory"]))):
        raise ValueError("InterpreterWithdrawalIdentity")
    backup = Path(record["BackupPath"])
    files = (("previous-gate.json", "completion-workflow-gate.json", record["RestoredGateSha256"]),
             ("previous-install.json", "cloudscribe-install.json", record["PreviousInstallSha256"]),
             ("previous-required.json", None, record["PreviousRequiredSha256"]))
    saved = {}
    for name, target, expected in files:
        raw = bounded(backup / name, 32768)
        if sha(raw) != expected:
            raise ValueError("InterpreterWithdrawalBackup")
        saved[name] = raw
    if saved["previous-gate.json"] != bounded(transition["PreviousConfigPath"], 32768):
        raise ValueError("InterpreterWithdrawalBackup")
    marker = json.loads(saved["previous-install.json"], object_pairs_hook=unique)
    required = json.loads(saved["previous-required.json"], object_pairs_hook=unique)
    if (marker.get("Product") != "CloudScribe Pro" or marker.get("CompletionGateProtocolVersion") != 1
            or marker.get("CompletionWorkflowGateSha256") != record["RestoredGateSha256"]
            or marker.get("AppSha256", "").lower() != config["AppSha256"]
            or sha(str(Path(marker["Profile"])).rstrip("/\\").upper().encode()) != config["ProfileRootSha256"]
            or any(key in marker for key in ("RecoveryPolicySha256", "ChirpArticlePolicySha256", "ChirpReservationResumePolicySha256"))
            or required != {"GateSha256": record["RestoredGateSha256"], "ScopeSha256": transition.get("PreviousScopeSha256", config["ScopeSha256"])}):
        raise ValueError("InterpreterWithdrawalBackup")
    interpreter_pending_installation(config, old, transition)
    command = request["command"]
    if command == "inspect-interpreter-withdrawal":
        return register.inspect_interpreter_withdrawal(record, pin, request["id"])
    if command == "claim-interpreter-withdrawal":
        return register.claim_interpreter_withdrawal(record, pin, request["id"])
    if command == "check-interpreter-withdrawal":
        return register.check_interpreter_withdrawal(record, pin, request["id"])
    for name, target, _ in files:
        path = Path(marker["Profile"]) / ".completion-workflow-required.json" if target is None else app.parent / target
        if bounded(path, 32768) != saved[name]:
            raise ValueError("InterpreterWithdrawalReadback")
    return register.complete_interpreter_withdrawal(record, pin, request["id"], module.digest({name: sha(raw) for name, raw in saved.items()}))


def successor_materials(request, config, config_keys, module):
    raw = bounded(request["handoffPath"], 32768); pin = sha(raw)
    if pin != request["handoffSha256"] or pin != request["identitySha256"]:
        raise ValueError("HandoffPin")
    h = json.loads(raw, object_pairs_hook=unique); module.validate_handoff(h)
    expected_config = (Path(config["ApplicationDirectory"]).parent / "completion-workflow-gate.json"
                       if request["command"] == "complete-successor" else Path(h["NewConfigPath"]))
    if (h["TransitionId"] != request["id"] or h["NewGateSha256"] != request["configSha256"]
            or sha(bounded(h["NewConfigPath"], 32768)) != h["NewGateSha256"]
            or os.path.normcase(os.path.abspath(expected_config)) != os.path.normcase(os.path.abspath(request["configPath"]))):
        raise ValueError("HandoffConfigPin")
    old_bytes = bounded(h["PreviousConfigPath"], 32768)
    if sha(old_bytes) != h["PreviousGateSha256"]:
        raise ValueError("PreviousGatePin")
    old = json.loads(old_bytes, object_pairs_hook=unique)
    typed_interpreter = module.interpreter_handoff(h)
    changes = {"AppSha256", "InfrastructureSha256", "CandidateApplicationDirectory", "ScriptPath", "ScriptSha256", "ModuleSha256",
               "RegisterPath", "ScopeSha256", "PlanManifestPath", "PlanManifestSha256"}
    if typed_interpreter:
        changes.add("PythonSha256")
    if (set(old) != config_keys or type(old["SchemaVersion"]) is not int or old["SchemaVersion"] != 1
            or any(old[k] != config[k] for k in config_keys - changes) or old["ScopeSha256"] != h["PreviousScopeSha256"]
            or config["ScopeSha256"] != h["NewScopeSha256"]):
        raise ValueError("HandoffScopeConfig")
    if typed_interpreter:
        if any(old[k] != config[k] for k in ("AppSha256", "InfrastructureSha256", "CandidateApplicationDirectory")):
            raise ValueError("HandoffScopeConfig")
        interpreter_identity(h, old, config, module)
    for name, field in (("gate_cli.py", "ScriptSha256"), ("completion_register.py", "ModuleSha256")):
        source = Path(old["ScriptPath"]) if name == "gate_cli.py" else Path(old["ScriptPath"]).with_name(name)
        if sha(bounded(source, 131072)) != old[field]:
            raise ValueError("PreviousSourcePin")
    new_scope = json.loads(bounded(h["NewScopePath"], 16384), object_pairs_hook=unique)
    module.validate_scope(new_scope)
    manifest_bytes = bounded(config["PlanManifestPath"], 1024 * 1024)
    manifest = json.loads(manifest_bytes, object_pairs_hook=unique)
    bindings = {"buildSha256": build_digest(config), "profileSha256": config["ProfileRootSha256"],
                "accountSha256": sha(config["AccountId"].encode()), "projectSha256": sha(config["ProjectId"].encode()),
                "plansSha256": module.digest(manifest)}
    if (sha(manifest_bytes) != config["PlanManifestSha256"] or module.digest(new_scope) != config["ScopeSha256"]
            or new_scope["schema"] != (6 if module.diagnostic_handoff(h) else h["SchemaVersion"] + 1)
            or new_scope["approvalSha256"] != h["SourceApprovalSha256"]
            or any(new_scope[k] != value for k, value in bindings.items())):
        raise ValueError("HandoffNewScopeBinding")
    sentence = module.sentence_handoff(h)
    continuation = h["SchemaVersion"] in (2, 3, 4)
    if module.diagnostic_handoff(h):
        module.validate_diagnostic_manifest(manifest, new_scope)
        original = {k:v for k,v in manifest.items() if k != "diagnostic"}
        old_manifest = json.loads(bounded(old["PlanManifestPath"],1024*1024),object_pairs_hook=unique)
        d=manifest["diagnostic"]
        pins={"FailedAuthorizationId":d["failedAuthorizationId"],"FailedLocalAttemptId":d["failedLocalAttemptId"],"FailedPlanSha256":d["planSha256"],"FailedCoverageOrdinal":d["coverageOrdinal"],"FailedPayloadSha256":d["payloadSha256"],"FailedPayloadBytes":d["payloadBytes"]}
        if (sha(bounded(old["PlanManifestPath"],1024*1024))!=old["PlanManifestSha256"] or module.digest(old_manifest)!=h["OriginalManifestSha256"]
                or module.canonical(original)!=module.canonical(old_manifest) or any(h[k]!=v for k,v in pins.items())
                or h["AcceptedEvidenceSha256"]!=new_scope["acceptedEvidenceSha256"]):raise ValueError("DiagnosticHistory")
    elif continuation:
        module.validate_continuation_manifest(manifest, new_scope)
        if sentence and (h["OriginalManifestSha256"] != manifest["replan"]["originalManifestSha256"]
                or any(h[k] != v for k,v in manifest["replan"]["failure"].items())):
            raise ValueError("SentenceFailure")
        if ((h["SchemaVersion"] == 2 and h["OriginalManifestSha256"] != manifest["continuation"]["originalManifestSha256"])
                or h["AcceptedEvidenceSha256"] != new_scope["acceptedEvidenceSha256"]):
            raise ValueError("ContinuationHandoffBinding")
        old_manifest_bytes = bounded(old["PlanManifestPath"], 1024 * 1024)
        if (sha(old_manifest_bytes) != old["PlanManifestSha256"]
                or module.digest(json.loads(old_manifest_bytes, object_pairs_hook=unique)) != h["OriginalManifestSha256"]):
            raise ValueError("ContinuationOriginalManifest")
        if h["SchemaVersion"] == 3:
            previous_manifest = json.loads(old_manifest_bytes, object_pairs_hook=unique)
            previous_original = {k: v for k, v in previous_manifest.items() if k != "continuation"}
            current_original = {k: v for k, v in manifest.items() if k != "continuation"}
            before_accepted = previous_manifest["continuation"]["accepted"]
            after_accepted = manifest["continuation"]["accepted"]
            before_budget = previous_manifest["continuation"]["budget"]
            after_budget = manifest["continuation"]["budget"]
            if (module.canonical(previous_original) != module.canonical(current_original)
                    or any(entry not in after_accepted for entry in before_accepted)
                    or any(after_budget[k] < before_budget[k] for k in ("projectCommittedMinorUnits", "allocationCommittedMinorUnits"))):
                raise ValueError("WholeArticleHistoryBinding")
    elif not module.diagnostic_handoff(h):
        module.validate_successor_manifest(manifest, new_scope)
    old_r = module.Register(old["RegisterPath"], old["ScopeSha256"])
    new_r = module.Register(config["RegisterPath"], config["ScopeSha256"])
    return h, pin, old, new_scope, manifest, continuation, old_r, new_r


def successor_command(request, config, config_keys, module, app):
    h, pin, old, new_scope, manifest, continuation, old_r, new_r = successor_materials(request, config, config_keys, module)
    typed_interpreter = module.interpreter_handoff(h)
    command = request["command"]
    expected_app = config["ApplicationDirectory"] if command == "complete-successor" else config["CandidateApplicationDirectory"]
    if os.path.normcase(str(app)) != os.path.normcase(os.path.abspath(expected_app)):
        raise ValueError("HandoffApplicationLocation")
    if command == "complete-successor":
        evidence = installed_readback(config, request["configSha256"])
        if module.diagnostic_handoff(h):
            diagnostic_acceptance(config,manifest,new_scope,module,database_sha=h["DatabaseSha256"])
        elif continuation:
            continuation_acceptance(config, manifest, new_scope, module, h["DatabaseSha256"])
            if module.sentence_handoff(h): terminal_failure(config,h)
        else:
            terminal_failure(config, h)
        return old_r.complete_successor(new_r, new_scope, h, pin, evidence)
    installed_readback(old, h["PreviousGateSha256"], allow_financial=not typed_interpreter)
    if module.diagnostic_handoff(h):
        diagnostic_acceptance(old,manifest,new_scope,module,database_sha=h["DatabaseSha256"])
    elif continuation:
        continuation_acceptance(old, manifest, new_scope, module, h["DatabaseSha256"])
        if module.sentence_handoff(h): terminal_failure(old,h)
    else:
        terminal_failure(old, h)
    if command == "inspect-successor":
        return old_r.inspect_successor(new_r, new_scope, h, pin)
    if command == "claim-successor":
        return old_r.claim_successor(new_r, new_scope, h, pin)
    return old_r.check_successor(new_r, new_scope, h, pin)


def main():
    if not sys.flags.isolated or not sys.flags.no_site:
        raise ValueError("IsolatedNoSiteInterpreterRequired")
    request_bytes = sys.stdin.buffer.read(8193)
    if len(request_bytes) > 8192:
        raise ValueError("InputBound")
    request = json.loads(request_bytes, object_pairs_hook=unique)
    if set(request) - {"command", "configPath", "configSha256", "appDirectory", "id", "identitySha256", "receipt", "policyPath", "transitionPath", "transitionSha256", "handoffPath", "handoffSha256", "withdrawalPath", "withdrawalSha256", "payloadSha256"}:
        raise ValueError("RequestShape")
    if "payloadSha256" in request and request["command"] != "check-diagnostic-dispatch":raise ValueError("RequestShape")
    config_bytes = bounded(request["configPath"], 32768)
    if sha(config_bytes) != request["configSha256"]:
        raise ValueError("GatePin")
    config = json.loads(config_bytes, object_pairs_hook=unique)
    expected = {"SchemaVersion", "PythonPath", "PythonSha256", "ScriptPath", "ScriptSha256", "ModuleSha256",
                "RegisterPath", "ScopeSha256", "ApplicationDirectory", "CandidateApplicationDirectory",
                "AppSha256", "InfrastructureSha256", "ProfileRootSha256", "AccountId", "ProjectId",
                "CredentialReferenceId", "CatalogEndpoint", "SynthesisEndpoint", "PlanManifestPath", "PlanManifestSha256", "VoiceName", "LanguageCode"}
    if set(config) != expected or type(config["SchemaVersion"]) is not int or config["SchemaVersion"] != 1:
        raise ValueError("GateSchema")
    if os.path.normcase(os.path.abspath(__file__)) != os.path.normcase(os.path.abspath(config["ScriptPath"])):
        raise ValueError("ScriptLocation")
    if sha(bounded(__file__, 131072)) != config["ScriptSha256"]:
        raise ValueError("ScriptHash")
    if os.path.normcase(os.path.abspath(sys.executable)) != os.path.normcase(os.path.abspath(config["PythonPath"])):
        raise ValueError("InterpreterLocation")
    if sha(bounded(sys.executable, 32 * 1024 * 1024)) != config["PythonSha256"]:
        raise ValueError("InterpreterHash")
    module_path = Path(__file__).parent / "completion_register.py"
    module_bytes = bounded(module_path, 131072)
    if sha(module_bytes) != config["ModuleSha256"]:
        raise ValueError("ModuleHash")
    # Execute exactly hash-verified source, never sys.path/PYTHONPATH/.pyc imports.
    module = types.ModuleType("verified_completion_register")
    exec(compile(module_bytes, str(module_path), "exec"), module.__dict__)
    app = Path(os.path.abspath(request["appDirectory"]))
    allowed_apps = [os.path.normcase(os.path.abspath(config[key])) for key in ("ApplicationDirectory", "CandidateApplicationDirectory")]
    if os.path.normcase(str(app)) not in allowed_apps:
        raise ValueError("ApplicationLocation")
    for file, pin in (("CloudScribe.dll", "AppSha256"), ("CloudScribe.Infrastructure.dll", "InfrastructureSha256")):
        if sha(bounded(app / file, 512 * 1024 * 1024)) != config[pin]:
            raise ValueError("BuildBinding")
    if request["command"] in ("inspect-successor", "claim-successor", "check-successor", "complete-successor"):
        result = successor_command(request, config, expected, module, app)
        print(json.dumps(result, separators=(",", ":")))
        return
    withdrawal_command = request["command"] in ("inspect-interpreter-withdrawal", "claim-interpreter-withdrawal", "check-interpreter-withdrawal", "complete-interpreter-withdrawal")
    build_command = withdrawal_command or request["command"] in ("inspect-build", "claim-build", "check-build", "complete-build")
    register = module.Register(config["RegisterPath"], config["ScopeSha256"],
                               expected_build=None if build_command else build_digest(config),
                               expected_gate=None if build_command else request["configSha256"])
    with register._open() as db:
        state = register._read(db)
        scope = state["scope"]
    expected_bindings = {
        "profileSha256": config["ProfileRootSha256"],
        "accountSha256": sha(config["AccountId"].encode()), "projectSha256": sha(config["ProjectId"].encode())}
    if any(scope[key] != value for key, value in expected_bindings.items()):
        raise ValueError("ScopeBindings")
    if not build_command and (state["currentBuild"] != build_digest(config)
                              or (state["currentGate"] is not None and state["currentGate"] != request["configSha256"])):
        raise ValueError("CurrentBuildBinding")
    if scope["schema"] in (2, 3, 4, 5, 6):
        if build_command and scope["schema"] != 4:
            raise ValueError("SuccessorBuildChangeNotAdmitted")
        if not build_command:
            register.require_predecessor_retired(request["configSha256"])
    manifest_bytes = bounded(config["PlanManifestPath"], 1024 * 1024)
    if sha(manifest_bytes) != config["PlanManifestSha256"]:
        raise ValueError("PlanManifestHash")
    manifest = json.loads(manifest_bytes, object_pairs_hook=unique)
    if module.digest(manifest) != scope["plansSha256"]:
        raise ValueError("PlanManifestPin")
    if not manifest["parts"] or any(part["voiceName"] != config["VoiceName"] or part["languageCode"] != config["LanguageCode"] for part in manifest["parts"]):
        raise ValueError("VoiceManifestBinding")
    command = request["command"]
    if command == "check-diagnostic-admission":
        if uuid.UUID(request["id"]).int!=0 or request.get("receipt") is not None:raise ValueError("DiagnosticPolicy")
        module.hash_value(request["identitySha256"])
        result=diagnostic_admission_check(register,config,request["configSha256"],manifest,scope,module,app,request["identitySha256"])
    elif command == "check-diagnostic-dispatch":
        if request.get("payloadSha256") != request["identitySha256"]:raise ValueError("DiagnosticPayload")
        module.hash_value(request["payloadSha256"]);module.uuid_identity(request["id"])
        if scope["schema"] != 6:
            register.status();result={"valid":True,"id":request["id"],"payloadSha256":request["payloadSha256"],"requireProtectedCapture":False,"idempotencyKeySha256":None}
        else:
            if os.path.normcase(str(app))!=os.path.normcase(os.path.abspath(config["ApplicationDirectory"])):raise ValueError("InstalledReadbackRequired")
            installed_readback(config,request["configSha256"],allow_financial=True)
            marker=json.loads(bounded(app.parent/"cloudscribe-install.json",32768),object_pairs_hook=unique)
            raw=bounded(app.parent/"chirp-article-admission-policy.json",8*1024*1024);pin=sha(raw)
            if marker.get("ChirpArticlePolicySha256")!=pin or state["diagnosticPolicySha256"]!=pin:raise ValueError("DiagnosticInstalledPolicy")
            policy=diagnostic_policy_bytes(raw,manifest,scope,module);module.require_epoch_policy_deadline(state,policy["ExpiresAtUtc"])
            if request["payloadSha256"]!=manifest["diagnostic"]["payloadSha256"]:raise ValueError("DiagnosticPayload")
            child=diagnostic_acceptance(config,manifest,scope,module,policy=policy,policy_sha=pin,dispatch=True)
            result=register.diagnostic_dispatch(request["id"],pin,request["payloadSha256"],child["AuthorizationId"],child["ClaimedLocalAttemptId"])
            result.update(requireProtectedCapture=True,idempotencyKeySha256=sha(child["ChildKey"].encode("utf-8")))
    elif withdrawal_command:
        result = interpreter_withdrawal_command(request, config, expected, register, module, scope, app)
    elif build_command:
        result = transition_command(request, config, expected, register, module, scope, app)
    elif command == "inspect":
        with register._open() as db:
            state = register._read(db)
            now = register.clock()
            if state["stopped"] or state["pending"] or register._time_rejection(state):
                raise ValueError("InspectionNotReady")
        result = {"valid": True, "deadlineUtc": module.effective_deadline(scope,state.get("clockEpoch"))}
    elif command == "status":
        result = register.status()
    elif command == "verify-completion":
        policy_bytes = bounded(request["policyPath"], 8 * 1024 * 1024)
        if sha(policy_bytes) != request["identitySha256"]:
            raise ValueError("PolicyHash")
        policy = json.loads(policy_bytes, object_pairs_hook=unique)
        verified_kind = module.successor_policy_kind(manifest, policy) if scope["schema"] in (2, 3, 4, 5, 6) else "completion-admission"
        module.verify_plan_policy(manifest, scope, policy, verified_kind)
        if scope["schema"]==6:diagnostic_policy_bytes(policy_bytes,manifest,scope,module)
        module.require_epoch_policy_deadline(state, policy["ExpiresAtUtc"])
        if scope["schema"] in (3, 4, 5):
            retained = retained_continuation_policy(config, manifest, scope, module, register, request["identitySha256"])
            continuation_acceptance(config, manifest, scope, module, retained=retained, pilot_policy_sha=state["pilotPolicySha256"])
        with register._open() as db:
            state = register._read(db)
            events = [json.loads(row[0]) for row in db.execute("SELECT body FROM events ORDER BY seq")]
            claims = {event["id"] for event in events if event["type"] == "claim" and event["kind"] == verified_kind and event["identity"] == request["identitySha256"]}
            verified = any(event["type"] == "complete" and event["id"] in claims and event["outcome"] == "Succeeded" for event in events)
            if not verified or state["stopped"] or state["pending"] or register._time_rejection(state):
                raise ValueError("ExistingCompletionNotVerified")
        result = {"valid": True}
    elif command in ("claim-refresh", "claim-resume", "claim-completion"):
        kind = {"claim-refresh": "refresh", "claim-resume": "resume-admission", "claim-completion": "completion-admission"}[command]
        if kind != "refresh":
            policy_bytes = bounded(request["policyPath"], 8 * 1024 * 1024)
            if sha(policy_bytes) != request["identitySha256"]:
                raise ValueError("PolicyHash")
            policy = json.loads(policy_bytes, object_pairs_hook=unique)
            if kind == "completion-admission" and scope["schema"] in (2, 3, 4, 5, 6):
                kind = module.successor_policy_kind(manifest, policy)
            module.verify_plan_policy(manifest, scope, policy, kind)
            if scope["schema"]==6:
                diagnostic_policy_bytes(policy_bytes,manifest,scope,module)
                diagnostic_acceptance(config,manifest,scope,module)
            module.require_epoch_policy_deadline(state, policy["ExpiresAtUtc"])
        pilot_proof = pilot_acceptance(config, manifest, state, module) if scope["schema"] == 2 and kind == "completion-admission" else None
        if scope["schema"] == 5 and kind == "completion-admission":
            pilot_proof = sentence_stage_acceptance(config,manifest,scope,module,state)
        accepted_proof = None
        if scope["schema"] in (3, 4, 5) and kind != "refresh":
            retained = None; target = None
            if kind == "resume-admission":
                retained = retained_continuation_policy(config, manifest, scope, module, register)
                if module.canonical(policy["HistoricalPolicy"]) != module.canonical(retained[0]):
                    raise ValueError("ContinuationRetainedPolicyPin")
                target = policy["AuthorizationId"]
            accepted_proof = continuation_acceptance(config, manifest, scope, module, retained=retained, resume_authorization=target, pilot_policy_sha=state["pilotPolicySha256"])
        result = register.claim(kind,
                                request["identitySha256"], request["id"], pilot_proof_sha=pilot_proof, accepted_proof_sha=accepted_proof)
    elif command == "check":
        if scope["schema"]==6:diagnostic_acceptance(config,manifest,scope,module)
        if scope["schema"] in (3, 4, 5):
            with register._open() as db:
                current = register._read(db); pending = current["pending"]
            if pending and pending["kind"] != "refresh":
                retained = retained_continuation_policy(config, manifest, scope, module, register) if pending["kind"] == "resume-admission" else None
                continuation_acceptance(config, manifest, scope, module, retained=retained, pilot_policy_sha=current["pilotPolicySha256"])
        result = {"valid": register.require_pending_before_effect(request["id"], request["identitySha256"])}
    elif command == "complete":
        receipt = request["receipt"]
        with register._open() as db:
            current = register._read(db); pending = current["pending"]
        if pending and pending["kind"] != "refresh":
            if os.path.normcase(str(app)) != os.path.normcase(os.path.abspath(config["ApplicationDirectory"])):
                raise ValueError("InstalledReadbackRequired")
            marker_bytes = bounded(app.parent / "cloudscribe-install.json", 32768)
            marker = json.loads(marker_bytes, object_pairs_hook=unique)
            if marker["Product"] != "CloudScribe Pro" or marker.get("CompletionGateProtocolVersion") != 1 or marker["CompletionWorkflowGateSha256"] != request["configSha256"]:
                raise ValueError("InstalledGatePin")
            profile = Path(os.path.abspath(marker["Profile"]))
            if sha(str(profile).rstrip("/\\").upper().encode()) != config["ProfileRootSha256"]:
                raise ValueError("InstalledProfileBinding")
            required = json.loads(bounded(profile / ".completion-workflow-required.json", 4096), object_pairs_hook=unique)
            if required != {"GateSha256": request["configSha256"], "ScopeSha256": config["ScopeSha256"]}:
                raise ValueError("RequiredGatePin")
            resume = pending["kind"] == "resume-admission"
            policy_bytes = bounded(app.parent / ("chirp-reservation-resume-admission.json" if resume else "chirp-article-admission-policy.json"), 8 * 1024 * 1024)
            pin = marker["ChirpReservationResumePolicySha256" if resume else "ChirpArticlePolicySha256"]
            if sha(policy_bytes) != pending["identity"] or not isinstance(pin, str) or pin.lower() != pending["identity"]:
                raise ValueError("InstalledPolicyPin")
            policy = json.loads(policy_bytes, object_pairs_hook=unique)
            module.verify_plan_policy(manifest, scope, policy, pending["kind"])
            if scope["schema"]==6:
                diagnostic_policy_bytes(policy_bytes,manifest,scope,module)
                diagnostic_acceptance(config,manifest,scope,module)
            module.require_epoch_policy_deadline(current, policy["ExpiresAtUtc"])
            if scope["schema"] in (3, 4, 5):
                retained = None; target = None
                if resume:
                    retained = retained_continuation_policy(config, manifest, scope, module, register)
                    if module.canonical(policy["HistoricalPolicy"]) != module.canonical(retained[0]):
                        raise ValueError("ContinuationRetainedPolicyPin")
                    target = policy["AuthorizationId"]
                continuation_acceptance(config, manifest, scope, module, retained=retained, resume_authorization=target, pilot_policy_sha=current["pilotPolicySha256"])
            receipt = {"id": request["id"], "identitySha256": pending["identity"], "outcome": "Succeeded",
                       "evidenceSha256": sha(marker_bytes), "policyVerified": True}
            if resume:
                receipt["zeroNewFunds"] = True  # Derived from exact policy validation, never a caller assertion.
            else:
                receipt["maximumNewAttempts"] = policy["MaximumNewAttempts"]
                receipt["additionalReservationCents"] = policy["MaximumAdditionalReservationMinorUnits"]
        result = {"outcome": register.complete(request["id"], receipt)}
        if result["outcome"] != "Succeeded":
            with register._open() as db:
                register._read(db)
                reason = json.loads(db.execute("SELECT body FROM events ORDER BY seq DESC LIMIT 1").fetchone()[0])["reason"]
            result["reason"] = {"WindowExpired": "Expired", "ClockRollback": "ClockRollback", "ClockDiscontinuity": "ClockDiscontinuity",
                                "ObservedFailure": "Stopped", "ReceiptValidation": "Protocol"}.get(reason, "Unknown")
    else:
        raise ValueError("UnknownCommand")
    print(json.dumps(result, separators=(",", ":")))


if __name__ == "__main__":
    try:
        main()
    except BaseException as error:
        # No exception prose/paths/provider messages/raw stdin ever reaches logs.
        reasons = {"WindowExpired": "Expired", "OutsideWindow": "Expired", "ClockRollback": "ClockRollback",
                   "ClockDiscontinuity": "ClockDiscontinuity",
                   "JournalClockRollback": "ClockRollback", "PendingOutcomeRequiresReconciliation": "PendingOutcome",
                   "OperationLimit": "Limit", "RunStopped": "Stopped", "InspectionNotReady": "Stopped",
                   "ScopePinMismatch": "PinMismatch", "GatePin": "PinMismatch", "BuildBinding": "PinMismatch",
                   "ModuleHash": "PinMismatch", "ScriptHash": "PinMismatch", "InterpreterHash": "PinMismatch",
                   "AnchorDatabaseMismatch": "Storage", "AnchorUnavailable": "Storage", "RegisterUnavailableOrOversized": "Storage"}
        reasons.update({"ReceiptSchema": "Protocol", "ReceiptMismatchOrReplay": "Protocol", "PlanManifestPin": "PinMismatch",
                        "ExactProposalBinding": "PinMismatch", "ExactRemainingCoverage": "PinMismatch", "PolicyWindow": "Expired"})
        reasons.update({"SuccessorHeadMismatch": "SuccessorHeadMismatch", "SuccessorPending": "SuccessorPending",
                        "TerminalDatabasePin": "SuccessorDatabaseMismatch", "TerminalDatabaseChanged": "SuccessorDatabaseMismatch",
                        "SuccessorNotActivated": "SuccessorNotActivated", "PredecessorNotRetired": "SuccessorNotActivated", "RunRetired": "RunRetired",
                        "OutsideWindow": "Expired", "PilotNotAccepted": "PilotEvidenceMissing", "PilotAttempt": "PilotEvidenceMissing",
                        "PilotOutputIncomplete": "PilotEvidenceMissing", "PilotStoredPlan": "PilotEvidenceMissing",
                        "PilotPolicyPin": "PilotEvidenceMismatch", "PilotAttemptIdentity": "PilotEvidenceMismatch",
                        "PilotOutputPin": "PilotEvidenceMismatch", "PilotAcceptedIdentity": "PilotEvidenceMismatch",
                        "PilotMediaLocation": "PilotEvidenceMismatch", "PilotMediaPin": "PilotEvidenceMismatch",
                        "PilotMappingAbsent": "PilotEvidenceMismatch", "PilotMappingBound": "PilotEvidenceMismatch",
                        "PilotMappingChain": "PilotEvidenceMismatch", "PilotMappingIdentity": "PilotEvidenceMismatch",
                        "PilotMappingDispatch": "PilotEvidenceMismatch", "PilotMappingReceipt": "PilotEvidenceMismatch"})
        for code in ("HandoffSchema", "HandoffId", "HandoffBinding", "HandoffPath", "HandoffPin", "HandoffConfigPin",
                     "HandoffScopeConfig", "HandoffNewScopeBinding", "HandoffApplicationLocation", "SuccessorScopeBinding",
                     "PredecessorPath", "SuccessorTransitionId", "SuccessorManifest", "SuccessorCoverage", "SuccessorPolicyBinding",
                     "SuccessorPilotOrBudget", "SuccessorAdmissionKind", "SuccessorBuildChangeNotAdmitted", "SuccessorActivationReplay"):
            reasons[code] = "SuccessorScopeMismatch"
        for code in ("ClosedDatabaseRequired", "TerminalAttempt", "TerminalQueue", "TerminalFailureEvidence",
                     "AcceptedWorkCannotRetire", "RecoverableOutputCannotRetire", "PendingOrUncertainWork",
                     "RetirementIdentityMismatch", "SuccessorAlreadyExists", "SuccessorAlreadyActivated"):
            reasons[code] = "SuccessorPending"
        reasons.update({"RetirementClockOrStop": "Stopped", "PreviousGatePin": "PinMismatch",
                        "PreviousSourcePin": "PinMismatch", "OverlappingRunPaths": "SuccessorScopeMismatch"})
        for code in ("ContinuationAcceptedAttempt", "ContinuationAcceptedBounds", "ContinuationAcceptedDuplicateOrUnknown",
                     "ContinuationAcceptedIdentity", "ContinuationAcceptedOmitted", "ContinuationAcceptedOrChangedCoverage",
                     "ContinuationAcceptedPin", "ContinuationAcceptedProof", "ContinuationAcceptedProvenance", "ContinuationAcceptedSchema",
                     "ContinuationAdmissionKind", "ContinuationAllInBudget", "ContinuationAttemptLimit", "ContinuationBudgetSchema",
                     "ContinuationBudgetType", "ContinuationDatabaseChanged", "ContinuationDatabasePin", "ContinuationEnvelopeBound",
                     "ContinuationExactBudgetOrCoverage", "ContinuationFinancialChanged", "ContinuationHandoffBinding",
                     "ContinuationHeldNotClean", "ContinuationHistoricalPolicy", "ContinuationManifest", "ContinuationMappingBound",
                     "ContinuationMappingPin", "ContinuationMediaLocation", "ContinuationMediaPin", "ContinuationOriginalManifest",
                     "ContinuationOutputIncomplete", "ContinuationOutputPin", "ContinuationPolicyBinding", "ContinuationProgressIdentity",
                     "ContinuationRemainingCount", "ContinuationRemainingNotUntouched", "ContinuationResumeTarget",
                     "ContinuationRetainedPolicyPin", "ContinuationRetentionNotAdmitted", "ContinuationStoredPlan"):
            reasons[code] = "PilotEvidenceMismatch"
        for code in ("ContinuationDatabaseChanged", "ContinuationDatabasePin", "ContinuationFinancialChanged", "ContinuationFinancialBound"):
            reasons[code] = "SuccessorDatabaseMismatch"
        for code in ("ContinuationHeldNotClean", "ContinuationOutputIncomplete", "ContinuationRemainingNotUntouched", "ContinuationResumeTarget"):
            reasons[code] = "SuccessorPending"
        for code in ("ContinuationAdmissionKind", "ContinuationAllInBudget", "ContinuationAttemptLimit", "ContinuationBudgetSchema",
                     "ContinuationBudgetType", "ContinuationExactBudgetOrCoverage", "ContinuationHandoffBinding", "ContinuationManifest",
                     "ContinuationOriginalManifest", "ContinuationPolicyBinding", "ContinuationRemainingCount", "ContinuationRetentionNotAdmitted"):
            reasons[code] = "SuccessorScopeMismatch"
        reasons["ContinuationRetainedPolicyPin"] = "PinMismatch"
        reasons.update({"WholeArticleGrantBinding": "SuccessorScopeMismatch",
                        "WholeArticleHistoryBinding": "SuccessorScopeMismatch",
                        "WholeArticlePredecessorNotExpired": "SuccessorPending"})
        reasons.update({"InterpreterTransitionSchema": "Protocol", "InterpreterTransitionBinding": "PinMismatch",
                        "InterpreterEvidencePin": "PinMismatch", "InterpreterEvidenceBinding": "PinMismatch",
                        "InterpreterPendingReadback": "PinMismatch"})
        reasons.update({"InterpreterWithdrawalSchema": "Protocol", "InterpreterWithdrawalIdentity": "PinMismatch",
                        "InterpreterWithdrawalPin": "PinMismatch", "InterpreterWithdrawalBackup": "PinMismatch",
                        "InterpreterWithdrawalReadback": "PinMismatch", "InterpreterWithdrawalState": "Stopped",
                        "InterpreterWithdrawalClock": "ClockRollback"})
        for code in ("SentenceManifest", "SentenceManifestBounds", "SentenceProposal", "SentenceHistory", "SentenceCoverage",
                     "SentenceRemainingCount", "SentenceFirstStage", "SentenceLegacy", "SentenceFailure", "SentenceAdmissionKind",
                     "SentencePolicyBinding", "SentenceReuse", "SentenceCounterBinding", "SentenceCounterOrWindowBinding"):
            reasons[code] = "SuccessorScopeMismatch"
        reasons["SentenceLegacyPin"] = "PilotEvidenceMismatch"
        reasons.update({"ClockEpochEvidence":"ClockRollback","ClockEpochPolicyDeadline":"Expired",
                        "FreshWindowApprovalRequired":"PinMismatch","ExpiredWindowBinding":"PinMismatch",
                        "ExpiredPredecessorRequired":"SuccessorPending"})
        for code in ("DiagnosticScope","DiagnosticManifest","DiagnosticCoverageOrBudget","DiagnosticPolicy","DiagnosticBudget","DiagnosticNativePin","DiagnosticParent","DiagnosticAcceptedCoverage","DiagnosticChild","DiagnosticChildExists","DiagnosticOwnedClaim","DiagnosticFinancialChanged","DiagnosticHistory","DiagnosticPayload","DiagnosticInstalledPolicy","DiagnosticOperation","DiagnosticDispatch","DiagnosticDispatchReplay","DiagnosticCounterOrEpoch","DiagnosticWindow"):
            reasons[code]="SuccessorScopeMismatch"
        reasons["DiagnosticDatabasePin"]="SuccessorDatabaseMismatch"
        reasons["TransitionFinancialAdmissionExists"]="SuccessorScopeMismatch"
        print(json.dumps(rejection_packet(error,reasons)))
        sys.exit(1)
