using CloudScribe.Application.Startup;

namespace CloudScribe.Infrastructure.Diagnostics;

public sealed record SafeStartupPhaseObservation(int Sequence, StartupPhase Phase, StartupPhaseState State,
    long PhaseMilliseconds, long StartupMilliseconds, StartupBudgetState Budget);
