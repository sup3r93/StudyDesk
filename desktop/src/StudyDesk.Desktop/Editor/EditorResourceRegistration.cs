namespace StudyDesk.Desktop.Editor;

/// <summary>Publishes the native registration result before any packaged editor navigation is allowed.</summary>
public sealed class EditorResourceRegistration
{
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets whether native CEF registration completed successfully.</summary>
    public bool IsRegistered => completion.Task.IsCompletedSuccessfully;

    /// <summary>Waits a bounded time for native registration and fails before any editor address is loaded.</summary>
    public Task WaitAsync(CancellationToken cancellationToken = default)
    {
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
    }

    /// <summary>Accepts only a result produced by native initialization; a rejection is terminal for this context.</summary>
    internal void Complete(bool registered)
    {
        if (registered)
        {
            completion.TrySetResult();
        }
        else
        {
            completion.TrySetException(new InvalidOperationException("The packaged editor resource factory could not be registered."));
        }
    }
}
