using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using StudyDesk.Contracts;
using StudyDesk.Desktop.Core.Storage;
using Xunit;

namespace StudyDesk.Desktop.Core.Tests.Contracts;

/// <summary>
/// Verifies that server receipts do not discard later offline edits.
/// </summary>
public sealed class OfflineAcknowledgmentTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(
        Path.GetTempPath(),
        "StudyDesk-OfflineAcknowledgmentTests-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Verifies that an accepted in-flight edit advances the server base
    /// while retaining a newer local edit and its distinct operation.
    /// </summary>
    /// <returns>A task that completes after the storage assertions.</returns>
    [Fact]
    public async Task AcceptedOlderOperationPreservesNewerPageEditAndOutbox()
    {
        using var store = await CreateStoreAsync();
        var spaceId = Guid.NewGuid();
        var pageId = Guid.NewGuid();
        var serverId = Guid.NewGuid();
        var generation = Guid.NewGuid();

        var localSpace = await store.SaveSpaceAsync(spaceId, "Nursing");
        var firstLocalPage = await store.SavePageAsync(
            pageId,
            spaceId,
            null,
            "ECG",
            """{"text":"First draft"}""");

        // This read represents the batch already sent to the server.
        var inFlight = await store.GetPendingOperationsAsync();
        var spaceOperation = Assert.Single(
            inFlight, operation => operation.Kind == "upsertSpace");
        var oldPageOperation = Assert.Single(
            inFlight, operation => operation.Kind == "upsertPage");

        var newerPage = await store.SavePageAsync(
            pageId,
            spaceId,
            null,
            "ECG",
            """{"text":"Newer offline draft"}""");

        var snapshot = new SyncSnapshotResponse(
            serverId,
            generation,
            2,
            new[] { localSpace with { Version = 1 } },
            new[] { firstLocalPage with { Version = 1 } });

        var push = new SyncPushResponse(
            generation,
            2,
            new[]
            {
                Accepted(spaceOperation.OperationId, 1),
                Accepted(oldPageOperation.OperationId, 1)
            });

        await store.ApplySyncAsync(snapshot, push);

        var storedPage = await store.GetPageAsync(pageId);
        Assert.NotNull(storedPage);
        Assert.Equal(newerPage.ContentJson, storedPage.ContentJson);

        var survivingOperation = Assert.Single(
            await store.GetPendingOperationsAsync());
        Assert.NotEqual(
            oldPageOperation.OperationId,
            survivingOperation.OperationId);
        Assert.Equal(pageId, survivingOperation.EntityId);
        Assert.Equal("upsertPage", survivingOperation.Kind);
        Assert.Equal(1, survivingOperation.ExpectedVersion);

        var serializedContent = survivingOperation.Payload
            .GetProperty("contentJson")
            .GetString();
        Assert.NotNull(serializedContent);

        using var content = JsonDocument.Parse(serializedContent);
        Assert.Equal(
            "Newer offline draft",
            content.RootElement.GetProperty("text").GetString());
    }

    /// <summary>
    /// Verifies that a conflicting in-flight edit and competing server
    /// snapshot do not overwrite a newer local draft.
    /// </summary>
    /// <returns>A task that completes after the storage assertions.</returns>
    [Fact]
    public async Task ConflictedOlderOperationPreservesNewerDraftAndOutbox()
    {
        using var store = await CreateStoreAsync();
        var spaceId = Guid.NewGuid();
        var pageId = Guid.NewGuid();
        var serverId = Guid.NewGuid();
        var generation = Guid.NewGuid();

        var localSpace = await store.SaveSpaceAsync(spaceId, "Nursing");
        await store.SavePageAsync(
            pageId,
            spaceId,
            null,
            "ECG",
            """{"text":"First local draft"}""");

        // No second outbox read occurs before applying this batch's receipt.
        var inFlight = await store.GetPendingOperationsAsync();
        var spaceOperation = Assert.Single(
            inFlight, operation => operation.Kind == "upsertSpace");
        var oldPageOperation = Assert.Single(
            inFlight, operation => operation.Kind == "upsertPage");

        var newerPage = await store.SavePageAsync(
            pageId,
            spaceId,
            null,
            "ECG",
            """{"text":"Newer local draft"}""");

        var remotePage = new PageDto(
            pageId,
            spaceId,
            null,
            "ECG from another device",
            """{"text":"Competing remote draft"}""",
            1,
            false,
            DateTimeOffset.UtcNow);

        var snapshot = new SyncSnapshotResponse(
            serverId,
            generation,
            2,
            new[] { localSpace with { Version = 1 } },
            new[] { remotePage });

        var conflict = new OperationReceiptDto(
            oldPageOperation.OperationId,
            "conflict",
            remotePage.Version,
            "The page changed on the server.",
            JsonSerializer.SerializeToElement(
                remotePage,
                JsonSerializerOptions.Web));

        var push = new SyncPushResponse(
            generation,
            2,
            new[]
            {
                Accepted(spaceOperation.OperationId, 1),
                conflict
            });

        await store.ApplySyncAsync(snapshot, push);

        var storedPage = await store.GetPageAsync(pageId);
        Assert.NotNull(storedPage);
        Assert.Equal(newerPage.ContentJson, storedPage.ContentJson);
        Assert.NotEqual(remotePage.ContentJson, storedPage.ContentJson);

        Assert.Empty(await store.GetPendingOperationsAsync());
        var issue = Assert.Single(await store.GetSyncIssuesAsync());
        Assert.Equal(oldPageOperation.OperationId, issue.OperationId);
        Assert.Equal(pageId, issue.EntityId);
        Assert.Equal("conflict", issue.Status);

        // Only an explicit choice may retry the newest draft on the current server base.
        await store.ResolveSyncIssueAsync(pageId, keepLocalDraft: true);
        var survivingOperation = Assert.Single(await store.GetPendingOperationsAsync());
        Assert.NotEqual(oldPageOperation.OperationId, survivingOperation.OperationId);
        Assert.Equal(pageId, survivingOperation.EntityId);
        Assert.Equal("upsertPage", survivingOperation.Kind);
        Assert.Equal(1, survivingOperation.ExpectedVersion);
        Assert.Equal(newerPage.ContentJson, survivingOperation.Payload.GetProperty("contentJson").GetString());
    }

    /// <summary>
    /// Verifies that a changed server generation leaves pending operations
    /// and locally committed content available for recovery.
    /// </summary>
    /// <returns>A task that completes after the storage assertions.</returns>
    [Fact]
    public async Task ChangedGenerationDoesNotClearLocalOutbox()
    {
        using var store = await CreateStoreAsync();
        var serverId = Guid.NewGuid();
        var originalGeneration = Guid.NewGuid();
        var replacementGeneration = Guid.NewGuid();

        await store.ApplySyncAsync(
            new SyncSnapshotResponse(
                serverId,
                originalGeneration,
                0,
                Array.Empty<SpaceDto>(),
                Array.Empty<PageDto>()),
            new SyncPushResponse(
                originalGeneration,
                0,
                Array.Empty<OperationReceiptDto>()));

        var spaceId = Guid.NewGuid();
        var pageId = Guid.NewGuid();
        await store.SaveSpaceAsync(spaceId, "Offline space");
        var draft = await store.SavePageAsync(
            pageId,
            spaceId,
            null,
            "Offline page",
            """{"text":"Keep this draft"}""");

        var pendingBefore = await store.GetPendingOperationsAsync();
        Assert.Equal(2, pendingBefore.Count);

        // Rejection or a recovery state is acceptable; deletion is not.
        var failure = await Record.ExceptionAsync(() => store.ApplySyncAsync(
            new SyncSnapshotResponse(
                serverId,
                replacementGeneration,
                0,
                Array.Empty<SpaceDto>(),
                Array.Empty<PageDto>()),
            new SyncPushResponse(
                replacementGeneration,
                0,
                Array.Empty<OperationReceiptDto>())));

        if (failure is not null)
        {
            Assert.IsType<InvalidOperationException>(failure);
        }

        var pendingAfter = await store.GetPendingOperationsAsync();
        Assert.Equal(
            pendingBefore.Select(operation => operation.OperationId).ToArray(),
            pendingAfter.Select(operation => operation.OperationId).ToArray());

        var storedPage = await store.GetPageAsync(pageId);
        Assert.NotNull(storedPage);
        Assert.Equal(draft.ContentJson, storedPage.ContentJson);
    }

    /// <summary>
    /// Removes only the uniquely named temporary directory owned by this test.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The cleanup path is not an expected direct child of the temp directory.
    /// </exception>
    public void Dispose()
    {
        var fullPath = Path.GetFullPath(_dataDirectory);
        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

        if (!string.Equals(
                Path.GetDirectoryName(fullPath),
                tempRoot,
                StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(fullPath).StartsWith(
                "StudyDesk-OfflineAcknowledgmentTests-",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Refusing to clean up an unexpected test directory.");
        }

        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }

    /// <summary>
    /// Creates and initializes storage in this test's isolated directory.
    /// </summary>
    /// <returns>The initialized local store.</returns>
    private async Task<SqliteLocalWorkspaceStore> CreateStoreAsync()
    {
        var store = new SqliteLocalWorkspaceStore(_dataDirectory);
        await store.InitializeAsync();
        return store;
    }

    /// <summary>
    /// Creates a receipt for an operation persisted by the server.
    /// </summary>
    /// <param name="operationId">The original operation identifier.</param>
    /// <param name="currentVersion">The confirmed server version.</param>
    /// <returns>An accepted receipt.</returns>
    private static OperationReceiptDto Accepted(
        Guid operationId,
        long currentVersion) =>
        new(operationId, "accepted", currentVersion, null, null);
}
