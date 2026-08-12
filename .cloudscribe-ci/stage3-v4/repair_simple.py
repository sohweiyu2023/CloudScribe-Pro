from pathlib import Path
root = Path('source')
store = root/'src/CloudScribe.Infrastructure/Files/DocumentContentStore.cs'
text = store.read_text(encoding='utf-8')
record = 'public sealed record DocumentContentCommit(string RelativePath, string Sha256, long ByteLength);\n\n'
if text.count(record) != 1: raise SystemExit('DocumentContentCommit preimage mismatch')
store.write_text(text.replace(record, ''), encoding='utf-8')
(store.parent/'DocumentContentCommit.cs').write_text('namespace CloudScribe.Infrastructure.Files;\n\npublic sealed record DocumentContentCommit(string RelativePath, string Sha256, long ByteLength);\n', encoding='utf-8')
bridge = root/'src/CloudScribe.Infrastructure/Persistence/LegacyDatabaseMigrationBridge.cs'
text = bridge.read_text(encoding='utf-8')
old = '    public async Task RecoverAbandonedEfMigrationLockAsync('
if text.count(old) != 1: raise SystemExit('lock recovery signature mismatch')
bridge.write_text(text.replace(old, '    public static async Task RecoverAbandonedEfMigrationLockAsync('), encoding='utf-8')
for rel, old_call in (
    ('src/CloudScribe.Infrastructure/Persistence/DatabaseInitializer.cs','legacyMigrationBridge.RecoverAbandonedEfMigrationLockAsync'),
    ('tests/CloudScribe.Infrastructure.Tests/Stage3MigrationTests.cs','bridge.RecoverAbandonedEfMigrationLockAsync')):
    p=root/rel; t=p.read_text(encoding='utf-8')
    if t.count(old_call)!=1: raise SystemExit(f'call preimage mismatch: {rel}')
    p.write_text(t.replace(old_call,'LegacyDatabaseMigrationBridge.RecoverAbandonedEfMigrationLockAsync'),encoding='utf-8')
v=root/'tools/verify_stage3_source.py'; t=v.read_text(encoding='utf-8')
for old,new in (
 ('src/CloudScribe.Infrastructure/Persistence/Migrations/20260812122000_Stage2Baseline.cs','src/CloudScribe.Infrastructure/Persistence/Migrations/Stage2Baseline.cs'),
 ('src/CloudScribe.Infrastructure/Persistence/Migrations/20260812123000_Stage3Documents.cs','src/CloudScribe.Infrastructure/Persistence/Migrations/Stage3Documents.cs')):
    if t.count(old)!=1: raise SystemExit(f'verifier preimage mismatch: {old}')
    t=t.replace(old,new)
needle='        "src/CloudScribe.Infrastructure/Files/DocumentContentStore.cs",\n'
if t.count(needle)!=1: raise SystemExit('verifier store preimage mismatch')
v.write_text(t.replace(needle, needle+'        "src/CloudScribe.Infrastructure/Files/DocumentContentCommit.cs",\n'),encoding='utf-8')
print('CLOUDSCRIBE_STAGE3_SIMPLE_REPAIR=PASS')