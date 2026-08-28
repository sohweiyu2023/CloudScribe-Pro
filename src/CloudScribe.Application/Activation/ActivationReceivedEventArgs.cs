namespace CloudScribe.Application.Activation;

/// <summary>
/// Immutable payload delivered when CloudScribe receives startup or secondary-instance activation.
/// </summary>
public sealed class ActivationReceivedEventArgs : EventArgs
{
    public ActivationReceivedEventArgs(
        ActivationSource source,
        IReadOnlyList<string> arguments,
        DateTimeOffset receivedAtUtc)
    {
        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source), source, "Activation source is not defined.");
        }

        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Any(static argument => argument is null))
        {
            throw new ArgumentException("Activation arguments cannot contain null values.", nameof(arguments));
        }

        if (receivedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Activation receipt time must be expressed in UTC.", nameof(receivedAtUtc));
        }

        Source = source;
        Arguments = arguments.ToArray();
        ReceivedAtUtc = receivedAtUtc;
    }

    public ActivationSource Source { get; }

    public IReadOnlyList<string> Arguments { get; }

    public DateTimeOffset ReceivedAtUtc { get; }
}
