"""Explicit stopped-run maintenance, never a paid admission or ordinary update.

Use -I -S and an independently reviewed plan hash. All source and evidence bytes
are pinned. The original stopped scope and financial data are never rewritten.
"""
from contextlib import ExitStack
import hashlib
import json
import os
from pathlib import Path
import sys
import types


def sources(pins):
    names = ('completion_register.py', 'maintenance_register.py', 'gate_cli.py',
             'pending_install_operator.py', 'pending_install_files.py',
             'maintenance_files.py', 'maintenance_operator.py')
    if not isinstance(pins, dict) or set(pins) != set(names): raise ValueError('RecoverySourcePins')
    loaded = {}
    for name in names:
        path = Path(__file__).resolve().with_name(name)
        for item in (path, *path.parents):
            if item.is_symlink() or item.is_junction(): raise ValueError('LinkedPath')
        with path.open('rb') as stream: data = stream.read(131073)
        if len(data) > 131072 or hashlib.sha256(data).hexdigest() != pins[name]:
            raise ValueError('RecoverySourcePins')
        if name in ('maintenance_register.py', 'maintenance_operator.py'): continue
        module = types.ModuleType(name[:-3]); module.__file__ = str(path)
        exec(compile(data, str(path), 'exec'), module.__dict__); loaded[name] = module
    return loaded


def perform(plan, *, expected_installation, phase, fault=lambda _: None, plan_path=None):
    keys = {'SchemaVersion', 'AdministrativeOnly', 'PaidAuthority', 'Transition', 'SourceSha256',
        'PreviousGate', 'NewGate', 'PreviousRequired', 'NewRequired', 'PreviousRequiredSha256',
        'NewRequiredSha256', 'OldInventory', 'NewInventory', 'Target', 'PreparedRoot', 'BackupRoot',
        'StageRoot', 'DisplacedRoot', 'RollbackStageRoot', 'RollbackDisplacedRoot', 'Required', 'TransitionId'}
    releasing = phase in ('inspect-release', 'release', 'resume-release')
    if releasing: keys |= {'Release', 'ManualMarker', 'ManualInventory', 'RequiredArchive'}
    if (not isinstance(plan, dict) or set(plan) != keys or type(plan['SchemaVersion']) is not int
            or plan['SchemaVersion'] != 1 or plan['AdministrativeOnly'] is not True
            or plan['PaidAuthority'] is not False or phase not in ('inspect', 'update', 'resume', 'rollback', 'resume-rollback', 'inspect-release', 'release', 'resume-release')):
        raise ValueError('RecoveryPlan')
    modules = sources(plan['SourceSha256']); m = modules['completion_register.py']; g = modules['gate_cli.py']
    o = modules['pending_install_operator.py']; files = modules['pending_install_files.py']; transaction = modules['maintenance_files.py']
    r = o.document(plan['Transition']); old = o.document(plan['PreviousGate']); new = o.document(plan['NewGate'])
    m._maintenance('validate', r, plan['Transition']['Sha256'])
    target = files.physical(plan['Target']); prepared = files.physical(plan['PreparedRoot']); backup = files.physical(plan['BackupRoot'])
    if (target != files.physical(expected_installation) or target != files.physical(Path(old['ApplicationDirectory']).parent)
            or old['ApplicationDirectory'] != new['ApplicationDirectory'] or plan['TransitionId'] != r['TransitionId']):
        raise ValueError('RecoveryInstallationRoot')
    # No account, Python trust, scope, deadline, voice, payload, register or manifest change.
    allowed = {'ScriptPath', 'ScriptSha256', 'ModuleSha256', 'CandidateApplicationDirectory', 'AppSha256', 'InfrastructureSha256'}
    if set(old) != set(new) or any(old[key] != new[key] for key in old if key not in allowed):
        raise ValueError('MaintenanceIdentity')
    if (files.physical(sys.executable) != files.physical(old['PythonPath'])
            or files.sha(Path(sys.executable)) != old['PythonSha256']): raise ValueError('RecoveryInterpreterPin')
    for config in (old, new):
        if (files.sha(Path(config['ScriptPath'])) != config['ScriptSha256']
                or files.sha(Path(config['ScriptPath']).with_name('completion_register.py')) != config['ModuleSha256']):
            raise ValueError('RecoverySourcePins')
    if files.physical(new['ScriptPath']) != Path(__file__).resolve().with_name('gate_cli.py'):
        raise ValueError('RecoverySourcePins')
    if new['ModuleSha256'] != plan['SourceSha256']['completion_register.py']:
        raise ValueError('RecoverySourcePins')
    build = lambda config: hashlib.sha256((config['AppSha256'] + '|' + config['InfrastructureSha256']).encode()).hexdigest()
    bindings = {'PreviousGateSha256': plan['PreviousGate']['Sha256'], 'NewGateSha256': plan['NewGate']['Sha256'],
        'PreviousBuildSha256': build(old), 'NewBuildSha256': build(new), 'OldInventorySha256': m.digest(plan['OldInventory']),
        'NewInventorySha256': m.digest(plan['NewInventory']), 'PreviousRequiredSha256': plan['PreviousRequiredSha256'],
        'NewRequiredSha256': plan['NewRequiredSha256'], 'ScopeSha256': old['ScopeSha256'], 'ProfileSha256': old['ProfileRootSha256']}
    if any(r[key] != value for key, value in bindings.items()): raise ValueError('MaintenanceIdentity')
    roots = tuple(files.physical(plan[key]) for key in ('Target', 'PreparedRoot', 'BackupRoot', 'StageRoot',
        'DisplacedRoot', 'RollbackStageRoot', 'RollbackDisplacedRoot'))
    for index, root in enumerate(roots):
        if any(root == other or root.is_relative_to(other) or other.is_relative_to(root) for other in roots[index + 1:]):
            raise ValueError('RecoveryPathOverlap')
    affected = tuple(files.physical(plan[key]) for key in ('Target', 'StageRoot', 'DisplacedRoot', 'RollbackStageRoot', 'RollbackDisplacedRoot'))
    references = [plan[key]['Path'] for key in ('Transition', 'PreviousGate', 'NewGate')]
    references += [plan['PreviousRequired'], plan['NewRequired'], old['PlanManifestPath'], old['RegisterPath']]
    if releasing: references += [plan['Release']['Path'], plan['ManualMarker']['Path']]
    references += [str(Path(__file__).resolve().with_name(name)) for name in plan['SourceSha256']]
    if plan_path: references.append(plan_path)
    if any(any(files.physical(path).is_relative_to(root) for root in affected) for path in references):
        raise ValueError('RecoveryMutableEvidence')
    if files.inventory(backup) != plan['OldInventory'] or files.inventory(prepared) != plan['NewInventory']:
        raise ValueError('RecoveryBackupChanged')
    for root, config, pin, required_path, required_pin in (
            (backup, old, r['PreviousGateSha256'], plan['PreviousRequired'], r['PreviousRequiredSha256']),
            (prepared, new, r['NewGateSha256'], plan['NewRequired'], r['NewRequiredSha256'])):
        if files.sha(root / 'completion-workflow-gate.json') != pin or files.sha(Path(required_path)) != required_pin:
            raise ValueError('RecoveryBackupChanged')
        if json.loads(o.raw(required_path, 4096), object_pairs_hook=o.unique) != {'GateSha256': pin, 'ScopeSha256': old['ScopeSha256']}:
            raise ValueError('RequiredGatePin')
        for name, field in (('CloudScribe.dll', 'AppSha256'), ('CloudScribe.Infrastructure.dll', 'InfrastructureSha256')):
            if files.sha(root / 'app' / name) != config[field]: raise ValueError('InstalledBuildBinding')
    marker = json.loads(o.raw(backup / 'cloudscribe-install.json', 32768), object_pairs_hook=o.unique)
    new_marker = json.loads(o.raw(prepared / 'cloudscribe-install.json', 32768), object_pairs_hook=o.unique)
    profile = files.physical(marker['Profile']); db_path = profile / 'data/cloudscribe.db'
    if (new_marker['Profile'] != marker['Profile'] or profile.is_relative_to(target)
            or hashlib.sha256(str(profile).rstrip('/\\').upper().encode()).hexdigest() != old['ProfileRootSha256']
            or files.physical(plan['Required']) != profile / '.completion-workflow-required.json'):
        raise ValueError('RecoveryProfile')
    # The cold manifest proof binds accepted outputs, all financial rows and no pending/uncertain work.
    manifest = json.loads(o.raw(old['PlanManifestPath'], 1024 * 1024), object_pairs_hook=o.unique)
    if files.sha(Path(old['PlanManifestPath'])) != old['PlanManifestSha256']:
        raise ValueError('RecoveryEvidencePin')
    register = m.Register(old['RegisterPath'], old['ScopeSha256'])
    with register._open() as connection: state = register._read(connection)
    scope = state['scope']
    if r['FinancialRowsSha256'] != manifest['diagnostic']['financialRowsSha256']:
        raise ValueError('DiagnosticFinancialChanged')
    def evidence():
        if files.inventory(backup) != plan['OldInventory'] or files.inventory(prepared) != plan['NewInventory']:
            raise ValueError('RecoveryBackupChanged')
        g.diagnostic_acceptance(dict(old, ApplicationDirectory=str(backup / 'app')), manifest, scope, m, database_sha=r['DatabaseSha256'])
    rollback = phase in ('rollback', 'resume-rollback')
    def journal(action):
        if action == 'complete':
            evidence()
            g.installed_readback(old if rollback else new, r['PreviousGateSha256' if rollback else 'NewGateSha256'], allow_financial=True)
        observed = m.digest({'files': plan['OldInventory' if rollback else 'NewInventory'],
            'required': r['PreviousRequiredSha256' if rollback else 'NewRequiredSha256'], 'database': r['DatabaseSha256']})
        return register.maintenance(r, plan['Transition']['Sha256'], ('rollback-' if rollback else '') + action, observed)
    with ExitStack() as locks:
        locks.enter_context(o.windows_handle(profile / '.instance.lock', exclusive=True))
        locks.enter_context(o.windows_handle(db_path))
        for suffix in ('-wal', '-shm'):
            path = Path(str(db_path) + suffix)
            if path.exists(): locks.enter_context(o.windows_handle(path))
        locks.enter_context(o.windows_handle(target.with_name(target.name + '.pending-install-recovery.lock'), exclusive=True, create=True))
        evidence()
        if releasing:
            return release_files(plan, modules, register, evidence, phase, fault)
        if phase == 'inspect':
            g.installed_readback(old, r['PreviousGateSha256'], allow_financial=True)
            if journal('inspect') != {'valid': True, 'authority': False}: raise ValueError('RecoveryJournalNotConfirmed')
            return {'valid': True, 'authority': False, 'providerRequests': 0}
        return (transaction.rollback if rollback else transaction.update)(files, plan, journal, evidence, fault,
            resume=phase in ('resume', 'resume-rollback'))


def release_files(plan, modules, register, evidence, phase, fault=lambda _: None):
    """Explicitly retire the completed maintenance scope, then detach its marker.

    Required marker stays present until the durable retirement has completed.
    Therefore interruption cannot accidentally enable the ordinary provider path.
    The operator holds the closed-app and data leases throughout this function.
    """
    o = modules['pending_install_operator.py']; m = modules['completion_register.py']; files = modules['pending_install_files.py']
    release = o.document(plan['Release']); identity = plan['Release']['Sha256']
    target = files.physical(plan['Target']); required = files.physical(plan['Required']); archive = files.physical(plan['RequiredArchive'])
    expected_archive = required.with_name(required.name + '.' + release['ReleaseId'] + '.retired')
    if archive != expected_archive or m.path_digest(archive) != release['RequiredArchivePathSha256']:
        raise ValueError('RecoveryPath')
    marker = target / 'cloudscribe-install.json'; source = files.physical(plan['ManualMarker']['Path'])
    raw = o.raw(source, 32768)
    if o.sha(raw) != plan['ManualMarker']['Sha256']: raise ValueError('RecoveryEvidencePin')
    previous = json.loads(o.raw(Path(plan['PreparedRoot']) / 'cloudscribe-install.json', 32768), object_pairs_hook=o.unique)
    manual = json.loads(raw, object_pairs_hook=o.unique)
    # Preserve all financial policy pins and every non-gate marker field.
    expected = {key: value for key, value in previous.items() if key not in ('CompletionWorkflowGateSha256', 'CompletionGateProtocolVersion')}
    if manual != expected: raise ValueError('MaintenanceIdentity')
    inventory = dict(plan['NewInventory']); inventory['cloudscribe-install.json'] = o.sha(raw)
    if plan['ManualInventory'] != inventory or release['ManualInventorySha256'] != m.digest(inventory):
        raise ValueError('MaintenanceIdentity')
    temporary = marker.with_name(marker.name + '.' + release['ReleaseId'] + '.manual')
    files.physical(temporary)
    actual = files.inventory(target)
    if temporary.exists():
        if files.sha(temporary) != o.sha(raw): raise ValueError('RecoveryStageChanged')
        actual.pop(temporary.name, None)
    if actual not in (plan['NewInventory'], inventory): raise ValueError('RecoveryInstallationChanged')
    with register._open() as db: state = register._read(db)
    item = state.get('maintenanceRelease')
    completed = item and item['completed'] and item['identity'] == identity and item['record'] == release
    if phase == 'inspect-release':
        if actual != plan['NewInventory'] or not required.is_file() or files.sha(required) != plan['NewRequiredSha256'] or archive.exists():
            raise ValueError('RecoveryInitialState')
        return dict(m._maintenance('release', register, release, identity, 'inspect'), providerRequests=0)
    if phase == 'release':
        if actual != plan['NewInventory'] or not required.is_file() or files.sha(required) != plan['NewRequiredSha256'] or archive.exists():
            raise ValueError('RecoveryInitialState')
        m._maintenance('release', register, release, identity, 'claim')
    elif not completed:
        m._maintenance('release', register, release, identity, 'check')
    fault('release-claimed'); evidence()
    if not completed:
        if not required.is_file() or files.sha(required) != plan['NewRequiredSha256'] or archive.exists():
            raise ValueError('RecoveryRequiredChanged')
        if not temporary.exists():
            with temporary.open('xb') as output: output.write(raw); output.flush(); os.fsync(output.fileno())
        if files.sha(temporary) != o.sha(raw): raise ValueError('RecoveryStageChanged')
        m._maintenance('release', register, release, identity, 'check'); os.replace(temporary, marker)
        fault('release-marker'); evidence()
        if files.inventory(target) != inventory: raise ValueError('RecoveryReadback')
        m._maintenance('release', register, release, identity, 'complete', m.digest({'files': inventory, 'required': plan['NewRequiredSha256']}))
    fault('release-retired'); evidence()
    if files.inventory(target) != inventory: raise ValueError('RecoveryReadback')
    if archive.exists():
        if required.exists() or files.sha(archive) != plan['NewRequiredSha256']: raise ValueError('RecoveryRequiredChanged')
    else:
        if not required.is_file() or files.sha(required) != plan['NewRequiredSha256']: raise ValueError('RecoveryRequiredChanged')
        required.rename(archive)
    fault('release-detached')
    if required.exists() or files.sha(archive) != plan['NewRequiredSha256']: raise ValueError('RecoveryReadback')
    evidence()
    return {'restored': True, 'authority': False, 'providerRequests': 0, 'manualMode': True}


def main():
    if len(sys.argv) != 4 or not (sys.flags.isolated and sys.flags.no_site): raise ValueError('RecoveryInvocation')
    # Bootstrap only the bounded parser; every executable source is verified again in perform.
    path = Path(sys.argv[1])
    if not path.is_absolute(): raise ValueError('RecoveryPath')
    for p in (path, *path.parents):
        if p.is_symlink() or p.is_junction(): raise ValueError('LinkedPath')
    with path.open('rb') as stream: data = stream.read(2097153)
    if len(data) > 2097152 or hashlib.sha256(data).hexdigest() != sys.argv[2]: raise ValueError('RecoveryEvidencePin')
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result: raise ValueError('DuplicateField')
            result[key] = value
        return result
    plan = json.loads(data, object_pairs_hook=unique)
    return perform(plan, expected_installation=Path(os.environ['LOCALAPPDATA']) / 'Programs/CloudScribe Pro',
        phase=sys.argv[3], plan_path=path)


def safe_failure(error):
    # Never persist exception prose, document contents or arbitrary file paths.
    codes = {'MaintenanceSchema', 'MaintenancePin', 'MaintenanceBinding', 'MaintenanceState', 'MaintenanceClock',
        'MaintenancePending', 'MaintenanceReplay', 'MaintenanceIdentity', 'RecoverySourcePins', 'RecoveryPath',
        'RecoveryPlan', 'RecoveryInstallationRoot', 'RecoveryInterpreterPin', 'RecoveryPathOverlap', 'RecoveryMutableEvidence',
        'RecoveryBackupChanged', 'RequiredGatePin', 'InstalledBuildBinding', 'RecoveryProfile', 'RecoveryEvidencePin',
        'DiagnosticFinancialChanged', 'DiagnosticDatabasePin', 'PendingOrUncertainWork', 'ClosedDatabaseRequired',
        'RecoveryLeaseUnavailable', 'RecoveryReadback', 'RecoveryStageChanged', 'RecoveryDisplacedChanged',
        'RecoveryRequiredChanged', 'RecoveryInitialState', 'RecoveryInstallationChanged', 'RecoveryInvocation',
        'RecoveryJournalNotConfirmed', 'LinkedPath', 'DuplicateField', 'ModuleHash'}
    detail = error.args[0] if len(error.args) == 1 and type(error.args[0]) is str else ''
    if isinstance(error, OSError) and len(error.args) == 2 and error.args[1] == 'RecoveryLeaseUnavailable': detail = 'RecoveryLeaseUnavailable'
    number = getattr(error, 'winerror', None) or getattr(error, 'errno', None)
    result = {'rejected': True, 'code': detail if detail in codes else 'Unclassified', 'detailsAvailable': detail in codes,
        'stage': 'stopped-maintenance', 'requiresReconciliation': True, 'sourceComponent': 'unavailable', 'sourceLine': None,
        'exceptionType': type(error).__name__ if type(error).__name__ in ('ValueError', 'Rejected', 'OSError', 'PermissionError',
            'FileNotFoundError', 'OperationalError') else 'Other', 'systemErrorCode': number if type(number) is int and 0 <= number <= 65535 else None}
    frame = error.__traceback__
    for _ in range(128):
        if frame is None: break
        name = Path(frame.tb_frame.f_code.co_filename).name
        if name in ('maintenance_operator.py', 'maintenance_files.py', 'maintenance_register.py', 'completion_register.py', 'gate_cli.py', 'pending_install_files.py'):
            result['sourceComponent'] = name; result['sourceLine'] = frame.tb_lineno
        frame = frame.tb_next
    return result


if __name__ == '__main__':
    try: print(json.dumps(main(), separators=(',', ':')))
    except Exception as error:
        print(json.dumps(safe_failure(error), separators=(',', ':'))); sys.exit(1)
