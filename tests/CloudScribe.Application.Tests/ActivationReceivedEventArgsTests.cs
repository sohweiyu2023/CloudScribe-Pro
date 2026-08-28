using CloudScribe.Application.Activation;

namespace CloudScribe.Application.Tests;

public sealed class ActivationReceivedEventArgsTests
{
    [Fact]
    public void DefensivelyCopiesArgumentsAndPreservesSource()
    {
        string[] source = ["first.txt"];
        ActivationReceivedEventArgs eventArgs = new(
            ActivationSource.SecondaryInstance,
            source,
            DateTimeOffset.UnixEpoch);

        source[0] = "mutated.txt";

        Assert.Equal(ActivationSource.SecondaryInstance, eventArgs.Source);
        Assert.Equal("first.txt", eventArgs.Arguments[0]);
        Assert.Equal(DateTimeOffset.UnixEpoch, eventArgs.ReceivedAtUtc);
    }

    [Fact]
    public void RejectsUndefinedSource()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ActivationReceivedEventArgs(
            (ActivationSource)999,
            [],
            DateTimeOffset.UnixEpoch));
    }
}
