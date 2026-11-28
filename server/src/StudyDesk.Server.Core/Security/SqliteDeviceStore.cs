using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace StudyDesk.Server.Core.Security;

/// <summary>Persists independent device verifiers and revocation metadata in a private SQLite store.</summary>
public sealed class SqliteDeviceStore
{
    private readonly string connectionString;
    private readonly string dataDirectory;

    /// <summary>Creates a store below the configured private application directory.</summary>
    /// <param name="dataDirectory">Absolute operator-configured data directory.</param>
    public SqliteDeviceStore(string dataDirectory)
    {
        PrivateFile.EnsureDirectory(dataDirectory);
        this.dataDirectory = dataDirectory;
        PrivateFile.ValidateSqliteDatabaseFiles(dataDirectory, "devices.sqlite3");
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDirectory, "devices.sqlite3"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
    }

    /// <summary>Creates the verifier schema without storing any raw credential.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS devices(
                id TEXT PRIMARY KEY, name TEXT NOT NULL, role INTEGER NOT NULL,
                verifier BLOB NOT NULL UNIQUE CHECK(length(verifier)=32),
                created_at TEXT NOT NULL, revoked_at TEXT NULL);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Issues one independent credential to be delivered only through a private local file.</summary>
    /// <param name="name">A non-secret operator-selected label.</param>
    /// <param name="role">The maximum capability granted to the association.</param>
    /// <param name="cancellationToken">Cancels database work.</param>
    /// <returns>Metadata and the one-time cleartext export value.</returns>
    public async Task<CreatedDevice> CreateAsync(string name, DeviceRole role,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 80 || name.Any(char.IsControl) || role != DeviceRole.Owner)
        {
            throw new ArgumentException("Use a device label of at most 80 printable characters and the supported owner role.", nameof(name));
        }

        string token = DeviceToken.Generate();
        byte[] verifier = DeviceToken.Hash(token);
        DeviceMetadata metadata = new(Guid.NewGuid(), name.Trim(), role, DateTimeOffset.UtcNow, null);
        try
        {
            using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "INSERT INTO devices(id,name,role,verifier,created_at) VALUES($id,$name,$role,$verifier,$created);";
            command.Parameters.AddWithValue("$id", metadata.Id.ToString("D"));
            command.Parameters.AddWithValue("$name", metadata.Name);
            command.Parameters.AddWithValue("$role", (int)role);
            command.Parameters.AddWithValue("$verifier", verifier);
            command.Parameters.AddWithValue("$created", metadata.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new CreatedDevice(metadata, token);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(verifier);
        }
    }

    /// <summary>Authenticates a raw credential and rejects malformed, unknown or revoked associations.</summary>
    /// <param name="token">Raw client credential from the authorization header.</param>
    /// <param name="cancellationToken">Cancels database work.</param>
    /// <returns>Safe metadata, or null when access is denied.</returns>
    public async Task<DeviceMetadata?> AuthenticateAsync(string? token,
        CancellationToken cancellationToken = default)
    {
        byte[] candidate;
        try
        {
            candidate = DeviceToken.Hash(token!);
        }
        catch (ArgumentException)
        {
            return null;
        }

        try
        {
            using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT id,name,role,created_at,revoked_at,verifier FROM devices WHERE verifier=$verifier LIMIT 1;";
            command.Parameters.AddWithValue("$verifier", candidate);
            using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || !reader.IsDBNull(4)
                || !DeviceToken.Verify(token, (byte[])reader[5]))
            {
                return null;
            }

            return ReadMetadata(reader);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidate);
        }
    }

    /// <summary>Lists association metadata without exposing stored verifiers.</summary>
    public async Task<IReadOnlyList<DeviceMetadata>> ListAsync(CancellationToken cancellationToken = default)
    {
        List<DeviceMetadata> devices = [];
        using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,role,created_at,revoked_at FROM devices ORDER BY created_at,id;";
        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            devices.Add(ReadMetadata(reader));
        }

        return devices;
    }

    /// <summary>Records a revocation idempotently; callers serialize it with authorized operations.</summary>
    /// <param name="deviceId">Association to revoke.</param>
    /// <param name="cancellationToken">Cancels database work.</param>
    /// <returns>Whether the association exists, including an already revoked association.</returns>
    public async Task<bool> RevokeAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE devices SET revoked_at=COALESCE(revoked_at,$revoked) WHERE id=$id;";
        command.Parameters.AddWithValue("$revoked", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", deviceId.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <summary>Opens an independent connection with explicit durability and foreign-key settings.</summary>
    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        PrivateFile.PrepareSqliteDatabase(dataDirectory, "devices.sqlite3");
        SqliteConnection connection = new(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Decodes only safe metadata and refuses undefined persisted capabilities.</summary>
    private static DeviceMetadata ReadMetadata(SqliteDataReader reader)
    {
        DeviceRole role = (DeviceRole)reader.GetInt32(2);
        if (!Enum.IsDefined(role))
        {
            throw new InvalidDataException("The device association contains an unsupported capability.");
        }

        return new DeviceMetadata(Guid.Parse(reader.GetString(0)), reader.GetString(1), role,
            DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
            reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture));
    }
}
