using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using StudyDesk.Contracts;
using StudyDesk.Server.Core.Configuration;
using StudyDesk.Server.Core.Security;
using Xunit;
using Xunit.Abstractions;

namespace StudyDesk.Server.Api.Tests;

/// <summary>Exercises real loopback transport, durable sync, capabilities and device revocation.</summary>
public sealed class ServerApiTests(ITestOutputHelper output)
{
    /// <summary>Anonymous callers receive only installation identity, never contents or device metadata.</summary>
    [Fact]
    public async Task PublicIdentityDoesNotDisclosePrivateData()
    {
        await using RunningServer server = await RunningServer.StartAsync();
        using HttpResponseMessage response = await server.Client.GetAsync("/v1/server");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal(["generation", "protocolVersion", "serverId"], json.RootElement.EnumerateObject().Select(item => item.Name).Order().ToArray());
        Assert.False(response.Headers.Contains("Server"));
        Assert.True(response.Headers.CacheControl?.NoStore);
        using HttpResponseMessage snapshot = await server.Client.GetAsync("/v1/snapshot/chunks?offset=0");
        Assert.Equal(HttpStatusCode.Unauthorized, snapshot.StatusCode);
        using HttpResponseMessage devices = await server.Client.GetAsync("/v1/devices");
        Assert.Equal(HttpStatusCode.Unauthorized, devices.StatusCode);
        using HttpResponseMessage bootstrap = await server.Client.PostAsJsonAsync("/v1/devices", new { name = "Untrusted" });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, bootstrap.StatusCode);
    }

    /// <summary>Stored verifiers, malformed credentials and duplicate headers cannot authenticate.</summary>
    [Fact]
    public async Task VerifiersAndMalformedCredentialsAreRejected()
    {
        await using RunningServer server = await RunningServer.StartAsync();
        CreatedDevice device = await server.Devices.CreateAsync("Desktop", DeviceRole.Owner);
        string verifier = Convert.ToHexStringLower(DeviceToken.Hash(device.Token));
        foreach (string credential in new[] { verifier, device.Token + "0", "not-a-token" })
        {
            using HttpRequestMessage request = new(HttpMethod.Get, "/v1/snapshot/chunks?offset=0");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + credential);
            using HttpResponseMessage response = await server.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using HttpRequestMessage duplicate = new(HttpMethod.Get, "/v1/snapshot/chunks?offset=0");
        duplicate.Headers.TryAddWithoutValidation("Authorization", new[] { "Bearer " + device.Token, "Bearer " + device.Token });
        using HttpResponseMessage duplicated = await server.Client.SendAsync(duplicate);
        Assert.Equal(HttpStatusCode.Unauthorized, duplicated.StatusCode);
    }

    /// <summary>Revocation is immediate and affects only the selected independent association.</summary>
    [Fact]
    public async Task RevocationRejectsSubsequentRequestsWithoutAffectingOtherDevices()
    {
        await using RunningServer server = await RunningServer.StartAsync();
        CreatedDevice first = await server.Devices.CreateAsync("Desktop", DeviceRole.Owner);
        CreatedDevice second = await server.Devices.CreateAsync("Laptop", DeviceRole.Owner);
        server.Use(first.Token);
        using HttpResponseMessage before = await server.Client.GetAsync("/v1/snapshot/chunks?offset=0");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        server.Use(second.Token);
        using HttpResponseMessage revoked = await server.Client.PostAsync($"/v1/devices/{first.Device.Id:D}/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        server.Use(first.Token);
        using HttpResponseMessage denied = await server.Client.GetAsync("/v1/snapshot/chunks?offset=0");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        server.Use(second.Token);
        using HttpResponseMessage unaffected = await server.Client.GetAsync("/v1/snapshot/chunks?offset=0");
        Assert.Equal(HttpStatusCode.OK, unaffected.StatusCode);
        using HttpResponseMessage metadata = await server.Client.GetAsync("/v1/devices");
        string text = await metadata.Content.ReadAsStringAsync();
        Assert.False(text.Contains(first.Token, StringComparison.Ordinal));
        Assert.False(text.Contains(second.Token, StringComparison.Ordinal));
        Assert.False(text.Contains("verifier", StringComparison.OrdinalIgnoreCase));
        Assert.Contains((await server.Devices.ListAsync()), device => device.Id == first.Device.Id && device.RevokedAt is not null);
    }

    /// <summary>Unscoped shared-space roles cannot be provisioned by the owner-only slice.</summary>
    [Theory]
    [InlineData(DeviceRole.Reader)]
    [InlineData(DeviceRole.Editor)]
    public async Task NonOwnerCannotUseOwnerEndpoints(DeviceRole role)
    {
        await using RunningServer server = await RunningServer.StartAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => server.Devices.CreateAsync("Limited", role));
        Assert.Empty(await server.Devices.ListAsync());
    }

    /// <summary>Durable pushes return conflict and duplicate receipts without silently overwriting data.</summary>
    [Fact]
    public async Task PushPersistsDataAndPreservesConflictingChanges()
    {
        await using RunningServer server = await RunningServer.StartAsync();
        CreatedDevice device = await server.Devices.CreateAsync("Desktop", DeviceRole.Owner);
        server.Use(device.Token);
        SyncSnapshotResponse initial = await server.GetSnapshotAsync();
        Guid spaceId = Guid.NewGuid();
        SyncOperationDto create = new(Guid.NewGuid(), "upsertSpace", spaceId, 0,
            JsonSerializer.SerializeToElement(new SpaceDto(spaceId, "Anatomy", 0, false, DateTimeOffset.UtcNow), JsonSerializerOptions.Web));
        SyncPushRequest batch = new(initial.Generation, [create]);
        using HttpResponseMessage accepted = await server.Client.PostAsJsonAsync("/v1/sync/push", batch);
        accepted.EnsureSuccessStatusCode();
        SyncPushResponse result = (await accepted.Content.ReadFromJsonAsync<SyncPushResponse>())!;
        Assert.Equal("accepted", Assert.Single(result.Receipts).Status);
        Assert.Equal(1, result.Cursor);
        using HttpResponseMessage replay = await server.Client.PostAsJsonAsync("/v1/sync/push", batch);
        SyncPushResponse duplicate = (await replay.Content.ReadFromJsonAsync<SyncPushResponse>())!;
        Assert.Equal("duplicate", Assert.Single(duplicate.Receipts).Status);
        Assert.Equal(1, duplicate.Cursor);
        using HttpResponseMessage conflict = await server.Client.PostAsJsonAsync("/v1/sync/push",
            new SyncPushRequest(initial.Generation, [create with { OperationId = Guid.NewGuid() }]));
        SyncPushResponse conflicted = (await conflict.Content.ReadFromJsonAsync<SyncPushResponse>())!;
        Assert.Equal("conflict", Assert.Single(conflicted.Receipts).Status);
        SyncSnapshotResponse current = await server.GetSnapshotAsync();
        Assert.Equal("Anatomy", Assert.Single(current.Spaces).Title);
    }

    /// <summary>JSON escaping may exceed four MiB while the actual Unicode document remains within two MiB.</summary>
    [Fact]
    public async Task UnicodeDocumentLargerThanFourMiBOnTheWireRoundTripsWithoutLoss()
    {
        await using RunningServer server = await RunningServer.StartAsync();
        CreatedDevice device = await server.Devices.CreateAsync("Unicode desktop", DeviceRole.Owner);
        server.Use(device.Token);
        ServerInfoResponse info = (await server.Client.GetFromJsonAsync<ServerInfoResponse>("/v1/server"))!;
        Guid spaceId = Guid.NewGuid();
        Guid pageId = Guid.NewGuid();
        string content = "{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\""
            + new string('\u03b1', 900_000) + "\"}]}]}";
        int contentBytes = Encoding.UTF8.GetByteCount(content);
        Assert.InRange(contentBytes, 1, 2 * 1024 * 1024);
        SyncPushRequest batch = new(info.Generation,
        [
            new SyncOperationDto(Guid.NewGuid(), "upsertSpace", spaceId, 0,
                JsonSerializer.SerializeToElement(new SpaceDto(spaceId, "Unicode", 0, false, DateTimeOffset.UtcNow), JsonSerializerOptions.Web)),
            new SyncOperationDto(Guid.NewGuid(), "upsertPage", pageId, 0,
                JsonSerializer.SerializeToElement(new PageDto(pageId, spaceId, null, "Unicode", content, 0, false, DateTimeOffset.UtcNow), JsonSerializerOptions.Web)),
        ]);
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(batch, JsonSerializerOptions.Web);
        output.WriteLine("Unicode regression: contentBytes={0}; requestBytes={1}.", contentBytes, serialized.Length);
        Assert.InRange(serialized.Length, 4 * 1024 * 1024 + 1, 16 * 1024 * 1024);
        using ByteArrayContent body = new(serialized);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using HttpResponseMessage response = await server.Client.PostAsync("/v1/sync/push", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        SyncPushResponse pushed = (await response.Content.ReadFromJsonAsync<SyncPushResponse>())!;
        Assert.Equal(2, pushed.Receipts.Count);
        Assert.All(pushed.Receipts, receipt => Assert.Equal("accepted", receipt.Status));
        SyncSnapshotResponse snapshot = await server.GetSnapshotAsync();
        Assert.Equal(content, Assert.Single(snapshot.Pages).ContentJson);
    }

    /// <summary>A restored installation generation rejects uploads with an explicit recovery response.</summary>
    [Fact]
    public async Task GenerationMismatchReturnsConflictAndDoesNotMutateData()
    {
        await using RunningServer server = await RunningServer.StartAsync();
        CreatedDevice device = await server.Devices.CreateAsync("Desktop", DeviceRole.Owner);
        server.Use(device.Token);
        using HttpResponseMessage response = await server.Client.PostAsJsonAsync("/v1/sync/push", new SyncPushRequest(Guid.NewGuid(), []));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        SyncPushResponse rejected = (await response.Content.ReadFromJsonAsync<SyncPushResponse>())!;
        Assert.Equal(0, rejected.Cursor);
        Assert.NotEqual(Guid.Empty, rejected.Generation);
    }

    /// <summary>Malformed, oversized and unauthenticated bodies are rejected at the appropriate boundary.</summary>
    [Fact]
    public async Task InvalidAndOversizedBodiesAreBounded()
    {
        await using RunningServer server = await RunningServer.StartAsync();
        using HttpResponseMessage anonymous = await server.Client.PostAsync("/v1/sync/push", new StringContent("not JSON"));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        CreatedDevice device = await server.Devices.CreateAsync("Desktop", DeviceRole.Owner);
        server.Use(device.Token);
        SyncSnapshotResponse before = await server.GetSnapshotAsync();
        using HttpResponseMessage malformed = await server.Client.PostAsync("/v1/sync/push", new StringContent("{", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using SocketsHttpHandler handler = new()
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            Expect100ContinueTimeout = TimeSpan.FromSeconds(30),
        };
        using HttpClient rejectionClient = new(handler, disposeHandler: false) { BaseAddress = server.Client.BaseAddress };
        using AnnouncedOversizedContent content = new(16 * 1024 * 1024 + 1);
        using HttpRequestMessage request = new(HttpMethod.Post, "/v1/sync/push")
        {
            Content = content,
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", device.Token);
        request.Headers.ExpectContinue = true;
        // Wait for the early rejection instead of racing a large upload against Kestrel closing the connection.
        using HttpResponseMessage oversized = await rejectionClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        Assert.False(content.SerializationAttempted);
        using HttpResponseMessage noJson = await server.Client.PostAsync("/v1/sync/push", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, noJson.StatusCode);
        SyncSnapshotResponse after = await server.GetSnapshotAsync();
        Assert.Equal(before.ServerId, after.ServerId);
        Assert.Equal(before.Generation, after.Generation);
        Assert.Equal(before.Cursor, after.Cursor);
        Assert.Empty(after.Spaces);
        Assert.Empty(after.Pages);
    }

    /// <summary>A stalled authenticated upload does not prevent revocation and cannot apply after it.</summary>
    [Fact]
    public async Task SlowUploadDoesNotBlockRevocationAndIsReauthorizedBeforeApply()
    {
        await using RunningServer server = await RunningServer.StartAsync();
        CreatedDevice uploading = await server.Devices.CreateAsync("Uploading laptop", DeviceRole.Owner);
        CreatedDevice revoker = await server.Devices.CreateAsync("Revoking desktop", DeviceRole.Owner);
        ServerInfoResponse info = (await server.Client.GetFromJsonAsync<ServerInfoResponse>("/v1/server"))!;
        Guid spaceId = Guid.NewGuid();
        SyncPushRequest batch = new(info.Generation,
        [
            new SyncOperationDto(Guid.NewGuid(), "upsertSpace", spaceId, 0,
                JsonSerializer.SerializeToElement(new SpaceDto(spaceId, "Must not be stored", 0, false, DateTimeOffset.UtcNow), JsonSerializerOptions.Web)),
        ]);
        using PausedContent body = new(JsonSerializer.SerializeToUtf8Bytes(batch, JsonSerializerOptions.Web));
        using HttpRequestMessage uploadRequest = new(HttpMethod.Post, "/v1/sync/push") { Content = body };
        uploadRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", uploading.Token);
        Task<HttpResponseMessage> upload = server.Client.SendAsync(uploadRequest);
        await body.Started.WaitAsync(TimeSpan.FromSeconds(5));
        await server.BodyReadStarted.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(2));
            using HttpRequestMessage revoke = new(HttpMethod.Post, $"/v1/devices/{uploading.Device.Id:D}/revoke");
            revoke.Headers.Authorization = new AuthenticationHeaderValue("Bearer", revoker.Token);
            using HttpResponseMessage response = await server.Client.SendAsync(revoke, deadline.Token);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        finally
        {
            body.Release();
        }

        using HttpResponseMessage rejected = await upload;
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        server.Use(revoker.Token);
        SyncSnapshotResponse snapshot = await server.GetSnapshotAsync();
        Assert.Empty(snapshot.Spaces);
        Assert.Equal(0, snapshot.Cursor);
    }

    /// <summary>One data directory cannot be served by multiple independent revocation gates.</summary>
    [Fact]
    public async Task SecondHostForTheSameDataDirectoryIsRejected()
    {
        await using RunningServer server = await RunningServer.StartAsync();
        ServerSettings duplicate = new(server.DataDirectory, "http://127.0.0.1", true, "http://127.0.0.1:0");
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await using WebApplication unexpected = await ServerApplication.BuildAsync(duplicate);
        });
        using HttpResponseMessage stillRunning = await server.Client.GetAsync("/v1/server");
        Assert.Equal(HttpStatusCode.OK, stillRunning.StatusCode);
    }

    /// <summary>The host lease does not block trusted local delivery of an additional owner association.</summary>
    [Fact]
    public async Task LocalProvisioningRemainsAvailableWhileHostIsRunning()
    {
        await using RunningServer server = await RunningServer.StartAsync();
        string output = Path.Combine(server.DataDirectory, "pairing", "laptop.studydesk-pairing.json");
        ServerSettings settings = new(server.DataDirectory, "https://example.com", false, "https://127.0.0.1:5050");
        DeviceMetadata created = await PairingProvisioner.CreateOwnerAsync(settings, "Laptop", output);
        using JsonDocument bundle = JsonDocument.Parse(await File.ReadAllBytesAsync(output));
        server.Use(bundle.RootElement.GetProperty("token").GetString()!);
        using HttpResponseMessage snapshot = await server.Client.GetAsync("/v1/snapshot/chunks?offset=0");
        Assert.Equal(HttpStatusCode.OK, snapshot.StatusCode);
        Assert.Equal(created.Id, bundle.RootElement.GetProperty("deviceId").GetGuid());
    }

    /// <summary>Announces an oversized body and fails if the client uploads instead of observing early rejection.</summary>
    private sealed class AnnouncedOversizedContent : HttpContent
    {
        private readonly long length;

        /// <summary>Advertises a known JSON length so the real HTTP server can reject its headers.</summary>
        public AnnouncedOversizedContent(long length)
        {
            this.length = length;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        /// <summary>Gets whether the transport incorrectly started the body before receiving the rejection.</summary>
        public bool SerializationAttempted { get; private set; }

        /// <summary>Rejects a continued upload; transport exceptions are not accepted as a successful server response.</summary>
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            SerializationAttempted = true;
            throw new InvalidOperationException("The oversized request must be rejected before its body is uploaded.");
        }

        /// <summary>Provides the actual Content-Length announcement used by Kestrel's size check.</summary>
        protected override bool TryComputeLength(out long computedLength)
        {
            computedLength = length;
            return true;
        }
    }

    /// <summary>Sends one JSON byte, then deliberately pauses the remaining upload until released.</summary>
    private sealed class PausedContent : HttpContent
    {
        private readonly byte[] bytes;
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Stores a small runtime-created payload and declares its JSON content type.</summary>
        public PausedContent(byte[] bytes)
        {
            this.bytes = bytes;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        /// <summary>Gets completion when the first chunk has been transmitted.</summary>
        public Task Started => started.Task;

        /// <summary>Allows the request to finish without holding any server coordination primitive.</summary>
        public void Release() => released.TrySetResult();

        /// <summary>Writes a deliberate transport pause inside the request body.</summary>
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(bytes.AsMemory(0, 1));
            await stream.FlushAsync();
            started.TrySetResult();
            await released.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await stream.WriteAsync(bytes.AsMemory(1));
        }

        /// <summary>Uses chunked transfer so the body can pause after its first byte.</summary>
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>Observes actual request-body reads without changing transport or authorization behavior.</summary>
    private sealed class ObservedReadStream(Stream inner, TaskCompletionSource readStarted) : Stream
    {
        /// <inheritdoc />
        public override bool CanRead => true;
        /// <inheritdoc />
        public override bool CanSeek => false;
        /// <inheritdoc />
        public override bool CanWrite => false;
        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();
        /// <inheritdoc />
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        /// <inheritdoc />
        public override void Flush() => throw new NotSupportedException();
        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count)
        {
            readStarted.TrySetResult();
            return inner.Read(buffer, offset, count);
        }

        /// <inheritdoc />
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            readStarted.TrySetResult();
            return inner.ReadAsync(buffer, offset, count, cancellationToken);
        }

        /// <inheritdoc />
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            readStarted.TrySetResult();
            return inner.ReadAsync(buffer, cancellationToken);
        }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();
        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Pairing authenticates against bounded cached identity without downloading any notes.</summary>
    [Fact]
    public async Task AccessAuthenticatesWithoutReturningWorkspaceContents()
    {
        await using RunningServer server = await RunningServer.StartAsync();
        using HttpResponseMessage anonymous = await server.Client.GetAsync("/v1/access");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        CreatedDevice device = await server.Devices.CreateAsync("Desktop", DeviceRole.Owner);
        ServerInfoResponse publicInfo = (await server.Client.GetFromJsonAsync<ServerInfoResponse>("/v1/server"))!;
        server.Use(device.Token);
        using HttpResponseMessage authorized = await server.Client.GetAsync("/v1/access");
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        byte[] bytes = await authorized.Content.ReadAsByteArrayAsync();
        Assert.InRange(bytes.Length, 1, 256);
        using JsonDocument json = JsonDocument.Parse(bytes);
        Assert.Equal(["generation", "protocolVersion", "serverId"], json.RootElement.EnumerateObject().Select(item => item.Name).Order().ToArray());
        Assert.Equal(publicInfo, json.RootElement.Deserialize<ServerInfoResponse>(JsonSerializerOptions.Web));
        using HttpResponseMessage revoked = await server.Client.PostAsync($"/v1/devices/{device.Device.Id:D}/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        using HttpResponseMessage denied = await server.Client.GetAsync("/v1/access");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    /// <summary>Ordered bounded chunks reconstruct every space and page once at one cursor.</summary>
    [Fact]
    public async Task SnapshotChunksReconstructAnOrderedCoherentWorkspace()
    {
        await using RunningServer server = await RunningServer.StartAsync();
        using HttpResponseMessage anonymous = await server.Client.GetAsync("/v1/snapshot/chunks?offset=0");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        CreatedDevice device = await server.Devices.CreateAsync("Chunk desktop", DeviceRole.Owner);
        server.Use(device.Token);
        ServerInfoResponse info = (await server.Client.GetFromJsonAsync<ServerInfoResponse>("/v1/access"))!;
        SpaceDto[] spaces = Enumerable.Range(0, 65)
            .Select(index => new SpaceDto(Guid.NewGuid(), "Space " + index, 0, false, DateTimeOffset.UtcNow)).ToArray();
        PageDto[] pages = Enumerable.Range(0, 3)
            .Select(index => new PageDto(Guid.NewGuid(), spaces[0].Id, null, "Page " + index,
                "{\"type\":\"doc\",\"content\":[]}", 0, false, DateTimeOffset.UtcNow)).ToArray();
        SyncOperationDto[] operations = spaces.Select(space => new SyncOperationDto(Guid.NewGuid(), "upsertSpace", space.Id, 0,
                JsonSerializer.SerializeToElement(space, JsonSerializerOptions.Web)))
            .Concat(pages.Select(page => new SyncOperationDto(Guid.NewGuid(), "upsertPage", page.Id, 0,
                JsonSerializer.SerializeToElement(page, JsonSerializerOptions.Web)))).ToArray();
        using HttpResponseMessage seeded = await server.Client.PostAsJsonAsync("/v1/sync/push", new SyncPushRequest(info.Generation, operations));
        SyncPushResponse pushed = (await seeded.Content.ReadFromJsonAsync<SyncPushResponse>())!;
        Assert.All(pushed.Receipts, receipt => Assert.Equal("accepted", receipt.Status));
        List<Guid> actual = [];
        long offset = 0;
        long? cursor = null;
        int chunkCount = 0;
        do
        {
            string route = "/v1/snapshot/chunks?offset=" + offset + (cursor is null ? "" : "&cursor=" + cursor);
            SyncSnapshotChunkResponse chunk = (await server.Client.GetFromJsonAsync<SyncSnapshotChunkResponse>(route))!;
            Assert.Equal(info.ServerId, chunk.ServerId);
            Assert.Equal(info.Generation, chunk.Generation);
            Assert.Equal(pushed.Cursor, chunk.Cursor);
            Assert.InRange(chunk.Spaces.Count, 0, 64);
            Assert.InRange(chunk.Pages.Count, 0, 1);
            actual.AddRange(chunk.Spaces.Select(space => space.Id));
            actual.AddRange(chunk.Pages.Select(page => page.Id));
            chunkCount++;
            Assert.InRange(chunkCount, 1, 4);
            if (chunk.NextOffset is not { } next)
            {
                break;
            }

            Assert.True(next > offset);
            Assert.Equal(offset + chunk.Spaces.Count + chunk.Pages.Count, next);
            offset = next;
            cursor = chunk.Cursor;
        } while (true);
        Guid[] expected = spaces.OrderBy(space => space.Id.ToString("D"), StringComparer.Ordinal).Select(space => space.Id)
            .Concat(pages.OrderBy(page => page.Id.ToString("D"), StringComparer.Ordinal).Select(page => page.Id)).ToArray();
        Assert.Equal(4, chunkCount);
        Assert.Equal(expected, actual);
        Assert.Equal(68, actual.Distinct().Count());
    }

    /// <summary>An archive beyond the desktop's per-response ceiling still downloads in bounded coherent chunks.</summary>
    [Fact]
    public async Task SnapshotChunksDownloadAnArchiveLargerThanThirtyTwoMiB()
    {
        await using RunningServer server = await RunningServer.StartAsync();
        CreatedDevice device = await server.Devices.CreateAsync("Large archive desktop", DeviceRole.Owner);
        server.Use(device.Token);
        ServerInfoResponse info = (await server.Client.GetFromJsonAsync<ServerInfoResponse>("/v1/access"))!;
        Guid spaceId = Guid.NewGuid();
        string content = "{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\""
            + new string('a', 2_000_000) + "\"}]}]}";
        for (int index = 0; index < 17; index++)
        {
            List<SyncOperationDto> operations = [];
            if (index == 0)
            {
                operations.Add(new SyncOperationDto(Guid.NewGuid(), "upsertSpace", spaceId, 0,
                    JsonSerializer.SerializeToElement(new SpaceDto(spaceId, "Large archive", 0, false, DateTimeOffset.UtcNow), JsonSerializerOptions.Web)));
            }

            Guid pageId = Guid.NewGuid();
            operations.Add(new SyncOperationDto(Guid.NewGuid(), "upsertPage", pageId, 0,
                JsonSerializer.SerializeToElement(new PageDto(pageId, spaceId, null, "Page " + index, content, 0, false, DateTimeOffset.UtcNow), JsonSerializerOptions.Web)));
            using HttpResponseMessage pushed = await server.Client.PostAsJsonAsync("/v1/sync/push", new SyncPushRequest(info.Generation, operations));
            Assert.Equal(HttpStatusCode.OK, pushed.StatusCode);
            SyncPushResponse receipts = (await pushed.Content.ReadFromJsonAsync<SyncPushResponse>())!;
            Assert.All(receipts.Receipts, receipt => Assert.Equal("accepted", receipt.Status));
        }

        List<SpaceDto> spaces = [];
        List<PageDto> pages = [];
        long offset = 0;
        long? cursor = null;
        int largestChunkBytes = 0;
        while (true)
        {
            string route = "/v1/snapshot/chunks?offset=" + offset + (cursor is null ? "" : "&cursor=" + cursor);
            using HttpResponseMessage downloaded = await server.Client.GetAsync(route);
            Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode);
            byte[] bytes = await downloaded.Content.ReadAsByteArrayAsync();
            Assert.InRange(bytes.Length, 1, 32 * 1024 * 1024);
            largestChunkBytes = Math.Max(largestChunkBytes, bytes.Length);
            SyncSnapshotChunkResponse chunk = JsonSerializer.Deserialize<SyncSnapshotChunkResponse>(bytes, JsonSerializerOptions.Web)!;
            Assert.Equal(info.ServerId, chunk.ServerId);
            Assert.Equal(info.Generation, chunk.Generation);
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

        Assert.Equal(spaceId, Assert.Single(spaces).Id);
        Assert.Equal(17, pages.Count);
        Assert.All(pages, page => Assert.Equal(content, page.ContentJson));
        byte[] fullJson = JsonSerializer.SerializeToUtf8Bytes(new SyncSnapshotResponse(info.ServerId, info.Generation, cursor!.Value, spaces, pages), JsonSerializerOptions.Web);
        Assert.True(fullJson.Length > 32 * 1024 * 1024);
        output.WriteLine("Chunk regression: pages={0}; fullSnapshotBytes={1}; largestChunkBytes={2}.", pages.Count, fullJson.Length, largestChunkBytes);
    }

    /// <summary>A concurrent mutation rejects continuation without disclosing or mixing partial contents.</summary>
    [Fact]
    public async Task SnapshotChunkCursorChangesReturnConflictWithoutDocuments()
    {
        await using RunningServer server = await RunningServer.StartAsync();
        CreatedDevice device = await server.Devices.CreateAsync("Chunk desktop", DeviceRole.Owner);
        server.Use(device.Token);
        SyncSnapshotChunkResponse initial = (await server.Client.GetFromJsonAsync<SyncSnapshotChunkResponse>("/v1/snapshot/chunks?offset=0"))!;
        Guid id = Guid.NewGuid();
        SyncOperationDto operation = new(Guid.NewGuid(), "upsertSpace", id, 0,
            JsonSerializer.SerializeToElement(new SpaceDto(id, "Private changed title", 0, false, DateTimeOffset.UtcNow), JsonSerializerOptions.Web));
        using HttpResponseMessage accepted = await server.Client.PostAsJsonAsync("/v1/sync/push", new SyncPushRequest(initial.Generation, [operation]));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using HttpResponseMessage conflict = await server.Client.GetAsync("/v1/snapshot/chunks?offset=0&cursor=" + initial.Cursor);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        Assert.Equal("error", Assert.Single(json.RootElement.EnumerateObject()).Name);
        Assert.False(json.RootElement.GetRawText().Contains("Private changed title", StringComparison.Ordinal));
        SyncSnapshotChunkResponse restarted = (await server.Client.GetFromJsonAsync<SyncSnapshotChunkResponse>("/v1/snapshot/chunks?offset=0"))!;
        Assert.Equal(initial.Cursor + 1, restarted.Cursor);
        Assert.Equal(id, Assert.Single(restarted.Spaces).Id);
    }

    /// <summary>Invalid offsets and ambiguous or missing continuation cursors fail before reading pages.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("?offset=-1")]
    [InlineData("?offset=9223372036854775808")]
    [InlineData("?offset=1")]
    [InlineData("?offset=0&cursor=-1")]
    [InlineData("?offset=1&cursor=0")]
    [InlineData("?offset=0&offset=0")]
    public async Task InvalidSnapshotChunkPositionsReturnBadRequest(string query)
    {
        await using RunningServer server = await RunningServer.StartAsync();
        CreatedDevice device = await server.Devices.CreateAsync("Chunk desktop", DeviceRole.Owner);
        server.Use(device.Token);
        using HttpResponseMessage response = await server.Client.GetAsync("/v1/snapshot/chunks" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>The removed full download is a small protected response rather than an unbounded alternate route.</summary>
    [Fact]
    public async Task FullSnapshotRouteReturnsOnlyBoundedMigrationGuidance()
    {
        await using RunningServer server = await RunningServer.StartAsync();
        using HttpResponseMessage anonymous = await server.Client.GetAsync("/v1/snapshot");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        CreatedDevice owner = await server.Devices.CreateAsync("Owner", DeviceRole.Owner);
        server.Use(owner.Token);
        using HttpResponseMessage response = await server.Client.GetAsync("/v1/snapshot");
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        byte[] bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.InRange(bytes.Length, 1, 256);
        using JsonDocument json = JsonDocument.Parse(bytes);
        Assert.Equal("error", Assert.Single(json.RootElement.EnumerateObject()).Name);
        Assert.Contains("/v1/snapshot/chunks?offset=0", json.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);

        // Persist a historical unscoped role using trusted local setup; network authorization still rejects it.
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(server.DataDirectory, "devices.sqlite3"), Pooling = false }.ToString());
        await connection.OpenAsync();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE devices SET role=$role WHERE id=$id;";
        command.Parameters.AddWithValue("$role", (int)DeviceRole.Reader);
        command.Parameters.AddWithValue("$id", owner.Device.Id.ToString("D"));
        await command.ExecuteNonQueryAsync();
        using HttpResponseMessage denied = await server.Client.GetAsync("/v1/snapshot");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    /// <summary>Owns a private ephemeral data directory and real Kestrel loopback listener.</summary>
    private sealed class RunningServer : IAsyncDisposable
    {
        private readonly WebApplication application;
        private readonly string directory;

        /// <summary>Stores resources that are disposed in reverse startup order.</summary>
        private RunningServer(WebApplication application, string directory, HttpClient client, Task bodyReadStarted)
        {
            this.application = application;
            this.directory = directory;
            Client = client;
            BodyReadStarted = bodyReadStarted;
            Devices = application.Services.GetRequiredService<SqliteDeviceStore>();
        }

        /// <summary>Gets the test-only client with automatic redirects disabled.</summary>
        public HttpClient Client { get; }
        /// <summary>Gets local provisioning access, never exposed by the network API.</summary>
        public SqliteDeviceStore Devices { get; }
        /// <summary>Gets the test-owned server data directory for lease and provisioning checks.</summary>
        public string DataDirectory => directory;
        /// <summary>Gets a signal emitted inside the first actual request-body read.</summary>
        public Task BodyReadStarted { get; }

        /// <summary>Starts a listener on an OS-selected loopback port with explicit HTTP development consent.</summary>
        public static async Task<RunningServer> StartAsync()
        {
            string directory = Path.Combine(Path.GetTempPath(), "StudyDesk.Api.Tests-" + Guid.NewGuid().ToString("N"));
            ServerSettings settings = new(directory, "http://127.0.0.1", true, "http://127.0.0.1:0");
            WebApplication application = await ServerApplication.BuildAsync(settings);
            TaskCompletionSource readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            application.Use((context, next) =>
            {
                if (context.Request.Path == "/v1/sync/push")
                {
                    context.Request.Body = new ObservedReadStream(context.Request.Body, readStarted);
                }

                return next();
            });
            await application.StartAsync();
            string address = Assert.Single(application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
            HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(address) };
            return new RunningServer(application, directory, client, readStarted.Task);
        }

        /// <summary>Assembles a complete snapshot only from the actual bounded HTTP chunk protocol.</summary>
        public async Task<SyncSnapshotResponse> GetSnapshotAsync()
        {
            List<SpaceDto> spaces = [];
            List<PageDto> pages = [];
            long offset = 0;
            SyncSnapshotChunkResponse? first = null;
            while (true)
            {
                string route = "/v1/snapshot/chunks?offset=" + offset + (first is null ? "" : "&cursor=" + first.Cursor);
                SyncSnapshotChunkResponse chunk = (await Client.GetFromJsonAsync<SyncSnapshotChunkResponse>(route))!;
                first ??= chunk;
                Assert.Equal(first.ServerId, chunk.ServerId);
                Assert.Equal(first.Generation, chunk.Generation);
                Assert.Equal(first.Cursor, chunk.Cursor);
                spaces.AddRange(chunk.Spaces);
                pages.AddRange(chunk.Pages);
                if (chunk.NextOffset is not { } next)
                {
                    return new SyncSnapshotResponse(first.ServerId, first.Generation, first.Cursor, spaces, pages);
                }

                Assert.True(next > offset);
                offset = next;
            }
        }

        /// <summary>Selects one runtime-generated credential without placing it in URLs.</summary>
        public void Use(string token) => Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        /// <summary>Stops requests and closes database resources before removing test-owned files.</summary>
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await application.StopAsync();
            await application.DisposeAsync();
            if (Path.GetFileName(directory).StartsWith("StudyDesk.Api.Tests-", StringComparison.Ordinal)
                && string.Equals(Path.GetDirectoryName(directory), Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
