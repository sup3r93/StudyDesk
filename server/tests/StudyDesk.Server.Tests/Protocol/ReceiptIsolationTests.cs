using System.Text.Json;
using StudyDesk.Contracts;
using StudyDesk.Server.Core.Storage;
using Xunit;

namespace StudyDesk.Server.Tests.Protocol;

/// <summary>
/// Verifies persistence of invalid receipts and isolation of operation IDs
/// between distinct device associations.
/// </summary>
public sealed class ReceiptIsolationTests
{
    /// <summary>
    /// Verifies that retrying an unknown operation kind returns its original
    /// invalid receipt even after another operation changes the workspace.
    /// </summary>
    [Fact]
    public async Task UnknownKindRetryAfterWorkspaceChangeRemainsInvalid()
    {
        using TemporaryStoreDirectory directory = new();
        using SqliteServerWorkspaceStore store = new(directory.Path);
        await store.InitializeAsync();

        SyncSnapshotResponse initial = await store.GetSnapshotAsync();
        Guid deviceId = Guid.NewGuid();
        Guid spaceId = Guid.NewGuid();
        Guid invalidOperationId = Guid.NewGuid();

        SyncOperationDto unknown = SpaceOperation(
            invalidOperationId,
            "unknownKind",
            spaceId,
            "Ignored",
            0);

        SyncPushRequest invalidRequest = new(
            initial.Generation,
            new[] { unknown });

        SyncPushResponse first = await store.ApplyOperationsAsync(
            deviceId,
            invalidRequest);

        OperationReceiptDto originalReceipt = Assert.Single(first.Receipts);
        Assert.Equal(invalidOperationId, originalReceipt.OperationId);
        Assert.Equal("invalid", originalReceipt.Status);
        Assert.Equal(initial.Cursor, first.Cursor);

        SyncOperationDto create = SpaceOperation(
            Guid.NewGuid(),
            "upsertSpace",
            spaceId,
            "Created later",
            0);

        SyncPushResponse created = await store.ApplyOperationsAsync(
            deviceId,
            new SyncPushRequest(initial.Generation, new[] { create }));

        Assert.Equal("accepted", Assert.Single(created.Receipts).Status);
        Assert.Equal(initial.Cursor + 1, created.Cursor);

        SyncPushResponse retry = await store.ApplyOperationsAsync(
            deviceId,
            invalidRequest);

        OperationReceiptDto repeatedReceipt = Assert.Single(retry.Receipts);
        Assert.Equal(invalidOperationId, repeatedReceipt.OperationId);
        Assert.Equal("invalid", repeatedReceipt.Status);
        Assert.Equal(originalReceipt.CurrentVersion, repeatedReceipt.CurrentVersion);
        Assert.Equal(originalReceipt.Message, repeatedReceipt.Message);
        Assert.Equal(
            originalReceipt.CurrentEntity?.GetRawText(),
            repeatedReceipt.CurrentEntity?.GetRawText());
        Assert.Equal(created.Cursor, retry.Cursor);

        SyncSnapshotResponse snapshot = await store.GetSnapshotAsync();
        Assert.Equal(created.Cursor, snapshot.Cursor);
        Assert.Equal("Created later", Assert.Single(snapshot.Spaces).Title);
    }

    /// <summary>
    /// Verifies that two devices may use the same operation ID for different
    /// entities, with separate accepted and duplicate receipts.
    /// </summary>
    [Fact]
    public async Task SameOperationIdOnDifferentDevicesHasSeparateReceipts()
    {
        using TemporaryStoreDirectory directory = new();
        using SqliteServerWorkspaceStore store = new(directory.Path);
        await store.InitializeAsync();

        SyncSnapshotResponse initial = await store.GetSnapshotAsync();
        Guid firstDeviceId = Guid.NewGuid();
        Guid secondDeviceId = Guid.NewGuid();
        Guid sharedOperationId = Guid.NewGuid();
        Guid firstSpaceId = Guid.NewGuid();
        Guid secondSpaceId = Guid.NewGuid();

        SyncOperationDto firstOperation = SpaceOperation(
            sharedOperationId,
            "upsertSpace",
            firstSpaceId,
            "First device",
            0);

        SyncOperationDto secondOperation = SpaceOperation(
            sharedOperationId,
            "upsertSpace",
            secondSpaceId,
            "Second device",
            0);

        SyncPushRequest firstRequest = new(
            initial.Generation,
            new[] { firstOperation });

        SyncPushRequest secondRequest = new(
            initial.Generation,
            new[] { secondOperation });

        SyncPushResponse first = await store.ApplyOperationsAsync(
            firstDeviceId,
            firstRequest);

        SyncPushResponse second = await store.ApplyOperationsAsync(
            secondDeviceId,
            secondRequest);

        Assert.Equal("accepted", Assert.Single(first.Receipts).Status);
        Assert.Equal("accepted", Assert.Single(second.Receipts).Status);
        Assert.Equal(initial.Cursor + 2, second.Cursor);

        SyncPushResponse firstRetry = await store.ApplyOperationsAsync(
            firstDeviceId,
            firstRequest);

        SyncPushResponse secondRetry = await store.ApplyOperationsAsync(
            secondDeviceId,
            secondRequest);

        OperationReceiptDto firstDuplicate = Assert.Single(firstRetry.Receipts);
        OperationReceiptDto secondDuplicate = Assert.Single(secondRetry.Receipts);

        Assert.Equal("duplicate", firstDuplicate.Status);
        Assert.Equal("duplicate", secondDuplicate.Status);
        Assert.Equal(sharedOperationId, firstDuplicate.OperationId);
        Assert.Equal(sharedOperationId, secondDuplicate.OperationId);
        Assert.Equal(second.Cursor, firstRetry.Cursor);
        Assert.Equal(second.Cursor, secondRetry.Cursor);

        SpaceDto? firstEntity = firstDuplicate.CurrentEntity?
            .Deserialize<SpaceDto>(JsonSerializerOptions.Web);
        SpaceDto? secondEntity = secondDuplicate.CurrentEntity?
            .Deserialize<SpaceDto>(JsonSerializerOptions.Web);

        Assert.NotNull(firstEntity);
        Assert.NotNull(secondEntity);
        Assert.Equal(firstSpaceId, firstEntity.Id);
        Assert.Equal(secondSpaceId, secondEntity.Id);

        SyncSnapshotResponse snapshot = await store.GetSnapshotAsync();
        Assert.Equal(second.Cursor, snapshot.Cursor);
        Assert.Equal(2, snapshot.Spaces.Count);
        Assert.Contains(snapshot.Spaces, space => space.Id == firstSpaceId);
        Assert.Contains(snapshot.Spaces, space => space.Id == secondSpaceId);
    }

    /// <summary>
    /// Creates an operation with a real SpaceDto serialized using web JSON options.
    /// </summary>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="kind">The requested operation kind.</param>
    /// <param name="spaceId">The target space ID.</param>
    /// <param name="title">The proposed space title.</param>
    /// <param name="expectedVersion">The expected server version.</param>
    /// <returns>An operation carrying a serialized space DTO.</returns>
    private static SyncOperationDto SpaceOperation(
        Guid operationId,
        string kind,
        Guid spaceId,
        string title,
        long expectedVersion)
    {
        SpaceDto payload = new(
            spaceId,
            title,
            expectedVersion,
            false,
            DateTimeOffset.UtcNow);

        return new SyncOperationDto(
            operationId,
            kind,
            spaceId,
            expectedVersion,
            JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web));
    }

    /// <summary>
    /// Owns a uniquely named test directory directly under the system temp path.
    /// </summary>
    private sealed class TemporaryStoreDirectory : IDisposable
    {
        private const string Prefix = "StudyDesk.ReceiptIsolationTests-";
        private readonly string _temporaryRoot;

        /// <summary>
        /// Creates a unique temporary directory for one test.
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
        /// Gets the directory supplied to the SQLite store.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// Deletes only this test's generated directory after validating its path.
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
