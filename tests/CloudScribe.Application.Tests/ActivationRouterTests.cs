using CloudScribe.Application.Activation;

namespace CloudScribe.Application.Tests;

public sealed class ActivationRouterTests
{
    [Fact]
    public void RoutesTypedActivation()
    {
        ActivationRouter router = new();
        ActivationReceivedEventArgs? received = null;
        router.ActivationReceived += (_, request) => received = request;
        ActivationReceivedEventArgs expected = new(ActivationSource.SecondaryInstance, ["file.txt"], DateTimeOffset.UtcNow);

        router.Route(expected);

        Assert.Same(expected, received);
    }

    [Fact]
    public void BuffersActivationUntilFirstSubscriberAndPreservesOrder()
    {
        ActivationRouter router = new();
        ActivationReceivedEventArgs first = new(ActivationSource.SecondaryInstance, ["first.txt"], DateTimeOffset.UtcNow);
        ActivationReceivedEventArgs second = new(ActivationSource.SecondaryInstance, ["second.txt"], DateTimeOffset.UtcNow.AddMilliseconds(1));
        router.Route(first);
        router.Route(second);
        List<ActivationReceivedEventArgs> received = [];

        router.ActivationReceived += (_, request) => received.Add(request);

        Assert.Collection(
            received,
            item => Assert.Same(first, item),
            item => Assert.Same(second, item));
    }

    [Fact]
    public void PendingActivationQueueFailsClosedAtItsBound()
    {
        ActivationRouter router = new();
        for (int index = 0; index < 32; index++)
        {
            router.Route(new ActivationReceivedEventArgs(ActivationSource.SecondaryInstance, [$"file-{index}.txt"], DateTimeOffset.UtcNow));
        }

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            router.Route(new ActivationReceivedEventArgs(ActivationSource.SecondaryInstance, ["overflow.txt"], DateTimeOffset.UtcNow)));

        Assert.Contains("bounded capacity", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ContinuesToHealthySubscribersAndAggregatesRecoverableFailures()
    {
        ActivationRouter router = new();
        bool healthySubscriberCalled = false;
        router.ActivationReceived += (_, _) => throw new InvalidOperationException("recoverable subscriber failure");
        router.ActivationReceived += (_, _) => healthySubscriberCalled = true;

        AggregateException exception = Assert.Throws<AggregateException>(() =>
            router.Route(new ActivationReceivedEventArgs(ActivationSource.SecondaryInstance, [], DateTimeOffset.UtcNow)));

        Assert.True(healthySubscriberCalled);
        Assert.Single(exception.InnerExceptions);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Fact]
    public void DoesNotSwallowFatalSubscriberFailure()
    {
        ActivationRouter router = new();
        OutOfMemoryException fatalException = (OutOfMemoryException)
            System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(OutOfMemoryException));
        router.ActivationReceived += (_, _) =>
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(fatalException).Throw();

        OutOfMemoryException thrown = Assert.Throws<OutOfMemoryException>(() =>
            router.Route(new ActivationReceivedEventArgs(ActivationSource.SecondaryInstance, [], DateTimeOffset.UtcNow)));

        Assert.Same(fatalException, thrown);
    }
}
