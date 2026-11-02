"""Synthetic historical interpreter mismatch; no providers, credentials or live paths."""
import copy
from contextlib import closing
from datetime import datetime,timedelta,timezone
import json
from pathlib import Path
import sqlite3
import time
import uuid
import completion_register as m
import test_continuation_cli as fixtures

def prepare(owner, whole_end_after=None):
    f = fixtures.ContinuationCliTests(); f.setUp(); owner.addCleanup(f.doCleanups)
    # All paths belong to the existing synthetic temporary fixture. The scope
    # retains an exact6h interval; naturally wait for its near end, no OS clock change.
    # Allow hosted-runner process startup before natural expiry, matching the
    # existing whole-article CLI fixture. The synthetic scope remains six hours;
    # production deadlines and every runtime expiry check are unchanged.
    end = datetime.now(timezone.utc) + timedelta(seconds=30)
    f.scope.update(startUtc=(end-timedelta(hours=6)).isoformat().replace('+00:00', 'Z'),
                   endUtc=end.isoformat().replace('+00:00', 'Z'))
    f.scope_path.write_text(json.dumps(f.scope))
    f.config['ScopeSha256'] = m.digest(f.scope); f.config_path.write_text(json.dumps(f.config))
    f.pin = fixtures.sha(f.config_path.read_bytes())
    f.h.update(NewScopeSha256=f.config['ScopeSha256'], NewGateSha256=f.pin)
    f.h_path.write_text(json.dumps(f.h)); f.h_pin = fixtures.sha(f.h_path.read_bytes())
    f.activate(); old_policy_path = f.admit(); old_policy = json.loads(old_policy_path.read_text())
    for ordinal in (8,9,10): f.add_attempt(ordinal, old_policy, accepted=True)
    old_manifest = copy.deepcopy(f.manifest); old_config = copy.deepcopy(f.config)
    old_config_path = f.config_path; old_pin = f.pin
    old_r = m.Register(old_config['RegisterPath'], old_config['ScopeSha256'])
    with old_r._open() as c:
        before_state = old_r._read(c)
        before_events = c.execute('SELECT seq,body,sha FROM events ORDER BY seq').fetchall()
    accepted = f.accepted_entries(); owner.assertEqual(10,len(accepted))
    grant = {'GrantId':str(uuid.uuid4()),'PlanSha256s':[f.f.proposal['PlanSha256']],'MaximumCatalogRefreshes':24}
    root = f.new_folder / 'whole4'; root.mkdir()
    f.new_folder = root
    f.manifest = copy.deepcopy(old_manifest)
    f.manifest['continuation'].update(accepted=accepted,financialRowsSha256=f.financial_pin())
    f.manifest['continuation']['budget'].update(projectCommittedMinorUnits=163,allocationCommittedMinorUnits=33)
    manifest_path = root/'manifest.json'; manifest_path.write_text(json.dumps(f.manifest))
    time.sleep(max(0,end.timestamp()-time.time()+0.05))
    start = datetime.now(timezone.utc)
    f.scope = dict(f.scope,schema=4,wholeArticleGrantSha256=m.digest(grant),maximumNewAttempts=116,
        approvalSha256=m.digest({'synthetic':'new whole article approval'}),
        predecessorRegisterPath=old_config['RegisterPath'],predecessorScopeSha256=old_config['ScopeSha256'],
        registerPathSha256=m.path_digest(root/'register.db'),transitionId=str(uuid.uuid4()),
        plansSha256=m.digest(f.manifest),acceptedEvidenceSha256=m.digest(accepted),
        startUtc=start.isoformat().replace('+00:00','Z'),endUtc=(start+timedelta(hours=6)).isoformat().replace('+00:00','Z'))
    if whole_end_after is not None:
        end=start+timedelta(seconds=whole_end_after)
        f.scope.update(startUtc=(end-timedelta(hours=6)).isoformat().replace('+00:00','Z'),endUtc=end.isoformat().replace('+00:00','Z'))
    f.scope_path = root/'scope.json'; f.scope_path.write_text(json.dumps(f.scope))
    f.config = dict(old_config,RegisterPath=str(root/'register.db'),ScopeSha256=m.digest(f.scope),
                    PlanManifestPath=str(manifest_path),PlanManifestSha256=fixtures.sha(manifest_path.read_bytes()))
    candidate=root/'package'/'app';candidate.mkdir(parents=True)
    for name in ('CloudScribe.dll','CloudScribe.Infrastructure.dll'):
        (candidate/name).write_bytes((Path(old_config['CandidateApplicationDirectory'])/name).read_bytes())
    f.config['CandidateApplicationDirectory']=str(candidate)
    old_sources=root/'old-source';old_sources.mkdir()
    for name in ('gate_cli.py','completion_register.py'):
        (old_sources/name).write_bytes((f.f.source/name).read_bytes()+b'\n# preserved synthetic historical source\n')
    f.config.update(PythonSha256=m.digest({'unavailable':'synthetic former interpreter'}),ScriptPath=str(old_sources/'gate_cli.py'),ScriptSha256=fixtures.sha((old_sources/'gate_cli.py').read_bytes()),ModuleSha256=fixtures.sha((old_sources/'completion_register.py').read_bytes()))
    f.config_path = root/'gate.json'; f.config_path.write_text(json.dumps(f.config)); f.pin = fixtures.sha(f.config_path.read_bytes())
    f.h = dict(f.h,SchemaVersion=3,TransitionKind='whole-article-continuation',TransitionId=f.scope['transitionId'],
        SourceApprovalSha256=f.scope['approvalSha256'],PreviousConfigPath=str(old_config_path),PreviousGateSha256=old_pin,
        PreviousScopeSha256=old_config['ScopeSha256'],ExpectedPreviousHeadSha256=before_state['head'],
        NewConfigPath=str(f.config_path),NewGateSha256=f.pin,NewScopePath=str(f.scope_path),NewScopeSha256=f.config['ScopeSha256'],
        DatabaseSha256=fixtures.sha(f.f.db.read_bytes()),OriginalManifestSha256=m.digest(old_manifest),AcceptedEvidenceSha256=m.digest(accepted))
    f.h_path = root/'handoff.json'; f.h_path.write_text(json.dumps(f.h)); f.h_pin=fixtures.sha(f.h_path.read_bytes())
    new_r=m.Register(f.config['RegisterPath'],f.config['ScopeSha256'])
    old_r.inspect_successor(new_r,f.scope,f.h,f.h_pin)
    old_r.claim_successor(new_r,f.scope,f.h,f.h_pin)
    old_r.check_successor(new_r,f.scope,f.h,f.h_pin)
    old_r.complete_successor(new_r,f.scope,f.h,f.h_pin,fixtures.sha(b'synthetic installed readback'))
    f.f.b.write_installed(f.config,f.pin,f.config_path)
    return f


def repair(f):
    import sys
    old=dict(f.config);old_path=f.config_path;old_pin=f.pin
    f.config=dict(old,PythonSha256=fixtures.sha(Path(sys.executable).read_bytes()),ScriptPath=str(f.f.source/'gate_cli.py'),ScriptSha256=fixtures.sha((f.f.source/'gate_cli.py').read_bytes()),ModuleSha256=fixtures.sha((f.f.source/'completion_register.py').read_bytes()))
    f.config_path=f.new_folder/'repaired-gate.json';f.config_path.write_text(json.dumps(f.config));f.pin=fixtures.sha(f.config_path.read_bytes())
    approval=m.digest({'synthetic':'distinct interpreter repair approval'})
    evidence=dict(SchemaVersion=1,RepairApprovalSha256=approval,PreviousPythonSha256=old['PythonSha256'],NewPythonSha256=f.config['PythonSha256'],PythonPathSha256=m.path_digest(sys.executable),FileBytes=Path(sys.executable).stat().st_size,PythonVersion=sys.version.split()[0],PublisherName='Python Software Foundation',SignerThumbprint='A'*40)
    evidence_path=f.new_folder/'identity.json';evidence_path.write_text(json.dumps(evidence))
    r=m.Register(f.config['RegisterPath'],f.config['ScopeSha256'])
    with r._open() as c:state=r._read(c)
    build=m.digest({'AppSha256':old['AppSha256'],'InfrastructureSha256':old['InfrastructureSha256']})
    # Use the production build digest, which deliberately binds both DLL pins.
    import gate_cli
    build=gate_cli.build_digest(old)
    t=dict(SchemaVersion=1,TransitionKind='interpreter-binding',TransitionId=str(uuid.uuid4()),SourceApprovalSha256=f.scope['approvalSha256'],RepairApprovalSha256=approval,ScopeSha256=r.scope_sha,ExpectedHeadSha256=state['head'],PreviousConfigPath=str(old_path),PreviousGateSha256=old_pin,NewGateSha256=f.pin,PreviousBuildSha256=build,NewBuildSha256=build,PreviousPythonSha256=old['PythonSha256'],NewPythonSha256=f.config['PythonSha256'],InterpreterEvidencePath=str(evidence_path),InterpreterEvidenceSha256=fixtures.sha(evidence_path.read_bytes()))
    path=f.new_folder/'repair.json';path.write_text(json.dumps(t))
    return dict(old=old,oldPath=old_path,oldPin=old_pin,record=t,path=path,pin=fixtures.sha(path.read_bytes()),evidence=evidence,evidencePath=evidence_path)


def command(f,t,verb):
    import subprocess,sys
    request=dict(command=verb,configPath=str(f.config_path),configSha256=f.pin,appDirectory=f.config['ApplicationDirectory'] if verb in ('complete-build','status') else f.config['CandidateApplicationDirectory'],id=t['record']['TransitionId'],identitySha256=t['pin'],transitionPath=str(t['path']),transitionSha256=t['pin'])
    p=subprocess.run([sys.executable,'-I','-S',f.config['ScriptPath']],input=json.dumps(request),capture_output=True,text=True,timeout=20)
    if p.stderr:raise AssertionError(p.stderr)
    return p.returncode,json.loads(p.stdout)
