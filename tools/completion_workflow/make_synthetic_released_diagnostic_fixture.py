"""Disposable H8 bridge/installer metadata; inert binaries and synthetic SQLite only."""
import copy
from datetime import datetime, timezone
import json
from pathlib import Path
import shutil
import sys
import tempfile
import time
import unittest
from unittest.mock import patch
import uuid


def prepare_released_diagnostic(owner):
    import completion_register as m
    import gate_cli as g
    import pending_install_files as files
    from test_unused_diagnostic_renewal_cli import prepare_unused_renewal

    f, _, policy, old, _ = prepare_unused_renewal(owner)
    root = f.f.b.f.root
    folder = root / 'post-manual-h8'
    folder.mkdir()
    target = Path(f.config['ApplicationDirectory']).parent
    profile = f.f.b.f.profile
    required = profile / '.completion-workflow-required.json'
    baseline_config = json.loads(Path(f.h['PreviousConfigPath']).read_bytes())
    baseline_manifest = json.loads(Path(baseline_config['PlanManifestPath']).read_bytes())
    backup = root / 'retained-before-synthetic-maintenance'
    shutil.copytree(target, backup)
    previous_required = folder / 'retained-original-required.json'
    previous_required.write_bytes(required.read_bytes())
    original_database = f.f.db.read_bytes()

    # A genuine unused H7 claim is withdrawn without activating its child. The
    # installer has not replaced files, so existing predecessor readback is exact.
    result = f.command('claim-successor')
    owner.assertEqual(0, result[0], result)
    orphan = m.Register(f.config['RegisterPath'], f.config['ScopeSha256'])
    with old._open() as db:
        before = old._read(db)
    with orphan._open() as db:
        child = orphan._read(db)
    withdrawal = dict(SchemaVersion=1, WithdrawalId=str(uuid.uuid4()),
        WithdrawalApprovalSha256=m.digest({'synthetic': 'separate administrative withdrawal'}),
        RepairTransitionId=f.h['TransitionId'], RepairIdentitySha256=f.h_pin,
        ScopeSha256=old.scope_sha, ExpectedHeadSha256=before['head'], PreviousGateSha256=f.pin,
        RestoredGateSha256=f.h['PreviousGateSha256'], BackupPath=str(backup),
        PreviousInstallSha256=files.sha(backup / 'cloudscribe-install.json'),
        PreviousRequiredSha256=files.sha(previous_required), NewScopeSha256=orphan.scope_sha,
        NewHeadSha256=child['head'], AdministrativeOnly=True)
    for phase in ('claim', 'complete'):
        old.successor_withdrawal(orphan, withdrawal, m.digest(withdrawal), phase,
            m.digest({'synthetic': 'unchanged predecessor files readback'}))

    # The compatible reader deployment is real frozen source, including both
    # indirectly hash-pinned extensions; no source is loaded from the live install.
    source = folder / 'source'
    source.mkdir()
    for name in ('gate_cli.py', 'completion_register.py', 'maintenance_register.py', 'successor_register.py'):
        (source / name).write_bytes(Path(__file__).with_name(name).read_bytes())
    package = folder / 'package'
    candidate = package / 'app'
    candidate.mkdir(parents=True)
    for name in ('CloudScribe.dll', 'CloudScribe.Infrastructure.dll'):
        data = ('inert maintained H8 fixture ' + name).encode()
        (candidate / name).write_bytes(data)
    maintained = dict(baseline_config, CandidateApplicationDirectory=str(candidate),
        ScriptPath=str(source / 'gate_cli.py'), ScriptSha256=files.sha(source / 'gate_cli.py'),
        ModuleSha256=files.sha(source / 'completion_register.py'),
        AppSha256=files.sha(candidate / 'CloudScribe.dll'),
        InfrastructureSha256=files.sha(candidate / 'CloudScribe.Infrastructure.dll'))
    maintained_path = folder / 'immutable-maintained-gate.json'
    maintained_path.write_bytes(json.dumps(maintained, separators=(',', ':')).encode())
    maintained_pin = files.sha(maintained_path)
    prepared = folder / 'maintenance-prepared'
    shutil.copytree(target, prepared)
    for name in ('CloudScribe.dll', 'CloudScribe.Infrastructure.dll'):
        shutil.copyfile(candidate / name, prepared / 'app' / name)
    (prepared / 'completion-workflow-gate.json').write_bytes(maintained_path.read_bytes())
    maintained_marker = json.loads((target / 'cloudscribe-install.json').read_bytes())
    maintained_marker.update(AppSha256=maintained['AppSha256'], CompletionWorkflowGateSha256=maintained_pin,
        CompletionGateProtocolVersion=1)
    (prepared / 'cloudscribe-install.json').write_text(json.dumps(maintained_marker))
    required_bytes = json.dumps({'GateSha256': maintained_pin, 'ScopeSha256': maintained['ScopeSha256']}).encode()
    with old._open() as db:
        before = old._read(db)
    build = g.sha((maintained['AppSha256'] + '|' + maintained['InfrastructureSha256']).encode())
    maintenance = dict(SchemaVersion=1, TransitionKind='stopped-unused-diagnostic-maintenance',
        TransitionId=str(uuid.uuid4()), MaintenanceApprovalSha256=m.digest({'synthetic': 'maintenance only'}),
        SourceApprovalSha256=before['scope']['approvalSha256'], ScopeSha256=old.scope_sha,
        ExpectedHeadSha256=before['head'], PreviousGateSha256=before['currentGate'], NewGateSha256=maintained_pin,
        PreviousBuildSha256=before['currentBuild'], NewBuildSha256=build,
        DatabaseSha256=g.sha(original_database), FinancialRowsSha256=baseline_manifest['diagnostic']['financialRowsSha256'],
        AcceptedEvidenceSha256=before['scope']['acceptedEvidenceSha256'], ProfileSha256=before['scope']['profileSha256'],
        OldInventorySha256=m.digest(files.inventory(backup)), NewInventorySha256=m.digest(files.inventory(prepared)),
        PreviousRequiredSha256=files.sha(previous_required), NewRequiredSha256=g.sha(required_bytes),
        UsedRefreshes=before['refresh'], UsedAdmissions=before['admissions'],
        UsedResumeAdmissions=before['resume-admission'], LastGoodUtcMicros=before['lastGoodUtc'],
        AdministrativeOnly=True, EnableNarration=False)
    old.maintenance(maintenance, m.digest(maintenance), 'claim')
    # The test-only filesystem simulation follows the real journal-first order.
    for path in prepared.rglob('*'):
        if path.is_file():
            shutil.copyfile(path, target / path.relative_to(prepared))
    required.write_bytes(required_bytes)
    readback = g.installed_readback(maintained, maintained_pin, allow_financial=True)
    old.maintenance(maintenance, m.digest(maintenance), 'complete', readback)

    # Release preserves financial marker fields. A subsequent ordinary manual UI
    # update drops their active pins, retaining the policy file and every DB row.
    release_id = str(uuid.uuid4())
    archive = required.with_name(required.name + '.' + release_id + '.retired')
    marker_path = target / 'cloudscribe-install.json'
    marker = json.loads(marker_path.read_bytes())
    release_marker = {k: v for k, v in marker.items()
        if k not in ('CompletionWorkflowGateSha256', 'CompletionGateProtocolVersion')}
    release_inventory = files.inventory(target)
    release_inventory['cloudscribe-install.json'] = g.sha(json.dumps(release_marker, separators=(',', ':')).encode())
    with old._open() as db:
        before = old._read(db)
    release = dict(SchemaVersion=1, ReleaseId=release_id,
        ReleaseApprovalSha256=m.digest({'synthetic': 'separate explicit manual release'}),
        MaintenanceIdentitySha256=m.digest(maintenance), ScopeSha256=old.scope_sha,
        ExpectedHeadSha256=before['head'], ManualInventorySha256=m.digest(release_inventory),
        RequiredArchivePathSha256=m.path_digest(archive), AdministrativeOnly=True, PaidAuthority=False)
    m._maintenance('release', old, release, m.digest(release), 'claim')
    marker_path.write_bytes(json.dumps(release_marker, separators=(',', ':')).encode())
    m._maintenance('release', old, release, m.digest(release), 'complete', m.digest({'synthetic': 'released marker readback'}))
    required.rename(archive)
    (target / 'completion-workflow-gate.json').rename(folder / 'retained-installed-gate.json')
    manual_marker = {k: v for k, v in release_marker.items()
        if k not in ('RecoveryPolicySha256', 'ChirpArticlePolicySha256', 'ChirpReservationResumePolicySha256')}
    manual_marker['CompletionGateProtocolVersion'] = 1
    marker_path.write_bytes(json.dumps(manual_marker, separators=(',', ':')).encode())
    with old._open() as db:
        before = old._read(db)
    owner.assertTrue(before['retired'])
    owner.assertEqual(original_database, f.f.db.read_bytes())

    approval = m.digest({'synthetic': 'new H8 one-outcome source and window approval'})
    consent = m.digest({'synthetic': 'new H8 protected capture consent'})
    policy = copy.deepcopy(policy)
    policy['SourceApprovalSha256'] = approval
    policy['SuccessorGrant']['GrantId'] = str(uuid.uuid4())
    policy['AppSha256'] = maintained['AppSha256']
    policy['InfrastructureSha256'] = maintained['InfrastructureSha256']
    manifest = copy.deepcopy(baseline_manifest)
    diagnostic = manifest['diagnostic']
    diagnostic['successorGrantSha256'] = g.sha(json.dumps(policy['SuccessorGrant'], separators=(',', ':')).encode())
    diagnostic['privateCaptureConsentSha256'] = consent
    policy['DiagnosticEvidence'].update(SuccessorGrantSha256=diagnostic['successorGrantSha256'], PrivateCaptureConsentSha256=consent)
    manifest_path = folder / 'manifest.json'
    manifest_path.write_text(json.dumps(manifest))
    observed = time.time_ns() // 1000
    mono = time.monotonic_ns() // 1000
    stamp = lambda value: datetime.fromtimestamp(value / 1e6, timezone.utc).isoformat().replace('+00:00', 'Z')
    epoch = dict(ObservedUtcMicros=observed, ObservedMonoMicros=mono,
        TrustedUtcLowerMicros=observed - 10_000, TrustedUtcUpperMicros=observed + 100_000,
        PreviousLastGoodUtcMicros=before['lastGoodUtc'], WindowsBootUtc=before['scope']['startUtc'],
        BootReadbackSha256=m.digest({'synthetic': 'independent clock metadata'}))
    scope = dict(before['scope'], approvalSha256=approval, privateCaptureConsentSha256=consent,
        buildSha256=build, plansSha256=m.digest(manifest), diagnosticWindowMode='fresh-10-minute',
        startUtc=stamp(observed), endUtc=stamp(observed + 600_000_000),
        predecessorRegisterPath=str(old.path), predecessorScopeSha256=old.scope_sha,
        registerPathSha256=m.path_digest(folder / 'register.db'), transitionId=str(uuid.uuid4()))
    scope_path = folder / 'scope.json'
    scope_path.write_text(json.dumps(scope))
    config = dict(maintained, RegisterPath=str(folder / 'register.db'), ScopeSha256=m.digest(scope),
        PlanManifestPath=str(manifest_path), PlanManifestSha256=files.sha(manifest_path))
    config_path = folder / 'gate.json'
    config_path.write_text(json.dumps(config))
    gate_pin = files.sha(config_path)
    h = dict(f.h, SchemaVersion=8, TransitionKind='post-manual-release-diagnostic',
        TransitionId=scope['transitionId'], SourceApprovalSha256=approval, WindowApprovalSha256=approval,
        FreshPrivateCaptureConsentSha256=consent, ClockEpochEvidence=epoch,
        InheritedClockEpochEvidence=before['clockEpoch'], PreviousConfigPath=str(maintained_path),
        PreviousGateSha256=maintained_pin, PreviousScopeSha256=old.scope_sha,
        ExpectedPreviousHeadSha256=before['head'], NewConfigPath=str(config_path), NewGateSha256=gate_pin,
        NewScopePath=str(scope_path), NewScopeSha256=config['ScopeSha256'], DatabaseSha256=g.sha(original_database),
        OriginalManifestSha256=m.digest(baseline_manifest), ManualReleaseSha256=m.digest(release),
        ManualMarkerSha256=files.sha(marker_path), ManualInventorySha256=m.digest(files.inventory(target)),
        RequiredArchiveSha256=files.sha(archive), UsedRefreshes=before['refresh'],
        UsedAdmissions=before['admissions'], UsedResumeAdmissions=before['resume-admission'])
    handoff_path = folder / 'handoff.json'
    handoff_path.write_text(json.dumps(h))
    policy['ExpiresAtUtc'] = m.effective_deadline(scope, epoch)
    policy_path = folder / 'diagnostic-policy.json'
    policy_path.write_text(json.dumps(policy, separators=(',', ':')))
    f.source = source
    f.f.source = source
    f.new_folder = folder
    f.new_app = candidate
    f.f.new_app = candidate
    f.scope = scope
    f.scope_path = scope_path
    f.manifest = manifest
    f.config = config
    f.config_path = config_path
    f.pin = gate_pin
    f.h = h
    f.h_path = handoff_path
    f.h_pin = files.sha(handoff_path)
    metadata = dict(root=str(root), package=str(package), target=str(target), profile=str(profile),
        gatePath=str(config_path), gateSha256=gate_pin, scopeSha256=config['ScopeSha256'], scopePath=str(scope_path),
        handoffPath=str(handoff_path), handoffSha256=f.h_pin, handoffId=h['TransitionId'],
        previousGatePath=str(maintained_path), previousGateSha256=maintained_pin, previousScopeSha256=old.scope_sha,
        expectedRefreshes=before['refresh'], expectedAdmissions=before['admissions'], expectedResumeAdmissions=before['resume-admission'],
        deadlineUtc=m.effective_deadline(scope, epoch), databasePath=str(f.f.db), databaseSha256=g.sha(original_database),
        policyPath=str(policy_path), policySha256=files.sha(policy_path), requiredArchive=str(archive),
        manualMarkerSha256=h['ManualMarkerSha256'], source=str(source), providerCalls=0, credentialReads=0, syntheticOnly=True)
    return f, policy_path, policy, old, before, metadata


def main():
    if len(sys.argv) != 2:
        raise ValueError('SyntheticRootRequired')
    root = Path(sys.argv[1]).absolute()
    if (root.exists() or any(p.is_symlink() or p.is_junction() for p in (root, *root.parents))
            or not root.resolve().is_relative_to(Path(tempfile.gettempdir()).resolve())):
        raise ValueError('SyntheticRootRequired')
    root.mkdir()
    owned = root / 'fixture'
    owned.mkdir()
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    class OwnedDirectory:
        name = str(owned)
        def cleanup(self):
            pass
    owner = unittest.TestCase()
    with patch('test_gate_cli.tempfile.TemporaryDirectory', return_value=OwnedDirectory()):
        *_, metadata = prepare_released_diagnostic(owner)
    print(json.dumps(metadata))


if __name__ == '__main__':
    main()
