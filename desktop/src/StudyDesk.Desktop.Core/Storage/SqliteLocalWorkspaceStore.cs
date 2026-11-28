using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudyDesk.Contracts;

namespace StudyDesk.Desktop.Core.Storage;

/// <summary>Persists offline documents, recovery revisions and immutable in-flight synchronization operations.</summary>
/// <remarks>The host selects the data directory. Each save commits the document and its pending intent atomically.</remarks>
public sealed class SqliteLocalWorkspaceStore : IDisposable
{
    private const int MaximumContentBytes = 2 * 1024 * 1024;
    private const int TargetBatchBytes = 3 * 1024 * 1024;
    private readonly string _databasePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    /// <summary>Uses a local application-owned directory selected by the environment-aware host.</summary>
    public SqliteLocalWorkspaceStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _databasePath = Path.Combine(Path.GetFullPath(dataDirectory), "workspace.sqlite3");
    }

    /// <summary>Creates durable storage without requiring a server connection or credentials.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) { return; }
            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, null, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            await ExecuteAsync(connection, transaction, """
                CREATE TABLE IF NOT EXISTS entities (
                    id TEXT PRIMARY KEY, kind TEXT NOT NULL CHECK(kind IN('space','page')),
                    payload TEXT NOT NULL, local_version INTEGER NOT NULL CHECK(local_version>0),
                    server_version INTEGER NOT NULL CHECK(server_version>=0));
                CREATE TABLE IF NOT EXISTS outbox (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT, entity_id TEXT NOT NULL UNIQUE REFERENCES entities(id),
                    operation_id TEXT NOT NULL UNIQUE, operation_json TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS sent_operations (
                    sequence INTEGER PRIMARY KEY, entity_id TEXT NOT NULL UNIQUE REFERENCES entities(id),
                    operation_id TEXT NOT NULL UNIQUE, operation_json TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS local_revisions (
                    entity_id TEXT NOT NULL REFERENCES entities(id), local_version INTEGER NOT NULL,
                    payload TEXT NOT NULL, created_at TEXT NOT NULL, PRIMARY KEY(entity_id,local_version));
                CREATE TABLE IF NOT EXISTS sync_issues (
                    operation_id TEXT PRIMARY KEY, entity_id TEXT NOT NULL UNIQUE REFERENCES entities(id),
                    status TEXT NOT NULL, current_version INTEGER NOT NULL, message TEXT);
                CREATE TABLE IF NOT EXISTS server_shadow (
                    entity_id TEXT PRIMARY KEY, payload TEXT NOT NULL, server_version INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS sync_metadata (
                    singleton INTEGER PRIMARY KEY CHECK(singleton=1), server_id TEXT NOT NULL,
                    generation TEXT NOT NULL, cursor INTEGER NOT NULL CHECK(cursor>=0));
                """, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            _initialized = true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads all local spaces, including data that has never reached a server.</summary>
    public async Task<IReadOnlyList<SpaceDto>> GetSpacesAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            return await ReadAllAsync<SpaceDto>(connection, null, "space", cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads the complete local page tree for a space, including trashed pages.</summary>
    public async Task<IReadOnlyList<PageDto>> GetPagesAsync(Guid spaceId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            var pages = await ReadAllAsync<PageDto>(connection, null, "page", cancellationToken).ConfigureAwait(false);
            return pages.Where(page => page.SpaceId == spaceId).ToArray();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Finds a local page without requiring a network round trip.</summary>
    public async Task<PageDto?> GetPageAsync(Guid pageId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            return await ReadEntityAsync<PageDto>(connection, null, pageId, "page", cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Atomically saves a space, recovery revision and the latest synchronization intent.</summary>
    public async Task<SpaceDto> SaveSpaceAsync(Guid id, string title, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(id);
        ValidateTitle(title);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            var current = await ReadEntityAsync<SpaceDto>(connection, transaction, id, "space", cancellationToken).ConfigureAwait(false);
            if (current is not null && current.Title == title.Trim()) { return current; }
            var saved = new SpaceDto(id, title.Trim(), checked((current?.Version ?? 0) + 1), false, DateTimeOffset.UtcNow);
            await SaveIntentAsync(connection, transaction, id, "space", saved.Version, saved, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return saved;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Saves a page and its pending intent together, preserving any operation already in flight.</summary>
    public async Task<PageDto> SavePageAsync(Guid id, Guid spaceId, Guid? parentId, string title, string contentJson,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(id);
        ValidateIdentity(spaceId);
        ValidateTitle(title);
        ValidateContent(contentJson);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            var current = await ReadEntityAsync<PageDto>(connection, transaction, id, "page", cancellationToken).ConfigureAwait(false);
            if (current is not null && current.SpaceId != spaceId)
            { throw new ArgumentException("Cross-space page moves are not supported.", nameof(spaceId)); }
            var saved = new PageDto(id, spaceId, parentId, title.Trim(), contentJson,
                checked((current?.Version ?? 0) + 1), current?.IsTrashed ?? false, DateTimeOffset.UtcNow);
            await ValidateHierarchyAsync(connection, transaction, saved, cancellationToken).ConfigureAwait(false);
            if (current is not null && current.Title == saved.Title && current.ParentId == parentId && current.ContentJson == contentJson)
            { return current; }
            await SaveIntentAsync(connection, transaction, id, "page", saved.Version, saved, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return saved;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Changes the local trash state without discarding document content or recovery history.</summary>
    public async Task<PageDto> SetPageTrashAsync(Guid id, bool trashed, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            var current = await ReadEntityAsync<PageDto>(connection, transaction, id, "page", cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The local page does not exist.");
            if (current.IsTrashed == trashed) { return current; }
            var saved = current with { IsTrashed = trashed, Version = checked(current.Version + 1), UpdatedAt = DateTimeOffset.UtcNow };
            await ValidateHierarchyAsync(connection, transaction, saved, cancellationToken).ConfigureAwait(false);
            await SaveIntentAsync(connection, transaction, id, "page", saved.Version, saved, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return saved;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Returns up to 100 immutable pending operations, targeting a bounded three-MiB wire batch.</summary>
    /// <remarks>Newer edits remain in the outbox while the frozen earlier intent is awaiting confirmation.</remarks>
    public async Task<IReadOnlyList<SyncOperationDto>> GetPendingOperationsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            await ExecuteAsync(connection, transaction, """
                INSERT INTO sent_operations(sequence,entity_id,operation_id,operation_json)
                SELECT o.sequence,o.entity_id,o.operation_id,o.operation_json FROM outbox o
                WHERE NOT EXISTS(SELECT 1 FROM sent_operations s WHERE s.entity_id=o.entity_id)
                ORDER BY o.sequence LIMIT MAX(0,100-(SELECT COUNT(*) FROM sent_operations s
                    WHERE NOT EXISTS(SELECT 1 FROM sync_issues i WHERE i.operation_id=s.operation_id)));
                """, cancellationToken).ConfigureAwait(false);
            var result = new List<SyncOperationDto>();
            long batchBytes = 0;
            await using (var command = Command(connection, transaction, "SELECT operation_json FROM sent_operations s                 WHERE NOT EXISTS(SELECT 1 FROM sync_issues i WHERE i.operation_id=s.operation_id)                 ORDER BY sequence LIMIT 100;"))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var operation = JsonSerializer.Deserialize<SyncOperationDto>(reader.GetString(0), JsonSerializerOptions.Web)!;
                    var wireBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(operation, JsonSerializerOptions.Web));
                    // A single valid document is never split; the HTTP host also bounds its request body.
                    if (result.Count > 0 && batchBytes + wireBytes > TargetBatchBytes) { break; }
                    result.Add(operation);
                    batchBytes += wireBytes;
                }
            }
            transaction.Commit();
            return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Applies durable acknowledgements and a coherent server snapshot without overwriting dirty local drafts.</summary>
    /// <remarks>Identity changes and stale snapshots fail before any mutation. Rejected intents remain pending for explicit resolution.</remarks>
    public async Task ApplySyncAsync(SyncSnapshotResponse snapshot, SyncPushResponse push, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(push);
        ValidateSnapshot(snapshot, push);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            await ValidateBindingAsync(connection, transaction, snapshot, cancellationToken).ConfigureAwait(false);
            var authoritative = snapshot.Spaces.ToDictionary(space => space.Id, space => (Kind: "space", Version: space.Version,
                Json: JsonSerializer.Serialize(space, JsonSerializerOptions.Web)));
            foreach (var page in snapshot.Pages)
            { authoritative.Add(page.Id, ("page", page.Version, JsonSerializer.Serialize(page, JsonSerializerOptions.Web))); }

            foreach (var receipt in push.Receipts)
            {
                var sent = await ReadOperationAsync(connection, transaction, "sent_operations", receipt.OperationId, cancellationToken).ConfigureAwait(false);
                if (sent is null) { continue; }
                if (receipt.Status is not ("accepted" or "duplicate"))
                {
                    var currentVersion = authoritative.TryGetValue(sent.EntityId, out var latest) ? latest.Version : receipt.CurrentVersion;
                    await ExecuteAsync(connection, transaction, """
                        INSERT INTO sync_issues VALUES($operation,$id,$status,$version,$message)
                        ON CONFLICT(operation_id) DO UPDATE SET status=excluded.status,current_version=excluded.current_version,message=excluded.message;
                        """, cancellationToken, ("$operation", Key(sent.OperationId)), ("$id", Key(sent.EntityId)),
                        ("$status", receipt.Status), ("$version", currentVersion), ("$message", receipt.Message)).ConfigureAwait(false);
                    continue;
                }
                if (!authoritative.TryGetValue(sent.EntityId, out var remote) || receipt.CurrentVersion <= 0 || remote.Version < receipt.CurrentVersion)
                { throw new InvalidOperationException("The acknowledgement is not covered by the supplied snapshot."); }
                await ExecuteAsync(connection, transaction, "DELETE FROM outbox WHERE operation_id=$operation;", cancellationToken,
                    ("$operation", Key(receipt.OperationId))).ConfigureAwait(false);
                if (remote.Version == receipt.CurrentVersion)
                {
                    await ExecuteAsync(connection, transaction, "UPDATE entities SET server_version=$version WHERE id=$id;", cancellationToken,
                        ("$version", remote.Version), ("$id", Key(sent.EntityId))).ConfigureAwait(false);
                    var newer = await ReadPendingForEntityAsync(connection, transaction, sent.EntityId, cancellationToken).ConfigureAwait(false);
                    if (newer is not null)
                    {
                        var rebased = newer with { ExpectedVersion = remote.Version, Kind = OperationKind(remote.Kind, newer.Payload.GetRawText(), remote.Json) };
                        await ExecuteAsync(connection, transaction, "UPDATE outbox SET operation_json=$json WHERE operation_id=$operation;", cancellationToken,
                            ("$json", JsonSerializer.Serialize(rebased, JsonSerializerOptions.Web)), ("$operation", Key(rebased.OperationId))).ConfigureAwait(false);
                    }
                }
                await ExecuteAsync(connection, transaction, "DELETE FROM sync_issues WHERE operation_id=$operation;", cancellationToken,
                    ("$operation", Key(receipt.OperationId))).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction, "DELETE FROM sent_operations WHERE operation_id=$operation;", cancellationToken,
                    ("$operation", Key(receipt.OperationId))).ConfigureAwait(false);
            }

            foreach (var (id, remote) in authoritative)
            {
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO server_shadow VALUES($id,$payload,$version)
                    ON CONFLICT(entity_id) DO UPDATE SET payload=excluded.payload,server_version=excluded.server_version;
                    """, cancellationToken, ("$id", Key(id)), ("$payload", remote.Json), ("$version", remote.Version)).ConfigureAwait(false);
                var dirty = await HasPendingAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
                if (!dirty)
                {
                    await ImportCleanAsync(connection, transaction, id, remote.Kind, remote.Json, remote.Version, cancellationToken).ConfigureAwait(false);
                }
            }
            await ExecuteAsync(connection, transaction, """
                INSERT INTO sync_metadata VALUES(1,$server,$generation,$cursor)
                ON CONFLICT(singleton) DO UPDATE SET cursor=excluded.cursor;
                """, cancellationToken, ("$server", Key(snapshot.ServerId)), ("$generation", Key(snapshot.Generation)), ("$cursor", snapshot.Cursor)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Lists blocked entities so the host can explain rejected uploads without misreporting success.</summary>
    public async Task<IReadOnlyList<LocalSyncIssue>> GetSyncIssuesAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = Command(connection, null, "SELECT operation_id,entity_id,status,current_version,message FROM sync_issues ORDER BY entity_id;");
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var issues = new List<LocalSyncIssue>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                issues.Add(new LocalSyncIssue(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                    reader.GetString(2), reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
            return issues;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Explicitly resolves a rejected entity using the latest downloaded server state.</summary>
    /// <remarks>Keeping the draft queues a new operation and may still conflict with a subsequent remote edit.
    /// Choosing the server retains previous local content in recovery revisions. The host must obtain the user's choice.</remarks>
    public async Task ResolveSyncIssueAsync(Guid entityId, bool keepLocalDraft, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            await using (var issueCommand = Command(connection, transaction, "SELECT EXISTS(SELECT 1 FROM sync_issues WHERE entity_id=$id);", ("$id", Key(entityId))))
            {
                if (Convert.ToInt64(await issueCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 0)
                { throw new InvalidOperationException("The entity has no synchronization issue to resolve."); }
            }
            string kind;
            string desired;
            await using (var command = Command(connection, transaction, "SELECT kind,payload FROM entities WHERE id=$id;", ("$id", Key(entityId))))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { throw new InvalidOperationException("The local entity is unavailable."); }
                kind = reader.GetString(0);
                desired = reader.GetString(1);
            }
            string? remote = null;
            long baseVersion = 0;
            await using (var command = Command(connection, transaction, "SELECT payload,server_version FROM server_shadow WHERE entity_id=$id;", ("$id", Key(entityId))))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { remote = reader.GetString(0); baseVersion = reader.GetInt64(1); }
            }
            if (!keepLocalDraft && remote is null) { throw new InvalidOperationException("The server has no entity version to restore; the local draft was preserved."); }
            await ExecuteAsync(connection, transaction, "DELETE FROM sync_issues WHERE entity_id=$id; DELETE FROM sent_operations WHERE entity_id=$id; DELETE FROM outbox WHERE entity_id=$id;",
                cancellationToken, ("$id", Key(entityId))).ConfigureAwait(false);
            if (keepLocalDraft)
            {
                var operation = new SyncOperationDto(Guid.NewGuid(), OperationKind(kind, desired, remote), entityId, baseVersion,
                    JsonSerializer.Deserialize<JsonElement>(desired, JsonSerializerOptions.Web));
                await ExecuteAsync(connection, transaction, "UPDATE entities SET server_version=$version WHERE id=$id;", cancellationToken,
                    ("$version", baseVersion), ("$id", Key(entityId))).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction, "INSERT INTO outbox(entity_id,operation_id,operation_json) VALUES($id,$operation,$json);", cancellationToken,
                    ("$id", Key(entityId)), ("$operation", Key(operation.OperationId)),
                    ("$json", JsonSerializer.Serialize(operation, JsonSerializerOptions.Web))).ConfigureAwait(false);
            }
            else
            {
                await ImportCleanAsync(connection, transaction, entityId, kind, remote!, baseVersion, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Releases the coordination primitive once no operation is using the store.</summary>
    public void Dispose() => _gate.Dispose();

    /// <summary>Writes the entity, history and latest desired intent within the caller's transaction.</summary>
    private static async Task SaveIntentAsync<T>(SqliteConnection connection, SqliteTransaction transaction, Guid id,
        string kind, long localVersion, T entity, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(entity, JsonSerializerOptions.Web);
        await using var shadowCommand = Command(connection, transaction, "SELECT payload,server_version FROM server_shadow WHERE entity_id=$id;", ("$id", Key(id)));
        string? shadow = null;
        long baseVersion = 0;
        await using (var reader = await shadowCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { shadow = reader.GetString(0); baseVersion = reader.GetInt64(1); }
        }
        await ExecuteAsync(connection, transaction, """
            INSERT INTO entities VALUES($id,$kind,$payload,$local,$server)
            ON CONFLICT(id) DO UPDATE SET payload=excluded.payload,local_version=excluded.local_version;
            """, cancellationToken, ("$id", Key(id)), ("$kind", kind), ("$payload", json), ("$local", localVersion), ("$server", baseVersion)).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "INSERT INTO local_revisions VALUES($id,$version,$payload,$created);", cancellationToken,
            ("$id", Key(id)), ("$version", localVersion), ("$payload", json), ("$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
        await using (var baseCommand = Command(connection, transaction, "SELECT server_version FROM entities WHERE id=$id;", ("$id", Key(id))))
        {
            var entityBase = Convert.ToInt64(await baseCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (entityBase != baseVersion) { shadow = null; }
            baseVersion = entityBase;
        }
        var operation = new SyncOperationDto(Guid.NewGuid(), OperationKind(kind, json, shadow), id, baseVersion,
            JsonSerializer.SerializeToElement(entity, JsonSerializerOptions.Web));
        await ExecuteAsync(connection, transaction, """
            INSERT INTO outbox(entity_id,operation_id,operation_json) VALUES($id,$operation,$json)
            ON CONFLICT(entity_id) DO UPDATE SET operation_id=excluded.operation_id,operation_json=excluded.operation_json;
            """, cancellationToken, ("$id", Key(id)), ("$operation", Key(operation.OperationId)),
            ("$json", JsonSerializer.Serialize(operation, JsonSerializerOptions.Web))).ConfigureAwait(false);
    }

    /// <summary>Selects an explicit lifecycle operation only when a server entity already exists.</summary>
    private static string OperationKind(string kind, string desiredJson, string? remoteJson)
    {
        if (kind == "space") { return "upsertSpace"; }
        var desired = JsonSerializer.Deserialize<PageDto>(desiredJson, JsonSerializerOptions.Web)!;
        var remote = remoteJson is null ? null : JsonSerializer.Deserialize<PageDto>(remoteJson, JsonSerializerOptions.Web);
        return remote is not null && remote.IsTrashed != desired.IsTrashed
            ? desired.IsTrashed ? "trashPage" : "restorePage" : "upsertPage";
    }

    /// <summary>Retains local revision monotonicity when importing authoritative clean content.</summary>
    private static async Task ImportCleanAsync(SqliteConnection connection, SqliteTransaction transaction, Guid id,
        string kind, string json, long serverVersion, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT local_version,payload,kind FROM entities WHERE id=$id;", ("$id", Key(id)));
        long localVersion = serverVersion;
        string? previous = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetString(2) != kind) { throw new InvalidOperationException("The server reused an entity identity with a different type."); }
                previous = reader.GetString(1);
                localVersion = Math.Max(serverVersion, reader.GetInt64(0));
                if (!SameContent(kind, previous, json)) { localVersion = checked(localVersion + 1); }
            }
        }
        var localJson = kind == "space"
            ? JsonSerializer.Serialize(JsonSerializer.Deserialize<SpaceDto>(json, JsonSerializerOptions.Web)! with { Version = localVersion }, JsonSerializerOptions.Web)
            : JsonSerializer.Serialize(JsonSerializer.Deserialize<PageDto>(json, JsonSerializerOptions.Web)! with { Version = localVersion }, JsonSerializerOptions.Web);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO entities VALUES($id,$kind,$payload,$local,$server)
            ON CONFLICT(id) DO UPDATE SET payload=excluded.payload,local_version=excluded.local_version,server_version=excluded.server_version;
            """, cancellationToken, ("$id", Key(id)), ("$kind", kind), ("$payload", localJson), ("$local", localVersion), ("$server", serverVersion)).ConfigureAwait(false);
        if (previous != localJson)
        {
            await ExecuteAsync(connection, transaction, "INSERT OR IGNORE INTO local_revisions VALUES($id,$version,$payload,$created);", cancellationToken,
                ("$id", Key(id)), ("$version", localVersion), ("$payload", localJson),
                ("$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
        }
    }

    /// <summary>Compares authoritative content independently of the desktop's local revision counter.</summary>
    private static bool SameContent(string kind, string left, string right) => kind == "space"
        ? JsonSerializer.Deserialize<SpaceDto>(left, JsonSerializerOptions.Web)! with { Version = 0 } ==
          JsonSerializer.Deserialize<SpaceDto>(right, JsonSerializerOptions.Web)! with { Version = 0 }
        : JsonSerializer.Deserialize<PageDto>(left, JsonSerializerOptions.Web)! with { Version = 0 } ==
          JsonSerializer.Deserialize<PageDto>(right, JsonSerializerOptions.Web)! with { Version = 0 };

    /// <summary>Checks local ancestry before storing a document, including same-space and cycle invariants.</summary>
    private static async Task ValidateHierarchyAsync(SqliteConnection connection, SqliteTransaction transaction,
        PageDto page, CancellationToken cancellationToken)
    {
        var space = await ReadEntityAsync<SpaceDto>(connection, transaction, page.SpaceId, "space", cancellationToken).ConfigureAwait(false);
        if (space is null || space.IsTrashed) { throw new ArgumentException("The owning space is unavailable.", nameof(page)); }
        var visited = new HashSet<Guid> { page.Id };
        var parentId = page.ParentId;
        while (parentId is { } id)
        {
            if (id == Guid.Empty || !visited.Add(id)) { throw new ArgumentException("The page hierarchy contains a cycle.", nameof(page)); }
            var parent = await ReadEntityAsync<PageDto>(connection, transaction, id, "page", cancellationToken).ConfigureAwait(false);
            if (parent is null || parent.SpaceId != page.SpaceId || parent.IsTrashed && !page.IsTrashed)
            { throw new ArgumentException("The parent page is unavailable or belongs to another space.", nameof(page)); }
            parentId = parent.ParentId;
        }
    }

    /// <summary>Rejects malformed or internally inconsistent server state before starting a transaction.</summary>
    private static void ValidateSnapshot(SyncSnapshotResponse snapshot, SyncPushResponse push)
    {
        ArgumentNullException.ThrowIfNull(snapshot.Spaces);
        ArgumentNullException.ThrowIfNull(snapshot.Pages);
        ArgumentNullException.ThrowIfNull(push.Receipts);
        if (snapshot.ServerId == Guid.Empty || snapshot.Generation == Guid.Empty || push.Generation != snapshot.Generation ||
            push.Cursor < 0 || snapshot.Cursor < push.Cursor || push.Receipts.Select(receipt => receipt.OperationId).Distinct().Count() != push.Receipts.Count)
        { throw new InvalidOperationException("The synchronization generation, cursor or receipts are inconsistent."); }
        var ids = new HashSet<Guid>();
        foreach (var space in snapshot.Spaces)
        {
            if (space.Id == Guid.Empty || space.Version <= 0 || !ids.Add(space.Id)) { throw new InvalidOperationException("The server space snapshot is invalid."); }
            ValidateTitle(space.Title);
        }
        var spaces = snapshot.Spaces.ToDictionary(space => space.Id);
        var pages = snapshot.Pages.ToDictionary(page => page.Id);
        foreach (var page in snapshot.Pages)
        {
            if (page.Id == Guid.Empty || page.Version <= 0 || !ids.Add(page.Id) || !spaces.ContainsKey(page.SpaceId))
            { throw new InvalidOperationException("The server page snapshot is invalid."); }
            ValidateTitle(page.Title);
            ValidateContent(page.ContentJson);
            var visited = new HashSet<Guid> { page.Id };
            var parentId = page.ParentId;
            while (parentId is { } id)
            {
                if (!visited.Add(id) || !pages.TryGetValue(id, out var parent) || parent.SpaceId != page.SpaceId)
                { throw new InvalidOperationException("The server page hierarchy is invalid."); }
                parentId = parent.ParentId;
            }
        }
    }

    /// <summary>Prevents accidentally merging a replacement or different server into existing offline work.</summary>
    private static async Task ValidateBindingAsync(SqliteConnection connection, SqliteTransaction transaction,
        SyncSnapshotResponse snapshot, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT server_id,generation,cursor FROM sync_metadata WHERE singleton=1;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) &&
            (reader.GetString(0) != Key(snapshot.ServerId) || reader.GetString(1) != Key(snapshot.Generation) || reader.GetInt64(2) > snapshot.Cursor))
        { throw new InvalidOperationException("The server identity, generation or cursor changed; local drafts were preserved."); }
    }

    /// <summary>Finds a frozen operation by the exact identity acknowledged by the server.</summary>
    private static async Task<SyncOperationDto?> ReadOperationAsync(SqliteConnection connection, SqliteTransaction transaction,
        string table, Guid operation, CancellationToken cancellationToken)
    {
        if (table != "sent_operations") { throw new ArgumentException("Unsupported operation table.", nameof(table)); }
        await using var command = Command(connection, transaction, "SELECT operation_json FROM sent_operations WHERE operation_id=$operation;", ("$operation", Key(operation)));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is string json ? JsonSerializer.Deserialize<SyncOperationDto>(json, JsonSerializerOptions.Web) : null;
    }

    /// <summary>Reads the latest editable intent for an entity independently of its frozen earlier operation.</summary>
    private static async Task<SyncOperationDto?> ReadPendingForEntityAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid id, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT operation_json FROM outbox WHERE entity_id=$id;", ("$id", Key(id)));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is string json ? JsonSerializer.Deserialize<SyncOperationDto>(json, JsonSerializerOptions.Web) : null;
    }

    /// <summary>Checks both pending and in-flight queues before importing remote content.</summary>
    private static async Task<bool> HasPendingAsync(SqliteConnection connection, SqliteTransaction transaction, Guid id, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction,
            "SELECT EXISTS(SELECT 1 FROM outbox WHERE entity_id=$id) OR EXISTS(SELECT 1 FROM sent_operations WHERE entity_id=$id);", ("$id", Key(id)));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
    }

    /// <summary>Finds an entity and rejects a caller attempting to reuse another entity type's identity.</summary>
    private static async Task<T?> ReadEntityAsync<T>(SqliteConnection connection, SqliteTransaction? transaction, Guid id,
        string kind, CancellationToken cancellationToken) where T : class
    {
        await using var command = Command(connection, transaction, "SELECT kind,payload FROM entities WHERE id=$id;", ("$id", Key(id)));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { return null; }
        if (reader.GetString(0) != kind) { throw new ArgumentException("The entity identity is already used by another type.", nameof(id)); }
        return JsonSerializer.Deserialize<T>(reader.GetString(1), JsonSerializerOptions.Web);
    }

    /// <summary>Reads all locally persisted entities of one contract type.</summary>
    private static async Task<IReadOnlyList<T>> ReadAllAsync<T>(SqliteConnection connection, SqliteTransaction? transaction,
        string kind, CancellationToken cancellationToken)
    {
        var result = new List<T>();
        await using var command = Command(connection, transaction, "SELECT payload FROM entities WHERE kind=$kind ORDER BY id;", ("$kind", kind));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        { result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), JsonSerializerOptions.Web)!); }
        return result;
    }

    /// <summary>Opens a connection with full commit durability and bounded locking.</summary>
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

    /// <summary>Constructs a command with bound values rather than SQL interpolation.</summary>
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) { command.Parameters.AddWithValue(name, value ?? DBNull.Value); }
        return command;
    }

    /// <summary>Executes one or more parameterized statements inside the supplied transaction.</summary>
    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Rejects empty identifiers before any write.</summary>
    private static void ValidateIdentity(Guid id)
    {
        if (id == Guid.Empty) { throw new ArgumentException("An entity identity is required.", nameof(id)); }
    }

    /// <summary>Bounds a visible title by its UTF-8 storage length.</summary>
    private static void ValidateTitle(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (Encoding.UTF8.GetByteCount(title) > 512) { throw new ArgumentException("The title exceeds 512 UTF-8 bytes.", nameof(title)); }
    }

    /// <summary>Requires bounded JSON document content with an object root.</summary>
    private static void ValidateContent(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (Encoding.UTF8.GetByteCount(content) > MaximumContentBytes) { throw new ArgumentException("Document content exceeds 2 MiB.", nameof(content)); }
        using var document = JsonDocument.Parse(content);
        if (document.RootElement.ValueKind != JsonValueKind.Object) { throw new ArgumentException("Document content must be a JSON object.", nameof(content)); }
    }

    /// <summary>Serializes an identity independently of the current culture.</summary>
    private static string Key(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);
}
