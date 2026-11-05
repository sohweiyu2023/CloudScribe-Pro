"""Hash-pinned successor validation shared by the existing register entrypoints."""

def successor_ready(self, old_state, successor, new_scope, h, identity, pending=False, check_clock=True):
    if released_diagnostic_handoff(h):
        return successor_released_ready(self, old_state, successor, new_scope, h, identity, pending, check_clock)
    if old_state["retired"] and not pending:
        raise Rejected("RunRetired")
    validate_handoff(h); hash_value(identity); validate_scope(new_scope)
    reject_withdrawn_handoff(old_state, h)
    if old_state["pending"] and old_state["pending"]["type"] == "successor-withdraw-claim":
        raise Rejected(WITHDRAWAL_STATE)
    typed_interpreter = interpreter_handoff(h)
    typed_pair = typed_interpreter and (old_state["scope"]["schema"], new_scope["schema"], h["SchemaVersion"]) == (4, 4, 3)
    sentence_pair = sentence_handoff(h) and (old_state["scope"]["schema"], new_scope["schema"], h["SchemaVersion"]) == (4, 5, 4)
    diagnostic_pair = diagnostic_handoff(h) and (old_state["scope"]["schema"], new_scope["schema"], h["SchemaVersion"]) in ((5, 6, 5),(5, 6, 6),(6, 6, 7),(6, 6, 9))
    if (((old_state["scope"]["schema"], new_scope["schema"], h["SchemaVersion"]) not in ((1, 2, 1), (2, 3, 2), (3, 4, 3)) and not typed_pair and not sentence_pair and not diagnostic_pair)
            or (typed_interpreter and not typed_pair)
            or (sentence_handoff(h) and not sentence_pair)
            or h["PreviousScopeSha256"] != self.scope_sha or h["NewScopeSha256"] != successor.scope_sha
            or digest(new_scope) != successor.scope_sha or new_scope["predecessorScopeSha256"] != self.scope_sha
            or physical(new_scope["predecessorRegisterPath"]) != self.path
            or new_scope["registerPathSha256"] != path_digest(successor.path)
            or h["TransitionId"] != new_scope["transitionId"] or h["SourceApprovalSha256"] != new_scope["approvalSha256"]
            or new_scope["approvalSha256"] == old_state["scope"]["approvalSha256"]
            or any(new_scope[k] != old_state["scope"][k] for k in ("profileSha256", "accountSha256", "projectSha256"))):
        raise Rejected("SuccessorScopeBinding")
    if diagnostic_handoff(h):
        if not diagnostic_pair:raise Rejected("DiagnosticCounterOrEpoch")
        diagnostic_window_binding(old_state,new_scope,h)
        if not expired_diagnostic_handoff(h) and self._time_rejection(old_state):raise Rejected(self._time_rejection(old_state))
    if sentence_pair:
        clock_stop = (clock_sentence_handoff(h) and old_state["stopReason"] in ("ClockRollback","ClockDiscontinuity")) or (expired_sentence_handoff(h) and old_state["stopReason"] == "WindowExpired")
        changed_window=any(new_scope[k] != old_state["scope"][k] for k in ("startUtc", "endUtc"))
        if expired_sentence_handoff(h):
            e=h["ClockEpochEvidence"]
            if (not changed_window or utc(new_scope["startUtc"]) != e["ObservedUtcMicros"]
                    or e["TrustedUtcLowerMicros"] < utc(old_state["scope"]["endUtc"])
                    or utc(new_scope["startUtc"]) < utc(old_state["scope"]["endUtc"])):
                raise Rejected("ExpiredWindowBinding")
        if ((changed_window and not expired_sentence_handoff(h))
                or not old_state["activated"] or (old_state["stopped"] and not clock_stop)
                or (h["UsedRefreshes"], h["UsedAdmissions"], h["UsedResumeAdmissions"]) != (old_state["refresh"], old_state["admissions"], old_state["resume-admission"])):
            raise Rejected("SentenceCounterOrWindowBinding")
    typed_zero_effect = (old_state["scope"]["schema"] == 4 and old_state["activated"]
                         and old_state["refresh"] == 0 and old_state["admissions"] == 0
                         and old_state["buildTransitions"] == 0 and old_state["helperSourceRepairs"] == 0
                         and old_state["interpreterActivation"] is None)
    if typed_interpreter and (not typed_zero_effect or (not pending and not interpreter_pre_effect(old_state))
            or new_scope["wholeArticleGrantSha256"] == old_state["scope"]["wholeArticleGrantSha256"]
            or h["RepairApprovalSha256"] == old_state["scope"]["approvalSha256"]):
        raise Rejected("SuccessorScopeBinding")
    if h["SchemaVersion"] == 3 and self.clock() < utc(old_state["scope"]["endUtc"]):
        raise Rejected("WholeArticlePredecessorNotExpired")
    if h["SchemaVersion"] in (2, 3, 4, 5, 6, 7, 9):
        if (h["OriginalManifestSha256"] != old_state["scope"]["plansSha256"]
                or h["AcceptedEvidenceSha256"] != new_scope["acceptedEvidenceSha256"]):
            raise Rejected("ContinuationAcceptedProof")
        self._require_ancestor_snapshot(old_state, h["PreviousGateSha256"])
    if old_state["stopped"] and old_state["stopReason"] != "WindowExpired" and not (diagnostic_renewal_handoff(h) and unused_diagnostic_predecessor(old_state)) and not ((clock_sentence_handoff(h) or expired_diagnostic_handoff(h)) and old_state["stopReason"] in ("ClockRollback","ClockDiscontinuity")):
        raise Rejected("RetirementClockOrStop")
    if check_clock:
        self._retirement_observation(old_state, self.clock(), self.monotonic(), h)
    if pending:
        item = old_state["retirement"]
        if not item or item["identity"] != identity or item["handoff"] != h:
            raise Rejected("RetirementIdentityMismatch")
    else:
        if old_state["retirement"] or old_state["pending"] or h["TransitionId"] in old_state["ids"]:
            raise Rejected("SuccessorPending")
        if old_state["head"] != h["ExpectedPreviousHeadSha256"]:
            raise Rejected("SuccessorHeadMismatch")
        if old_state["currentGate"] != h["PreviousGateSha256"]:
            raise Rejected("SuccessorScopeBinding")
    now = successor.clock()
    if check_clock and not utc(new_scope["startUtc"]) <= now < utc(new_scope["endUtc"]):
        raise Rejected("OutsideWindow")


def successor_ancestor(self, current, config_pin, seen=None, maximum_depth=None):
    """Read only terminal ancestors, never recursively acquire an owned lease."""
    seen = set() if seen is None else set(seen)
    renewed = current["activation"] and (diagnostic_renewal_handoff(current["activation"].get("sentenceHandoff")) or released_diagnostic_handoff(current["activation"].get("sentenceHandoff")) or reserved_diagnostic_handoff(current["activation"].get("sentenceHandoff")))
    maximum_depth = (9 if current["scope"].get("diagnosticPurpose") == "reserved-provider-evidence" else 8 if renewed else 7 if current["scope"]["schema"] == 6 else 6 if current["scope"]["schema"] == 5 else 5) if maximum_depth is None else maximum_depth
    identity = os.path.normcase(str(self.path))
    if identity in seen or len(seen) >= maximum_depth:
        raise Rejected("PredecessorNotRetired")
    seen.add(identity)
    schema = current["scope"]["schema"]
    if schema == 1:
        return
    scope = current["scope"]
    old = Register(scope["predecessorRegisterPath"], scope["predecessorScopeSha256"])
    if not old.path.is_file() or old.path.stat().st_size > 2_000_000:
        raise Rejected("RegisterUnavailableOrOversized")
    with closing(sqlite3.connect(old.path.as_uri() + "?mode=ro", uri=True, timeout=0)) as db:
        db.execute("PRAGMA query_only=ON"); db.execute("BEGIN")
        state = old._read(db)
        a = current["activation"]; item = state["retirement"]
        activation_pin = (a["gateSha256"] if a and schema == 4 and current["helperSourceRepairs"] == 1 else config_pin)
        if current.get("maintenance"): activation_pin = _maintenance("activation", current, activation_pin)
        typed_pair = schema == 4 and state["scope"]["schema"] == 4 and item and interpreter_handoff(item["handoff"])
        typed_pair = typed_pair or (schema == 6 and state["scope"]["schema"] == 6 and item and (diagnostic_renewal_handoff(item["handoff"]) or released_diagnostic_handoff(item["handoff"]) or reserved_diagnostic_handoff(item["handoff"])))
        if ((state["scope"]["schema"] != schema - 1 and not typed_pair) or not state["retired"] or state["pending"] or not a or not item
                or item["identity"] != a["identity"] or state["head"] != a["previousRetirementHead"]
                or item["id"] != scope["transitionId"] or item["handoff"]["NewScopeSha256"] != self.scope_sha
                or item["handoff"]["NewGateSha256"] != activation_pin or current["currentGate"] != config_pin
                or item["handoff"]["SourceApprovalSha256"] != scope["approvalSha256"]
                or item["handoff"]["PreviousGateSha256"] != state["currentGate"]
                or ("interpreterHandoff" in a and a["interpreterHandoff"] != item["handoff"])
                or (schema in (3, 4, 5, 6) and ((item["handoff"]["SchemaVersion"] != schema - 1 and not (schema==6 and expired_diagnostic_handoff(item["handoff"])))
                    or item["handoff"]["OriginalManifestSha256"] != state["scope"]["plansSha256"]
                    or item["handoff"]["AcceptedEvidenceSha256"] != scope["acceptedEvidenceSha256"]))):
            raise Rejected("PredecessorNotRetired")
        if schema == 6:
            h = item["handoff"]
            if not diagnostic_handoff(h) or a.get("sentenceHandoff") != h:raise Rejected("DiagnosticCounterOrEpoch")
            diagnostic_window_binding(state,scope,h)
            if current["clockEpoch"] != h.get("ClockEpochEvidence",state["clockEpoch"]):raise Rejected("DiagnosticCounterOrEpoch")
        if schema == 5:
            h = item["handoff"]
            if (not sentence_handoff(h) or a.get("sentenceHandoff") != h
                    or (not expired_sentence_handoff(h) and any(scope[k] != state["scope"][k] for k in ("startUtc", "endUtc")))
                    or (expired_sentence_handoff(h) and (utc(scope["startUtc"]) != h["ClockEpochEvidence"]["ObservedUtcMicros"] or h["ClockEpochEvidence"]["TrustedUtcLowerMicros"] < utc(state["scope"]["endUtc"])))
                    or (h["UsedRefreshes"], h["UsedAdmissions"], h["UsedResumeAdmissions"]) != (state["refresh"], state["admissions"], state["resume-admission"])):
                raise Rejected("SentenceCounterOrWindowBinding")
        old._require_ancestor_snapshot(state, state["currentGate"], seen, maximum_depth)


def successor_released_baseline(state, h):
    validate_handoff(h)
    release = state.get('maintenanceRelease')
    history = state.get('maintenance', [])
    if (not released_diagnostic_handoff(h) or not state['retired'] or not state['stopped']
            or not unused_diagnostic_predecessor(state) or not release or not release['completed']
            or not history or history[-1]['outcome'] != 'Completed'
            or release['identity'] != h['ManualReleaseSha256']
            or digest(release['record']) != h['ManualReleaseSha256']
            or release['record']['MaintenanceIdentitySha256'] != history[-1]['identity']
            or h['RequiredArchiveSha256'] != history[-1]['transition']['NewRequiredSha256']
            or h['PreviousScopeSha256'] != digest(state['scope'])
            or h['OriginalManifestSha256'] != state['scope']['plansSha256']
            or h['AcceptedEvidenceSha256'] != state['scope']['acceptedEvidenceSha256']
            or h['PreviousGateSha256'] != state['currentGate']
            or h['InheritedClockEpochEvidence'] != state['clockEpoch']
            or (h['UsedRefreshes'], h['UsedAdmissions'], h['UsedResumeAdmissions']) !=
                (state['refresh'], state['admissions'], state['resume-admission'])):
        raise Rejected('ReleasedDiagnosticState')
    previous = {state['scope']['approvalSha256'], state['scope']['privateCaptureConsentSha256'],
                release['record']['ReleaseApprovalSha256']}
    scope = state['scope']; seen = set()
    while scope['schema'] != 1:
        path = physical(scope['predecessorRegisterPath']); identity = os.path.normcase(str(path))
        if identity in seen or len(seen) >= 7 or not path.is_file() or path.stat().st_size > 2_000_000:
            raise Rejected('PredecessorNotRetired')
        seen.add(identity)
        with closing(sqlite3.connect(path.as_uri() + '?mode=ro', uri=True, timeout=0)) as ancestor:
            ancestor.execute('PRAGMA query_only=ON')
            row = ancestor.execute('SELECT body FROM scope WHERE id=1').fetchone()
        if not row: raise Rejected('PredecessorNotRetired')
        earlier = json.loads(row[0]); validate_scope(earlier)
        if digest(earlier) != scope['predecessorScopeSha256'] or earlier['registerPathSha256'] != path_digest(path):
            raise Rejected('PredecessorNotRetired')
        previous.add(earlier['approvalSha256'])
        if earlier.get('privateCaptureConsentSha256'): previous.add(earlier['privateCaptureConsentSha256'])
        scope = earlier
    previous.update(item['transition']['MaintenanceApprovalSha256'] for item in history)
    activation = state.get('activation', {}) or {}
    for transition in (activation.get('sentenceHandoff', {}),
                       (state.get('successorWithdrawal') or {}).get('withdrawal', {})):
        previous.update(value for key, value in transition.items() if key.endswith('ApprovalSha256'))
    withdrawn = state.get('successorWithdrawal')
    if withdrawn: previous.add(withdrawn['sourceApproval'])
    if (h['SourceApprovalSha256'] in previous or h['FreshPrivateCaptureConsentSha256'] in previous
            or h['SourceApprovalSha256'] == h['FreshPrivateCaptureConsentSha256']):
        raise Rejected('FreshReleasedDiagnosticApprovalRequired')


def successor_released_window(old, scope, h):
    successor_released_baseline(old, h)
    epoch = h['ClockEpochEvidence']
    if (scope['schema'] != 6 or scope['diagnosticPurpose'] != 'unchanged-provider-evidence' or scope['diagnosticWindowMode'] != 'fresh-10-minute'
            or scope['privateCaptureConsentSha256'] != h['FreshPrivateCaptureConsentSha256']
            or scope['acceptedEvidenceSha256'] != old['scope']['acceptedEvidenceSha256']
            or utc(scope['startUtc']) != epoch['ObservedUtcMicros']
            or epoch['TrustedUtcLowerMicros'] < utc(old['scope']['endUtc'])
            or epoch['PreviousLastGoodUtcMicros'] != old['lastGoodUtc']):
        raise Rejected('ReleasedDiagnosticWindow')


def successor_released_ready(self, state, successor, scope, h, identity, pending, check_clock):
    validate_scope(scope); hash_value(identity)
    successor_released_window(state, scope, h)
    self._require_ancestor_snapshot(state, state['currentGate'])
    if (digest(scope) != successor.scope_sha
            or h['NewScopeSha256'] != successor.scope_sha or h['PreviousScopeSha256'] != self.scope_sha
            or scope['predecessorScopeSha256'] != self.scope_sha
            or physical(scope['predecessorRegisterPath']) != self.path
            or scope['registerPathSha256'] != path_digest(successor.path)
            or scope['transitionId'] != h['TransitionId'] or scope['approvalSha256'] != h['SourceApprovalSha256']
            or any(scope[k] != state['scope'][k] for k in ('profileSha256', 'accountSha256', 'projectSha256'))):
        raise Rejected('ReleasedDiagnosticBinding')
    item = state['retirement']
    if pending:
        if (not item or item['type'] != 'released-diagnostic-claim' or item['identity'] != identity
                or item['handoff'] != h or (state['pending'] is None and not item.get('completed'))
                or (state['pending'] is not None and state['pending'] != item)):
            raise Rejected('ReleasedDiagnosticPending')
    elif (state['pending'] or item or h['TransitionId'] in state['ids']
          or h['ExpectedPreviousHeadSha256'] != state['head']):
        raise Rejected('ReleasedDiagnosticAlreadyClaimed')
    if check_clock:
        successor_released_clock(state, h, successor.clock(), successor.monotonic())


def successor_released_clock(state, h, now, raw_mono, logical_mono=None):
    _, drift = Register._retirement_observation(state, now, raw_mono, h)
    epoch = h['ClockEpochEvidence']; elapsed = raw_mono - epoch['ObservedMonoMicros']
    if epoch['TrustedUtcUpperMicros'] + elapsed + 250_000 >= epoch['ObservedUtcMicros'] + 600_000_000:
        raise Rejected('WindowExpired')
    if logical_mono is not None and logical_mono != max(state['lastMono'], raw_mono):
        raise Rejected('ClockRollback')
    return drift


def successor_released_replay(state, event, previous):
    if event['type'] == 'released-diagnostic-claim':
        h = event['handoff']; successor_released_baseline(state, h)
        if (state['pending'] or state['retirement'] or event['id'] != h['TransitionId']
                or h['ExpectedPreviousHeadSha256'] != previous
                or event['id'] in state['ids'] or event['clockCondition'] != 'released-terminal'
                or event['clockDriftMicros'] != successor_released_clock(state, h, event['utc'], event['observedMono'], event['mono'])):
            raise Rejected('ReleasedDiagnosticBinding')
        item = dict(event, kind='released-diagnostic')
        state['pending'] = state['retirement'] = item
        state['ids'].add(event['id'])
    else:
        item = state['pending']
        if (not item or item['type'] != 'released-diagnostic-claim' or event['id'] != item['id']
                or event['identity'] != item['identity']):
            raise Rejected('ReleasedDiagnosticPending')
        successor_released_baseline(state, item['handoff']); hash_value(event['receiptSha256'])
        successor_released_clock(state, item['handoff'], event['utc'], event['observedMono'], event['mono'])
        state['retirement'] = dict(item, completed=True)
        state['pending'] = None

def successor_verify_reserved_policy(manifest, scope, policy, kind):
    """Fresh single-dispatch authority over an exact existing cent; no new grant."""
    d = manifest["diagnostic"]; r = d["reservedResume"]
    required = set("SchemaVersion MappingId AppSha256 InfrastructureSha256 ProfileRootSha256 SourceApprovalSha256 ExpiresAtUtc HistoricalPolicy AuthorizationId OriginalAttemptRowSha256 CleanQueueRowSha256 FinancialSha256 OriginalApprovedAtUtc OriginalEnvelope ExecutionEnvelope CatalogSnapshotId CatalogExpiresAtUtc ProviderEvidenceSha256 SelectionSha256 RuntimeEvidenceSha256 RequestIdentitySha256 MaximumExistingDispatches MaximumNewReservations MaximumAdditionalReservationMinorUnits".split())
    if (kind != "resume-admission" or not isinstance(policy, dict) or set(policy) not in (required, required | {"PriorMappingSha256"})
            or type(policy["SchemaVersion"]) is not int or policy["SchemaVersion"] != 1
            or policy["SourceApprovalSha256"] != scope["approvalSha256"]
            or policy["ProfileRootSha256"] != scope["profileSha256"]
            or hashlib.sha256((policy["AppSha256"]+"|"+policy["InfrastructureSha256"]).encode()).hexdigest() != scope["buildSha256"]):
        raise Rejected("DiagnosticPolicy")
    for key,value in policy.items():
        if key.endswith("Sha256"):hash_value(value)
    for key in ("MappingId", "AuthorizationId", "CatalogSnapshotId"):uuid_identity(policy[key])
    if (uuid_identity(policy["AuthorizationId"]) != uuid_identity(r["authorizationId"])
            or policy["OriginalAttemptRowSha256"] != r["attemptRowSha256"]
            or policy["CleanQueueRowSha256"] != r["cleanQueueRowSha256"]
            or policy["FinancialSha256"] != r["financialSha256"]
            or digest(policy["HistoricalPolicy"]) != r["historicalPolicyCanonicalSha256"]
            or policy["HistoricalPolicy"]["SourceApprovalSha256"] != r["originalApprovalSha256"]
            or any(type(policy[k]) is not int or policy[k] != v for k,v in
                (("MaximumExistingDispatches",1),("MaximumNewReservations",0),("MaximumAdditionalReservationMinorUnits",0)))):
        raise Rejected("DiagnosticChild")
    expiry = datetime.fromisoformat(policy["ExpiresAtUtc"].replace("Z","+00:00"))
    catalog = datetime.fromisoformat(policy["CatalogExpiresAtUtc"].replace("Z","+00:00"))
    approved = datetime.fromisoformat(policy["OriginalApprovedAtUtc"].replace("Z","+00:00"))
    if (any(x.utcoffset() != timedelta(0) for x in (expiry,catalog,approved))
            or not approved.timestamp() <= time.time() < expiry.timestamp() <= catalog.timestamp()
            or int(expiry.timestamp()*1e6) > utc(scope["endUtc"])):raise Rejected("PolicyWindow")
    original = policy["OriginalEnvelope"]; execution = policy["ExecutionEnvelope"]
    envelope_keys = set("AccountId CredentialReferenceId CapabilityProvenanceId PricingProvenanceId RequestRevision VoiceName AudioEncoding CompiledPayloadSha256 CompiledPayloadBytes RecoveryAuthorizationId ChirpAuthorizationId".split())
    part = next(x for x in manifest["parts"] if x["planSha256"] == d["planSha256"] and x["coverageOrdinal"] == d["coverageOrdinal"])
    for e in (original,execution):
        if (not isinstance(e,dict) or set(e) != envelope_keys or e["RecoveryAuthorizationId"] is not None
                or uuid_identity(e["ChirpAuthorizationId"]) != uuid_identity(r["authorizationId"])
                or e["AccountId"] != manifest["accountId"] or e["AudioEncoding"] != "MP3"
                or e["CompiledPayloadSha256"] != d["payloadSha256"] or e["CompiledPayloadBytes"] != d["payloadBytes"]
                or e["VoiceName"] != part["voiceName"] or type(e["RequestRevision"]) is not int or e["RequestRevision"] < 0):
            raise Rejected("DiagnosticPayload")
    if any(original[k] != execution[k] for k in envelope_keys - {"RequestRevision"}):raise Rejected("DiagnosticPayload")
    history = policy["HistoricalPolicy"]
    # Historical policy is checked at the real reservation time, never as current authority.
    old_manifest = dict(manifest,diagnostic={k:v for k,v in d.items() if k != "reservedResume"})
    old_manifest["diagnostic"]["projectCommittedMinorUnits"] -= 1
    old_manifest["diagnostic"]["allocationCommittedMinorUnits"] -= 1
    old_manifest["diagnostic"]["privateCaptureConsentSha256"] = history["DiagnosticEvidence"]["PrivateCaptureConsentSha256"]
    old_scope = dict(scope, diagnosticPurpose="unchanged-provider-evidence", approvalSha256=r["originalApprovalSha256"],
        privateCaptureConsentSha256=old_manifest["diagnostic"]["privateCaptureConsentSha256"], plansSha256=digest(old_manifest))
    # Existing validator intentionally uses wall-clock for ordinary fresh admission.
    # A supplied historical time is scoped to this one validation only.
    verify_diagnostic_policy(old_manifest,old_scope,history,"diagnostic-admission", observed_time=approved.timestamp())
