"""Pinned administrative extension of Register, not a second journal or paid workflow.

Executed only by completion_register's hash-verified loader. Its existing namespace
provides Rejected, hashing, UUID, clock and Register primitives. No provider imports.
"""

MAINTENANCE_KEYS = frozenset(('SchemaVersion TransitionKind TransitionId MaintenanceApprovalSha256 '
    'SourceApprovalSha256 ScopeSha256 ExpectedHeadSha256 PreviousGateSha256 NewGateSha256 '
    'PreviousBuildSha256 NewBuildSha256 DatabaseSha256 FinancialRowsSha256 AcceptedEvidenceSha256 '
    'ProfileSha256 OldInventorySha256 NewInventorySha256 PreviousRequiredSha256 NewRequiredSha256 '
    'UsedRefreshes UsedAdmissions UsedResumeAdmissions LastGoodUtcMicros AdministrativeOnly EnableNarration').split())


def maintenance_validate(record, identity):
    if (not isinstance(record, dict) or set(record) != MAINTENANCE_KEYS
            or type(record['SchemaVersion']) is not int or record['SchemaVersion'] != 1
            or record['TransitionKind'] != 'stopped-unused-diagnostic-maintenance'
            or record['AdministrativeOnly'] is not True or record['EnableNarration'] is not False):
        raise Rejected('MaintenanceSchema')
    hash_value(identity)
    if digest(record) != identity:
        raise Rejected('MaintenancePin')
    uuid_identity(record['TransitionId'])
    if str(uuid.UUID(record['TransitionId'])) != record['TransitionId']:
        raise Rejected('MaintenanceSchema')
    for key in record:
        if key.endswith('Sha256'):
            hash_value(record[key])
    for key in ('UsedRefreshes', 'UsedAdmissions', 'UsedResumeAdmissions', 'LastGoodUtcMicros'):
        if type(record[key]) is not int or record[key] < 0:
            raise Rejected('MaintenanceSchema')
    if (record['MaintenanceApprovalSha256'] == record['SourceApprovalSha256']
            or record['PreviousGateSha256'] == record['NewGateSha256']
            or record['PreviousBuildSha256'] == record['NewBuildSha256']):
        raise Rejected('MaintenanceBinding')


def maintenance_shape(kind):
    base = {'type', 'utc', 'mono', 'observedMono', 'id', 'identity'}
    if kind in ('maintenance-claim', 'maintenance-rollback-claim', 'maintenance-release-claim'):
        return base | {'transition'}
    if kind in ('maintenance-complete', 'maintenance-rollback-complete', 'maintenance-release-complete'):
        return base | {'receiptSha256'}
    raise Rejected('MaintenanceSchema')


def maintenance_baseline(state, record):
    scope = state['scope']
    if (scope['schema'] != 6 or not state['activated'] or state['retired'] or state['retirement']
            or not state['stopped'] or state['stopReason'] != 'ObservedFailure'
            or state['lastFailedKind'] != 'refresh' or state['diagnosticDispatch'] is not None
            or record['ScopeSha256'] != digest(scope)
            or record['SourceApprovalSha256'] != scope['approvalSha256']
            or record['ProfileSha256'] != scope['profileSha256']
            or record['AcceptedEvidenceSha256'] != scope['acceptedEvidenceSha256']
            or record['LastGoodUtcMicros'] != state['lastGoodUtc']
            or (record['UsedRefreshes'], record['UsedAdmissions'], record['UsedResumeAdmissions'])
                != (state['refresh'], state['admissions'], state['resume-admission'])):
        raise Rejected('MaintenanceState')


def maintenance_ready(state, record, identity, now, pending=False):
    maintenance_validate(record, identity)
    maintenance_baseline(state, record)
    if now < utc(state['scope']['endUtc']) or now < state['lastUtc']:
        raise Rejected('MaintenanceClock')
    if pending:
        p = state['pending']
        if (not p or p['type'] not in ('maintenance-claim', 'maintenance-rollback-claim')
                or p['id'] != record['TransitionId'] or p['identity'] != identity or p['transition'] != record):
            raise Rejected('MaintenancePending')
    else:
        forbidden = {state['scope']['approvalSha256'], state['scope']['privateCaptureConsentSha256']}
        for item in state.get('maintenance', []):
            forbidden.add(item['transition']['MaintenanceApprovalSha256'])
        w = state['successorWithdrawal']
        if w:
            forbidden.update((w['sourceApproval'], w['withdrawal']['WithdrawalApprovalSha256']))
        if (state['pending'] or record['TransitionId'] in state['ids']
                or record['MaintenanceApprovalSha256'] in forbidden
                or record['ExpectedHeadSha256'] != state['head']
                or record['PreviousGateSha256'] != state['currentGate']
                or record['PreviousBuildSha256'] != state['currentBuild']):
            raise Rejected('MaintenanceBinding')


def maintenance_replay(register, state, event, previous):
    kind = event['type']
    history = state.setdefault('maintenance', [])
    if type(event['observedMono']) is not int or event['observedMono'] < 0 or event['mono'] != max(state['lastMono'], event['observedMono']):
        raise Rejected('MaintenanceClock')
    if kind.startswith('maintenance-release-'):
        maintenance_release_replay(state, event, previous)
        return
    if kind == 'maintenance-claim':
        record = event['transition']
        # Replay's head is the preceding event, not the final head of _read.
        maintenance_ready(dict(state, head=previous), record, event['identity'], event['utc'])
        if record['ExpectedHeadSha256'] != previous or event['id'] != record['TransitionId']:
            raise Rejected('MaintenanceBinding')
        history.append(dict(id=event['id'], identity=event['identity'], transition=record, outcome='Pending'))
        state['ids'].add(event['id']); state['pending'] = dict(event, kind='maintenance')
        return
    if not history:
        raise Rejected('MaintenancePending')
    item = history[-1]; record = item['transition']
    if event['id'] != item['id'] or event['identity'] != item['identity']:
        raise Rejected('MaintenancePending')
    maintenance_baseline(state, record)
    if kind == 'maintenance-rollback-claim':
        if event['transition'] != record or item['outcome'] not in ('Pending', 'Completed'):
            raise Rejected('MaintenanceReplay')
        if ((item['outcome'] == 'Pending' and (not state['pending'] or state['pending']['type'] != 'maintenance-claim'))
                or (item['outcome'] == 'Completed' and state['pending'])):
            raise Rejected('MaintenancePending')
        item['outcome'] = 'RollbackPending'; state['pending'] = dict(event, kind='maintenance-rollback')
        return
    pending = state['pending']
    rollback = kind == 'maintenance-rollback-complete'
    if (not pending or pending['type'] != ('maintenance-rollback-claim' if rollback else 'maintenance-claim')
            or item['outcome'] != ('RollbackPending' if rollback else 'Pending')):
        raise Rejected('MaintenanceReplay')
    hash_value(event['receiptSha256'])
    state['currentGate'] = record['PreviousGateSha256' if rollback else 'NewGateSha256']
    state['currentBuild'] = record['PreviousBuildSha256' if rollback else 'NewBuildSha256']
    state['pending'] = None; item['outcome'] = 'RolledBack' if rollback else 'Completed'


def maintenance_operate(register, record, identity, phase, observed=None):
    if phase not in ('inspect', 'claim', 'check', 'complete', 'rollback-claim', 'rollback-check', 'rollback-complete'):
        raise Rejected('MaintenanceSchema')
    with register._open() as db:
        state = register._read(db); now = register.clock()
        if phase in ('inspect', 'claim'):
            maintenance_ready(state, record, identity, now)
        elif phase == 'rollback-claim':
            maintenance_validate(record, identity); maintenance_baseline(state, record)
            history = state.get('maintenance', [])
            if (not history or history[-1]['identity'] != identity or history[-1]['transition'] != record
                    or history[-1]['outcome'] not in ('Pending', 'Completed') or now < state['lastUtc']):
                raise Rejected('MaintenancePending')
        else:
            maintenance_ready(state, record, identity, now, pending=True)
            rollback = phase.startswith('rollback-')
            if state['pending']['type'] != ('maintenance-rollback-claim' if rollback else 'maintenance-claim'):
                raise Rejected('MaintenancePending')
        if phase in ('inspect', 'check', 'rollback-check'):
            return {'valid': True, 'authority': False}
        kind = 'maintenance-' + phase
        event = dict(type=kind, utc=now, id=record['TransitionId'], identity=identity, observedMono=register.monotonic())
        event['mono'] = max(state['lastMono'], event['observedMono'])
        if phase in ('claim', 'rollback-claim'):
            event['transition'] = record
        else:
            hash_value(observed); event['receiptSha256'] = observed
        # Run the exact replay predicate before appending. It never grants paid authority.
        probe = dict(state); probe['maintenance'] = [dict(x) for x in state.get('maintenance', [])]; probe['ids'] = set(state['ids'])
        maintenance_replay(register, probe, event, state['head'])
        register._append(db, event, state['head']); db.commit(); register._seal(db)
        return {'outcome': 'Succeeded', 'authority': False}


def maintenance_activation(state, original):
    history = state.get('maintenance', [])
    return history[0]['transition']['PreviousGateSha256'] if history else original


def maintenance_release_ready(state, record, identity, now, head, pending=False):
    keys = {'SchemaVersion', 'ReleaseId', 'ReleaseApprovalSha256', 'MaintenanceIdentitySha256',
        'ScopeSha256', 'ExpectedHeadSha256', 'ManualInventorySha256', 'RequiredArchivePathSha256',
        'AdministrativeOnly', 'PaidAuthority'}
    if (not isinstance(record, dict) or set(record) != keys or type(record['SchemaVersion']) is not int
            or record['SchemaVersion'] != 1 or record['AdministrativeOnly'] is not True or record['PaidAuthority'] is not False):
        raise Rejected('MaintenanceSchema')
    hash_value(identity)
    if digest(record) != identity: raise Rejected('MaintenancePin')
    uuid_identity(record['ReleaseId'])
    if str(uuid.UUID(record['ReleaseId'])) != record['ReleaseId']:
        raise Rejected('MaintenanceSchema')
    for key in keys:
        if key.endswith('Sha256'): hash_value(record[key])
    history = state.get('maintenance', [])
    if not history or history[-1]['outcome'] != 'Completed': raise Rejected('MaintenanceState')
    maintenance_baseline(state, history[-1]['transition'])
    if now < utc(state['scope']['endUtc']) or now < state['lastUtc']: raise Rejected('MaintenanceClock')
    if (record['ScopeSha256'] != digest(state['scope']) or record['MaintenanceIdentitySha256'] != history[-1]['identity']):
        raise Rejected('MaintenanceIdentity')
    if pending:
        p = state['pending']
        if not p or p['type'] != 'maintenance-release-claim' or p['transition'] != record or p['identity'] != identity:
            raise Rejected('MaintenancePending')
    elif (state['pending'] or state.get('maintenanceRelease') or record['ExpectedHeadSha256'] != head
            or record['ReleaseId'] in state['ids'] or record['ReleaseApprovalSha256'] in
            ({item['transition']['MaintenanceApprovalSha256'] for item in history}
             | {state['scope']['approvalSha256'], state['scope']['privateCaptureConsentSha256']})):
        raise Rejected('MaintenanceBinding')
    w = state['successorWithdrawal']
    if w and record['ReleaseApprovalSha256'] in (w['sourceApproval'], w['withdrawal']['WithdrawalApprovalSha256']):
        raise Rejected('MaintenanceBinding')


def maintenance_release_replay(state, event, previous):
    if event['type'] == 'maintenance-release-claim':
        record = event['transition']
        maintenance_release_ready(state, record, event['identity'], event['utc'], previous)
        if event['id'] != record['ReleaseId']: raise Rejected('MaintenanceBinding')
        state['pending'] = dict(event, kind='maintenance-release'); state['ids'].add(event['id'])
        state['maintenanceRelease'] = {'record': record, 'identity': event['identity'], 'completed': False}
    else:
        p = state['pending']
        if not p or p['id'] != event['id'] or p['identity'] != event['identity']: raise Rejected('MaintenancePending')
        maintenance_release_ready(state, p['transition'], event['identity'], event['utc'], previous, pending=True)
        hash_value(event['receiptSha256'])
        state['pending'] = None; state['retired'] = True; state['maintenanceRelease']['completed'] = True


def maintenance_release(register, record, identity, phase, observed=None):
    if phase not in ('inspect', 'claim', 'check', 'complete'): raise Rejected('MaintenanceSchema')
    with register._open() as db:
        state = register._read(db); now = register.clock()
        maintenance_release_ready(state, record, identity, now, state['head'], pending=phase in ('check', 'complete'))
        if phase in ('inspect', 'check'): return {'valid': True, 'authority': False}
        sample = register.monotonic()
        event = dict(type='maintenance-release-' + phase, utc=now, mono=max(state['lastMono'], sample),
            observedMono=sample, id=record['ReleaseId'], identity=identity)
        # A monotonic sample is audit evidence, never a renewed clock epoch.
        if phase == 'claim': event['transition'] = record
        else:
            hash_value(observed); event['receiptSha256'] = observed
        probe = dict(state); probe['ids'] = set(state['ids'])
        if state.get('maintenanceRelease'): probe['maintenanceRelease'] = dict(state['maintenanceRelease'])
        maintenance_replay(register, probe, event, state['head'])
        register._append(db, event, state['head']); db.commit(); register._seal(db)
        return {'outcome': 'Succeeded', 'authority': False}
