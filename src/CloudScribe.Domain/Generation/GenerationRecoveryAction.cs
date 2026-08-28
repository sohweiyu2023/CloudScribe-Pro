namespace CloudScribe.Domain.Generation;

public sealed record GenerationRecoveryAction(GenerationRecoveryKind Kind, string Reason)
{
    public static GenerationRecoveryAction None(string reason) => new(GenerationRecoveryKind.None, reason);

    public static GenerationRecoveryAction Requeue(string reason) => new(GenerationRecoveryKind.Requeue, reason);

    public static GenerationRecoveryAction Reconcile(string reason) => new(GenerationRecoveryKind.Reconcile, reason);
}
