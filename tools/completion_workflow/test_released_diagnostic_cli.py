"""Real manual-file reader against sealed synthetic release journals; never live authority."""
import copy
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import uuid

import completion_register as m
import gate_cli as g
import test_stopped_maintenance as fixture


class ReleasedManualReadbackTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='cloudscribe-h8-readback-')
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.f = fixture.StoppedMaintenanceTests()
        self.f.setUp()
        self.addCleanup(self.f.doCleanups)
        self.f.action('claim')
        self.f.action('complete')
        self.old = self.f.r
        self.target = self.root / 'installation'
        self.app = self.target / 'app'
        self.app.mkdir(parents=True)
        self.profile = self.root / 'profile'
        self.profile.mkdir()
        self.encoder = self.app / 'CloudScribe.dll'
        self.infrastructure = self.app / 'CloudScribe.Infrastructure.dll'
        self.encoder.write_bytes(b'synthetic inert managed app identity')
        self.infrastructure.write_bytes(b'synthetic inert infrastructure identity')
        sha = lambda data: hashlib.sha256(data).hexdigest()
        self.config = dict(ApplicationDirectory=str(self.app), AppSha256=sha(self.encoder.read_bytes()),
            InfrastructureSha256=sha(self.infrastructure.read_bytes()), ProfileRootSha256=sha(str(self.profile).upper().encode()))
        self.marker = dict(Product='CloudScribe Pro', Profile=str(self.profile), AppSha256=self.config['AppSha256'])
        self.marker_path = self.target / 'cloudscribe-install.json'
        self.write_marker()
        self.inventory = self.read_inventory()
        before = self.read_state()
        release_id = str(uuid.uuid4())
        self.required = self.profile / '.completion-workflow-required.json'
        self.archive = self.required.with_name(self.required.name + '.' + release_id + '.retired')
        # Pin actual inert archive bytes through a separately completed maintenance
        # transaction; never repin or rewrite the fixture's committed history.
        self.archive_bytes = b'synthetic archived requirement bytes'
        self.archive.write_bytes(self.archive_bytes)
        # A completed maintenance already sealed the prior synthetic digest. Keep
        # archive binding honest by constructing a NEW independent maintenance
        # transaction with this requirement hash and distinct fresh approval.
        second = dict(self.f.record, TransitionId=str(uuid.uuid4()), ExpectedHeadSha256=before['head'],
            NewRequiredSha256=sha(self.archive_bytes),
            PreviousGateSha256=before['currentGate'], PreviousBuildSha256=before['currentBuild'],
            NewGateSha256=m.digest({'synthetic': 'second maintained gate'}),
            NewBuildSha256=m.digest({'synthetic': 'second maintained build'}),
            MaintenanceApprovalSha256=m.digest({'synthetic': 'second independent maintenance approval'}))
        for phase in ('claim', 'complete'):
            self.old.maintenance(second, m.digest(second), phase, m.digest({'synthetic': 'exact file readback'}))
        before = self.read_state()
        self.release = dict(SchemaVersion=1, ReleaseId=release_id,
            ReleaseApprovalSha256=m.digest({'synthetic': 'fresh explicit manual-mode release'}),
            MaintenanceIdentitySha256=m.digest(second), ScopeSha256=self.old.scope_sha,
            ExpectedHeadSha256=before['head'], ManualInventorySha256=m.digest(self.inventory),
            RequiredArchivePathSha256=m.path_digest(self.archive), AdministrativeOnly=True, PaidAuthority=False)
        for phase in ('claim', 'complete'):
            m._maintenance('release', self.old, self.release, m.digest(self.release), phase,
                m.digest({'synthetic': 'actual manual marker and archive proof'}))
        self.state = self.read_state()
        stamp = lambda value: datetime.fromtimestamp(value / 1e6, timezone.utc).isoformat().replace('+00:00', 'Z')
        now = self.f.clock.now + 1_000_000
        epoch = dict(ObservedUtcMicros=now, ObservedMonoMicros=now,
            TrustedUtcLowerMicros=now - 100_000, TrustedUtcUpperMicros=now + 100_000,
            PreviousLastGoodUtcMicros=self.state['lastGoodUtc'], WindowsBootUtc=stamp(now - 60_000_000),
            BootReadbackSha256=m.digest({'synthetic': 'independent boot record'}))
        self.h = dict(copy.deepcopy(self.f.f.f.h), SchemaVersion=8, TransitionKind='post-manual-release-diagnostic',
            TransitionId=str(uuid.uuid4()), SourceApprovalSha256=m.digest({'synthetic': 'new paid diagnostic decision'}),
            PreviousScopeSha256=self.old.scope_sha, PreviousGateSha256=self.state['currentGate'],
            ExpectedPreviousHeadSha256=self.state['head'], ClockEpochEvidence=epoch,
            InheritedClockEpochEvidence=self.state['clockEpoch'], UsedRefreshes=self.state['refresh'],
            UsedAdmissions=self.state['admissions'], UsedResumeAdmissions=self.state['resume-admission'],
            ManualReleaseSha256=m.digest(self.release), ManualMarkerSha256=sha(self.marker_path.read_bytes()),
            RequiredArchiveSha256=sha(self.archive_bytes), ManualInventorySha256=m.digest(self.inventory),
            FreshPrivateCaptureConsentSha256=m.digest({'synthetic': 'new protected response consent'}))
        self.h['WindowApprovalSha256'] = self.h['SourceApprovalSha256']

    def read_state(self):
        with self.old._open() as db:
            return self.old._read(db)

    def write_marker(self):
        self.marker_path.write_bytes(json.dumps(self.marker, separators=(',', ':')).encode())

    def read_inventory(self):
        return {path.relative_to(self.target).as_posix(): g.sha(path.read_bytes())
            for path in self.target.rglob('*') if path.is_file()}

    def readback(self, handoff=None):
        return g.released_manual_readback(self.config, self.h if handoff is None else handoff, self.old, m)

    def test_both_inactive_protocol_variants_have_real_files_and_authenticated_history(self):
        for protocol in (None, 1):
            if protocol is None:
                self.marker.pop('CompletionGateProtocolVersion', None)
            else:
                self.marker['CompletionGateProtocolVersion'] = protocol
            self.write_marker()
            handoff = dict(self.h, ManualMarkerSha256=g.sha(self.marker_path.read_bytes()),
                ManualInventorySha256=m.digest(self.read_inventory()))
            with self.subTest(protocol=protocol):
                self.assertIsNone(self.readback(handoff))
        self.assertEqual(self.state['head'], self.read_state()['head'])
        self.assertTrue(self.read_state()['retired'])

    def test_active_gate_or_financial_markers_are_not_manual_authority(self):
        for key in ('CompletionWorkflowGateSha256', 'RecoveryPolicySha256', 'ChirpArticlePolicySha256', 'ChirpReservationResumePolicySha256'):
            with self.subTest(key=key):
                self.marker[key] = 'a' * 64
                self.write_marker()
                handoff = dict(self.h, ManualMarkerSha256=g.sha(self.marker_path.read_bytes()),
                    ManualInventorySha256=m.digest(self.read_inventory()))
                with self.assertRaises(ValueError): self.readback(handoff)
                self.marker.pop(key)
        self.write_marker()

    def test_wrong_pins_changed_files_and_foreign_requirement_reject(self):
        for key in ('ManualMarkerSha256', 'ManualInventorySha256', 'RequiredArchiveSha256'):
            with self.subTest(key=key), self.assertRaises((ValueError, m.Rejected)):
                self.readback(dict(self.h, **{key: m.digest({'synthetic': 'wrong pin'})}))
        self.encoder.write_bytes(b'changed synthetic app bytes')
        with self.assertRaises(ValueError): self.readback()
        self.encoder.write_bytes(b'synthetic inert managed app identity')
        self.required.write_text(json.dumps({'GateSha256': 'a' * 64, 'ScopeSha256': self.old.scope_sha}))
        with self.assertRaises(ValueError): self.readback()
        self.required.unlink()
        self.archive.write_bytes(b'changed archive bytes')
        with self.assertRaises(ValueError): self.readback()

    def test_successor_materials_rejects_linked_predecessor_before_loading_it(self):
        new_path = self.root / 'reviewed-new-gate.json'
        new_path.write_text(json.dumps(self.config))
        previous = self.root / 'linked-predecessor.json'
        previous.write_text('{}')
        handoff = dict(self.h, PreviousConfigPath=str(previous), NewConfigPath=str(new_path),
            NewGateSha256=g.sha(new_path.read_bytes()), NewScopeSha256='a' * 64)
        handoff_path = self.root / 'reviewed-handoff.json'
        handoff_path.write_text(json.dumps(handoff))
        pin = g.sha(handoff_path.read_bytes())
        request = dict(command='inspect-successor', id=handoff['TransitionId'],
            handoffPath=str(handoff_path), handoffSha256=pin, identitySha256=pin,
            configPath=str(new_path), configSha256=handoff['NewGateSha256'])
        original = Path.is_symlink
        def linked(path):
            return path == previous or original(path)
        with patch.object(Path, 'is_symlink', linked):
            with self.assertRaisesRegex(ValueError, '^LinkedPath$'):
                g.successor_materials(request, self.config, set(self.config), m)


if __name__ == '__main__':
    unittest.main()
