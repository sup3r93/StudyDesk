using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudyDesk.Contracts;
using StudyDesk.Desktop.Core.Storage;
using StudyDesk.Server.Core.Storage;
using Xunit;

namespace StudyDesk.Sync.Integration.Tests;

/// <summary>Exercises real SQLite stores across offline edits, restarts, retries and concurrent devices.</summary>
public sealed class WorkspaceSynchronizationTests
{
    /// <summary>Verifies durable offline creation, revision history and pending operations after reopening.</summary>
    [Fact]
    public async Task OfflineDocumentsAndPendingIntentsSurviveReopen()
    {
        using var files = new TestFiles();
        var spaceId = Guid.NewGuid();
        var pageId = Guid.NewGuid();
        using (var first = files.Local("desktop"))
        {
            await first.SaveSpaceAsync(spaceId, "Nursing");
            await first.SavePageAsync(pageId, spaceId, null, "Lecture", "{\"type\":\"doc\"}");
            await first.SavePageAsync(pageId, spaceId, null, "Lecture revised", "{\"type\":\"doc\",\"content\":[]}");
        }
        using var reopened = files.Local("desktop");
        Assert.Equal("Nursing", Assert.Single(await reopened.GetSpacesAsync()).Title);
        var page = Assert.Single(await reopened.GetPagesAsync(spaceId));
        Assert.Equal("Lecture revised", page.Title);
        Assert.Equal(2, page.Version);
        Assert.Equal(2, (await reopened.GetPendingOperationsAsync()).Count);
        Assert.Equal(3, await files.ScalarAsync("desktop", "SELECT COUNT(*) FROM local_revisions;"));
    }

    /// <summary>Verifies initial upload and download between two independent local copies.</summary>
    [Fact]
    public async Task FirstSyncReplicatesAnOfflineSpaceAndPageToAnotherClient()
    {
        using var files = new TestFiles();
        using var server = files.Server();
        using var first = files.Local("first");
        using var second = files.Local("second");
        var (space, page) = await SeedAsync(first);
        await SynchronizeAsync(first, server, Guid.NewGuid());
        await SynchronizeAsync(second, server, Guid.NewGuid());
        Assert.Empty(await first.GetPendingOperationsAsync());
        Assert.Equal(space.Title, Assert.Single(await second.GetSpacesAsync()).Title);
        Assert.Equal(page.ContentJson, (await second.GetPageAsync(page.Id))!.ContentJson);
    }

    /// <summary>Verifies retry immutability and preservation of an edit made while an older upload is in flight.</summary>
    [Fact]
    public async Task AcknowledgingAnOlderUploadDoesNotDeleteANewerEdit()
    {
        using var files = new TestFiles();
        using var server = files.Server();
        using var local = files.Local("desktop");
        var (_, page) = await SeedAsync(local);
        var device = Guid.NewGuid();
        var initial = await server.GetSnapshotAsync();
        var frozen = await local.GetPendingOperationsAsync();
        await local.SavePageAsync(page.Id, page.SpaceId, null, "Newer local draft", "{\"text\":\"newer\"}");
        var retry = await local.GetPendingOperationsAsync();
        Assert.Equal(frozen.Select(operation => operation.OperationId), retry.Select(operation => operation.OperationId));
        Assert.Equal(JsonSerializer.Serialize(frozen, JsonSerializerOptions.Web), JsonSerializer.Serialize(retry, JsonSerializerOptions.Web));
        var push = await server.ApplyOperationsAsync(device, new(initial.Generation, frozen));
        await local.ApplySyncAsync(await server.GetSnapshotAsync(), push);
        Assert.Equal("Newer local draft", (await local.GetPageAsync(page.Id))!.Title);
        var next = Assert.Single(await local.GetPendingOperationsAsync());
        Assert.Equal(1, next.ExpectedVersion);
        Assert.DoesNotContain(next.OperationId, frozen.Select(operation => operation.OperationId));
        await SynchronizeAsync(local, server, device);
        Assert.Equal("Newer local draft", Assert.Single((await server.GetSnapshotAsync()).Pages).Title);
        Assert.Empty(await local.GetPendingOperationsAsync());
    }

    /// <summary>Verifies later remote changes prevent automatic rebasing, even after another local edit.</summary>
    [Fact]
    public async Task LaterRemoteRevisionPreservesConflictInsteadOfSilentlyRebasing()
    {
        using var files = new TestFiles();
        using var server = files.Server();
        using var local = files.Local("desktop");
        var (_, page) = await SeedAsync(local);
        var device = Guid.NewGuid();
        var initial = await server.GetSnapshotAsync();
        var frozen = await local.GetPendingOperationsAsync();
        await local.SavePageAsync(page.Id, page.SpaceId, null, "Local draft two", "{}");
        var accepted = await server.ApplyOperationsAsync(device, new(initial.Generation, frozen));
        var remote = Assert.Single((await server.GetSnapshotAsync()).Pages);
        var remoteEdit = Operation(remote with { Title = "Other device" }, 1);
        Assert.Equal("accepted", Assert.Single((await server.ApplyOperationsAsync(Guid.NewGuid(), new(initial.Generation, [remoteEdit]))).Receipts).Status);
        await local.ApplySyncAsync(await server.GetSnapshotAsync(), accepted);
        await local.SavePageAsync(page.Id, page.SpaceId, null, "Local draft three", "{}");
        var pending = Assert.Single(await local.GetPendingOperationsAsync());
        Assert.Equal(0, pending.ExpectedVersion);
        var rejected = await server.ApplyOperationsAsync(device, new(initial.Generation, [pending]));
        Assert.Equal("conflict", Assert.Single(rejected.Receipts).Status);
        await local.ApplySyncAsync(await server.GetSnapshotAsync(), rejected);
        Assert.Equal("Local draft three", (await local.GetPageAsync(page.Id))!.Title);
        Assert.Single(await local.GetSyncIssuesAsync());
        Assert.Empty(await local.GetPendingOperationsAsync());
        Assert.Equal("Other device", Assert.Single((await server.GetSnapshotAsync()).Pages).Title);
    }

    /// <summary>Verifies a never-uploaded page can be trashed before its initial synchronization.</summary>
    [Fact]
    public async Task NewlyCreatedThenTrashedPageSynchronizesAndCanBeRestored()
    {
        using var files = new TestFiles();
        using var server = files.Server();
        using var local = files.Local("desktop");
        var (_, page) = await SeedAsync(local);
        await local.SetPageTrashAsync(page.Id, true);
        var pending = await local.GetPendingOperationsAsync();
        Assert.Equal("upsertPage", Assert.Single(pending, operation => operation.EntityId == page.Id).Kind);
        var device = Guid.NewGuid();
        await SynchronizeAsync(local, server, device);
        Assert.True(Assert.Single((await server.GetSnapshotAsync()).Pages).IsTrashed);
        await local.SetPageTrashAsync(page.Id, false);
        Assert.Equal("restorePage", Assert.Single(await local.GetPendingOperationsAsync()).Kind);
        await SynchronizeAsync(local, server, device);
        Assert.False(Assert.Single((await server.GetSnapshotAsync()).Pages).IsTrashed);
    }

    /// <summary>Verifies edit and lifecycle transitions while previous operations are in flight.</summary>
    [Fact]
    public async Task EditTrashAndRestoreDuringUploadsRetainEveryLatestIntent()
    {
        using var files = new TestFiles();
        using var server = files.Server();
        using var local = files.Local("desktop");
        var (_, page) = await SeedAsync(local);
        var device = Guid.NewGuid();
        await SynchronizeAsync(local, server, device);
        await local.SavePageAsync(page.Id, page.SpaceId, null, "Updated before trash", "{\"revision\":2}");
        var upload = await local.GetPendingOperationsAsync();
        await local.SetPageTrashAsync(page.Id, true);
        var state = await server.GetSnapshotAsync();
        var ack = await server.ApplyOperationsAsync(device, new(state.Generation, upload));
        await local.ApplySyncAsync(await server.GetSnapshotAsync(), ack);
        var trash = Assert.Single(await local.GetPendingOperationsAsync());
        Assert.Equal("trashPage", trash.Kind);
        await local.SetPageTrashAsync(page.Id, false);
        var trashAck = await server.ApplyOperationsAsync(device, new(state.Generation, [trash]));
        await local.ApplySyncAsync(await server.GetSnapshotAsync(), trashAck);
        var restore = Assert.Single(await local.GetPendingOperationsAsync());
        Assert.Equal("restorePage", restore.Kind);
        await SynchronizeAsync(local, server, device);
        var stored = Assert.Single((await server.GetSnapshotAsync()).Pages);
        Assert.False(stored.IsTrashed);
        Assert.Equal("Updated before trash", stored.Title);
        Assert.Equal("{\"revision\":2}", stored.ContentJson);
    }

    /// <summary>Verifies a lost acknowledgement is safely recovered after both stores restart.</summary>
    [Fact]
    public async Task LostAcknowledgementAndRestartRecoverUsingDurableDuplicateReceipts()
    {
        using var files = new TestFiles();
        var device = Guid.NewGuid();
        IReadOnlyList<SyncOperationDto> frozen;
        Guid generation;
        using (var local = files.Local("desktop"))
        using (var server = files.Server())
        {
            await SeedAsync(local);
            frozen = await local.GetPendingOperationsAsync();
            generation = (await server.GetSnapshotAsync()).Generation;
            var ack = await server.ApplyOperationsAsync(device, new(generation, frozen));
            Assert.All(ack.Receipts, receipt => Assert.Equal("accepted", receipt.Status));
        }
        using var reopenedLocal = files.Local("desktop");
        using var reopenedServer = files.Server();
        var retry = await reopenedLocal.GetPendingOperationsAsync();
        Assert.Equal(frozen.Select(operation => operation.OperationId), retry.Select(operation => operation.OperationId));
        var duplicate = await reopenedServer.ApplyOperationsAsync(device, new(generation, retry));
        Assert.All(duplicate.Receipts, receipt => Assert.Equal("duplicate", receipt.Status));
        await reopenedLocal.ApplySyncAsync(await reopenedServer.GetSnapshotAsync(), duplicate);
        Assert.Empty(await reopenedLocal.GetPendingOperationsAsync());
        Assert.Equal(2, (await reopenedServer.GetSnapshotAsync()).Cursor);
    }

    /// <summary>Verifies two clients cannot overwrite one another from stale base revisions.</summary>
    [Fact]
    public async Task ConcurrentClientsPreserveTheRejectedDraftAndTheAcceptedServerRevision()
    {
        using var files = new TestFiles();
        using var server = files.Server();
        using var first = files.Local("first");
        using var second = files.Local("second");
        var (_, page) = await SeedAsync(first);
        await SynchronizeAsync(first, server, Guid.NewGuid());
        await SynchronizeAsync(second, server, Guid.NewGuid());
        await first.SavePageAsync(page.Id, page.SpaceId, null, "First client", "{}");
        await second.SavePageAsync(page.Id, page.SpaceId, null, "Second client draft", "{}");
        await SynchronizeAsync(first, server, Guid.NewGuid());
        var rejected = await SynchronizeAsync(second, server, Guid.NewGuid());
        Assert.Equal("conflict", Assert.Single(rejected.Receipts).Status);
        Assert.Equal("Second client draft", (await second.GetPageAsync(page.Id))!.Title);
        Assert.Equal("First client", Assert.Single((await server.GetSnapshotAsync()).Pages).Title);
        Assert.Single(await second.GetSyncIssuesAsync());
        Assert.Empty(await second.GetPendingOperationsAsync());
    }

    /// <summary>Verifies a different server identity or generation cannot replace bound offline data.</summary>
    [Fact]
    public async Task ChangedGenerationOrIdentityCannotEraseTheOutbox()
    {
        using var files = new TestFiles();
        using var local = files.Local("desktop");
        using var server = files.Server();
        var (_, page) = await SeedAsync(local);
        var snapshot = await server.GetSnapshotAsync();
        await local.ApplySyncAsync(snapshot, new(snapshot.Generation, snapshot.Cursor, []));
        var pending = await local.GetPendingOperationsAsync();
        var changed = snapshot with { Generation = Guid.NewGuid() };
        await Assert.ThrowsAsync<InvalidOperationException>(() => local.ApplySyncAsync(changed, new(changed.Generation, 0, [])));
        var different = snapshot with { ServerId = Guid.NewGuid() };
        await Assert.ThrowsAsync<InvalidOperationException>(() => local.ApplySyncAsync(different, new(different.Generation, 0, [])));
        var retained = await local.GetPendingOperationsAsync();
        Assert.Equal(pending.Select(operation => operation.OperationId), retained.Select(operation => operation.OperationId));
        Assert.Equal(page.ContentJson, (await local.GetPageAsync(page.Id))!.ContentJson);
    }

    /// <summary>Verifies repeated unchanged snapshots do not fabricate new local revisions.</summary>
    [Fact]
    public async Task RepeatedSnapshotImportIsStableAndDoesNotQueueAnUpload()
    {
        using var files = new TestFiles();
        using var local = files.Local("desktop");
        using var server = files.Server();
        var (_, page) = await SeedAsync(local);
        await SynchronizeAsync(local, server, Guid.NewGuid());
        var before = (await local.GetPageAsync(page.Id))!;
        for (var count = 0; count < 3; count++) { await SynchronizeAsync(local, server, Guid.NewGuid()); }
        Assert.Equal(before, await local.GetPageAsync(page.Id));
        Assert.Empty(await local.GetPendingOperationsAsync());
    }

    /// <summary>Verifies local hierarchy validation rejects cycles and cross-space parent references atomically.</summary>
    [Fact]
    public async Task LocalParentValidationRejectsCyclesAndCrossSpaceParents()
    {
        using var files = new TestFiles();
        using var local = files.Local("desktop");
        var (space, parent) = await SeedAsync(local);
        var child = await local.SavePageAsync(Guid.NewGuid(), space.Id, parent.Id, "Child", "{}");
        await Assert.ThrowsAsync<ArgumentException>(() => local.SavePageAsync(parent.Id, space.Id, child.Id, "Cycle", "{}"));
        var otherSpace = await local.SaveSpaceAsync(Guid.NewGuid(), "Other");
        await Assert.ThrowsAsync<ArgumentException>(() => local.SavePageAsync(Guid.NewGuid(), otherSpace.Id, parent.Id, "Invalid", "{}"));
        Assert.Null((await local.GetPageAsync(parent.Id))!.ParentId);
        Assert.Equal(2, (await local.GetPagesAsync(space.Id)).Count);
    }

    /// <summary>Verifies the authoritative store independently rejects cycles from an untrusted client.</summary>
    [Fact]
    public async Task ServerIndependentlyRejectsParentCycles()
    {
        using var files = new TestFiles();
        using var local = files.Local("desktop");
        using var server = files.Server();
        var (space, parent) = await SeedAsync(local);
        var child = await local.SavePageAsync(Guid.NewGuid(), space.Id, parent.Id, "Child", "{}");
        await SynchronizeAsync(local, server, Guid.NewGuid());
        var snapshot = await server.GetSnapshotAsync();
        var cyclic = Operation(parent with { ParentId = child.Id }, 1);
        var rejected = await server.ApplyOperationsAsync(Guid.NewGuid(), new(snapshot.Generation, [cyclic]));
        Assert.Equal("invalid", Assert.Single(rejected.Receipts).Status);
        Assert.Null(Assert.Single((await server.GetSnapshotAsync()).Pages, page => page.Id == parent.Id).ParentId);
        Assert.Equal(snapshot.Cursor, (await server.GetSnapshotAsync()).Cursor);
    }

    /// <summary>Verifies a failed outbox write rolls back the document and recovery revision as one unit.</summary>
    [Fact]
    public async Task FailedOutboxWriteDoesNotPretendTheDocumentWasSaved()
    {
        using var files = new TestFiles();
        using var local = files.Local("desktop");
        var (_, page) = await SeedAsync(local);
        var pending = await local.GetPendingOperationsAsync();
        await files.ExecuteAsync("desktop", "CREATE TRIGGER fail_outbox BEFORE INSERT ON outbox BEGIN SELECT RAISE(ABORT,'Injected write failure'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => local.SavePageAsync(page.Id, page.SpaceId, null, "Must roll back", "{}"));
        Assert.Equal(page, await local.GetPageAsync(page.Id));
        Assert.Equal(2, await files.ScalarAsync("desktop", "SELECT COUNT(*) FROM local_revisions;"));
        Assert.Equal(pending.Select(operation => operation.OperationId), (await local.GetPendingOperationsAsync()).Select(operation => operation.OperationId));
    }

    /// <summary>Verifies a receipt persistence failure cannot commit an unacknowledgeable server mutation.</summary>
    [Fact]
    public async Task ServerReceiptFailureRollsBackTheEntityAndCursor()
    {
        using var files = new TestFiles();
        using var server = files.Server();
        var initial = await server.GetSnapshotAsync();
        await files.ExecuteAsync("server", "CREATE TRIGGER fail_receipt BEFORE INSERT ON receipts BEGIN SELECT RAISE(ABORT,'Injected receipt failure'); END;");
        var space = new SpaceDto(Guid.NewGuid(), "Rollback", 0, false, DateTimeOffset.UtcNow);
        var operation = new SyncOperationDto(Guid.NewGuid(), "upsertSpace", space.Id, 0, JsonSerializer.SerializeToElement(space, JsonSerializerOptions.Web));
        await Assert.ThrowsAsync<SqliteException>(() => server.ApplyOperationsAsync(Guid.NewGuid(), new(initial.Generation, [operation])));
        var after = await server.GetSnapshotAsync();
        Assert.Empty(after.Spaces);
        Assert.Equal(initial.Cursor, after.Cursor);
        Assert.Equal(0, await files.ScalarAsync("server", "SELECT COUNT(*) FROM revisions;"));
    }

    /// <summary>Verifies an unusable configured directory propagates an I/O error rather than reporting success.</summary>
    [Fact]
    public async Task UnusableStorageDirectoryFailsExplicitly()
    {
        using var files = new TestFiles();
        var blocked = files.PathFor("not-a-directory");
        await File.WriteAllTextAsync(blocked, "occupied");
        using var local = new SqliteLocalWorkspaceStore(blocked);
        await Assert.ThrowsAnyAsync<IOException>(() => local.SaveSpaceAsync(Guid.NewGuid(), "Unsaved"));
        using var server = new SqliteServerWorkspaceStore(blocked);
        await Assert.ThrowsAnyAsync<IOException>(() => server.InitializeAsync());
    }

    /// <summary>Verifies the wire format uses shared camelCase contracts and retains Unicode document text.</summary>
    [Fact]
    public async Task WirePayloadUsesCamelCaseAndRetainsUnicode()
    {
        using var files = new TestFiles();
        using var local = files.Local("desktop");
        using var server = files.Server();
        var space = await local.SaveSpaceAsync(Guid.NewGuid(), "Scienze infermieristiche");
        var page = await local.SavePageAsync(Guid.NewGuid(), space.Id, null, "Valutazione qualità", "{\"text\":\"Perché è già così? 🩺\"}");
        var operation = Assert.Single(await local.GetPendingOperationsAsync(), value => value.EntityId == page.Id);
        Assert.True(operation.Payload.TryGetProperty("spaceId", out _));
        Assert.True(operation.Payload.TryGetProperty("contentJson", out _));
        Assert.False(operation.Payload.TryGetProperty("SpaceId", out _));
        await SynchronizeAsync(local, server, Guid.NewGuid());
        Assert.Equal(page.ContentJson, Assert.Single((await server.GetSnapshotAsync()).Pages).ContentJson);
    }

    /// <summary>Verifies malformed local documents and oversized titles never create pending intents.</summary>
    [Fact]
    public async Task InvalidContentAndTitleCannotEnterTheOutbox()
    {
        using var files = new TestFiles();
        using var local = files.Local("desktop");
        var space = await local.SaveSpaceAsync(Guid.NewGuid(), "Valid");
        await Assert.ThrowsAnyAsync<JsonException>(() => local.SavePageAsync(Guid.NewGuid(), space.Id, null, "Broken", "{"));
        await Assert.ThrowsAsync<ArgumentException>(() => local.SavePageAsync(Guid.NewGuid(), space.Id, null, "Array", "[]"));
        await Assert.ThrowsAsync<ArgumentException>(() => local.SaveSpaceAsync(Guid.NewGuid(), new string('x', 513)));
        Assert.Empty(await local.GetPagesAsync(space.Id));
        Assert.Single(await local.GetPendingOperationsAsync());
    }

    /// <summary>Verifies both explicit conflict choices and keeps discarded local content in recovery history.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExplicitResolutionCanKeepTheDraftOrRestoreTheServerVersion(bool keepLocalDraft)
    {
        using var files = new TestFiles();
        using var server = files.Server();
        using var first = files.Local("first");
        using var second = files.Local("second");
        var (_, page) = await SeedAsync(first);
        var firstDevice = Guid.NewGuid();
        var secondDevice = Guid.NewGuid();
        await SynchronizeAsync(first, server, firstDevice);
        await SynchronizeAsync(second, server, secondDevice);
        await first.SavePageAsync(page.Id, page.SpaceId, null, "Accepted remote edit", "{}");
        await second.SavePageAsync(page.Id, page.SpaceId, null, "Conflicting private edit", "{}");
        await SynchronizeAsync(first, server, firstDevice);
        await SynchronizeAsync(second, server, secondDevice);
        var oldIssue = Assert.Single(await second.GetSyncIssuesAsync());
        await second.SavePageAsync(page.Id, page.SpaceId, null, "Newest private recovery draft", "{}");
        Assert.Empty(await second.GetPendingOperationsAsync());
        using var reopened = files.Local("second");
        Assert.Equal(oldIssue, Assert.Single(await reopened.GetSyncIssuesAsync()));
        await reopened.ResolveSyncIssueAsync(page.Id, keepLocalDraft);
        Assert.Empty(await reopened.GetSyncIssuesAsync());
        if (keepLocalDraft)
        {
            var retry = Assert.Single(await reopened.GetPendingOperationsAsync());
            Assert.NotEqual(oldIssue.OperationId, retry.OperationId);
            Assert.Equal(2, retry.ExpectedVersion);
        }
        else
        {
            Assert.Empty(await reopened.GetPendingOperationsAsync());
        }
        await SynchronizeAsync(reopened, server, secondDevice);
        var expected = keepLocalDraft ? "Newest private recovery draft" : "Accepted remote edit";
        Assert.Equal(expected, (await reopened.GetPageAsync(page.Id))!.Title);
        Assert.Equal(expected, Assert.Single((await server.GetSnapshotAsync()).Pages).Title);
        Assert.Equal(1, await files.ScalarAsync("second", "SELECT EXISTS(SELECT 1 FROM local_revisions WHERE payload LIKE '%Newest private recovery draft%');"));
    }

    /// <summary>Verifies a full rejected batch does not starve unrelated new documents.</summary>
    [Fact]
    public async Task OneHundredBlockedEntitiesDoNotPreventAnUnrelatedUpload()
    {
        using var files = new TestFiles();
        using var local = files.Local("desktop");
        using var server = files.Server();
        for (var index = 0; index < 100; index++)
        {
            await local.SaveSpaceAsync(Guid.NewGuid(), "Initial space " + index.ToString(CultureInfo.InvariantCulture));
        }
        var generation = (await server.GetSnapshotAsync()).Generation;
        var initial = await local.GetPendingOperationsAsync();
        Assert.Equal(100, initial.Count);
        var acceptedElsewhere = await server.ApplyOperationsAsync(Guid.NewGuid(), new(generation, initial));
        Assert.All(acceptedElsewhere.Receipts, receipt => Assert.Equal("accepted", receipt.Status));
        var device = Guid.NewGuid();
        var conflicting = await server.ApplyOperationsAsync(device, new(generation, initial));
        Assert.All(conflicting.Receipts, receipt => Assert.Equal("conflict", receipt.Status));
        await local.ApplySyncAsync(await server.GetSnapshotAsync(), conflicting);
        Assert.Equal(100, (await local.GetSyncIssuesAsync()).Count);
        var unrelated = await local.SaveSpaceAsync(Guid.NewGuid(), "Unblocked space");
        var pending = Assert.Single(await local.GetPendingOperationsAsync());
        Assert.Equal(unrelated.Id, pending.EntityId);
        await SynchronizeAsync(local, server, device);
        Assert.Equal(100, (await local.GetSyncIssuesAsync()).Count);
        Assert.Contains((await server.GetSnapshotAsync()).Spaces, space => space.Id == unrelated.Id);
    }

    /// <summary>Verifies escaped outer JSON is not mistaken for an oversized inner document.</summary>
    [Fact]
    public async Task EscapedUnicodeEnvelopeDoesNotRejectAValidDocument()
    {
        using var files = new TestFiles();
        using var local = files.Local("desktop");
        using var server = files.Server();
        var space = await local.SaveSpaceAsync(Guid.NewGuid(), "Unicode study notes");
        var content = "{\"text\":\"" + new string('è', 400_000) + "\"}";
        var page = await local.SavePageAsync(Guid.NewGuid(), space.Id, null, "Unicode", content);
        var response = await SynchronizeAsync(local, server, Guid.NewGuid());
        Assert.All(response.Receipts, receipt => Assert.Equal("accepted", receipt.Status));
        Assert.Equal(content, Assert.Single((await server.GetSnapshotAsync()).Pages).ContentJson);
        Assert.Equal(page.Title, (await local.GetPageAsync(page.Id))!.Title);
        Assert.Empty(await local.GetPendingOperationsAsync());
    }

    /// <summary>Verifies batching large documents preserves the unsent remainder for the next request.</summary>
    [Fact]
    public async Task LargeDocumentBatchLeavesTheRemainderPendingWithoutDroppingAnOperation()
    {
        using var files = new TestFiles();
        using var local = files.Local("desktop");
        using var server = files.Server();
        var space = await local.SaveSpaceAsync(Guid.NewGuid(), "Large notes");
        var content = "{\"text\":\"" + new string('è', 400_000) + "\"}";
        var first = await local.SavePageAsync(Guid.NewGuid(), space.Id, null, "Large first", content);
        var second = await local.SavePageAsync(Guid.NewGuid(), space.Id, null, "Large second", content);
        var batch = await local.GetPendingOperationsAsync();
        Assert.Equal(2, batch.Count);
        Assert.Equal(first.Id, batch[1].EntityId);
        var device = Guid.NewGuid();
        await SynchronizeAsync(local, server, device);
        Assert.Equal(second.Id, Assert.Single(await local.GetPendingOperationsAsync()).EntityId);
        await SynchronizeAsync(local, server, device);
        Assert.Empty(await local.GetPendingOperationsAsync());
        Assert.Equal(2, (await server.GetSnapshotAsync()).Pages.Count);
        Assert.Empty(await local.GetSyncIssuesAsync());
    }

    /// <summary>Creates a small offline workspace used by the independent synchronization scenarios.</summary>
    private static async Task<(SpaceDto Space, PageDto Page)> SeedAsync(SqliteLocalWorkspaceStore local)
    {
        var space = await local.SaveSpaceAsync(Guid.NewGuid(), "Nursing");
        var page = await local.SavePageAsync(Guid.NewGuid(), space.Id, null, "Lecture", "{\"type\":\"doc\",\"content\":[]}");
        return (space, page);
    }

    /// <summary>Performs the same snapshot, upload and acknowledgement flow used by the desktop host.</summary>
    private static async Task<SyncPushResponse> SynchronizeAsync(SqliteLocalWorkspaceStore local, SqliteServerWorkspaceStore server, Guid device)
    {
        var before = await server.GetSnapshotAsync();
        var pending = await local.GetPendingOperationsAsync();
        var push = await server.ApplyOperationsAsync(device, new(before.Generation, pending));
        await local.ApplySyncAsync(await server.GetSnapshotAsync(), push);
        return push;
    }

    /// <summary>Builds a direct server operation to exercise validation independently of desktop checks.</summary>
    private static SyncOperationDto Operation(PageDto page, long expectedVersion) => new(Guid.NewGuid(), "upsertPage", page.Id,
        expectedVersion, JsonSerializer.SerializeToElement(page, JsonSerializerOptions.Web));

    /// <summary>Owns isolated test directories and provides failure injection against actual SQLite files.</summary>
    private sealed class TestFiles : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "StudyDesk-storage-tests", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        /// <summary>Creates the unique parent directory before stores or failure fixtures are created.</summary>
        public TestFiles() => Directory.CreateDirectory(_root);

        /// <summary>Resolves an isolated store directory within this fixture.</summary>
        public string PathFor(string name) => System.IO.Path.Combine(_root, name);

        /// <summary>Creates a local store whose lifetime is explicitly owned by the test.</summary>
        public SqliteLocalWorkspaceStore Local(string name) => new(PathFor(name));

        /// <summary>Creates an authoritative store whose lifetime is explicitly owned by the test.</summary>
        public SqliteServerWorkspaceStore Server() => new(PathFor("server"));

        /// <summary>Executes a test-only failure injection statement against the physical database.</summary>
        public async Task ExecuteAsync(string name, string sql)
        {
            await using var connection = Connection(name);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>Reads a test-only invariant from the database without replacing the production persistence layer.</summary>
        public async Task<long> ScalarAsync(string name, string sql)
        {
            await using var connection = Connection(name);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        /// <summary>Creates an unpooled connection so directory cleanup does not depend on a global pool.</summary>
        private SqliteConnection Connection(string name) => new(new SqliteConnectionStringBuilder
        { DataSource = System.IO.Path.Combine(PathFor(name), "workspace.sqlite3"), Pooling = false }.ToString());

        /// <summary>Deletes only the unique fixture root after all explicitly scoped store instances are disposed.</summary>
        public void Dispose() => Directory.Delete(_root, true);
    }
}
