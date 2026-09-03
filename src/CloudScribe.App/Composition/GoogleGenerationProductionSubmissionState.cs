using CloudScribe.App.ViewModels;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

/// <summary>
/// The exact current compile/approval handoff required by production Stage6 generation.
/// The submission envelope and execution snapshot must describe the same compiled provider payload;
/// the runtime request factory enforces that binding before any provider submission can occur.
/// </summary>
public sealed record GoogleGenerationProductionSubmissionState(
    GoogleGenerationSubmissionEnvelope SubmissionEnvelope,
    GoogleGenerationUiExecutionSnapshot Snapshot,
    long CurrentEstimateMinorUnits);
