using CloudScribe.Application.Safety;
using CloudScribe.Domain.Safety;

namespace CloudScribe.Infrastructure.Safety;

public sealed record RestoreRecoveryContext(
    RestoreRecoveryState State,
    RestoreTransactionJournal Journal);
