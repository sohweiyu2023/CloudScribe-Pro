namespace CloudScribe.Application.Startup;

public enum StartupPhaseState
{
    Started,
    Completed,
    Interrupted,
    BudgetArmed,
    BudgetDisarmed,
    BudgetExpired,
}
