using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using StudyDesk.Contracts;
using StudyDesk.Desktop.Synchronization;
using Xunit;

namespace StudyDesk.Desktop.Ui.Tests;

/// <summary>Checks native authenticated access and coherent, all-or-nothing snapshot assembly.</summary>
public sealed partial class NativeTransportTests
{
    /// <summary>Pairing confirms access without requiring a snapshot download.</summary>
    [Fact]
    public async Task PairingUsesLightweightAuthenticatedAccessCheck()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        string body = JsonSerializer.Serialize(new ServerInfoResponse(Guid.NewGuid(), Guid.NewGuid(), 1), JsonSerializerOptions.Web);
        Task<string> request = ReceiveOneAsync(listener, "200 OK", body, null, timeout.Token);
        string credential = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using DataServerClient client = new(Address(listener), credential, true);
        await client.VerifyAccessAsync(timeout.Token);
        string headers = await request;
        Assert.StartsWith("GET /v1/access ", headers, StringComparison.Ordinal);
        Assert.Contains("Authorization: Bearer " + credential, headers, StringComparison.Ordinal);
    }

    /// <summary>Continuation requests carry the first snapshot's immutable cursor and produce one complete result.</summary>
    [Fact]
    public async Task SnapshotAssemblesCoherentChunksWithPinnedCursor()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Guid server = Guid.NewGuid();
        Guid generation = Guid.NewGuid();
        SpaceDto space = new(Guid.NewGuid(), "Synthetic space", 1, false, DateTimeOffset.UtcNow);
        PageDto page = new(Guid.NewGuid(), space.Id, null, "Synthetic note", "{\"type\":\"doc\"}", 1, false, DateTimeOffset.UtcNow);
        SyncSnapshotChunkResponse first = new(server, generation, 23, 1, [space], []);
        SyncSnapshotChunkResponse last = new(server, generation, 23, null, [], [page]);
        Task<string[]> requests = ServeChunksAsync(listener, [first, last], timeout.Token);
        using DataServerClient client = new(Address(listener), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), true);
        SyncSnapshotResponse snapshot = await client.GetSnapshotAsync(timeout.Token);
        string[] headers = await requests;
        Assert.Single(snapshot.Spaces);
        Assert.Single(snapshot.Pages);
        Assert.Equal(space.Id, snapshot.Spaces[0].Id);
        Assert.Equal(page.Id, snapshot.Pages[0].Id);
        Assert.Equal(23, snapshot.Cursor);
        Assert.StartsWith("GET /v1/snapshot/chunks?offset=0 ", headers[0], StringComparison.Ordinal);
        Assert.StartsWith("GET /v1/snapshot/chunks?offset=1&cursor=23 ", headers[1], StringComparison.Ordinal);
    }

    /// <summary>A changed identity, generation, cursor or repeated entity prevents any completed snapshot result.</summary>
    [Theory]
    [InlineData("server")]
    [InlineData("generation")]
    [InlineData("cursor")]
    [InlineData("duplicate")]
    public async Task IncoherentSnapshotChunksAreRejected(string problem)
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Guid server = Guid.NewGuid();
        Guid generation = Guid.NewGuid();
        SpaceDto space = new(Guid.NewGuid(), "Synthetic space", 1, false, DateTimeOffset.UtcNow);
        SyncSnapshotChunkResponse first = new(server, generation, 23, 1, [space], []);
        SyncSnapshotChunkResponse last = new(problem == "server" ? Guid.NewGuid() : server,
            problem == "generation" ? Guid.NewGuid() : generation, problem == "cursor" ? 24 : 23,
            null, problem == "duplicate" ? [space] : [], []);
        Task<string[]> requests = ServeChunksAsync(listener, [first, last], timeout.Token);
        using DataServerClient client = new(Address(listener), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), true);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetSnapshotAsync(timeout.Token));
        Assert.Equal(2, (await requests).Length);
    }

    /// <summary>A continuation must advance by exactly the number of entities supplied in its chunk.</summary>
    [Fact]
    public async Task NonAdvancingSnapshotContinuationIsRejected()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        SyncSnapshotChunkResponse invalid = new(Guid.NewGuid(), Guid.NewGuid(), 23, 0, [], []);
        Task<string[]> requests = ServeChunksAsync(listener, [invalid], timeout.Token);
        using DataServerClient client = new(Address(listener), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), true);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetSnapshotAsync(timeout.Token));
        Assert.Single(await requests);
    }

    /// <summary>Serves a deterministic sequence of small snapshot chunks for the native client tests.</summary>
    private static async Task<string[]> ServeChunksAsync(TcpListener listener, IReadOnlyList<SyncSnapshotChunkResponse> chunks, CancellationToken cancellationToken)
    {
        List<string> received = [];
        foreach (SyncSnapshotChunkResponse chunk in chunks)
        {
            string body = JsonSerializer.Serialize(chunk, JsonSerializerOptions.Web);
            received.Add(await ReceiveOneAsync(listener, "200 OK", body, null, cancellationToken));
        }

        return [.. received];
    }
}
