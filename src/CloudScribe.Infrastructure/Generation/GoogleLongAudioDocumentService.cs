using System.Text;
using CloudScribe.Application.Diagnostics;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Files;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Reopens immutable saved source and exact durable job, never inventing a new attempt on restart.</summary>
public sealed partial class GoogleLongAudioDocumentService(InstalledGoogleLongAudioStore installed,
    IDbContextFactory<CloudScribeDbContext> database, DocumentContentStore contents, GoogleLongAudioRuntimeFactory runtime, TimeProvider clock,
    ILogger<GoogleLongAudioDocumentService> logger, IGoogleTrendHistoryHealth trendHistory, INativeMediaTool? articleNative = null)
{
    public async Task<GoogleLongAudioDocumentReview> ReviewAsync(Guid documentId, CancellationToken token = default)
    {
        var context = await installed.LoadVerifiedAsync(token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Long Audio has not been configured for this installation. Ordinary article generation remains separate.");
        var jobs = context.Scope.DocumentJobs?.Where(x => x.DocumentId == documentId).ToArray();
        if (jobs is not { Length: 1 }) throw new InvalidOperationException("No single configured Long Audio job matches this document. No request was sent.");
        var job = jobs[0];
        using var db = await database.CreateDbContextAsync(token).ConfigureAwait(false);
        var document = await db.Documents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == documentId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The configured source document is unavailable.");
        var revision = await db.DocumentRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == job.RevisionId && x.DocumentId == documentId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The exact saved source revision is unavailable.");
        string text = revision.ContentText;
        if (revision.ContentRelativePath is not null)
        {
            var bytes = await contents.ReadVerifiedAsync(new(revision.ContentRelativePath, revision.ContentSha256,
                revision.ContentByteLength ?? throw new InvalidDataException("The source content length is missing.")), token).ConfigureAwait(false);
            if (!string.Equals(new UTF8Encoding(false, true).GetString(bytes), text, StringComparison.Ordinal))
                throw new InvalidDataException("The source revision and saved content disagree.");
        }
        var request = GoogleLongAudioDocumentCompiler.Compile(context.Scope, job, text);
        var admission = new GoogleLongAudioLedgerAdmission(context.ParentPolicyPath, context.ParentPolicySha256,
            context.ScopePath, context.ScopeSha256, context.LedgerPath, (_, _, _) => ValueTask.CompletedTask, clock).WithExtension(context.Extension);
        var cost = await admission.ReviewAsync(request, token).ConfigureAwait(false);
        var replacements = await ReviewReplacementsAsync(context, request, text, token).ConfigureAwait(false);
        cost = cost with { ProjectedProvisionUsdCents = checked(cost.ProjectedProvisionUsdCents + replacements.Additional) };
        if (!cost.WithinProvidedCap) throw new InvalidOperationException("The exact failed-range replacement exceeds the cumulative cap.");
        bool historical = document.CurrentRevisionId != job.RevisionId;
        if (historical && !cost.IsAlreadyCommitted)
            throw new InvalidOperationException("The document changed before this job started. Review its current revision instead of generating older text.");
        return new(documentId, job.RevisionId, document.Title, job.SourceSha256, historical, context.ScopeSha256,
            job, request, cost, GoogleLongAudioDocumentCompiler.OutputAllowance(context.Scope, job),
            context.Scope.AdaptiveRecoveryRootBindings?.Contains(request.BindingSha256, StringComparer.Ordinal) == true,
            replacements.Count, replacements.Additional,
            context.Scope.DeferredUnknownRanges?.Where(x => string.Equals(x.RootBindingSha256, request.BindingSha256, StringComparison.Ordinal)).ToArray());
    }

    public async Task<GoogleLongAudioRuntimeSession> OpenAsync(GoogleLongAudioDocumentReview review, GoogleLongAudioMediaTools tools,
        bool captureConfirmed, CancellationToken token = default)
    {
        var current = await ReviewAsync(review.DocumentId, token).ConfigureAwait(false);
        if (!string.Equals(current.ScopeSha256, review.ScopeSha256, StringComparison.Ordinal)
            || !string.Equals(current.Request.BindingSha256, review.Request.BindingSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("The Long Audio source or configuration changed after review. Nothing was submitted.");
        return await runtime.OpenAsync(current.Request, tools, current.OutputAllowance, captureConfirmed, token).ConfigureAwait(false);
    }
}
