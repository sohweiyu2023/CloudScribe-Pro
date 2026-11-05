"""Nonfinancial catalog preparation in the existing sealed register.

Never renews a scope, replaces its clock epoch, admits a policy or clears a stop.
An interrupted lease remains pending and cannot be claimed again after restart.
"""

PREPARATION_FIELDS = frozenset(('SchemaVersion PreparationId ApprovalSha256 ScopeSha256 '
    'PreviousGateSha256 NewBuildSha256 HeldAuthorizationId HeldLedgerSha256 HeldQueueSha256 '
    'FinancialRowsSha256 AcceptedEvidenceSha256 ClockEpochEvidence NotAfterUtc MaximumSeconds '
    'AdministrativeOnly EnableNarration').split())


def preparation_validate(record, identity):
    if (not isinstance(record, dict) or set(record) != PREPARATION_FIELDS
            or type(record['SchemaVersion']) is not int or record['SchemaVersion'] != 1
            or record['AdministrativeOnly'] is not True or record['EnableNarration'] is not False
            or type(record['MaximumSeconds']) is not int or not 1 <= record['MaximumSeconds'] <= 1800):
        raise Rejected('PreparationSchema')
    if digest(record) != identity: raise Rejected('PreparationPin')
    for key, value in record.items():
        if key.endswith('Sha256'): hash_value(value)
    preparation_id(record['PreparationId']); uuid_identity(record['HeldAuthorizationId'])
    e = record['ClockEpochEvidence']
    if (not isinstance(e, dict) or set(e) != {'ObservedUtcMicros', 'ObservedMonoMicros',
            'TrustedUtcLowerMicros', 'TrustedUtcUpperMicros', 'WindowsBootUtc', 'BootReadbackSha256'}):
        raise Rejected('PreparationClock')
    for key in ('ObservedUtcMicros', 'ObservedMonoMicros', 'TrustedUtcLowerMicros', 'TrustedUtcUpperMicros'):
        if type(e[key]) is not int or e[key] < 0: raise Rejected('PreparationClock')
    hash_value(e['BootReadbackSha256'])
    if (not e['TrustedUtcLowerMicros'] <= e['ObservedUtcMicros'] <= e['TrustedUtcUpperMicros']
            or e['TrustedUtcUpperMicros'] - e['TrustedUtcLowerMicros'] > 5000000
            or utc(e['WindowsBootUtc']) > e['TrustedUtcLowerMicros']
            or utc(record['NotAfterUtc']) <= e['TrustedUtcUpperMicros']):
        raise Rejected('PreparationClock')


def preparation_id(value):
    uuid_identity(value)
    if str(uuid.UUID(value)) != value: raise Rejected('PreparationSchema')


def preparation_shape(kind):
    base = {'type', 'utc', 'mono', 'observedMono', 'id', 'identity'}
    if kind == 'preparation-claim': return base | {'plan'}
    if kind == 'preparation-complete': return base | {'receipt'}
    raise Rejected('PreparationSchema')


def preparation_baseline(state, record, identity):
    preparation_validate(record, identity)
    history = state.get('maintenance', [])
    if not history or history[-1]['outcome'] != 'Completed': raise Rejected('PreparationState')
    t = history[-1]['transition']; s = state['scope']
    if (t['TransitionKind'] != 'expired-reserved-diagnostic-preparation'
            or t['PreparationSha256'] != identity or t['ScopeSha256'] != record['ScopeSha256']
            or record['ScopeSha256'] != digest(s) or t['PreviousGateSha256'] != record['PreviousGateSha256']
            or t['NewBuildSha256'] != record['NewBuildSha256'] or state['currentBuild'] != t['NewBuildSha256']
            or state['currentGate'] != t['NewGateSha256']
            or record['ApprovalSha256'] in (s['approvalSha256'], s['privateCaptureConsentSha256'])
            or any(record[k] != t[k] for k in ('HeldAuthorizationId', 'HeldLedgerSha256', 'HeldQueueSha256',
                'FinancialRowsSha256', 'AcceptedEvidenceSha256'))
            or s['schema'] != 6 or s['diagnosticPurpose'] != 'reserved-provider-evidence'
            or not state['activated'] or not state['stopped'] or state['retired'] or state['retirement']
            or state['diagnosticPolicySha256'] is not None or state['diagnosticDispatch'] is not None):
        raise Rejected('PreparationState')


def preparation_clock(record, now, mono, claimed=None):
    e = record['ClockEpochEvidence']; elapsed = mono - e['ObservedMonoMicros']
    if (elapsed < 0 or abs(now - e['ObservedUtcMicros'] - elapsed) > 250000
            or e['TrustedUtcUpperMicros'] + elapsed >= utc(record['NotAfterUtc'])):
        raise Rejected('PreparationClock')
    if claimed and (mono < claimed['observedMono']
            or mono - claimed['observedMono'] >= record['MaximumSeconds'] * 1000000):
        raise Rejected('PreparationExpired')


def preparation_ready(state, record, identity, now, mono, pending=False):
    preparation_baseline(state, record, identity)
    if now < max(state['lastUtc'], utc(state['scope']['endUtc'])): raise Rejected('PreparationClock')
    p = state['pending']
    if pending:
        if (not p or p['type'] != 'preparation-claim' or p['plan'] != record or p['identity'] != identity):
            raise Rejected('PreparationPending')
    elif (p or state['refresh'] >= state['scope']['maxRefreshes']
            or any(x['plan']['ApprovalSha256'] == record['ApprovalSha256']
                or x['plan']['PreparationId'] == record['PreparationId'] for x in state.get('preparation', []))):
        raise Rejected('PreparationReplayOrLimit')
    preparation_clock(record, now, mono, p if pending else None)


def preparation_replay(register, state, event, previous):
    if (type(event['observedMono']) is not int or event['observedMono'] < 0
            or event['mono'] != max(state['lastMono'], event['observedMono'])):
        raise Rejected('PreparationClock')
    history = state.setdefault('preparation', [])
    if event['type'] == 'preparation-claim':
        r = event['plan']; preparation_ready(state, r, event['identity'], event['utc'], event['observedMono'])
        preparation_id(event['id'])
        if event['id'] in state['ids']: raise Rejected('PreparationReplayOrLimit')
        state['refresh'] += 1
        state['ids'].add(event['id']); state['pending'] = dict(event, kind='preparation')
        history.append(dict(plan=r, identity=event['identity'], outcome='Pending'))
    elif event['type'] == 'preparation-complete':
        p = state['pending']
        if not p or event['id'] != p['id'] or event['identity'] != p['identity']: raise Rejected('PreparationPending')
        preparation_ready(state, p['plan'], p['identity'], event['utc'], event['observedMono'], pending=True)
        receipt = event['receipt']
        expected = {'id', 'identitySha256', 'outcome', 'evidenceSha256', 'catalogVerified', 'probeInputEmpty',
            'probeHttpStatus', 'probeStatus', 'audioAbsent'}
        if (not isinstance(receipt, dict) or set(receipt) != expected or receipt['id'] != p['id']
                or receipt['identitySha256'] != p['identity'] or receipt['outcome'] not in ('Succeeded', 'Failed')):
            raise Rejected('PreparationReceipt')
        hash_value(receipt['evidenceSha256'])
        if receipt['outcome'] == 'Succeeded' and (receipt['catalogVerified'] is not True
                or receipt['probeInputEmpty'] is not True or receipt['audioAbsent'] is not True
                or type(receipt['probeHttpStatus']) is not int or receipt['probeHttpStatus'] != 400
                or receipt['probeStatus'] != 'INVALID_ARGUMENT'): raise Rejected('PreparationReceipt')
        history[-1]['outcome'] = receipt['outcome']; state['pending'] = None
    else: raise Rejected('PreparationSchema')


def preparation_operate(register, record, identity, operation_id, phase, receipt=None):
    if phase not in ('claim', 'check', 'complete'): raise Rejected('PreparationSchema')
    with register._open() as db:
        state = register._read(db); now = register.clock(); sample = register.monotonic()
        preparation_ready(state, record, identity, now, sample, pending=phase != 'claim')
        preparation_id(operation_id)
        if phase != 'claim' and state['pending']['id'] != operation_id: raise Rejected('PreparationPending')
        if phase == 'check': return {'valid': True, 'authority': False}
        event = dict(type='preparation-' + phase, utc=now, mono=max(state['lastMono'], sample),
            observedMono=sample, id=operation_id, identity=identity)
        event['plan' if phase == 'claim' else 'receipt'] = record if phase == 'claim' else receipt
        probe = dict(state); probe['ids'] = set(state['ids'])
        probe['preparation'] = [dict(x) for x in state.get('preparation', [])]
        preparation_replay(register, probe, event, state['head'])
        register._append(db, event, state['head']); db.commit(); register._seal(db)
        return {'outcome': 'Succeeded', 'authority': False, 'id': event['id'],
            'identitySha256': identity, 'scopeSha256': register.scope_sha}
