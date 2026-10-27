using System.Globalization;
using CloudScribe.Application.Logging;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Application.Startup;

/// <summary>Observational startup metadata only; never creates, changes or cancels a deadline.</summary>
public sealed class StartupPhaseTimings(TimeProvider? timeProvider = null)
{
    public const int MaximumRecords = 64;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly long _origin = (timeProvider ?? TimeProvider.System).GetTimestamp();
    private readonly System.Threading.Lock _gate = new();
    private readonly Dictionary<StartupPhase, long> _starts = [];
    private readonly List<StartupPhase> _active = [];
    private readonly List<Entry> _pending = [];
    private ILogger? _logger;
    private int _count;
    private StartupBudgetState _budgetState;
    private long _budgetStart;
    private long _budgetMilliseconds;

    public void Start(StartupPhase phase) => Record(phase, StartupPhaseState.Started);
    public void Complete(StartupPhase phase) => Record(phase, StartupPhaseState.Completed);

    public void InterruptCurrent()
    {
        StartupPhase phase;
        lock (_gate) phase = _active.Count == 0 ? StartupPhase.Unknown : _active[^1];
        Record(phase, StartupPhaseState.Interrupted);
    }

    public void ArmBudget(int seconds) => Record(StartupPhase.BackendBudget, StartupPhaseState.BudgetArmed, seconds);
    public void DisarmBudget() => Record(StartupPhase.BackendBudget, StartupPhaseState.BudgetDisarmed);
    public void BudgetExpired() => Record(StartupPhase.BackendBudget, StartupPhaseState.BudgetExpired);

    public void AttachLogger(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        Entry[] pending;
        lock (_gate)
        {
            if (_logger is not null) return;
            _logger = logger;
            pending = _pending.ToArray();
            _pending.Clear();
        }

        foreach (Entry entry in pending) Emit(logger, entry);
    }

    public string BufferedSummary()
    {
        lock (_gate)
        {
            return string.Join(" | ", _pending.Select(entry => string.Create(CultureInfo.InvariantCulture,
                $"sequence={entry.Sequence}; phase={entry.Phase}; state={entry.State}; phaseMs={entry.PhaseMilliseconds}; startupMs={entry.StartupMilliseconds}; budget={entry.Budget}")));
        }
    }

    private void Record(StartupPhase phase, StartupPhaseState state, int budgetSeconds = 0)
    {
        try
        {
            if (!Enum.IsDefined(phase)) phase = StartupPhase.Unknown;
            ILogger? logger;
            Entry entry;
            lock (_gate)
            {
                if (_count >= MaximumRecords) return;
                long now = _time.GetTimestamp();
                if (state == StartupPhaseState.Started)
                {
                    _starts[phase] = now;
                    _active.Add(phase);
                }
                else if (state is StartupPhaseState.Completed or StartupPhaseState.Interrupted)
                {
                    int index = _active.LastIndexOf(phase);
                    if (index >= 0) _active.RemoveAt(index);
                }
                else if (state == StartupPhaseState.BudgetArmed)
                {
                    _budgetState = StartupBudgetState.Armed;
                    _budgetStart = now;
                    _starts[StartupPhase.BackendBudget] = now;
                    _budgetMilliseconds = Math.Max(budgetSeconds, 0) * 1000L;
                }
                else if (state == StartupPhaseState.BudgetDisarmed) _budgetState = StartupBudgetState.Disarmed;
                else if (state == StartupPhaseState.BudgetExpired) _budgetState = StartupBudgetState.Expired;

                long phaseMs = _starts.TryGetValue(phase, out long start) ? Elapsed(start, now) : 0;
                long remaining = _budgetState switch
                {
                    StartupBudgetState.Armed => Math.Max(0, _budgetMilliseconds - Elapsed(_budgetStart, now)),
                    StartupBudgetState.Expired => 0,
                    _ => -1,
                };
                entry = new Entry(++_count, phase, state, phaseMs, Elapsed(_origin, now), _budgetState, remaining);
                logger = _logger;
                if (logger is null) _pending.Add(entry);
            }

            if (logger is not null) Emit(logger, entry);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // A timing observer must never replace the operation's outcome.
        }
    }

    private long Elapsed(long start, long end) => (long)Math.Clamp(_time.GetElapsedTime(start, end).TotalMilliseconds, 0, 86_400_000);

    private static void Emit(ILogger logger, Entry entry)
    {
        try
        {
            CloudScribeLog.StartupPhaseTiming(logger, entry.Sequence, entry.Phase, entry.State,
                entry.PhaseMilliseconds, entry.StartupMilliseconds, entry.Budget, entry.RemainingMilliseconds);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // Existing bounded sinks may be unavailable; startup still proceeds.
        }
    }

    private static bool IsFatal(Exception exception) => exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private sealed record Entry(int Sequence, StartupPhase Phase, StartupPhaseState State,
        long PhaseMilliseconds, long StartupMilliseconds, StartupBudgetState Budget, long RemainingMilliseconds);
}
