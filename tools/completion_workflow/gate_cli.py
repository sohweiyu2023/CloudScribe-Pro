"""Pinned private workflow bridge. -I -S; no user modules, UI or provider requests."""
import hashlib
from contextlib import closing
import json
import os
from pathlib import Path
import sqlite3
import sys
import types
import uuid


def sha(data):
    return hashlib.sha256(data).hexdigest()


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


def verify_pilot_mapping(connection, attempt, envelope, receipt, config, historical):
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
                or p["AppSha256"] != config["AppSha256"] or p["InfrastructureSha256"] != config["InfrastructureSha256"]
                or p["MaximumExistingDispatches"] != 1 or p["MaximumNewReservations"] != 0 or p["MaximumAdditionalReservationMinorUnits"] != 0):
            raise ValueError("PilotMappingIdentity")
        approvals.add(p["SourceApprovalSha256"]); previous = row["PolicySha256"]
        last_created = row["CreatedAtUnixMilliseconds"]
        d = dispatch[0]
        if d["MappingId"] == row["MappingId"]:
            from datetime import datetime
            expiry = datetime.fromisoformat(p["ExpiresAtUtc"].replace("Z", "+00:00")).timestamp() * 1000
            catalog_expiry = datetime.fromisoformat(p["CatalogExpiresAtUtc"].replace("Z", "+00:00")).timestamp() * 1000
            if (d["PolicySha256"] != row["PolicySha256"] or d["ClaimedLocalAttemptId"] != attempt["ClaimedLocalAttemptId"]
                    or not row["CreatedAtUnixMilliseconds"] <= d["ClaimedAtUnixMilliseconds"] < min(expiry, catalog_expiry)):
                raise ValueError("PilotMappingDispatch")
            used = row["PolicySha256"]
    if used is None or used != previous or used != receipt.get("ExecutionMappingSha256"):
        raise ValueError("PilotMappingReceipt")
    return used


def transition_command(request, config, config_keys, register, module, scope, app):
    data = bounded(request["transitionPath"], 32768)
    pin = sha(data)
    if pin != request["transitionSha256"] or request["identitySha256"] != pin:
        raise ValueError("TransitionPin")
    transition = json.loads(data, object_pairs_hook=unique)
    module.validate_transition(transition)
    if (transition["NewGateSha256"] != request["configSha256"] or transition["NewBuildSha256"] != build_digest(config)
            or transition["TransitionId"] != request["id"] or transition["ScopeSha256"] != config["ScopeSha256"]
            or transition["SourceApprovalSha256"] != scope["approvalSha256"]):
        raise ValueError("TransitionBinding")
    old_bytes = bounded(transition["PreviousConfigPath"], 32768)
    if sha(old_bytes) != transition["PreviousGateSha256"]:
        raise ValueError("PreviousGatePin")
    old = json.loads(old_bytes, object_pairs_hook=unique)
    source_repair = module.transition_kind(transition) == "helper-source"
    changes = {"ScriptPath", "ScriptSha256", "ModuleSha256"}
    if not source_repair:
        changes |= {"AppSha256", "InfrastructureSha256", "CandidateApplicationDirectory"}
    if (set(old) != config_keys or type(old["SchemaVersion"]) is not int or old["SchemaVersion"] != 1
            or any(old[key] != config[key] for key in config_keys - changes)
            or build_digest(old) != transition["PreviousBuildSha256"]):
        raise ValueError("TransitionConfigBinding")
    if source_repair and all(old[key] == config[key] for key in ("ScriptSha256", "ModuleSha256")):
        raise ValueError("SourceRepairMustChangeSource")
    # The predecessor source remains preserved; it is verified, never executed.
    if (sha(bounded(old["ScriptPath"], 131072)) != old["ScriptSha256"]
            or sha(bounded(Path(old["ScriptPath"]).with_name("completion_register.py"), 131072)) != old["ModuleSha256"]):
        raise ValueError("PreviousSourcePin")
    command = request["command"]
    expected_app = config["ApplicationDirectory"] if command == "complete-build" else config["CandidateApplicationDirectory"]
    if os.path.normcase(str(app)) != os.path.normcase(os.path.abspath(expected_app)):
        raise ValueError("TransitionApplicationLocation")
    if command == "complete-build":
        evidence = installed_readback(config, request["configSha256"])
        return register.complete_build(transition, pin, request["id"], evidence)
    installed_readback(old, transition["PreviousGateSha256"])
    if command == "inspect-build":
        return register.inspect_build(transition, pin, request["id"])
    if command == "claim-build":
        return register.claim_build(transition, pin, request["id"])
    return register.check_build(transition, pin, request["id"])


def successor_command(request, config, config_keys, module, app):
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
    changes = {"AppSha256", "InfrastructureSha256", "CandidateApplicationDirectory", "ScriptPath", "ScriptSha256", "ModuleSha256",
               "RegisterPath", "ScopeSha256", "PlanManifestPath", "PlanManifestSha256"}
    if (set(old) != config_keys or type(old["SchemaVersion"]) is not int or old["SchemaVersion"] != 1
            or any(old[k] != config[k] for k in config_keys - changes) or old["ScopeSha256"] != h["PreviousScopeSha256"]
            or config["ScopeSha256"] != h["NewScopeSha256"]):
        raise ValueError("HandoffScopeConfig")
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
            or new_scope["schema"] != 2 or new_scope["approvalSha256"] != h["SourceApprovalSha256"]
            or any(new_scope[k] != value for k, value in bindings.items())):
        raise ValueError("HandoffNewScopeBinding")
    module.validate_successor_manifest(manifest, new_scope)
    old_r = module.Register(old["RegisterPath"], old["ScopeSha256"])
    new_r = module.Register(config["RegisterPath"], config["ScopeSha256"])
    command = request["command"]
    expected_app = config["ApplicationDirectory"] if command == "complete-successor" else config["CandidateApplicationDirectory"]
    if os.path.normcase(str(app)) != os.path.normcase(os.path.abspath(expected_app)):
        raise ValueError("HandoffApplicationLocation")
    if command == "complete-successor":
        evidence = installed_readback(config, request["configSha256"])
        terminal_failure(config, h)
        return old_r.complete_successor(new_r, new_scope, h, pin, evidence)
    installed_readback(old, h["PreviousGateSha256"], allow_financial=True)
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
    if set(request) - {"command", "configPath", "configSha256", "appDirectory", "id", "identitySha256", "receipt", "policyPath", "transitionPath", "transitionSha256", "handoffPath", "handoffSha256"}:
        raise ValueError("RequestShape")
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
    build_command = request["command"] in ("inspect-build", "claim-build", "check-build", "complete-build")
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
    if scope["schema"] == 2:
        if build_command:
            raise ValueError("SuccessorBuildChangeNotAdmitted")
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
    if build_command:
        result = transition_command(request, config, expected, register, module, scope, app)
    elif command == "inspect":
        with register._open() as db:
            state = register._read(db)
            now = register.clock()
            if state["stopped"] or state["pending"] or register._time_rejection(state):
                raise ValueError("InspectionNotReady")
        result = {"valid": True, "deadlineUtc": scope["endUtc"]}
    elif command == "status":
        result = register.status()
    elif command == "verify-completion":
        policy_bytes = bounded(request["policyPath"], 8 * 1024 * 1024)
        if sha(policy_bytes) != request["identitySha256"]:
            raise ValueError("PolicyHash")
        policy = json.loads(policy_bytes, object_pairs_hook=unique)
        verified_kind = module.successor_policy_kind(manifest, policy) if scope["schema"] == 2 else "completion-admission"
        module.verify_plan_policy(manifest, scope, policy, verified_kind)
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
            if kind == "completion-admission" and scope["schema"] == 2:
                kind = module.successor_policy_kind(manifest, policy)
            module.verify_plan_policy(manifest, scope, policy, kind)
        pilot_proof = pilot_acceptance(config, manifest, state, module) if scope["schema"] == 2 and kind == "completion-admission" else None
        result = register.claim(kind,
                                request["identitySha256"], request["id"], pilot_proof_sha=pilot_proof)
    elif command == "check":
        result = {"valid": register.require_pending_before_effect(request["id"], request["identitySha256"])}
    elif command == "complete":
        receipt = request["receipt"]
        with register._open() as db:
            pending = register._read(db)["pending"]
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
        detail = str(error)
        reason = reasons.get(detail, "Storage" if isinstance(error, (OSError, sqlite3.Error)) else "Unknown")
        print(json.dumps({"rejected": True, "category": type(error).__name__, "reason": reason,
                          "code": detail if detail in reasons else "Unclassified", "requiresReconciliation": True}))
        sys.exit(1)
