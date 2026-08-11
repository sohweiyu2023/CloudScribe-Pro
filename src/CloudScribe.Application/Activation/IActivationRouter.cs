namespace CloudScribe.Application.Activation;

/// <summary>
/// Routes immutable process activations. Implementations must retain a bounded number of
/// activations received before the first subscriber so startup file-open requests are not lost.
/// </summary>
public interface IActivationRouter
{
    event EventHandler<ActivationReceivedEventArgs>? ActivationReceived;

    void Route(ActivationReceivedEventArgs request);
}
