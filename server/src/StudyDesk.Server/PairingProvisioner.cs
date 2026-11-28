using System.Security.Cryptography;
using System.Text.Json;
using StudyDesk.Contracts;
using StudyDesk.Server.Core.Configuration;
using StudyDesk.Server.Core.Security;
using StudyDesk.Server.Core.Storage;

namespace StudyDesk.Server;

/// <summary>Issues owner associations only from a trusted local operator command.</summary>
public static class PairingProvisioner
{
    /// <summary>Writes one private bundle without overwriting an existing file or logging its token.</summary>
    /// <param name="settings">Validated local configuration.</param>
    /// <param name="name">Non-secret device label.</param>
    /// <param name="outputPath">Absolute one-time export path with the pairing suffix.</param>
    /// <param name="cancellationToken">Cancels provisioning before completion.</param>
    /// <returns>Non-secret metadata for the newly associated device.</returns>
    public static async Task<DeviceMetadata> CreateOwnerAsync(ServerSettings settings, string name,
        string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!Path.IsPathFullyQualified(outputPath)
            || !outputPath.EndsWith(".studydesk-pairing.json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Select an absolute output path ending in .studydesk-pairing.json.", nameof(outputPath));
        }

        PrivateFile.ValidateSqliteDatabaseFiles(settings.DataDirectory, "devices.sqlite3");
        PrivateFile.ValidateSqliteDatabaseFiles(settings.DataDirectory, "workspace.sqlite3");
        PrivateFile.PrepareSqliteDatabase(settings.DataDirectory, "workspace.sqlite3");
        SqliteDeviceStore devices = new(settings.DataDirectory);
        await devices.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using SqliteServerWorkspaceStore workspace = new(settings.DataDirectory);
        SyncSnapshotChunkResponse snapshot = await workspace.GetSnapshotChunkAsync(0, null, cancellationToken).ConfigureAwait(false);
        CreatedDevice created = await devices.CreateAsync(name, DeviceRole.Owner, cancellationToken).ConfigureAwait(false);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            serverUrl = settings.PublicOrigin.GetLeftPart(UriPartial.Authority),
            serverId = snapshot.ServerId,
            deviceId = created.Device.Id,
            token = created.Token,
        });
        try
        {
            await PrivateFile.WriteNewAsync(outputPath, bytes, cancellationToken).ConfigureAwait(false);
            return created.Device;
        }
        catch
        {
            // A failed delivery must not leave an active, undistributed device association.
            await devices.RevokeAsync(created.Device.Id, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
