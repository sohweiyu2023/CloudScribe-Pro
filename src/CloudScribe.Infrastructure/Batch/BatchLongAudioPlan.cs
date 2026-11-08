using CloudScribe.Application.Batch.Authority;

namespace CloudScribe.Infrastructure.Batch;

/// <summary>Either the exact Long Audio approval for one file, or why Long Audio cannot take it (plain language).</summary>
public sealed record BatchLongAudioPlan(BatchLongAudioApproval? Approval, string? Unsupported)
{
    public static BatchLongAudioPlan No(string reason) => new(null, reason);
}
