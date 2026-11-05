"""Finish ONLY a caller-pinned, already activated inert H8 fixture admission.

No fixture creation, real profile, provider, credential or reservation operations.
Usage: python -I -S -B verify_synthetic_released_admission.py metadata.json sha256
"""
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import types
import uuid

BRIDGE_FAILURE = None


def require(condition, code):
    if not condition:
        raise ValueError(code)


def physical(path):
    path = Path(path)
    require(path.is_absolute() and not str(path).startswith(('\\\\', '//')), 'SyntheticRootRequired')
    for part in (path, *path.parents):
        require(not part.is_symlink() and not part.is_junction(), 'SyntheticLinkedPath')
    return path.resolve()


def bounded(path, maximum):
    with physical(path).open('rb') as stream:
        raw = stream.read(maximum + 1)
    require(len(raw) <= maximum, 'SyntheticInputBound')
    return raw


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def unique(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, 'SyntheticDuplicateField')
        result[key] = value
    return result


def parsed(raw):
    return json.loads(raw, object_pairs_hook=unique)


def main():
    require(len(sys.argv) == 3 and sys.flags.isolated and sys.flags.no_site, 'SyntheticInvocation')
    metadata_path = physical(sys.argv[1])
    temporary = physical(tempfile.gettempdir())
    require(metadata_path.is_relative_to(temporary), 'SyntheticRootRequired')
    require(re.fullmatch('[a-f0-9]{64}', sys.argv[2]) is not None, 'SyntheticPin')
    metadata_raw = bounded(metadata_path, 65536)
    require(sha(metadata_raw) == sys.argv[2], 'SyntheticPin')
    meta = parsed(metadata_raw)
    require(meta.get('syntheticOnly') is True and type(meta.get('providerCalls')) is int
        and meta['providerCalls'] == 0 and type(meta.get('credentialReads')) is int
        and meta['credentialReads'] == 0, 'SyntheticOnly')
    root = physical(meta['root'])
    require(root != temporary and root.is_relative_to(temporary), 'SyntheticRootRequired')

    def confined(path):
        result = physical(path)
        require(result.is_relative_to(root), 'SyntheticPathEscape')
        return result

    gate_path = confined(meta['gatePath'])
    gate_raw = bounded(gate_path, 32768)
    require(sha(gate_raw) == meta['gateSha256'], 'SyntheticPin')
    config = parsed(gate_raw)
    target = confined(meta['target'])
    profile = confined(meta['profile'])
    app = confined(config['ApplicationDirectory'])
    require(app == target / 'app' and not profile.is_relative_to(target), 'SyntheticIdentity')
    require(config['AccountId'].startswith('synthetic-') and config['ProjectId'].startswith('synthetic-'), 'SyntheticIdentity')
    for name in ('CloudScribe.dll', 'CloudScribe.Infrastructure.dll'):
        require(bounded(app / name, 4096) == ('inert maintained H8 fixture ' + name).encode(), 'SyntheticInertBinary')
    require(sha(bounded(app / 'CloudScribe.dll', 4096)) == config['AppSha256']
        and sha(bounded(app / 'CloudScribe.Infrastructure.dll', 4096)) == config['InfrastructureSha256'], 'SyntheticPin')
    require(physical(sys.executable) == physical(config['PythonPath'])
        and sha(bounded(sys.executable, 32 * 1024 * 1024)) == config['PythonSha256'], 'SyntheticInterpreter')
    script = confined(config['ScriptPath'])
    source = script.parent
    require(script.name == 'gate_cli.py' and sha(bounded(script, 131072)) == config['ScriptSha256'], 'SyntheticPin')
    module_path = confined(source / 'completion_register.py')
    module_raw = bounded(module_path, 131072)
    require(sha(module_raw) == config['ModuleSha256'], 'SyntheticPin')
    # The pinned core independently verifies its two bounded adjacent extensions.
    for name in ('maintenance_register.py', 'successor_register.py'):
        confined(source / name)
    scope_path = confined(meta['scopePath'])
    scope = parsed(bounded(scope_path, 32768))
    require(sha(json.dumps(scope, sort_keys=True, separators=(',', ':'), ensure_ascii=True).encode())
        == config['ScopeSha256'] == meta['scopeSha256'], 'SyntheticPin')
    require(scope['schema'] == 6 and scope['maximumNewAttempts'] == 1, 'SyntheticIdentity')
    confined(scope['predecessorRegisterPath'])
    policy_path = confined(meta['policyPath'])
    policy_raw = bounded(policy_path, 8 * 1024 * 1024)
    require(sha(policy_raw) == meta['policySha256'], 'SyntheticPin')
    policy = parsed(policy_raw)
    require(policy['SchemaVersion'] == 4 and policy['MaximumNewAttempts'] == 1
        and policy['MaximumAdditionalReservationMinorUnits'] == 1
        and policy['Budget']['ProjectCeilingMinorUnits'] - policy['Budget']['ProjectCommittedMinorUnits'] == 1
        and policy['SourceApprovalSha256'] == scope['approvalSha256']
        and policy['DiagnosticEvidence']['PrivateCaptureConsentSha256'] == scope['privateCaptureConsentSha256']
        and policy['DiagnosticEvidence']['RequiredCaptureMode'] == 1, 'SyntheticPolicy')
    database = confined(meta['databasePath'])
    require(database == profile / 'data' / 'cloudscribe.db', 'SyntheticIdentity')
    database_raw = bounded(database, 64 * 1024 * 1024)
    require(sha(database_raw) == meta['databaseSha256'], 'SyntheticDatabaseChanged')
    for suffix in ('-wal', '-shm'):
        sidecar = confined(str(database) + suffix)
        if sidecar.exists() and suffix == '-wal':
            require(not bounded(sidecar, 1), 'SyntheticDatabaseChanged')
    require(sha(bounded(confined(meta['handoffPath']), 32768)) == meta['handoffSha256'], 'SyntheticPin')
    confined(config['RegisterPath'])
    manifest_path = confined(config['PlanManifestPath'])
    require(sha(bounded(manifest_path, 1024 * 1024)) == config['PlanManifestSha256'], 'SyntheticPin')
    module = types.ModuleType('pinned_synthetic_h8_reader')
    module.__file__ = str(module_path)
    exec(compile(module_raw, str(module_path), 'exec'), module.__dict__)
    register = module.Register(config['RegisterPath'], config['ScopeSha256'])

    # Reject an accidental real predecessor pointer before the production reader
    # can acquire that ancestor's journal lease or inspect its history.
    seen = set()
    at = register
    for _ in range(8):
        confined(at.path)
        confined(at.anchor)
        require(at.path not in seen, 'SyntheticIdentity')
        seen.add(at.path)
        with at._open() as connection:
            parent_state = at._read(connection)
        parent_scope = parent_state['scope']
        if parent_scope['schema'] == 1:
            break
        parent_path = confined(parent_scope['predecessorRegisterPath'])
        at = module.Register(parent_path, parent_scope['predecessorScopeSha256'])
    else:
        raise ValueError('SyntheticIdentity')

    def state():
        with register._open() as connection:
            return register._read(connection)

    def command(name, operation=None, receipt=None):
        global BRIDGE_FAILURE
        request = dict(command=name, configPath=str(gate_path), configSha256=meta['gateSha256'],
            appDirectory=str(app), id=operation or str(uuid.uuid4()),
            identitySha256=meta['policySha256'] if name != 'status' else sha(b'synthetic status'),
            handoffPath=meta['handoffPath'], handoffSha256=meta['handoffSha256'],
            policyPath=str(policy_path) if name != 'status' else None, receipt=receipt)
        process = subprocess.run([sys.executable, '-I', '-S', '-B', str(script)], input=json.dumps(request),
            text=True, capture_output=True, timeout=30)
        require(len(process.stdout) <= 32768, 'SyntheticBridgeRejected')
        response = parsed(process.stdout)
        if process.returncode != 0 or process.stderr or response.get('rejected'):
            codes = {'ReleasedDiagnosticBinding', 'ReleasedDiagnosticState', 'ReleasedDiagnosticPending',
                'ReleasedDiagnosticWindow', 'FreshReleasedDiagnosticApprovalRequired', 'ReleasedManualProfile',
                'ReleasedManualRequirement', 'ReleasedManualMarker', 'ReleasedManualBuild', 'ReleasedManualInventory',
                'ReleasedRequiredArchive', 'DiagnosticFinancialChanged', 'DiagnosticPolicy', 'DiagnosticNativePin',
                'DiagnosticInstalledPolicy', 'DiagnosticParent', 'DiagnosticBudget', 'DiagnosticCoverageOrBudget',
                'DiagnosticChildExists', 'PendingOrUncertainWork', 'ClosedDatabaseRequired', 'ClockRollback',
                'ClockDiscontinuity', 'WindowExpired', 'OutsideWindow', 'ClockEpochPolicyDeadline', 'ScriptLocation',
                'ScriptHash', 'ModuleHash', 'InterpreterLocation', 'InterpreterHash', 'InstalledGatePin',
                'InstalledGateBytes', 'InstalledBuildBinding', 'RequiredGatePin', 'PolicyPin'}
            line = response.get('failureSourceLine')
            BRIDGE_FAILURE = dict(Command=name,
                Code=response.get('code') if response.get('code') in codes else 'Unclassified',
                Component=response.get('failureComponent') if response.get('failureComponent') in ('bridge', 'register', 'successor', 'maintenance') else 'unavailable',
                SourceLine=line if type(line) is int and 1 <= line <= 10000 else 0)
            raise ValueError('SyntheticBridgeRejected')
        return response

    before = state()
    require(before['activated'] and not before['stopped'] and not before['retired'] and before['pending'] is None
        and before['diagnosticDispatch'] is None and before['diagnosticPolicySha256'] is None, 'SyntheticAlreadyUsed')
    require((before['refresh'], before['admissions'], before['resume-admission']) ==
        (meta['expectedRefreshes'], meta['expectedAdmissions'], meta['expectedResumeAdmissions']), 'SyntheticCounters')
    register.require_predecessor_retired(meta['gateSha256'])
    command('status')
    operation = str(uuid.uuid4())
    command('claim-completion', operation)
    command('check', operation)
    installed_policy = confined(target / 'chirp-article-admission-policy.json')
    if installed_policy.exists():
        retained = confined(root / 'retained-pre-H8-admission-policy.private.json')
        with retained.open('xb') as stream:
            stream.write(bounded(installed_policy, 8 * 1024 * 1024)); stream.flush(); os.fsync(stream.fileno())
    marker_path = confined(target / 'cloudscribe-install.json')
    marker = parsed(bounded(marker_path, 32768))
    require(marker.get('CompletionWorkflowGateSha256') == meta['gateSha256'], 'SyntheticPin')
    marker['ChirpArticlePolicySha256'] = meta['policySha256']
    # Explicit synthetic equivalent of the installer's admitted file/readback phase.
    for destination, value in ((installed_policy, policy_raw),
            (marker_path, json.dumps(marker, separators=(',', ':')).encode())):
        temporary_path = confined(str(destination) + '.synthetic-admission-' + operation)
        with temporary_path.open('xb') as stream:
            stream.write(value); stream.flush(); os.fsync(stream.fileno())
        os.replace(temporary_path, destination)
    command('complete', operation, {'outcome': 'Succeeded'})
    after = state()
    require(after['pending'] is None and after['diagnosticDispatch'] is None
        and after['diagnosticPolicySha256'] == meta['policySha256']
        and after['diagnostic-admission'] == before['diagnostic-admission'] + 1
        and after['admissions'] == before['admissions'] + 1
        and after['refresh'] == before['refresh'] and after['resume-admission'] == before['resume-admission'], 'SyntheticCounters')
    require(database_raw == bounded(database, 64 * 1024 * 1024), 'SyntheticDatabaseChanged')
    return dict(Passed=True, SyntheticOnly=True, ProviderCalls=0, CredentialReads=0,
        DatabaseUnchanged=True, RetainedRefreshes=after['refresh'], TotalAdmissions=after['admissions'],
        AddedDiagnosticAdmissions=1, DiagnosticDispatch=None, PolicySha256=meta['policySha256'])


if __name__ == '__main__':
    try:
        print(json.dumps(main(), separators=(',', ':')))
    except Exception as error:
        allowed = {'SyntheticRootRequired', 'SyntheticLinkedPath', 'SyntheticInputBound', 'SyntheticDuplicateField',
            'SyntheticInvocation', 'SyntheticPin', 'SyntheticOnly', 'SyntheticPathEscape', 'SyntheticIdentity',
            'SyntheticInertBinary', 'SyntheticInterpreter', 'SyntheticPolicy', 'SyntheticDatabaseChanged',
            'SyntheticBridgeRejected', 'SyntheticAlreadyUsed', 'SyntheticCounters'}
        code = error.args[0] if type(error) is ValueError and len(error.args) == 1 and type(error.args[0]) is str and error.args[0] in allowed else 'SyntheticVerificationFailed'
        print(json.dumps(dict(Passed=False, Code=code, ExceptionType=type(error).__name__
            if type(error).__name__ in ('ValueError', 'Rejected', 'OSError', 'PermissionError', 'FileNotFoundError') else 'Other',
            BridgeFailure=BRIDGE_FAILURE, ProviderCalls=0, CredentialReads=0)))
        raise SystemExit(1)
