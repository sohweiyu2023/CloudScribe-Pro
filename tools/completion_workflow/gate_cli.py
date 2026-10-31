"""Pinned private workflow bridge. -I -S; no user modules, UI or provider requests."""
import hashlib
import json
import os
from pathlib import Path
import sys
import types


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


def installed_readback(config, pin):
    """Read actual installation, never caller success flags; no credential access."""
    app = Path(os.path.abspath(config["ApplicationDirectory"]))
    for name, field in (("CloudScribe.dll", "AppSha256"), ("CloudScribe.Infrastructure.dll", "InfrastructureSha256")):
        if sha(bounded(app / name, 512 * 1024 * 1024)) != config[field]:
            raise ValueError("InstalledBuildBinding")
    marker_bytes = bounded(app.parent / "cloudscribe-install.json", 32768)
    marker = json.loads(marker_bytes, object_pairs_hook=unique)
    if any(field in marker for field in ("RecoveryPolicySha256", "ChirpArticlePolicySha256", "ChirpReservationResumePolicySha256")):
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
    changes = {"AppSha256", "InfrastructureSha256", "CandidateApplicationDirectory", "ScriptPath", "ScriptSha256", "ModuleSha256"}
    if (set(old) != config_keys or type(old["SchemaVersion"]) is not int or old["SchemaVersion"] != 1
            or any(old[key] != config[key] for key in config_keys - changes)
            or build_digest(old) != transition["PreviousBuildSha256"]):
        raise ValueError("TransitionConfigBinding")
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


def main():
    if not sys.flags.isolated or not sys.flags.no_site:
        raise ValueError("IsolatedNoSiteInterpreterRequired")
    request_bytes = sys.stdin.buffer.read(8193)
    if len(request_bytes) > 8192:
        raise ValueError("InputBound")
    request = json.loads(request_bytes, object_pairs_hook=unique)
    if set(request) - {"command", "configPath", "configSha256", "appDirectory", "id", "identitySha256", "receipt", "policyPath", "transitionPath", "transitionSha256"}:
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
        module.verify_plan_policy(manifest, scope, json.loads(policy_bytes, object_pairs_hook=unique), "completion-admission")
        with register._open() as db:
            state = register._read(db)
            events = [json.loads(row[0]) for row in db.execute("SELECT body FROM events ORDER BY seq")]
            claims = {event["id"] for event in events if event["type"] == "claim" and event["kind"] == "completion-admission" and event["identity"] == request["identitySha256"]}
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
            module.verify_plan_policy(manifest, scope, json.loads(policy_bytes, object_pairs_hook=unique), kind)
        result = register.claim(kind,
                                request["identitySha256"], request["id"])
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
        reason = reasons.get(str(error), "Unknown")
        print(json.dumps({"rejected": True, "category": type(error).__name__, "reason": reason, "requiresReconciliation": True}))
        sys.exit(1)
