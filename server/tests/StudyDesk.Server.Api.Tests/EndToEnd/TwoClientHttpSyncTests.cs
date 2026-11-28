using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using StudyDesk.Contracts;
using StudyDesk.Desktop.Core.Storage;
using StudyDesk.Server.Core.Configuration;
using StudyDesk.Server.Core.Security;
using Xunit;

namespace StudyDesk.Server.Api.Tests.EndToEnd;

/// <summary>Integrates two actual desktop SQLite stores through the actual Kestrel HTTP sync API.</summary>
public sealed class TwoClientHttpSyncTests
{
    private static readonly string[] OfflineKinds = ["upsertSpace", "upsertPage"];
    private static readonly string[] PushRequestProperties = ["generation", "operations"];
    private static readonly string[] PushResponseProperties = ["cursor", "generation", "receipts"];
    private static readonly string[] SnapshotProperties = ["cursor", "generation", "nextOffset", "pages", "serverId", "spaces"];

    /// <summary>Preserves a concurrent offline draft, resolves it using the observed server version and survives restart.</summary>
    [Fact]
    public async Task TwoOfflineClientsResolveARealHttpConflictAndReopenCleanly()
    {
        await using TestInstallation installation = await TestInstallation.StartAsync();
        CreatedDevice ownerA = await installation.Devices.CreateAsync("Desktop A", DeviceRole.Owner);
        CreatedDevice ownerB = await installation.Devices.CreateAsync("Laptop B", DeviceRole.Owner);
        Assert.NotEqual(ownerA.Device.Id, ownerB.Device.Id);
        ServerInfoResponse identity = (await installation.Client.GetFromJsonAsync<ServerInfoResponse>("/v1/server"))!;
        Assert.NotEqual(Guid.Empty, identity.ServerId);
        Assert.NotEqual(Guid.Empty, identity.Generation);
        Assert.Equal(1, identity.ProtocolVersion);
        Guid spaceId = Guid.NewGuid();
        Guid pageId = Guid.NewGuid();
        string original = Document("Offline anatomy notes: alpha \u03b1.");
        string draftA = Document("Desktop draft retained for an explicit choice.");
        string revisionB = Document("Laptop revision published while the desktop remains offline.");
        Guid rejectedOperationId;
        using SqliteLocalWorkspaceStore clientB = new(installation.ClientBDirectory);

        using (SqliteLocalWorkspaceStore clientA = new(installation.ClientADirectory))
        {
            // Both entities and their pending operations exist before any authenticated network request.
            await clientA.SaveSpaceAsync(spaceId, "Anatomy");
            await clientA.SavePageAsync(pageId, spaceId, null, "Lecture", original);
            Assert.Equal(original, (await clientA.GetPageAsync(pageId))!.ContentJson);
            IReadOnlyList<SyncOperationDto> offline = await clientA.GetPendingOperationsAsync();
            Assert.Equal(2, offline.Count);
            Assert.Equal(OfflineKinds, offline.Select(operation => operation.Kind));
            Assert.All(offline, operation => Assert.Equal(0, operation.ExpectedVersion));
            SpaceDto outgoingSpace = offline[0].Payload.Deserialize<SpaceDto>(JsonSerializerOptions.Web)!;
            PageDto outgoingPage = offline[1].Payload.Deserialize<PageDto>(JsonSerializerOptions.Web)!;
            Assert.Equal(spaceId, outgoingSpace.Id);
            Assert.Equal(pageId, outgoingPage.Id);
            Assert.Equal(spaceId, outgoingPage.SpaceId);
            Assert.Null(outgoingPage.ParentId);
            Assert.Equal(original, outgoingPage.ContentJson);
            Assert.Equal(JsonValueKind.String, offline[1].Payload.GetProperty("contentJson").ValueKind);

            SyncExchange first = await SynchronizeAsync(clientA, installation.Client, ownerA.Token, identity);
            Assert.Equal(offline.Select(operation => operation.OperationId), first.Sent.Select(operation => operation.OperationId));
            Assert.Equal(2, first.Push.Cursor);
            Assert.All(first.Push.Receipts, receipt => Assert.Equal("accepted", receipt.Status));
            Assert.Equal(1, Assert.Single(first.Snapshot.Pages).Version);
            Assert.Empty(await clientA.GetPendingOperationsAsync());
            Assert.Empty(await clientA.GetSyncIssuesAsync());

            // B downloads through its own authenticated HTTP exchange, rather than receiving a constructed snapshot.
            SyncExchange downloaded = await SynchronizeAsync(clientB, installation.Client, ownerB.Token, identity);
            Assert.Empty(downloaded.Sent);
            Assert.Equal(first.Snapshot.ServerId, downloaded.Snapshot.ServerId);
            Assert.Equal(first.Snapshot.Generation, downloaded.Snapshot.Generation);
            Assert.Equal(first.Snapshot.Cursor, downloaded.Snapshot.Cursor);
            Assert.Equal(spaceId, Assert.Single(await clientB.GetSpacesAsync()).Id);
            Assert.Equal(original, (await clientB.GetPageAsync(pageId))!.ContentJson);

            await clientA.SavePageAsync(pageId, spaceId, null, "Lecture", draftA);
            await clientB.SavePageAsync(pageId, spaceId, null, "Lecture", revisionB);
            SyncExchange publishedB = await SynchronizeAsync(clientB, installation.Client, ownerB.Token, identity);
            Assert.Equal(1, Assert.Single(publishedB.Sent).ExpectedVersion);
            Assert.Equal(2, Assert.Single(publishedB.Push.Receipts).CurrentVersion);
            Assert.Equal("accepted", Assert.Single(publishedB.Push.Receipts).Status);
            Assert.Equal(revisionB, Assert.Single(publishedB.Snapshot.Pages).ContentJson);

            SyncExchange conflictedA = await SynchronizeAsync(clientA, installation.Client, ownerA.Token, identity);
            SyncOperationDto rejected = Assert.Single(conflictedA.Sent);
            rejectedOperationId = rejected.OperationId;
            Assert.Equal(1, rejected.ExpectedVersion);
            OperationReceiptDto conflict = Assert.Single(conflictedA.Push.Receipts);
            Assert.Equal(rejectedOperationId, conflict.OperationId);
            Assert.Equal("conflict", conflict.Status);
            Assert.Equal(2, conflict.CurrentVersion);
            Assert.Equal(revisionB, conflict.CurrentEntity!.Value.Deserialize<PageDto>(JsonSerializerOptions.Web)!.ContentJson);
            Assert.Equal(revisionB, Assert.Single(conflictedA.Snapshot.Pages).ContentJson);
            Assert.Equal(draftA, (await clientA.GetPageAsync(pageId))!.ContentJson);
            LocalSyncIssue issue = Assert.Single(await clientA.GetSyncIssuesAsync());
            Assert.Equal(rejectedOperationId, issue.OperationId);
            Assert.Equal(pageId, issue.EntityId);
            Assert.Equal("conflict", issue.Status);
            Assert.Equal(2, issue.CurrentVersion);
            Assert.Empty(await clientA.GetPendingOperationsAsync());
        }

        // Restart while conflicted proves the draft and blocked status are durable, not in-memory flags.
        using (SqliteLocalWorkspaceStore resumedA = new(installation.ClientADirectory))
        {
            Assert.Equal(draftA, (await resumedA.GetPageAsync(pageId))!.ContentJson);
            LocalSyncIssue durableIssue = Assert.Single(await resumedA.GetSyncIssuesAsync());
            Assert.Equal(rejectedOperationId, durableIssue.OperationId);
            Assert.Empty(await resumedA.GetPendingOperationsAsync());
            await resumedA.ResolveSyncIssueAsync(pageId, keepLocalDraft: true);
            Assert.Empty(await resumedA.GetSyncIssuesAsync());
            SyncOperationDto retry = Assert.Single(await resumedA.GetPendingOperationsAsync());
            Assert.NotEqual(rejectedOperationId, retry.OperationId);
            Assert.Equal(2, retry.ExpectedVersion);
            Assert.Equal(draftA, retry.Payload.Deserialize<PageDto>(JsonSerializerOptions.Web)!.ContentJson);
            SyncExchange resolved = await SynchronizeAsync(resumedA, installation.Client, ownerA.Token, identity);
            Assert.Equal(retry.OperationId, Assert.Single(resolved.Sent).OperationId);
            OperationReceiptDto accepted = Assert.Single(resolved.Push.Receipts);
            Assert.Equal("accepted", accepted.Status);
            Assert.Equal(3, accepted.CurrentVersion);
            Assert.Equal(4, resolved.Snapshot.Cursor);
            Assert.Equal(draftA, Assert.Single(resolved.Snapshot.Pages).ContentJson);
            Assert.Empty(await resumedA.GetPendingOperationsAsync());
            Assert.Empty(await resumedA.GetSyncIssuesAsync());
        }

        using SqliteLocalWorkspaceStore reopenedA = new(installation.ClientADirectory);
        PageDto finalA = (await reopenedA.GetPageAsync(pageId))!;
        Assert.Equal(pageId, finalA.Id);
        Assert.Equal(spaceId, finalA.SpaceId);
        Assert.Equal(draftA, finalA.ContentJson);
        Assert.False(finalA.IsTrashed);
        Assert.Empty(await reopenedA.GetPendingOperationsAsync());
        Assert.Empty(await reopenedA.GetSyncIssuesAsync());
        SyncExchange finalB = await SynchronizeAsync(clientB, installation.Client, ownerB.Token, identity);
        Assert.Empty(finalB.Sent);
        Assert.Equal(draftA, (await clientB.GetPageAsync(pageId))!.ContentJson);
        Assert.Empty(await clientB.GetSyncIssuesAsync());
    }

    /// <summary>Builds a real, portable Tiptap paragraph document without editor or transport mocks.</summary>
    private static string Document(string text) => JsonSerializer.Serialize(new
    {
        type = "doc",
        content = new[] { new { type = "paragraph", content = new[] { new { type = "text", text } } } },
    }, JsonSerializerOptions.Web);

    /// <summary>Exchanges actual pending intents, receipts and snapshots over authenticated HTTP.</summary>
    private static async Task<SyncExchange> SynchronizeAsync(SqliteLocalWorkspaceStore local, HttpClient client,
        string token, ServerInfoResponse expected)
    {
        IReadOnlyList<SyncOperationDto> operations = await local.GetPendingOperationsAsync();
        SyncPushRequest request = new(expected.Generation, operations);
        JsonElement requestShape = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);
        Assert.Equal(PushRequestProperties, requestShape.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        using HttpRequestMessage upload = new(HttpMethod.Post, "/v1/sync/push") { Content = JsonContent.Create(request) };
        upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage pushed = await client.SendAsync(upload);
        Assert.Equal(HttpStatusCode.OK, pushed.StatusCode);
        using JsonDocument pushJson = JsonDocument.Parse(await pushed.Content.ReadAsStringAsync());
        Assert.Equal(PushResponseProperties, pushJson.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        SyncPushResponse push = pushJson.RootElement.Deserialize<SyncPushResponse>(JsonSerializerOptions.Web)!;
        Assert.Equal(expected.Generation, push.Generation);
        Assert.Equal(operations.Select(operation => operation.OperationId), push.Receipts.Select(receipt => receipt.OperationId));
        List<SpaceDto> spaces = [];
        List<PageDto> pages = [];
        long offset = 0;
        long? cursor = null;
        while (true)
        {
            string route = "/v1/snapshot/chunks?offset=" + offset + (cursor is null ? "" : "&cursor=" + cursor);
            using HttpRequestMessage download = new(HttpMethod.Get, route);
            download.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage downloaded = await client.SendAsync(download);
            Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode);
            using JsonDocument snapshotJson = JsonDocument.Parse(await downloaded.Content.ReadAsStringAsync());
            Assert.Equal(SnapshotProperties, snapshotJson.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            SyncSnapshotChunkResponse chunk = snapshotJson.RootElement.Deserialize<SyncSnapshotChunkResponse>(JsonSerializerOptions.Web)!;
            Assert.Equal(expected.ServerId, chunk.ServerId);
            Assert.Equal(expected.Generation, chunk.Generation);
            Assert.True(chunk.Cursor >= push.Cursor);
            if (cursor is not null)
            {
                Assert.Equal(cursor, chunk.Cursor);
            }

            cursor = chunk.Cursor;
            spaces.AddRange(chunk.Spaces);
            pages.AddRange(chunk.Pages);
            if (chunk.NextOffset is not { } next)
            {
                break;
            }

            Assert.True(next > offset);
            offset = next;
        }

        SyncSnapshotResponse snapshot = new(expected.ServerId, expected.Generation, cursor!.Value, spaces, pages);
        await local.ApplySyncAsync(snapshot, push);
        return new SyncExchange(operations, push, snapshot);
    }

    /// <summary>Retains the actual exchange for assertions about identities, payloads and conditional versions.</summary>
    private sealed record SyncExchange(IReadOnlyList<SyncOperationDto> Sent, SyncPushResponse Push, SyncSnapshotResponse Snapshot);

    /// <summary>Owns isolated server/client directories, a real host and a redirect-free HTTP transport.</summary>
    private sealed class TestInstallation : IAsyncDisposable
    {
        private readonly WebApplication application;
        private readonly string rootDirectory;

        /// <summary>Stores only resources actually created by this test installation.</summary>
        private TestInstallation(WebApplication application, string rootDirectory, HttpClient client)
        {
            this.application = application;
            this.rootDirectory = rootDirectory;
            Client = client;
            Devices = application.Services.GetRequiredService<SqliteDeviceStore>();
        }

        /// <summary>Gets the native HTTP client; no browser, proxy, redirect or authentication mock is used.</summary>
        public HttpClient Client { get; }
        /// <summary>Gets local owner provisioning access without adding any network bootstrap endpoint.</summary>
        public SqliteDeviceStore Devices { get; }
        /// <summary>Gets A's independent durable offline workspace.</summary>
        public string ClientADirectory => Path.Combine(rootDirectory, "client-a");
        /// <summary>Gets B's independent durable offline workspace.</summary>
        public string ClientBDirectory => Path.Combine(rootDirectory, "client-b");

        /// <summary>Starts an explicitly permitted ephemeral loopback listener against real SQLite storage.</summary>
        public static async Task<TestInstallation> StartAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "StudyDesk.TwoClient.Tests-" + Guid.NewGuid().ToString("N"));
            PrivateFile.EnsureDirectory(root);
            ServerSettings settings = new(Path.Combine(root, "server"), "http://127.0.0.1", true, "http://127.0.0.1:0");
            WebApplication app = await ServerApplication.BuildAsync(settings);
            await app.StartAsync();
            string address = Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
            HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { BaseAddress = new Uri(address) };
            return new TestInstallation(app, root, client);
        }

        /// <summary>Closes all resources before removing only this installation's verified temporary root.</summary>
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await application.StopAsync();
            await application.DisposeAsync();
            if (Path.GetFileName(rootDirectory).StartsWith("StudyDesk.TwoClient.Tests-", StringComparison.Ordinal)
                && string.Equals(Path.GetDirectoryName(rootDirectory), Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }
    }
}
