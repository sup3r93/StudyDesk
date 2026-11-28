namespace StudyDesk.Server.Core.Runtime;

/// <summary>Serializes authorization, data acceptance and revocation within one server process.</summary>
public sealed class DeviceOperationGate : IDisposable
{
    private readonly SemaphoreSlim semaphore = new(1, 1);

    /// <summary>Executes one complete authorization/operation unit while holding the gate.</summary>
    /// <typeparam name="T">Operation result type.</typeparam>
    /// <param name="action">The complete operation, including fresh authorization.</param>
    /// <param name="cancellationToken">Cancels waiting or the operation.</param>
    /// <returns>The completed operation's result.</returns>
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>Releases the synchronization primitive after server requests have stopped.</summary>
    public void Dispose() => semaphore.Dispose();
}
