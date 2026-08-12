from __future__ import annotations
import os, tarfile
from pathlib import Path
root = Path('source').resolve()
payload = Path(os.environ['RUNNER_TEMP']) / 'stage3-base.tar.gz'
with tarfile.open(payload, 'r:gz') as archive:
    members = archive.getmembers()
    if len(members) != 33:
        raise SystemExit(f'expected 33 payload files, found {len(members)}')
    for member in members:
        rel = Path(member.name)
        if rel.is_absolute() or '..' in rel.parts or not member.isfile():
            raise SystemExit(f'unsafe payload member: {member.name}')
        stream = archive.extractfile(member)
        if stream is None:
            raise SystemExit(f'unreadable payload member: {member.name}')
        dest = root.joinpath(*rel.parts)
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_bytes(stream.read())
legacy = root / 'src/CloudScribe.Infrastructure/Persistence/ObservabilityDbContext.cs'
if not legacy.is_file():
    raise SystemExit('Stage 2 ObservabilityDbContext preimage missing')
legacy.unlink()
print('CLOUDSCRIBE_STAGE3_BASE_APPLY=PASS files=33')