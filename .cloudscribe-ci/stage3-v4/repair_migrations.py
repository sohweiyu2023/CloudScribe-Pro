from pathlib import Path
import runpy
import subprocess

root=Path('source/src/CloudScribe.Infrastructure/Persistence/Migrations')
p=root/'20260812122000_Stage2Baseline.cs'; q=root/'Stage2Baseline.cs'; t=p.read_text(encoding='utf-8')
a='    public const string MigrationId = "20260812122000_Stage2Baseline";\n'
if t.count(a)!=1: raise SystemExit('Stage2 anchor mismatch')
t=t.replace(a,a+'\n    private static readonly string[] BillableOperationTimeIndexColumns = ["OperationId", "OccurredAtUnixMilliseconds"];\n')
old='columns: new[] { "OperationId", "OccurredAtUnixMilliseconds" }'
if t.count(old)!=1: raise SystemExit('Stage2 index mismatch')
q.write_text(t.replace(old,'columns: BillableOperationTimeIndexColumns'),encoding='utf-8'); p.unlink()
p=root/'20260812123000_Stage3Documents.cs'; q=root/'Stage3Documents.cs'; src=p.read_text(encoding='utf-8')
a='    public const string MigrationId = "20260812123000_Stage3Documents";\n'
arrays=('\n    private static readonly string[] BookmarkDocumentOffsetColumns = ["DocumentId", "GraphemeOffset"];\n'
'    private static readonly string[] RevisionDocumentCreatedColumns = ["DocumentId", "CreatedAtUnixMilliseconds"];\n'
'    private static readonly string[] SectionDocumentOrdinalColumns = ["DocumentId", "Ordinal"];\n'
'    private static readonly string[] DocumentStatusUpdatedColumns = ["Status", "UpdatedAtUnixMilliseconds"];\n')
if src.count(a)!=1: raise SystemExit('Stage3 anchor mismatch')
header=src[:src.index('    protected override void Up')].replace(a,a+arrays)
down_start=src.index('    protected override void Down')
down=src[down_start:]; down_body,_=down.rsplit('\n}',1)
start=src.index('        migrationBuilder.CreateTable(',src.index('    protected override void Up'))
idx=src.index('        migrationBuilder.CreateIndex(',start)
up_close=src.index('\n    }\n\n    protected override void Down',idx)
region=src[start:idx]; indexes=src[idx:up_close]
tables=['documents','tags','bookmarks','document_revisions','document_sections','reading_positions','document_tags']; blocks=[]
for i,name in enumerate(tables):
    marker=f'        migrationBuilder.CreateTable(\n            name: "{name}",'; s=region.index(marker)
    e=region.index(f'        migrationBuilder.CreateTable(\n            name: "{tables[i+1]}",',s) if i+1<len(tables) else len(region)
    blocks.append(region[s:e].rstrip()+'\n')
for old,new in (
 ('columns: new[] { "DocumentId", "GraphemeOffset" }','columns: BookmarkDocumentOffsetColumns'),
 ('columns: new[] { "DocumentId", "CreatedAtUnixMilliseconds" }','columns: RevisionDocumentCreatedColumns'),
 ('columns: new[] { "DocumentId", "Ordinal" }','columns: SectionDocumentOrdinalColumns'),
 ('columns: new[] { "Status", "UpdatedAtUnixMilliseconds" }','columns: DocumentStatusUpdatedColumns')): indexes=indexes.replace(old,new)
if 'new[] {' in indexes: raise SystemExit('unhoisted Stage3 index array remains')
names=['CreateDocuments','CreateTags','CreateBookmarks','CreateRevisions','CreateSections','CreateReadingPositions','CreateDocumentTags']
up='    protected override void Up(MigrationBuilder migrationBuilder)\n    {\n'+''.join(f'        {n}(migrationBuilder);\n' for n in names)+'        CreateIndexes(migrationBuilder);\n    }\n\n'
helpers=''.join(f'    private static void {n}(MigrationBuilder migrationBuilder)\n    {{\n{b}    }}\n\n' for n,b in zip(names,blocks,strict=True))
helpers+=f'    private static void CreateIndexes(MigrationBuilder migrationBuilder)\n    {{\n{indexes.rstrip()}\n    }}\n'
q.write_text(header+up+down_body+'\n\n'+helpers+'}\n',encoding='utf-8'); p.unlink()
print('CLOUDSCRIBE_STAGE3_MIGRATION_REPAIR=PASS')
runpy.run_path(str(Path(__file__).with_name('repair_bridge_analyzers.py')), run_name='__main__')

# The source payload is generated and then analyzer-repaired above. Normalize that
# final candidate before the SHA-256 manifest is frozen and before builds/tests run,
# so the later --verify-no-changes gate genuinely tests post-test immutability rather
# than discovering formatting debt after certification has already completed.
source_root = Path('source')
restore = subprocess.run(
    [
        'dotnet',
        'restore',
        'CloudScribe.sln',
        '--locked-mode',
        '--disable-parallel',
        '--configfile',
        'NuGet.config',
    ],
    cwd=source_root,
    check=False,
)
if restore.returncode != 0:
    raise SystemExit('Pre-freeze locked restore failed')

format_result = subprocess.run(
    [
        'dotnet',
        'format',
        'CloudScribe.sln',
        '--no-restore',
        '--verbosity',
        'minimal',
    ],
    cwd=source_root,
    check=False,
)
if format_result.returncode != 0:
    raise SystemExit('Pre-freeze dotnet format failed')
print('CLOUDSCRIBE_STAGE3_CANDIDATE_FORMAT_NORMALIZED=PASS')
