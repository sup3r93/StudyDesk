using StudyDesk.Contracts;

namespace StudyDesk.Server.Core.Storage;

/// <summary>Persists authoritative workspace state and independently retryable operations.</summary>
public interface IServerWorkspaceStore
{
    /// <summary>Creates the schema without replacing an existing installation identity.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads a coherent committed snapshot, including retained trash markers.</summary>
    Task<SyncSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads a bounded snapshot segment at a stable global offset and committed cursor.</summary>
    /// <remarks>A continuation offset requires the cursor returned by its first segment.</remarks>
    Task<SyncSnapshotChunkResponse> GetSnapshotChunkAsync(long offset, long? expectedCursor,
        CancellationToken cancellationToken = default);

    /// <summary>Commits authorized device operations and their receipts atomically.</summary>
    Task<SyncPushResponse> ApplyOperationsAsync(Guid deviceId, SyncPushRequest request,
        CancellationToken cancellationToken = default);
}
