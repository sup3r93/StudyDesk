using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudyDesk.Contracts;
using StudyDesk.Server.Core.Storage;
using Xunit;

namespace StudyDesk.Sync.Integration.Tests;

/// <summary>Verifies bounded, coherent and read-only snapshot transfer against actual SQLite data.</summary>
public sealed class SnapshotChunkTests
{
    /// <summary>Reconstructs a snapshot larger than the client response limit without any oversized response.</summary>
    [Fact]
    public async Task LargeSnapshotChunksAreBoundedCompleteAndDoNotMutateStorage()
    {
        using var fixture = new ChunkFixture();
        await fixture.SeedLargeSnapshotAsync();
        var original = await fixture.Server.GetSnapshotAsync();
        var fullBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(original, JsonSerializerOptions.Web));
        Assert.True(fullBytes > 32 * 1024 * 1024, "The synthetic snapshot must exceed the client response limit.");
        var spaces = new List<SpaceDto>();
        var pages = new List<PageDto>();
        long offset = 0;
        long? cursor = null;
        var chunks = 0;
        while (true)
        {
            var chunk = await fixture.Server.GetSnapshotChunkAsync(offset, cursor);
            Assert.Equal(original.ServerId, chunk.ServerId);
            Assert.Equal(original.Generation, chunk.Generation);
            Assert.Equal(original.Cursor, chunk.Cursor);
            Assert.InRange(chunk.Spaces.Count, 0, 64);
            Assert.InRange(chunk.Pages.Count, 0, 1);
            Assert.True(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(chunk, JsonSerializerOptions.Web)) < 32 * 1024 * 1024);
            spaces.AddRange(chunk.Spaces);
            pages.AddRange(chunk.Pages);
            chunks++;
            if (chunk.NextOffset is not { } nextOffset) { break; }
            Assert.Equal(offset + chunk.Spaces.Count + chunk.Pages.Count, nextOffset);
            Assert.True(nextOffset > offset);
            offset = nextOffset;
            cursor = chunk.Cursor;
        }
        Assert.True(chunks > 2);
        Assert.Equal(original.Spaces, spaces);
        Assert.Equal(original.Pages, pages);
        var after = await fixture.Server.GetSnapshotAsync();
        Assert.Equal(original.ServerId, after.ServerId);
        Assert.Equal(original.Generation, after.Generation);
        Assert.Equal(original.Cursor, after.Cursor);
        Assert.Equal(original.Spaces, after.Spaces);
        Assert.Equal(original.Pages, after.Pages);
        Assert.Equal(0, await fixture.ScalarAsync("SELECT COUNT(*) FROM receipts;"));
        Assert.Equal(0, await fixture.ScalarAsync("SELECT COUNT(*) FROM revisions;"));
    }

    /// <summary>Verifies a committed intervening write invalidates the next continuation before data is returned.</summary>
    [Fact]
    public async Task ContinuationRejectsChangedCursorAndAllowsAFreshTransfer()
    {
        using var fixture = new ChunkFixture();
        var empty = await fixture.Server.GetSnapshotAsync();
        var operations = Enumerable.Range(0, 70).Select(index => CreateSpace("Space " + index.ToString(CultureInfo.InvariantCulture))).ToArray();
        var accepted = await fixture.Server.ApplyOperationsAsync(Guid.NewGuid(), new(empty.Generation, operations));
        Assert.All(accepted.Receipts, receipt => Assert.Equal("accepted", receipt.Status));
        var first = await fixture.Server.GetSnapshotChunkAsync(0, null);
        Assert.Equal(64, first.NextOffset);
        Assert.Equal(64, first.Spaces.Count);
        var change = await fixture.Server.ApplyOperationsAsync(Guid.NewGuid(), new(empty.Generation, [CreateSpace("New note collection")]));
        Assert.Equal("accepted", Assert.Single(change.Receipts).Status);
        await Assert.ThrowsAsync<SnapshotCursorChangedException>(() => fixture.Server.GetSnapshotChunkAsync(first.NextOffset!.Value, first.Cursor));
        Assert.Equal(64, first.Spaces.Count);
        Assert.Empty(first.Pages);
        var restarted = await fixture.Server.GetSnapshotChunkAsync(0, null);
        Assert.Equal(first.Cursor + 1, restarted.Cursor);
        var final = await fixture.Server.GetSnapshotChunkAsync(restarted.NextOffset!.Value, restarted.Cursor);
        Assert.Null(final.NextOffset);
        Assert.Equal(71, restarted.Spaces.Count + final.Spaces.Count);
        Assert.Equal(change.Cursor, (await fixture.Server.GetSnapshotAsync()).Cursor);
    }

    /// <summary>Verifies offset and cursor validation cannot produce ambiguous continuation behavior.</summary>
    [Theory]
    [InlineData(-1L, null)]
    [InlineData(1L, null)]
    [InlineData(0L, -1L)]
    [InlineData(long.MaxValue, 0L)]
    public async Task InvalidContinuationArgumentsAreRejected(long offset, long? cursor)
    {
        using var fixture = new ChunkFixture();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Server.GetSnapshotChunkAsync(offset, cursor));
        Assert.Empty((await fixture.Server.GetSnapshotAsync()).Spaces);
    }

    /// <summary>Verifies an empty snapshot and an exact terminal offset have explicit end markers.</summary>
    [Fact]
    public async Task EmptyAndTerminalChunksHaveNoContinuation()
    {
        using var fixture = new ChunkFixture();
        var empty = await fixture.Server.GetSnapshotChunkAsync(0, null);
        Assert.Null(empty.NextOffset);
        Assert.Empty(empty.Spaces);
        Assert.Empty(empty.Pages);
        var write = await fixture.Server.ApplyOperationsAsync(Guid.NewGuid(), new(empty.Generation, [CreateSpace("One collection")]));
        var terminal = await fixture.Server.GetSnapshotChunkAsync(1, write.Cursor);
        Assert.Null(terminal.NextOffset);
        Assert.Empty(terminal.Spaces);
        Assert.Empty(terminal.Pages);
    }

    /// <summary>Constructs a valid protocol operation without relying on snapshot implementation details.</summary>
    private static SyncOperationDto CreateSpace(string title)
    {
        var space = new SpaceDto(Guid.NewGuid(), title, 0, false, DateTimeOffset.UtcNow);
        return new SyncOperationDto(Guid.NewGuid(), "upsertSpace", space.Id, 0,
            JsonSerializer.SerializeToElement(space, JsonSerializerOptions.Web));
    }

    /// <summary>Owns one isolated database and provides a fast synthetic large-data fixture.</summary>
    private sealed class ChunkFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "StudyDesk-chunk-tests", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        /// <summary>Creates a server store within a uniquely owned test directory.</summary>
        public ChunkFixture()
        {
            Directory.CreateDirectory(_root);
            Server = new SqliteServerWorkspaceStore(_root);
        }

        /// <summary>Gets the real persistence service under test.</summary>
        public SqliteServerWorkspaceStore Server { get; }

        /// <summary>Inserts legitimate committed rows exceeding 32 MiB in serialized aggregate without large HTTP uploads.</summary>
        public async Task SeedLargeSnapshotAsync()
        {
            await Server.InitializeAsync();
            await using var connection = Connection();
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();
            var spaceIds = Enumerable.Range(0, 130).Select(_ => Guid.NewGuid()).ToArray();
            var stamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            foreach (var id in spaceIds)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO spaces VALUES($id,$title,1,0,$stamp);";
                command.Parameters.AddWithValue("$id", id.ToString("D", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$title", "Synthetic collection");
                command.Parameters.AddWithValue("$stamp", stamp);
                await command.ExecuteNonQueryAsync();
            }
            var content = "{\"text\":\"" + new string('è', 700_000) + "\"}";
            Guid? parent = null;
            for (var index = 0; index < 9; index++)
            {
                var id = Guid.NewGuid();
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO pages VALUES($id,$space,$parent,$title,$content,1,$trash,$stamp);";
                command.Parameters.AddWithValue("$id", id.ToString("D", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$space", spaceIds[0].ToString("D", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$parent", parent is { } parentId ? parentId.ToString("D", CultureInfo.InvariantCulture) : DBNull.Value);
                command.Parameters.AddWithValue("$title", "Large synthetic note " + index.ToString(CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$content", content);
                command.Parameters.AddWithValue("$trash", index == 8 ? 1 : 0);
                command.Parameters.AddWithValue("$stamp", stamp);
                await command.ExecuteNonQueryAsync();
                parent = id;
            }
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "UPDATE installation SET cursor=139 WHERE singleton=1;";
                await command.ExecuteNonQueryAsync();
            }
            transaction.Commit();
        }

        /// <summary>Reads an invariant from the synthetic fixture without modifying the store.</summary>
        public async Task<long> ScalarAsync(string sql)
        {
            await using var connection = Connection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        /// <summary>Creates an unpooled connection to the uniquely owned fixture database.</summary>
        private SqliteConnection Connection() => new(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(_root, "workspace.sqlite3"), Pooling = false, ForeignKeys = true }.ToString());

        /// <summary>Closes the server before deleting only this fixture's directory.</summary>
        public void Dispose()
        {
            Server.Dispose();
            Directory.Delete(_root, true);
        }
    }
}
