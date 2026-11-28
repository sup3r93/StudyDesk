using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using StudyDesk.Contracts;
using StudyDesk.Desktop.Core.Storage;
using Xunit;

namespace StudyDesk.Desktop.Core.Tests.Contracts;

/// <summary>
/// Verifies that offline workspace data and synchronization operations
/// survive closing and reopening the local store.
/// </summary>
public sealed class OfflineRestartTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(
        Path.GetTempPath(),
        "StudyDesk-OfflineRestartTests-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Verifies that locally saved space and page content remains available
    /// with pending operations after a store restart.
    /// </summary>
    /// <returns>A task that completes after the reopened store is checked.</returns>
    [Fact]
    public async Task OfflineSpaceAndPageSurviveRestart()
    {
        var spaceId = Guid.NewGuid();
        var pageId = Guid.NewGuid();
        SpaceDto savedSpace;
        PageDto savedPage;

        using (var firstStore = new SqliteLocalWorkspaceStore(_dataDirectory))
        {
            await firstStore.InitializeAsync();
            savedSpace = await firstStore.SaveSpaceAsync(
                spaceId,
                "Nursing notes");
            savedPage = await firstStore.SavePageAsync(
                pageId,
                spaceId,
                null,
                "ECG",
                """{"blocks":[{"type":"paragraph","text":"Offline draft"}]}""");
        }

        using var reopenedStore =
            new SqliteLocalWorkspaceStore(_dataDirectory);
        await reopenedStore.InitializeAsync();

        var restoredSpace = Assert.Single(
            await reopenedStore.GetSpacesAsync());
        Assert.Equal(savedSpace, restoredSpace);

        var restoredPage = Assert.Single(
            await reopenedStore.GetPagesAsync(spaceId));
        AssertPageEquivalent(savedPage, restoredPage);

        var pageById = await reopenedStore.GetPageAsync(pageId);
        Assert.NotNull(pageById);
        AssertPageEquivalent(savedPage, pageById);

        var pending = await reopenedStore.GetPendingOperationsAsync();
        Assert.Equal(2, pending.Count);
        Assert.Contains(
            pending,
            operation => operation.Kind == "upsertSpace"
                && operation.EntityId == spaceId);
        Assert.Contains(
            pending,
            operation => operation.Kind == "upsertPage"
                && operation.EntityId == pageId);
    }

    /// <summary>
    /// Verifies that operations already selected for transmission retain
    /// their IDs, payloads and expected versions after a store restart.
    /// </summary>
    /// <returns>A task that completes after the reopened outbox is checked.</returns>
    [Fact]
    public async Task SentOperationsRetainIdentityAndPayloadAfterRestart()
    {
        var spaceId = Guid.NewGuid();
        var pageId = Guid.NewGuid();
        SyncOperationDto[] sentOperations;

        using (var firstStore = new SqliteLocalWorkspaceStore(_dataDirectory))
        {
            await firstStore.InitializeAsync();
            await firstStore.SaveSpaceAsync(spaceId, "Nursing notes");
            await firstStore.SavePageAsync(
                pageId,
                spaceId,
                null,
                "ECG",
                """{"blocks":[{"type":"paragraph","text":"Persist me"}]}""");

            // The production store freezes these operations when read.
            sentOperations = (await firstStore.GetPendingOperationsAsync())
                .ToArray();
            Assert.Equal(2, sentOperations.Length);
        }

        using var reopenedStore =
            new SqliteLocalWorkspaceStore(_dataDirectory);
        await reopenedStore.InitializeAsync();

        var retriedOperations =
            (await reopenedStore.GetPendingOperationsAsync()).ToArray();
        Assert.Equal(sentOperations.Length, retriedOperations.Length);

        foreach (var original in sentOperations)
        {
            var retried = Assert.Single(
                retriedOperations, operation => operation.OperationId == original.OperationId);

            Assert.Equal(original.Kind, retried.Kind);
            Assert.Equal(original.EntityId, retried.EntityId);
            Assert.Equal(
                original.ExpectedVersion,
                retried.ExpectedVersion);
            AssertPayloadEquivalent(original, retried);
        }
    }

    /// <summary>
    /// Deletes only this test instance's uniquely named temporary directory.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The cleanup path is outside the expected temporary location.
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
                "StudyDesk-OfflineRestartTests-",
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
    /// Compares page fields and parses content JSON before comparing it.
    /// </summary>
    /// <param name="expected">The page saved before the restart.</param>
    /// <param name="actual">The page read after the restart.</param>
    private static void AssertPageEquivalent(
        PageDto expected,
        PageDto actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.SpaceId, actual.SpaceId);
        Assert.Equal(expected.ParentId, actual.ParentId);
        Assert.Equal(expected.Title, actual.Title);
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.IsTrashed, actual.IsTrashed);
        Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);

        Assert.True(
            JsonNode.DeepEquals(
                JsonNode.Parse(expected.ContentJson),
                JsonNode.Parse(actual.ContentJson)),
            "The restored page content differs semantically.");
    }

    /// <summary>
    /// Compares the typed DTO represented by two operation payloads.
    /// </summary>
    /// <param name="expected">The operation selected before the restart.</param>
    /// <param name="actual">The operation selected after the restart.</param>
    private static void AssertPayloadEquivalent(
        SyncOperationDto expected,
        SyncOperationDto actual)
    {
        if (expected.Kind == "upsertSpace")
        {
            var expectedSpace = expected.Payload.Deserialize<SpaceDto>(
                JsonSerializerOptions.Web);
            var actualSpace = actual.Payload.Deserialize<SpaceDto>(
                JsonSerializerOptions.Web);

            Assert.NotNull(expectedSpace);
            Assert.NotNull(actualSpace);
            Assert.Equal(expectedSpace, actualSpace);
            return;
        }

        if (expected.Kind == "upsertPage")
        {
            var expectedPage = expected.Payload.Deserialize<PageDto>(
                JsonSerializerOptions.Web);
            var actualPage = actual.Payload.Deserialize<PageDto>(
                JsonSerializerOptions.Web);

            Assert.NotNull(expectedPage);
            Assert.NotNull(actualPage);
            AssertPageEquivalent(expectedPage, actualPage);
            return;
        }

        throw new Xunit.Sdk.XunitException(
            $"Unexpected operation kind in this test: {expected.Kind}");
    }
}
