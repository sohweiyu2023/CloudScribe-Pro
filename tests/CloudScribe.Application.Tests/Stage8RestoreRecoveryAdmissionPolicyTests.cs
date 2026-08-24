using CloudScribe.Application.Safety;

namespace CloudScribe.Application.Tests;

public sealed class Stage8RestoreRecoveryAdmissionPolicyTests
{
    [Fact]
    public void Authenticated_rollback_required_state_is_rollback_only()
    {
        var state = new RestoreRecoveryState(true, true, true, true, true, false);
        Assert.Equal("rollback-only", RestoreRecoveryAdmissionPolicy.RequireRecoveryAction(state));
    }

    [Fact]
    public void Terminal_rolled_back_state_is_no_op()
    {
        var state = new RestoreRecoveryState(true, true, true, true, false, true);
        Assert.Equal("no-op-terminal-rolled-back", RestoreRecoveryAdmissionPolicy.RequireRecoveryAction(state));
    }

    [Fact]
    public void Untrusted_or_contradictory_recovery_state_fails_closed()
    {
        Assert.Throws<InvalidOperationException>(() => RestoreRecoveryAdmissionPolicy.RequireRecoveryAction(new(false, true, true, true, false, false)));
        Assert.Throws<InvalidOperationException>(() => RestoreRecoveryAdmissionPolicy.RequireRecoveryAction(new(true, true, true, true, true, true)));
    }
}
