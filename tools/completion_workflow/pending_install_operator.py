"""Explicit administrative recovery only. No paid authority or provider entrypoint.

Run with -I -S and a separately reviewed plan SHA. Never import caller/user modules.
The old and orphaned diagnostic scopes remain stopped/inactive after restoration.
"""
from contextlib import ExitStack, contextmanager
import ctypes
from ctypes import wintypes
import hashlib
import json
import os
from pathlib import Path
import sys
import types


def raw(path, limit=131072):
    path=Path(path)
    if not path.is_absolute():raise ValueError('RecoveryPath')
    for p in (path,*path.parents):
        if p.is_symlink() or p.is_junction():raise ValueError('LinkedPath')
    with path.open('rb') as stream:data=stream.read(limit+1)
    if len(data)>limit:raise ValueError('Oversized')
    return data


def sha(data):return hashlib.sha256(data).hexdigest()


def unique(pairs):
    result={}
    for key,value in pairs:
        if key in result:raise ValueError('DuplicateField')
        result[key]=value
    return result


def document(reference,limit=32768):
    if not isinstance(reference,dict) or set(reference)!={'Path','Sha256'}:raise ValueError('RecoveryReference')
    data=raw(reference['Path'],limit)
    if sha(data)!=reference['Sha256']:raise ValueError('RecoveryEvidencePin')
    return json.loads(data,object_pairs_hook=unique)


def load_sources(pins):
    names=('completion_register.py','gate_cli.py','pending_install_files.py','pending_install_operator.py')
    if not isinstance(pins,dict) or set(pins)!=set(names):raise ValueError('RecoverySourcePins')
    result={}
    for name in names:
        path=Path(__file__).resolve().with_name(name);data=raw(path)
        if sha(data)!=pins[name]:raise ValueError('RecoverySourcePins')
        if name=='pending_install_operator.py':continue
        module=types.ModuleType(name[:-3]);module.__file__=str(path)
        exec(compile(data,str(path),'exec'),module.__dict__);result[name]=module
    return result


@contextmanager
def windows_handle(path, *, exclusive=False, create=False):
    if os.name!='nt':raise ValueError('RecoveryWindowsRequired')
    raw_parent=Path(path).parent
    for p in (Path(path),*raw_parent.parents,raw_parent):
        if p.is_symlink() or p.is_junction():raise ValueError('LinkedPath')
    kernel=ctypes.WinDLL('kernel32',use_last_error=True)
    kernel.CreateFileW.argtypes=(wintypes.LPCWSTR,wintypes.DWORD,wintypes.DWORD,ctypes.c_void_p,wintypes.DWORD,wintypes.DWORD,wintypes.HANDLE)
    kernel.CreateFileW.restype=wintypes.HANDLE
    kernel.CloseHandle.argtypes=(wintypes.HANDLE,);kernel.CloseHandle.restype=wintypes.BOOL
    handle=kernel.CreateFileW(str(path),0x80000000 | (0x40000000 if create else 0),0 if exclusive else 1,None,4 if create else 3,0x80,None)
    if handle==ctypes.c_void_p(-1).value:raise OSError(ctypes.get_last_error(),'RecoveryLeaseUnavailable')
    try:yield
    finally:kernel.CloseHandle(handle)


def perform(plan, *, expected_installation, phase, fault=lambda _:None, plan_path=None):
    keys={'SchemaVersion','AdministrativeOnly','PaidAuthority','Withdrawal','OriginalHandoff','ImmutableOldGate',
          'ImmutableNewGate','SourceSha256','OldInventory','NewInventory','SavedRequired','NewRequiredSha256',
          'StageRoot','DisplacedRoot','OperatorPath'}
    if (not isinstance(plan,dict) or set(plan)!=keys or type(plan['SchemaVersion']) is not int or plan['SchemaVersion']!=1
            or plan['AdministrativeOnly'] is not True or plan['PaidAuthority'] is not False
            or phase not in ('inspect','restore','resume')):raise ValueError('RecoveryPlan')
    sources=load_sources(plan['SourceSha256']);m=sources['completion_register.py'];g=sources['gate_cli.py'];files=sources['pending_install_files.py']
    if files.physical(plan['OperatorPath'])!=Path(__file__).resolve():raise ValueError('RecoverySourcePins')
    r=document(plan['Withdrawal']);h=document(plan['OriginalHandoff']);new=document(plan['ImmutableNewGate']);old=document(plan['ImmutableOldGate'])
    if (files.physical(sys.executable)!=files.physical(new['PythonPath'])
            or sha(raw(sys.executable,32*1024*1024))!=new['PythonSha256']):raise ValueError('RecoveryInterpreterPin')
    target=files.physical(Path(new['ApplicationDirectory']).parent)
    if target!=files.physical(expected_installation):raise ValueError('RecoveryInstallationRoot')
    affected=tuple(files.physical(p) for p in (target,plan['StageRoot'],plan['DisplacedRoot']))
    references=[plan[key]['Path'] for key in ('Withdrawal','OriginalHandoff','ImmutableOldGate','ImmutableNewGate','SavedRequired')]
    references += [str(Path(__file__).resolve().with_name(name)) for name in plan['SourceSha256']]
    references += [h['NewScopePath'],new['PlanManifestPath']]
    if plan_path is not None:references.append(plan_path)
    if any(any(files.physical(path).is_relative_to(root) for root in affected) for path in references):
        raise ValueError('RecoveryMutableEvidence')
    if files.physical(plan['ImmutableNewGate']['Path'])!=files.physical(h['NewConfigPath']):
        raise ValueError('RecoveryMutableEvidence')
    if (plan['ImmutableNewGate']['Sha256']!=h['NewGateSha256'] or plan['ImmutableOldGate']['Sha256']!=h['PreviousGateSha256']
            or plan['OriginalHandoff']['Sha256']!=r['RepairIdentitySha256']
            or plan['ImmutableOldGate']['Sha256']!=r['RestoredGateSha256']
            or old['ApplicationDirectory']!=new['ApplicationDirectory']):raise ValueError('RecoveryIdentity')
    backup=files.physical(r['BackupPath']);saved=plan['SavedRequired']
    if files.inventory(backup)!=plan['OldInventory'] or sha(raw(saved['Path'],4096))!=saved['Sha256']:
        raise ValueError('RecoveryBackupChanged')
    marker=json.loads(raw(backup/'cloudscribe-install.json',32768),object_pairs_hook=unique)
    profile=files.physical(marker['Profile']);db=profile/'data/cloudscribe.db'
    if sha(str(profile).rstrip('/\\').upper().encode())!=new['ProfileRootSha256'] or profile.is_relative_to(target):raise ValueError('RecoveryProfile')
    if files.sha(backup/'completion-workflow-gate.json')!=r['RestoredGateSha256'] or files.sha(backup/'cloudscribe-install.json')!=r['PreviousInstallSha256'] or saved['Sha256']!=r['PreviousRequiredSha256']:
        raise ValueError('RecoveryBackupChanged')
    saved_required=document(saved,4096)
    if (saved_required!={'GateSha256':r['RestoredGateSha256'],'ScopeSha256':old['ScopeSha256']}
            or marker.get('AppSha256','').lower()!=old['AppSha256']
            or any(files.sha(backup/'app'/name)!=old[field] for name,field in
                   (('CloudScribe.dll','AppSha256'),('CloudScribe.Infrastructure.dll','InfrastructureSha256')))):
        raise ValueError('RecoveryBackupChanged')
    previous=m.Register(old['RegisterPath'],old['ScopeSha256']);child=m.Register(new['RegisterPath'],new['ScopeSha256'])
    scope=json.loads(raw(h['NewScopePath'],16384),object_pairs_hook=unique)
    manifest=json.loads(raw(new['PlanManifestPath'],1024*1024),object_pairs_hook=unique)
    if (m.digest(scope)!=new['ScopeSha256'] or sha(raw(new['PlanManifestPath'],1024*1024))!=new['PlanManifestSha256']
            or plan['Withdrawal']['Sha256']!=sha(raw(plan['Withdrawal']['Path'],32768))):raise ValueError('RecoveryEvidencePin')
    proof_config=dict(new,ApplicationDirectory=str(backup/'app'))
    def evidence():
        if files.inventory(backup)!=plan['OldInventory']:raise ValueError('RecoveryBackupChanged')
        g.diagnostic_acceptance(proof_config,manifest,scope,m,database_sha=h['DatabaseSha256'])
    def journal(action):
        with previous._open() as connection:
            state=previous._read(connection)
        immutable=state['activation']['sentenceHandoff']['NewConfigPath']
        if files.physical(immutable)!=files.physical(plan['ImmutableOldGate']['Path']):raise ValueError('RecoveryImmutablePredecessor')
        if state['retirement'] is not None and state['retirement']['handoff']!=h:raise ValueError('RecoveryOriginalHandoff')
        if action=='complete':
            g.installed_readback(old,r['RestoredGateSha256'],allow_financial=True)
            evidence()
        observed=m.digest({'files':plan['OldInventory'],'required':saved['Sha256'],'database':h['DatabaseSha256']})
        return previous.successor_withdrawal(child,r,plan['Withdrawal']['Sha256'],action,observed)
    required=profile/'.completion-workflow-required.json'
    # No process is terminated. Open handles prevent app startup/DB writes during recovery.
    with ExitStack() as locks:
        locks.enter_context(windows_handle(profile/'.instance.lock',exclusive=True))
        locks.enter_context(windows_handle(db))
        for suffix in ('-wal','-shm'):
            sidecar=Path(str(db)+suffix)
            if sidecar.exists():locks.enter_context(windows_handle(sidecar))
        locks.enter_context(windows_handle(target.with_name(target.name+'.pending-install-recovery.lock'),exclusive=True,create=True))
        restoration=files.Restoration(target,backup,plan['StageRoot'],plan['DisplacedRoot'],required,saved['Path'],
            plan['OldInventory'],plan['NewInventory'],saved['Sha256'],plan['NewRequiredSha256'],r['WithdrawalId'],journal,evidence,fault)
        if phase!='resume':g.installed_readback(new,h['NewGateSha256'],allow_financial=True)
        if phase=='inspect':
            restoration.proof()
            if journal('inspect')!={'valid':True,'authority':False}:raise ValueError('RecoveryJournalNotConfirmed')
            return {'valid':True,'authority':False,'providerRequests':0}
        return restoration.restore(resume=phase=='resume')


def main():
    if len(sys.argv)!=4 or not (sys.flags.isolated and sys.flags.no_site):raise ValueError('RecoveryInvocation')
    plan=document({'Path':sys.argv[1],'Sha256':sys.argv[2]},2*1024*1024)
    expected=Path(os.environ['LOCALAPPDATA'])/'Programs/CloudScribe Pro'
    return perform(plan,expected_installation=expected,phase=sys.argv[3],plan_path=sys.argv[1])


def safe_failure(error):
    codes=set(('RecoveryPath LinkedPath Oversized DuplicateField RecoveryReference RecoveryEvidencePin RecoverySourcePins '
        'RecoveryWindowsRequired RecoveryPlan RecoveryInstallationRoot RecoveryIdentity RecoveryBackupChanged RecoveryProfile RecoveryInterpreterPin '
        'RecoveryImmutablePredecessor RecoveryOriginalHandoff RecoveryInvocation RecoveryJournalNotConfirmed RecoveryInventory '
        'RecoveryInventoryBounds RecoveryPathOverlap RecoveryRequiredChanged RecoveryInitialState RecoveryDisplacedChanged '
        'RecoveryInstallationChanged RecoveryStageChanged RecoveryReadback SuccessorWithdrawalState SuccessorWithdrawalSchema '
        'SuccessorWithdrawalIdentity ClockRollback DiagnosticFinancialChanged DiagnosticDatabasePin DiagnosticChildExists '
        'PendingOrUncertainWork DiagnosticAcceptedCoverage DiagnosticParent ClosedDatabaseRequired InstalledBuildBinding RecoveryMutableEvidence RecoveryLeaseUnavailable '
        'InstalledGatePin InstalledGateBytes RequiredGatePin InstalledProfileBinding AnchorDatabaseMismatch').split())
    detail=error.args[0] if len(error.args)==1 and type(error.args[0]) is str else ''
    if isinstance(error,OSError) and len(error.args)==2 and error.args[1]=='RecoveryLeaseUnavailable':detail='RecoveryLeaseUnavailable'
    kind=type(error).__name__
    result={'rejected':True,'code':detail if detail in codes else 'Unclassified',
        'exceptionType':kind if kind in ('ValueError','Rejected','OSError','PermissionError','FileNotFoundError','OperationalError') else 'Other',
        'stage':'pending-install-restoration','requiresReconciliation':True,'detailsAvailable':detail in codes}
    frame=error.__traceback__;result['sourceComponent']='unavailable';result['sourceLine']=None
    for _ in range(128):
        if frame is None:break
        name=Path(frame.tb_frame.f_code.co_filename).name
        if name in ('pending_install_operator.py','pending_install_files.py','gate_cli.py','completion_register.py') and 1<=frame.tb_lineno<=10000:
            result['sourceComponent']=name;result['sourceLine']=frame.tb_lineno
        frame=frame.tb_next
    else:result['sourceComponent']='unavailable';result['sourceLine']=None
    number=getattr(error,'winerror',None) or getattr(error,'errno',None)
    result['systemErrorCode']=number if type(number) is int and 0<=number<=65535 else None
    return result


if __name__=='__main__':
    try:print(json.dumps(main(),separators=(',',':')))
    except Exception as error:
        print(json.dumps(safe_failure(error),separators=(',',':')));sys.exit(1)
