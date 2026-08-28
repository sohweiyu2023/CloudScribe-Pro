using CloudScribe.Application.Safety;

namespace CloudScribe.Application.Tests;

public sealed class Stage8RestoreRecoveryAdmissionPolicyTests
{
    [Fact]
    public void AuthenticatedRollbackRequiredStateIsRollbackOnly()
    {
        var state = new RestoreRecoveryState(true, true, true, true, true, false);
        Assert.Equal("rollback-only", RestoreRecoveryAdmissionPolicy.RequireRecoveryAction(state));
    }

    [Fact]
    public void TerminalRolledBackStateIsNoOp()
    {
        var state = new RestoreRecoveryState(true, true, true, true, false, true);
        Assert.Equal("no-op-terminal-rolled-back", RestoreRecoveryAdmissionPolicy.RequireRecoveryAction(state));
    }

    [Fact]
    public void UntrustedOrContradictoryRecoveryStateFailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() => RestoreRecoveryAdmissionPolicy.RequireRecoveryAction(new(false, true, true, true, false, false)));
        Assert.Throws<InvalidOperationException>(() => RestoreRecoveryAdmissionPolicy.RequireRecoveryAction(new(true, true, true, true, true, true)));
    }
}
