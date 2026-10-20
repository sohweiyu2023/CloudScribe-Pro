namespace CloudScribe.App.Composition;

/// <summary>Async startup must not move the Windows desktop lifetime onto an MTA pool thread.</summary>
public static class DesktopApartment
{
    public static Task<int> RunAsync(Func<int> desktopLifetime)
    {
        ArgumentNullException.ThrowIfNull(desktopLifetime);
        TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread desktop = new(() =>
        {
            try { completion.SetResult(desktopLifetime()); }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            { completion.SetException(exception); }
        }) { Name = "CloudScribe desktop" };
        if (OperatingSystem.IsWindows()) desktop.SetApartmentState(ApartmentState.STA);
        desktop.Start();
        return completion.Task;
    }
}
