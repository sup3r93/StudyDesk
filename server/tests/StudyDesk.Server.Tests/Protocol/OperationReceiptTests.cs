using System.Text.Json;
using StudyDesk.Contracts;
using StudyDesk.Server.Core.Storage;
using Xunit;

namespace StudyDesk.Server.Tests.Protocol;

/// <summary>
/// Verifies durable operation receipts, idempotency, version conflicts,
/// and server generation checks against the SQLite workspace store.
/// </summary>
public sealed class OperationReceiptTests
{
    /// <summary>
    /// Verifies that creating a space and page succeeds once, while identical
    /// retries return duplicate receipts without changing versions or cursor.
    /// </summary>
    [Fact]
    public async Task CreateSpaceAndPageIdenticalRetryHasOneEffect()
    {
        using TemporaryStoreDirectory directory = new();
        using SqliteServerWorkspaceStore store = new(directory.Path);
        await store.InitializeAsync();

        SyncSnapshotResponse initial = await store.GetSnapshotAsync();
        Guid deviceId = Guid.NewGuid();
        Guid spaceId = Guid.NewGuid();
        Guid pageId = Guid.NewGuid();

        SpaceDto space = new(
            spaceId,
            "Nursing",
            0,
            false,
            DateTimeOffset.UtcNow);

        PageDto page = new(
            pageId,
            spaceId,
            null,
            "ECG",
            "{}",
            0,
            false,
            DateTimeOffset.UtcNow);

        SyncOperationDto createSpace = new(
            Guid.NewGuid(),
            "upsertSpace",
            spaceId,
            0,
            Payload(space));

        SyncOperationDto createPage = new(
            Guid.NewGuid(),
            "upsertPage",
            pageId,
            0,
            Payload(page));

        SyncPushRequest request = new(
            initial.Generation,
            new[] { createSpace, createPage });

        SyncPushResponse first = await store.ApplyOperationsAsync(deviceId, request);
        SyncPushResponse retry = await store.ApplyOperationsAsync(deviceId, request);
        SyncSnapshotResponse snapshot = await store.GetSnapshotAsync();

        Assert.Equal(initial.Generation, first.Generation);
        Assert.Equal(initial.Cursor + 2, first.Cursor);
        Assert.Equal(first.Cursor, retry.Cursor);
        Assert.Equal(first.Cursor, snapshot.Cursor);

        Assert.Collection(
            first.Receipts,
            receipt =>
            {
                Assert.Equal(createSpace.OperationId, receipt.OperationId);
                Assert.Equal("accepted", receipt.Status);
                Assert.Equal(1, receipt.CurrentVersion);
            },
            receipt =>
            {
                Assert.Equal(createPage.OperationId, receipt.OperationId);
                Assert.Equal("accepted", receipt.Status);
                Assert.Equal(1, receipt.CurrentVersion);
            });

        Assert.Collection(
            retry.Receipts,
            receipt =>
            {
                Assert.Equal(createSpace.OperationId, receipt.OperationId);
                Assert.Equal("duplicate", receipt.Status);
                Assert.Equal(first.Receipts[0].CurrentVersion, receipt.CurrentVersion);
                Assert.Equal(
                    first.Receipts[0].CurrentEntity?.Deserialize<SpaceDto>(JsonSerializerOptions.Web),
                    receipt.CurrentEntity?.Deserialize<SpaceDto>(JsonSerializerOptions.Web));
            },
            receipt =>
            {
                Assert.Equal(createPage.OperationId, receipt.OperationId);
                Assert.Equal("duplicate", receipt.Status);
                Assert.Equal(first.Receipts[1].CurrentVersion, receipt.CurrentVersion);
                Assert.Equal(
                    first.Receipts[1].CurrentEntity?.Deserialize<PageDto>(JsonSerializerOptions.Web),
                    receipt.CurrentEntity?.Deserialize<PageDto>(JsonSerializerOptions.Web));
            });

        SpaceDto storedSpace = Assert.Single(snapshot.Spaces);
        PageDto storedPage = Assert.Single(snapshot.Pages);

        Assert.Equal(spaceId, storedSpace.Id);
        Assert.Equal(1, storedSpace.Version);
        Assert.Equal(pageId, storedPage.Id);
        Assert.Equal(spaceId, storedPage.SpaceId);
        Assert.Equal(1, storedPage.Version);
    }

    /// <summary>
    /// Verifies that retrying a conflicted operation returns its original
    /// conflict receipt, even after the current entity changes again.
    /// </summary>
    [Fact]
    public async Task ConflictRetryRemainsConflictAfterLaterUpdate()
    {
        using TemporaryStoreDirectory directory = new();
        using SqliteServerWorkspaceStore store = new(directory.Path);
        await store.InitializeAsync();

        SyncSnapshotResponse initial = await store.GetSnapshotAsync();
        Guid deviceId = Guid.NewGuid();
        Guid spaceId = Guid.NewGuid();

        SyncOperationDto create = SpaceOperation(
            Guid.NewGuid(), spaceId, "First", 0);

        SyncPushResponse created = await store.ApplyOperationsAsync(
            deviceId,
            new SyncPushRequest(initial.Generation, new[] { create }));

        Assert.Equal("accepted", Assert.Single(created.Receipts).Status);

        SyncOperationDto stale = SpaceOperation(
            Guid.NewGuid(), spaceId, "Stale edit", 0);

        SyncPushRequest staleRequest = new(
            initial.Generation,
            new[] { stale });

        SyncPushResponse firstConflict = await store.ApplyOperationsAsync(
            deviceId,
            staleRequest);

        OperationReceiptDto original = Assert.Single(firstConflict.Receipts);
        Assert.Equal(stale.OperationId, original.OperationId);
        Assert.Equal("conflict", original.Status);
        Assert.Equal(1, original.CurrentVersion);
        Assert.NotNull(original.CurrentEntity);
        Assert.Equal(created.Cursor, firstConflict.Cursor);

        SyncOperationDto newer = SpaceOperation(
            Guid.NewGuid(), spaceId, "Second", 1);

        SyncPushResponse updated = await store.ApplyOperationsAsync(
            deviceId,
            new SyncPushRequest(initial.Generation, new[] { newer }));

        Assert.Equal("accepted", Assert.Single(updated.Receipts).Status);

        SyncPushResponse retry = await store.ApplyOperationsAsync(
            deviceId,
            staleRequest);

        OperationReceiptDto repeated = Assert.Single(retry.Receipts);
        Assert.Equal(stale.OperationId, repeated.OperationId);
        Assert.Equal("conflict", repeated.Status);
        Assert.Equal(original.CurrentVersion, repeated.CurrentVersion);
        Assert.Equal(original.Message, repeated.Message);
        Assert.Equal(
            original.CurrentEntity?.Deserialize<SpaceDto>(JsonSerializerOptions.Web),
            repeated.CurrentEntity?.Deserialize<SpaceDto>(JsonSerializerOptions.Web));
        Assert.Equal(updated.Cursor, retry.Cursor);

        SyncSnapshotResponse snapshot = await store.GetSnapshotAsync();
        Assert.Equal("Second", Assert.Single(snapshot.Spaces).Title);
        Assert.Equal(2, Assert.Single(snapshot.Spaces).Version);
    }

    /// <summary>
    /// Verifies that an operation ID cannot be reused with a different
    /// payload to overwrite an earlier accepted operation.
    /// </summary>
    [Fact]
    public async Task ReusedOperationIdWithDifferentPayloadIsInvalid()
    {
        using TemporaryStoreDirectory directory = new();
        using SqliteServerWorkspaceStore store = new(directory.Path);
        await store.InitializeAsync();

        SyncSnapshotResponse initial = await store.GetSnapshotAsync();
        Guid deviceId = Guid.NewGuid();
        Guid spaceId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();

        SyncOperationDto original = SpaceOperation(
            operationId, spaceId, "Original", 0);

        SyncPushResponse first = await store.ApplyOperationsAsync(
            deviceId,
            new SyncPushRequest(initial.Generation, new[] { original }));

        Assert.Equal("accepted", Assert.Single(first.Receipts).Status);

        SyncOperationDto changed = SpaceOperation(
            operationId, spaceId, "Changed", 0);

        SyncPushResponse reused = await store.ApplyOperationsAsync(
            deviceId,
            new SyncPushRequest(initial.Generation, new[] { changed }));

        OperationReceiptDto receipt = Assert.Single(reused.Receipts);
        Assert.Equal(operationId, receipt.OperationId);
        Assert.Equal("invalid", receipt.Status);
        Assert.Equal(first.Cursor, reused.Cursor);

        SyncSnapshotResponse snapshot = await store.GetSnapshotAsync();
        SpaceDto space = Assert.Single(snapshot.Spaces);
        Assert.Equal("Original", space.Title);
        Assert.Equal(1, space.Version);
        Assert.Equal(first.Cursor, snapshot.Cursor);
    }

    /// <summary>
    /// Verifies that a request naming the wrong server generation neither
    /// creates an entity nor advances the synchronization cursor.
    /// </summary>
    [Fact]
    public async Task WrongGenerationDoesNotMutateWorkspace()
    {
        using TemporaryStoreDirectory directory = new();
        using SqliteServerWorkspaceStore store = new(directory.Path);
        await store.InitializeAsync();

        SyncSnapshotResponse before = await store.GetSnapshotAsync();
        Guid wrongGeneration = before.Generation == Guid.Empty
            ? Guid.NewGuid()
            : Guid.Empty;

        SyncOperationDto create = SpaceOperation(
            Guid.NewGuid(), Guid.NewGuid(), "Must not exist", 0);

        SyncPushResponse response = await store.ApplyOperationsAsync(
            Guid.NewGuid(),
            new SyncPushRequest(wrongGeneration, new[] { create }));

        SyncSnapshotResponse after = await store.GetSnapshotAsync();

        Assert.Equal(before.Generation, response.Generation);
        Assert.Equal(before.Cursor, response.Cursor);
        Assert.All(
            response.Receipts,
            receipt => Assert.NotEqual("accepted", receipt.Status));

        Assert.Equal(before.ServerId, after.ServerId);
        Assert.Equal(before.Generation, after.Generation);
        Assert.Equal(before.Cursor, after.Cursor);
        Assert.Empty(after.Spaces);
        Assert.Empty(after.Pages);
    }

    /// <summary>
    /// Creates a space operation using the published DTO and web JSON options.
    /// </summary>
    /// <param name="operationId">Stable ID for this attempted operation.</param>
    /// <param name="spaceId">ID of the affected space.</param>
    /// <param name="title">Proposed space title.</param>
    /// <param name="expectedVersion">Server version expected by the client.</param>
    /// <returns>An operation with a serialized SpaceDto payload.</returns>
    private static SyncOperationDto SpaceOperation(
        Guid operationId,
        Guid spaceId,
        string title,
        long expectedVersion)
    {
        SpaceDto space = new(
            spaceId,
            title,
            expectedVersion,
            false,
            DateTimeOffset.UtcNow);

        return new SyncOperationDto(
            operationId,
            "upsertSpace",
            spaceId,
            expectedVersion,
            Payload(space));
    }

    /// <summary>
    /// Serializes a contract DTO with the web JSON options used by the protocol.
    /// </summary>
    /// <typeparam name="T">Contract DTO type.</typeparam>
    /// <param name="value">DTO to serialize.</param>
    /// <returns>An independently owned JSON element.</returns>
    private static JsonElement Payload<T>(T value) =>
        JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);

    /// <summary>
    /// Owns one uniquely named test directory and removes only that directory.
    /// </summary>
    private sealed class TemporaryStoreDirectory : IDisposable
    {
        private const string Prefix = "StudyDesk.Server.Tests-";

        private readonly string _temporaryRoot;

        /// <summary>
        /// Creates a fresh directory directly beneath the system temp directory.
        /// </summary>
        public TemporaryStoreDirectory()
        {
            _temporaryRoot = System.IO.Path.GetFullPath(
                System.IO.Path.GetTempPath());

            Path = System.IO.Path.Combine(
                _temporaryRoot,
                Prefix + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(Path);
        }

        /// <summary>
        /// Gets the unique directory passed to the SQLite store.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// Removes the owned directory only after checking its exact location
        /// and generated name.
        /// </summary>
        public void Dispose()
        {
            string resolved = System.IO.Path.GetFullPath(Path);
            string? parent = System.IO.Path.GetDirectoryName(resolved);
            string name = System.IO.Path.GetFileName(resolved);

            if (!string.Equals(
                    parent?.TrimEnd(System.IO.Path.DirectorySeparatorChar),
                    _temporaryRoot.TrimEnd(System.IO.Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase) ||
                !name.StartsWith(Prefix, StringComparison.Ordinal) ||
                !Guid.TryParseExact(name[Prefix.Length..], "N", out _))
            {
                throw new InvalidOperationException(
                    "Refusing to delete a directory outside this test's temp location.");
            }

            if (Directory.Exists(resolved))
            {
                Directory.Delete(resolved, recursive: true);
            }
        }
    }
}
