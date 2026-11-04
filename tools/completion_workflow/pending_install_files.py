"""No standalone entrypoint: staged restoration for an admitted administrative withdrawal.

The operator adapter must hold the closed-app/profile and installation leases and
verify financial/media evidence before calling this helper. This module cannot
admit a scope, reserve money, activate capture or call a provider.
"""
from pathlib import Path, PurePosixPath
import hashlib
import os
import shutil


def sha(path):
    h = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            h.update(block)
    return h.hexdigest()


def physical(path):
    path = Path(path)
    if not path.is_absolute() or str(path).startswith(('\\\\', '//')):
        raise ValueError('RecoveryPath')
    for part in (path, *path.parents):
        if part.is_symlink() or part.is_junction():
            raise ValueError('LinkedPath')
    return path.resolve()


def inventory(root):
    root = physical(root)
    if not root.is_dir():
        raise ValueError('RecoveryInventory')
    result = {}; folded = set(); total = 0
    for count,path in enumerate(root.rglob('*')):
        if count>=16384:raise ValueError('RecoveryInventoryBounds')
        physical(path)
        if path.is_dir():
            continue
        if not path.is_file():
            raise ValueError('RecoveryInventory')
        name = path.relative_to(root).as_posix()
        if name.casefold() in folded:
            raise ValueError('RecoveryInventory')
        folded.add(name.casefold()); total += path.stat().st_size
        if len(result) >= 8192 or total > 1024 * 1024 * 1024:
            raise ValueError('RecoveryInventoryBounds')
        result[name] = sha(path)
    return result


def validate_inventory(expected):
    if not isinstance(expected, dict) or not expected or len(expected) > 8192:
        raise ValueError('RecoveryInventory')
    folded = set()
    for name, pin in expected.items():
        path = PurePosixPath(name)
        if (not isinstance(name, str) or path.is_absolute() or '\\' in name or ':' in name
                or any(p in ('', '.', '..') for p in name.split('/'))
                or name.casefold() in folded or not isinstance(pin, str)
                or len(pin) != 64 or any(c not in '0123456789abcdef' for c in pin)):
            raise ValueError('RecoveryInventory')
        folded.add(name.casefold())


class Restoration:
    """Filesystem half only. Every advance requires the same journal claim and evidence."""
    def __init__(self, target, backup, stage, displaced, required, saved_required,
                 old_files, new_files, old_required_sha, new_required_sha,
                 withdrawal_id, journal, assert_evidence, fault=lambda _: None):
        self.target, self.backup, self.stage, self.displaced, self.required, self.saved_required = (
            physical(p) for p in (target, backup, stage, displaced, required, saved_required))
        roots = (self.target, self.backup, self.stage, self.displaced)
        if self.stage.parent != self.target.parent or self.displaced.parent != self.target.parent:
            raise ValueError('RecoveryPath')
        for index, root in enumerate(roots):
            for other in roots[index + 1:]:
                if root == other or root.is_relative_to(other) or other.is_relative_to(root):
                    raise ValueError('RecoveryPathOverlap')
            if self.required.is_relative_to(root) or self.saved_required.is_relative_to(root):
                raise ValueError('RecoveryPathOverlap')
        if self.required == self.saved_required:
            raise ValueError('RecoveryPathOverlap')
        validate_inventory(old_files); validate_inventory(new_files)
        self.old, self.new = dict(old_files), dict(new_files)
        self.old_required_sha, self.new_required_sha = old_required_sha, new_required_sha
        # UUID parsing is deliberately delegated to the journal's exact admitted record.
        if not isinstance(withdrawal_id, str) or any(c not in '0123456789abcdef-' for c in withdrawal_id):
            raise ValueError('RecoveryIdentity')
        self.temporary_required = self.required.with_name(self.required.name + '.' + withdrawal_id + '.restore')
        physical(self.temporary_required)
        self.journal, self.assert_evidence, self.fault = journal, assert_evidence, fault

    def admitted(self, phase):
        result = self.journal(phase)
        key, value = ('valid', True) if phase == 'check' else ('outcome', 'Succeeded')
        if (not isinstance(result, dict) or result.get('authority') is not False
                or result.get(key) != value or (phase == 'check' and result.get(key) is not True)):
            raise ValueError('RecoveryJournalNotConfirmed')

    def proof(self):
        self.assert_evidence()  # Closed profile, exact DB/ledger/media and original handoff.
        if inventory(self.backup) != self.old or sha(self.saved_required) != self.old_required_sha:
            raise ValueError('RecoveryBackupChanged')
        if sha(self.required) not in (self.old_required_sha, self.new_required_sha):
            raise ValueError('RecoveryRequiredChanged')

    def restore(self, resume=False):
        self.proof()
        if not resume:
            if (inventory(self.target) != self.new or sha(self.required) != self.new_required_sha
                    or self.stage.exists() or self.displaced.exists() or self.temporary_required.exists()):
                raise ValueError('RecoveryInitialState')
            self.admitted('claim')  # Durable blocked state precedes ALL filesystem effects.
        else:
            self.admitted('check')  # Never silently create a replacement claim after restart.
        self.fault('claimed')
        self.proof()
        if self.displaced.exists():
            if inventory(self.displaced) != self.new:
                raise ValueError('RecoveryDisplacedChanged')
        elif not self.target.exists() or inventory(self.target) != self.new:
            raise ValueError('RecoveryInstallationChanged')
        if not self.target.exists() or inventory(self.target) != self.old:
            if self.target.exists() and inventory(self.target) != self.new:
                raise ValueError('RecoveryInstallationChanged')
            self.stage.mkdir(exist_ok=True)
            present = inventory(self.stage)
            if any(name not in self.old or self.old[name] != pin for name, pin in present.items()):
                raise ValueError('RecoveryStageChanged')
            for name, pin in self.old.items():
                destination = self.stage / name
                if name not in present:
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    # Exclusive creation; partial copies remain blocked for manual reconciliation.
                    with (self.backup / name).open('rb') as source, destination.open('xb') as output:
                        shutil.copyfileobj(source, output); output.flush(); os.fsync(output.fileno())
                    if sha(destination) != pin:
                        raise ValueError('RecoveryStageChanged')
            if inventory(self.stage) != self.old:
                raise ValueError('RecoveryStageChanged')
            self.fault('staged'); self.proof(); self.admitted('check')
            if self.target.exists():
                if self.displaced.exists() or inventory(self.target) != self.new:
                    raise ValueError('RecoveryInstallationChanged')
                self.target.rename(self.displaced)
            self.fault('displaced'); self.proof(); self.admitted('check')
            self.stage.rename(self.target)
        elif self.stage.exists():
            raise ValueError('RecoveryStageChanged')
        self.fault('restored-installation'); self.proof(); self.admitted('check')
        if inventory(self.target) != self.old:
            raise ValueError('RecoveryReadback')
        if sha(self.required) != self.old_required_sha:
            if not self.temporary_required.exists():
                with self.saved_required.open('rb') as source, self.temporary_required.open('xb') as output:
                    shutil.copyfileobj(source, output); output.flush(); os.fsync(output.fileno())
            if sha(self.temporary_required) != self.old_required_sha:
                raise ValueError('RecoveryRequiredChanged')
            os.replace(self.temporary_required, self.required)
        self.fault('restored-required'); self.proof(); self.admitted('check')
        if (inventory(self.target) != self.old or inventory(self.displaced) != self.new
                or sha(self.required) != self.old_required_sha):
            raise ValueError('RecoveryReadback')
        self.admitted('complete')
        self.fault('completed')
        return {'restored': True, 'authority': False, 'providerRequests': 0}
