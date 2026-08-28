namespace CloudScribe.Application.Activation;

/// <summary>
/// Thread-safe, bounded activation dispatcher. Activations received before the shell subscribes
/// are retained in order and delivered to the first active subscriber set.
/// </summary>
public sealed class ActivationRouter : IActivationRouter
{
    private const int MaximumPendingActivations = 32;

    private readonly System.Threading.Lock _gate = new();
    private readonly Queue<ActivationReceivedEventArgs> _pending = new();
    private EventHandler<ActivationReceivedEventArgs>? _activationReceived;
    private bool _dispatching;

    public event EventHandler<ActivationReceivedEventArgs>? ActivationReceived
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            bool shouldDrain;
            lock (_gate)
            {
                _activationReceived += value;
                shouldDrain = !_dispatching && _pending.Count > 0;
                if (shouldDrain)
                {
                    _dispatching = true;
                }
            }

            if (shouldDrain)
            {
                DrainPending();
            }
        }
        remove
        {
            if (value is null)
            {
                return;
            }

            lock (_gate)
            {
                _activationReceived -= value;
            }
        }
    }

    public void Route(ActivationReceivedEventArgs request)
    {
        ArgumentNullException.ThrowIfNull(request);
        bool shouldDrain;
        lock (_gate)
        {
            if (_pending.Count >= MaximumPendingActivations)
            {
                throw new InvalidOperationException(
                    $"The pending activation queue reached its bounded capacity of {MaximumPendingActivations} items.");
            }

            _pending.Enqueue(request);
            shouldDrain = _activationReceived is not null && !_dispatching;
            if (shouldDrain)
            {
                _dispatching = true;
            }
        }

        if (shouldDrain)
        {
            DrainPending();
        }
    }

    private void DrainPending()
    {
        List<Exception>? failures = null;
        try
        {
            while (true)
            {
                ActivationReceivedEventArgs request;
                EventHandler<ActivationReceivedEventArgs>? handlers;
                lock (_gate)
                {
                    handlers = _activationReceived;
                    if (handlers is null || _pending.Count == 0)
                    {
                        _dispatching = false;
                        break;
                    }

                    request = _pending.Dequeue();
                }

                DispatchToHandlers(handlers, request, ref failures);
            }
        }
        catch
        {
            lock (_gate)
            {
                _dispatching = false;
            }

            throw;
        }

        if (failures is not null)
        {
            throw new AggregateException("One or more activation subscribers failed.", failures);
        }
    }

    private void DispatchToHandlers(
        EventHandler<ActivationReceivedEventArgs> handlers,
        ActivationReceivedEventArgs request,
        ref List<Exception>? failures)
    {
        foreach (EventHandler<ActivationReceivedEventArgs> handler in handlers.GetInvocationList()
                     .Cast<EventHandler<ActivationReceivedEventArgs>>())
        {
            try
            {
                handler(this, request);
            }
            catch (Exception exception) when (!IsFatalActivationException(exception))
            {
                (failures ??= []).Add(exception);
            }
        }
    }

    private static bool IsFatalActivationException(Exception exception) => exception is
        OutOfMemoryException
        or StackOverflowException
        or AccessViolationException;
}
