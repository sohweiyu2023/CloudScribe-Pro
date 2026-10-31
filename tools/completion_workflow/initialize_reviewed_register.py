"""Initialize restrictive counter ONLY after independent review/readiness.

The file hashes must come from the trusted external review, not this tool's output.
This creates no spending admission, credentials, provider request or app activation.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
import types


def main():
    if not sys.flags.isolated or not sys.flags.no_site:
        raise ValueError("IsolationRequired")
    parser = argparse.ArgumentParser()
    for name in ("gate", "gate-sha256", "scope", "scope-sha256", "approval", "approval-sha256"):
        parser.add_argument("--" + name, required=True)
    arguments = parser.parse_args()
    def read(path, maximum):
        path = Path(os.path.abspath(path))
        for part in (path, *path.parents):
            if part.is_symlink() or part.is_junction():
                raise ValueError("LinkedMaterial")
        with path.open("rb") as stream:
            data = stream.read(maximum + 1)
        if len(data) > maximum:
            raise ValueError("MaterialBound")
        return data
    def sha(data):
        return hashlib.sha256(data).hexdigest()
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError("DuplicateField")
            result[key] = value
        return result
    gate_bytes = read(arguments.gate, 32768)
    if sha(gate_bytes) != arguments.gate_sha256 or sha(read(arguments.approval, 131072)) != arguments.approval_sha256:
        raise ValueError("ExternalReviewPinMismatch")
    config = json.loads(gate_bytes, object_pairs_hook=unique)
    source = Path(config["ScriptPath"]).with_name("completion_register.py")
    source_bytes = read(source, 131072)
    if sha(source_bytes) != config["ModuleSha256"] or sha(read(config["ScriptPath"], 131072)) != config["ScriptSha256"]:
        raise ValueError("SourcePin")
    if os.path.normcase(os.path.abspath(sys.executable)) != os.path.normcase(os.path.abspath(config["PythonPath"])) or sha(read(sys.executable, 32 * 1024 * 1024)) != config["PythonSha256"]:
        raise ValueError("InterpreterPin")
    module = types.ModuleType("independently_reviewed_completion_register")
    exec(compile(source_bytes, str(source), "exec"), module.__dict__)
    scope = json.loads(read(arguments.scope, 16384), object_pairs_hook=unique)
    if module.digest(scope) != arguments.scope_sha256 or arguments.scope_sha256 != config["ScopeSha256"] or scope["approvalSha256"] != arguments.approval_sha256:
        raise ValueError("ScopeReviewPin")
    app = Path(os.path.abspath(config["ApplicationDirectory"]))
    marker = json.loads(read(app.parent / "cloudscribe-install.json", 32768), object_pairs_hook=unique)
    if marker["Product"] != "CloudScribe Pro" or marker.get("CompletionGateProtocolVersion") != 1 or "CompletionWorkflowGateSha256" in marker:
        raise ValueError("VerifiedUnactivatedHookBuildRequired")
    for file, field in (("CloudScribe.dll", "AppSha256"), ("CloudScribe.Infrastructure.dll", "InfrastructureSha256")):
        if sha(read(app / file, 512 * 1024 * 1024)) != config[field]:
            raise ValueError("InstalledBuildMismatch")
    profile = Path(os.path.abspath(marker["Profile"]))
    if sha(str(profile).rstrip("/\\").upper().encode()) != config["ProfileRootSha256"] or (profile / ".completion-workflow-required.json").exists():
        raise ValueError("ProfileOrPriorRunMismatch")
    manifest_bytes = read(config["PlanManifestPath"], 1024 * 1024)
    manifest = json.loads(manifest_bytes, object_pairs_hook=unique)
    bindings = {"buildSha256": sha((config["AppSha256"] + "|" + config["InfrastructureSha256"]).encode()),
                "profileSha256": config["ProfileRootSha256"], "accountSha256": sha(config["AccountId"].encode()),
                "projectSha256": sha(config["ProjectId"].encode()), "plansSha256": module.digest(manifest)}
    if sha(manifest_bytes) != config["PlanManifestSha256"] or any(scope[key] != value for key, value in bindings.items()):
        raise ValueError("ScopeBindings")
    register = module.Register.create(config["RegisterPath"], scope, arguments.scope_sha256)
    status = register.status()
    print(json.dumps({"initialized": True, "scopeSha256": arguments.scope_sha256, "deadlineUtc": status["deadlineUtc"],
                      "spendingAuthority": False, "providerCalls": 0, "appActivated": False}))


if __name__ == "__main__":
    try:
        main()
    except BaseException as error:
        print(json.dumps({"rejected": True, "category": type(error).__name__, "requiresReconciliation": True}))
        sys.exit(1)
