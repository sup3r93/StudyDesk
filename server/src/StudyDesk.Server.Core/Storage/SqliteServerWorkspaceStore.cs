using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudyDesk.Contracts;

namespace StudyDesk.Server.Core.Storage;

/// <summary>Stores authoritative entities, history and operation receipts in one SQLite database.</summary>
/// <remarks>Authorization is enforced by the host before entering this persistence boundary.</remarks>
public sealed class SqliteServerWorkspaceStore : IServerWorkspaceStore, IDisposable
{
    private const int MaximumContentBytes = 2 * 1024 * 1024;
    // An outer JSON string may escape each content byte into up to six ASCII bytes.
    private const int MaximumPayloadBytes = MaximumContentBytes * 6 + 8192;
    private const int MaximumOperations = 100;
    private readonly string _databasePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    /// <summary>Uses an application-owned directory selected by the environment-aware host.</summary>
    public SqliteServerWorkspaceStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _databasePath = Path.Combine(Path.GetFullPath(dataDirectory), "workspace.sqlite3");
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, null, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            await ExecuteAsync(connection, transaction, """
                CREATE TABLE IF NOT EXISTS installation (
                    singleton INTEGER PRIMARY KEY CHECK(singleton=1), server_id TEXT NOT NULL,
                    generation TEXT NOT NULL, cursor INTEGER NOT NULL CHECK(cursor>=0));
                CREATE TABLE IF NOT EXISTS spaces (
                    id TEXT PRIMARY KEY, title TEXT NOT NULL, version INTEGER NOT NULL CHECK(version>0),
                    is_trashed INTEGER NOT NULL CHECK(is_trashed IN(0,1)), updated_at TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS pages (
                    id TEXT PRIMARY KEY, space_id TEXT NOT NULL REFERENCES spaces(id),
                    parent_id TEXT REFERENCES pages(id), title TEXT NOT NULL, content_json TEXT NOT NULL,
                    version INTEGER NOT NULL CHECK(version>0), is_trashed INTEGER NOT NULL CHECK(is_trashed IN(0,1)),
                    updated_at TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_pages_space ON pages(space_id);
                CREATE TABLE IF NOT EXISTS receipts (
                    device_id TEXT NOT NULL, operation_id TEXT NOT NULL, digest TEXT NOT NULL,
                    receipt_json TEXT NOT NULL, PRIMARY KEY(device_id,operation_id));
                CREATE TABLE IF NOT EXISTS revisions (
                    entity_id TEXT NOT NULL, version INTEGER NOT NULL, entity_json TEXT NOT NULL,
                    created_at TEXT NOT NULL, PRIMARY KEY(entity_id,version));
                """, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction,
                "INSERT OR IGNORE INTO installation VALUES(1,$server,$generation,0);", cancellationToken,
                ("$server", Key(Guid.NewGuid())), ("$generation", Key(Guid.NewGuid()))).ConfigureAwait(false);
            transaction.Commit();
            _initialized = true;
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public async Task<SyncSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            var identity = await ReadIdentityAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var spaces = new List<SpaceDto>();
            await using (var command = Command(connection, transaction, "SELECT id,title,version,is_trashed,updated_at FROM spaces ORDER BY id;"))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    spaces.Add(ReadSpace(reader));
                }
            }

            var pages = new List<PageDto>();
            await using (var command = Command(connection, transaction, "SELECT id,space_id,parent_id,title,content_json,version,is_trashed,updated_at FROM pages ORDER BY id;"))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    pages.Add(ReadPage(reader));
                }
            }

            transaction.Commit();
            return new SyncSnapshotResponse(identity.ServerId, identity.Generation, identity.Cursor, spaces, pages);
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public async Task<SyncSnapshotChunkResponse> GetSnapshotChunkAsync(long offset, long? expectedCursor,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (expectedCursor is < 0) { throw new ArgumentOutOfRangeException(nameof(expectedCursor), "The expected cursor cannot be negative."); }
        if (offset > 0 && expectedCursor is null)
        { throw new ArgumentException("A continuation offset requires its initial snapshot cursor.", nameof(expectedCursor)); }
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            var identity = await ReadIdentityAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (expectedCursor is { } requiredCursor && identity.Cursor != requiredCursor)
            { throw new SnapshotCursorChangedException(); }

            long spaceCount;
            long pageCount;
            await using (var command = Command(connection, transaction, "SELECT (SELECT COUNT(*) FROM spaces),(SELECT COUNT(*) FROM pages);"))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                { throw new InvalidOperationException("The snapshot entity counts are unavailable."); }
                spaceCount = reader.GetInt64(0);
                pageCount = reader.GetInt64(1);
            }
            var totalCount = checked(spaceCount + pageCount);
            if (offset > totalCount)
            { throw new ArgumentOutOfRangeException(nameof(offset), "The snapshot offset is beyond the current snapshot."); }

            var spaces = new List<SpaceDto>();
            if (offset < spaceCount)
            {
                await using var command = Command(connection, transaction,
                    "SELECT id,title,version,is_trashed,updated_at FROM spaces ORDER BY id LIMIT 64 OFFSET $offset;", ("$offset", offset));
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                { spaces.Add(ReadSpace(reader)); }
            }
            var pages = new List<PageDto>();
            var consumedOffset = checked(offset + spaces.Count);
            if (consumedOffset >= spaceCount && consumedOffset < totalCount)
            {
                var pageOffset = consumedOffset - spaceCount;
                await using var command = Command(connection, transaction,
                    "SELECT id,space_id,parent_id,title,content_json,version,is_trashed,updated_at FROM pages ORDER BY id LIMIT 1 OFFSET $offset;",
                    ("$offset", pageOffset));
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                { pages.Add(ReadPage(reader)); }
                consumedOffset = checked(consumedOffset + pages.Count);
            }
            long? nextOffset = consumedOffset < totalCount ? consumedOffset : null;
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return new SyncSnapshotChunkResponse(identity.ServerId, identity.Generation, identity.Cursor, nextOffset, spaces, pages);
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public async Task<SyncPushResponse> ApplyOperationsAsync(Guid deviceId, SyncPushRequest request,
        CancellationToken cancellationToken = default)
    {
        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("A device identity is required.", nameof(deviceId));
        }

        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Operations);
        if (request.Operations.Count > MaximumOperations || request.Operations.Any(operation => operation is null))
        {
            throw new ArgumentException("The operation batch is invalid or exceeds 100 items.", nameof(request));
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            var identity = await ReadIdentityAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (identity.Generation != request.Generation)
            {
                return new SyncPushResponse(identity.Generation, identity.Cursor, request.Operations.Select(operation =>
                    Invalid(operation, "Server generation changed; preserve drafts and resnapshot.")).ToArray());
            }

            var cursor = identity.Cursor;
            var receipts = new List<OperationReceiptDto>();
            foreach (var operation in request.Operations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var digest = Digest(operation);
                var previous = await ReadReceiptAsync(connection, transaction, deviceId, operation.OperationId, cancellationToken).ConfigureAwait(false);
                if (previous is not null)
                {
                    var original = previous.Value.Receipt;
                    receipts.Add(previous.Value.Digest != digest
                        ? Invalid(operation, "Operation identity was already used with different content.")
                        : original.Status == "accepted" ? original with { Status = "duplicate" } : original);
                    continue;
                }
                var receipt = await ApplyOneAsync(connection, transaction, operation, cancellationToken).ConfigureAwait(false);
                if (receipt.Status == "accepted")
                {
                    cursor = checked(cursor + 1);
                }

                await ExecuteAsync(connection, transaction,
                    "INSERT INTO receipts VALUES($device,$operation,$digest,$receipt);", cancellationToken,
                    ("$device", Key(deviceId)), ("$operation", Key(operation.OperationId)), ("$digest", digest),
                    ("$receipt", JsonSerializer.Serialize(receipt, JsonSerializerOptions.Web))).ConfigureAwait(false);
                receipts.Add(receipt);
            }
            await ExecuteAsync(connection, transaction, "UPDATE installation SET cursor=$cursor WHERE singleton=1;",
                cancellationToken, ("$cursor", cursor)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return new SyncPushResponse(identity.Generation, cursor, receipts);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Releases the instance coordination primitive after all operations have completed.</summary>
    public void Dispose() => _gate.Dispose();

    /// <summary>Validates and applies one entity change inside the existing transaction.</summary>
    private static async Task<OperationReceiptDto> ApplyOneAsync(SqliteConnection connection,
        SqliteTransaction transaction, SyncOperationDto operation, CancellationToken cancellationToken)
    {
        if (operation.OperationId == Guid.Empty || operation.EntityId == Guid.Empty || operation.ExpectedVersion < 0 ||
            operation.Payload.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(operation.Payload.GetRawText()) > MaximumPayloadBytes)
        {
            return Invalid(operation, "Invalid operation identity, revision or payload.");
        }

        try
        {
            if (operation.Kind == "upsertSpace")
            {
                var proposed = operation.Payload.Deserialize<SpaceDto>(JsonSerializerOptions.Web);
                if (proposed is null || proposed.Id != operation.EntityId || !ValidTitle(proposed.Title) || proposed.IsTrashed)
                {
                    return Invalid(operation, "Invalid space payload.");
                }

                if (await FindPageAsync(connection, transaction, proposed.Id, cancellationToken).ConfigureAwait(false) is not null)
                {
                    return Invalid(operation, "Entity identity is already used by a page.");
                }

                var current = await FindSpaceAsync(connection, transaction, proposed.Id, cancellationToken).ConfigureAwait(false);
                if ((current?.Version ?? 0) != operation.ExpectedVersion)
                {
                    return Conflict(operation, current?.Version ?? 0, current);
                }

                var saved = proposed with { Title = proposed.Title.Trim(), Version = checked((current?.Version ?? 0) + 1), UpdatedAt = DateTimeOffset.UtcNow };
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO spaces VALUES($id,$title,$version,0,$updated)
                    ON CONFLICT(id) DO UPDATE SET title=excluded.title,version=excluded.version,updated_at=excluded.updated_at;
                    """, cancellationToken, ("$id", Key(saved.Id)), ("$title", saved.Title), ("$version", saved.Version),
                    ("$updated", Stamp(saved.UpdatedAt))).ConfigureAwait(false);
                return await RememberRevisionAsync(connection, transaction, operation, saved.Version, saved, cancellationToken).ConfigureAwait(false);
            }
            if (operation.Kind is not ("upsertPage" or "trashPage" or "restorePage"))
            {
                return Invalid(operation, "Unknown operation kind.");
            }

            var page = operation.Payload.Deserialize<PageDto>(JsonSerializerOptions.Web);
            if (page is null || page.Id != operation.EntityId || page.SpaceId == Guid.Empty || !ValidTitle(page.Title) || !ValidContent(page.ContentJson))
            {
                return Invalid(operation, "Invalid page payload.");
            }

            if (await FindSpaceAsync(connection, transaction, page.Id, cancellationToken).ConfigureAwait(false) is not null)
            {
                return Invalid(operation, "Entity identity is already used by a space.");
            }

            var existing = await FindPageAsync(connection, transaction, page.Id, cancellationToken).ConfigureAwait(false);
            if ((existing?.Version ?? 0) != operation.ExpectedVersion)
            {
                return Conflict(operation, existing?.Version ?? 0, existing);
            }

            if (existing is not null && existing.SpaceId != page.SpaceId)
            {
                return Invalid(operation, "Cross-space page moves are not supported.");
            }

            if (operation.Kind == "trashPage" && (!page.IsTrashed || existing is null) ||
                operation.Kind == "restorePage" && (page.IsTrashed || existing is null))
            {
                return Invalid(operation, "Trash or restore requires a matching existing page state.");
            }

            if (operation.Kind == "upsertPage" && existing is not null && existing.IsTrashed != page.IsTrashed)
            {
                return Invalid(operation, "Use an explicit trash or restore operation for a lifecycle change.");
            }

            var space = await FindSpaceAsync(connection, transaction, page.SpaceId, cancellationToken).ConfigureAwait(false);
            if (space is null || space.IsTrashed)
            {
                return Invalid(operation, "The owning space is unavailable.");
            }

            if (!await ValidParentAsync(connection, transaction, page, cancellationToken).ConfigureAwait(false))
            {
                return Invalid(operation, "The page hierarchy is invalid, cyclic or unavailable.");
            }

            var stored = page with { Title = page.Title.Trim(), Version = checked((existing?.Version ?? 0) + 1), UpdatedAt = DateTimeOffset.UtcNow };
            await ExecuteAsync(connection, transaction, """
                INSERT INTO pages VALUES($id,$space,$parent,$title,$content,$version,$trash,$updated)
                ON CONFLICT(id) DO UPDATE SET parent_id=excluded.parent_id,title=excluded.title,
                    content_json=excluded.content_json,version=excluded.version,is_trashed=excluded.is_trashed,updated_at=excluded.updated_at;
                """, cancellationToken, ("$id", Key(stored.Id)), ("$space", Key(stored.SpaceId)),
                ("$parent", stored.ParentId is null ? null : Key(stored.ParentId.Value)), ("$title", stored.Title),
                ("$content", stored.ContentJson), ("$version", stored.Version), ("$trash", stored.IsTrashed ? 1 : 0),
                ("$updated", Stamp(stored.UpdatedAt))).ConfigureAwait(false);
            return await RememberRevisionAsync(connection, transaction, operation, stored.Version, stored, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException) { return Invalid(operation, "Payload is not a compatible JSON entity."); }
        catch (OverflowException) { return Invalid(operation, "Entity revision limit was reached."); }
    }

    /// <summary>Ensures each ancestor exists in the same space and cannot form a cycle.</summary>
    private static async Task<bool> ValidParentAsync(SqliteConnection connection, SqliteTransaction transaction,
        PageDto page, CancellationToken cancellationToken)
    {
        var visited = new HashSet<Guid> { page.Id };
        var parentId = page.ParentId;
        while (parentId is { } id)
        {
            if (id == Guid.Empty || !visited.Add(id))
            {
                return false;
            }

            var parent = await FindPageAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
            if (parent is null || parent.SpaceId != page.SpaceId || parent.IsTrashed && !page.IsTrashed)
            {
                return false;
            }

            parentId = parent.ParentId;
        }
        return true;
    }

    /// <summary>Persists the resulting recovery revision before constructing its success receipt.</summary>
    private static async Task<OperationReceiptDto> RememberRevisionAsync<T>(SqliteConnection connection,
        SqliteTransaction transaction, SyncOperationDto operation, long version, T entity, CancellationToken cancellationToken)
    {
        var value = JsonSerializer.SerializeToElement(entity, JsonSerializerOptions.Web);
        await ExecuteAsync(connection, transaction, "INSERT INTO revisions VALUES($id,$version,$json,$created);",
            cancellationToken, ("$id", Key(operation.EntityId)), ("$version", version), ("$json", value.GetRawText()),
            ("$created", Stamp(DateTimeOffset.UtcNow))).ConfigureAwait(false);
        return new OperationReceiptDto(operation.OperationId, "accepted", version, null, value);
    }

    /// <summary>Builds a non-success conflict receipt without changing the stored entity.</summary>
    private static OperationReceiptDto Conflict<T>(SyncOperationDto operation, long version, T? current) =>
        new(operation.OperationId, "conflict", version, "The entity changed on the server; preserve the local draft.",
            current is null ? null : JsonSerializer.SerializeToElement(current, JsonSerializerOptions.Web));

    /// <summary>Builds a non-success validation receipt with no private diagnostic details.</summary>
    private static OperationReceiptDto Invalid(SyncOperationDto operation, string message) =>
        new(operation.OperationId, "invalid", 0, message, null);

    /// <summary>Hashes immutable operation content, including metadata and raw JSON representation.</summary>
    private static string Digest(SyncOperationDto operation)
    {
        var payload = operation.Payload.ValueKind == JsonValueKind.Undefined ? "undefined" : operation.Payload.GetRawText();
        var value = string.Join('\n', operation.Kind, Key(operation.EntityId), operation.ExpectedVersion.ToString(CultureInfo.InvariantCulture), payload);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    /// <summary>Reads a stored operation receipt without exposing its verifier to callers.</summary>
    private static async Task<(string Digest, OperationReceiptDto Receipt)?> ReadReceiptAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid device, Guid operation, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT digest,receipt_json FROM receipts WHERE device_id=$device AND operation_id=$operation;",
            ("$device", Key(device)), ("$operation", Key(operation)));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return (reader.GetString(0), JsonSerializer.Deserialize<OperationReceiptDto>(reader.GetString(1), JsonSerializerOptions.Web)!);
    }

    /// <summary>Reads the single persistent installation identity and change sequence.</summary>
    private static async Task<(Guid ServerId, Guid Generation, long Cursor)> ReadIdentityAsync(SqliteConnection connection,
        SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT server_id,generation,cursor FROM installation WHERE singleton=1;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Server storage is not initialized.");
        }

        return (Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetInt64(2));
    }

    /// <summary>Finds one authoritative space in the current transaction.</summary>
    private static async Task<SpaceDto?> FindSpaceAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid id, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT id,title,version,is_trashed,updated_at FROM spaces WHERE id=$id;", ("$id", Key(id)));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSpace(reader) : null;
    }

    /// <summary>Finds one authoritative page in the current transaction.</summary>
    private static async Task<PageDto?> FindPageAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid id, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT id,space_id,parent_id,title,content_json,version,is_trashed,updated_at FROM pages WHERE id=$id;", ("$id", Key(id)));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadPage(reader) : null;
    }

    /// <summary>Maps a complete space row to the shared contract.</summary>
    private static SpaceDto ReadSpace(SqliteDataReader reader) => new(Guid.Parse(reader.GetString(0)), reader.GetString(1),
        reader.GetInt64(2), reader.GetInt64(3) != 0, DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture));

    /// <summary>Maps a complete page row to the shared contract.</summary>
    private static PageDto ReadPage(SqliteDataReader reader) => new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
        reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)), reader.GetString(3), reader.GetString(4), reader.GetInt64(5),
        reader.GetInt64(6) != 0, DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture));

    /// <summary>Opens a connection with bounded locking, foreign keys and full commit durability.</summary>
    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = _databasePath, Pooling = false, ForeignKeys = true, DefaultTimeout = 5 }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, null, "PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;", cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }

    /// <summary>Creates a parameterized command without concatenating user values into SQL.</summary>
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    /// <summary>Executes a parameterized statement in the supplied transaction.</summary>
    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Bounds a user-visible title before any database mutation.</summary>
    private static bool ValidTitle(string? title) => !string.IsNullOrWhiteSpace(title) && Encoding.UTF8.GetByteCount(title) <= 512;

    /// <summary>Checks that content is bounded JSON with an object root.</summary>
    private static bool ValidContent(string? content)
    {
        if (content is null || Encoding.UTF8.GetByteCount(content) > MaximumContentBytes)
        {
            return false;
        }

        try { using var document = JsonDocument.Parse(content); return document.RootElement.ValueKind == JsonValueKind.Object; }
        catch (JsonException) { return false; }
    }

    /// <summary>Formats an identifier independently of the current culture.</summary>
    private static string Key(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>Formats a UTC timestamp without losing precision.</summary>
    private static string Stamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
