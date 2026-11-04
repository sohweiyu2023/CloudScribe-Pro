"""Maintenance file transaction using the reviewed restoration copier.

No authority, network, credential or profile-writing entrypoint. The operator owns
all leases, journal checks and exact cold-data evidence. Partial trees are retained.
"""


def update(files, plan, journal, evidence, fault=lambda _: None, resume=False):
    transaction = files.Restoration(plan['Target'], plan['PreparedRoot'], plan['StageRoot'],
        plan['DisplacedRoot'], plan['Required'], plan['NewRequired'], plan['NewInventory'],
        plan['OldInventory'], plan['NewRequiredSha256'], plan['PreviousRequiredSha256'],
        plan['TransitionId'], journal, evidence, fault)
    return transaction.restore(resume=resume)


def rollback(files, plan, journal, evidence, fault=lambda _: None, resume=False):
    """Restore the retained old build even after an interrupted forward swap.

    Never delete a partial/unknown tree. Only exact known target trees may move;
    exclusive copies and exact verification happen before any replacement.
    """
    target, backup, stage, retained, required, saved = (files.physical(plan[key]) for key in
        ('Target', 'BackupRoot', 'RollbackStageRoot', 'RollbackDisplacedRoot', 'Required', 'PreviousRequired'))
    # Reuse Restoration's path overlap, inventory, marker, copy and journal guards.
    transaction = files.Restoration(target, backup, stage, retained, required, saved,
        plan['OldInventory'], plan['NewInventory'], plan['PreviousRequiredSha256'],
        plan['NewRequiredSha256'], plan['TransitionId'], journal, evidence, fault)
    transaction.proof()
    if target.exists() and files.inventory(target) not in (plan['OldInventory'], plan['NewInventory']):
        raise ValueError('RecoveryInstallationChanged')
    if retained.exists() and files.inventory(retained) != plan['NewInventory']:
        raise ValueError('RecoveryDisplacedChanged')
    transaction.admitted('check' if resume else 'claim')
    fault('rollback-claimed')
    # Staging from immutable backup remains restartable; no deletion or overwrite.
    stage.mkdir(exist_ok=True)
    present = files.inventory(stage)
    if any(name not in plan['OldInventory'] or pin != plan['OldInventory'][name] for name, pin in present.items()):
        raise ValueError('RecoveryStageChanged')
    for name, pin in plan['OldInventory'].items():
        if name in present:
            continue
        destination = stage / name; destination.parent.mkdir(parents=True, exist_ok=True)
        with (backup / name).open('rb') as source, destination.open('xb') as output:
            files.shutil.copyfileobj(source, output); output.flush(); files.os.fsync(output.fileno())
        if files.sha(destination) != pin:
            raise ValueError('RecoveryStageChanged')
    if files.inventory(stage) != plan['OldInventory']:
        raise ValueError('RecoveryStageChanged')
    fault('rollback-staged'); transaction.proof(); transaction.admitted('check')
    if target.exists():
        actual = files.inventory(target)
        if actual == plan['NewInventory']:
            if retained.exists(): raise ValueError('RecoveryDisplacedChanged')
            target.rename(retained)
        elif actual != plan['OldInventory']:
            raise ValueError('RecoveryInstallationChanged')
    fault('rollback-displaced'); transaction.proof(); transaction.admitted('check')
    if not target.exists(): stage.rename(target)
    if files.inventory(target) != plan['OldInventory']:
        raise ValueError('RecoveryReadback')
    temporary = transaction.temporary_required
    if files.sha(required) != plan['PreviousRequiredSha256']:
        if not temporary.exists():
            with saved.open('rb') as source, temporary.open('xb') as output:
                files.shutil.copyfileobj(source, output); output.flush(); files.os.fsync(output.fileno())
        if files.sha(temporary) != plan['PreviousRequiredSha256']:
            raise ValueError('RecoveryRequiredChanged')
        files.os.replace(temporary, required)
    fault('rollback-required'); transaction.proof(); transaction.admitted('check')
    if files.sha(required) != plan['PreviousRequiredSha256']:
        raise ValueError('RecoveryReadback')
    transaction.admitted('complete'); fault('rollback-completed')
    return {'restored': True, 'authority': False, 'providerRequests': 0}
