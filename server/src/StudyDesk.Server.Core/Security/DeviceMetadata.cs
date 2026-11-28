using System.Text.Json.Serialization;

namespace StudyDesk.Server.Core.Security;

/// <summary>Identifies the maximum server capability granted to a paired device.</summary>
public enum DeviceRole
{
    /// <summary>May administer the owner's data server.</summary>
    Owner,
    /// <summary>Reserved for scoped, read-only sharing; unsupported by owner-only APIs.</summary>
    Reader,
    /// <summary>Reserved for scoped editing; unsupported by owner-only APIs.</summary>
    Editor,
}

/// <summary>Provides device administration metadata without any credential or verifier.</summary>
/// <param name="Id">Opaque association identifier.</param>
/// <param name="Name">Operator-selected display label.</param>
/// <param name="Role">Assigned capability.</param>
/// <param name="CreatedAt">Association creation time.</param>
/// <param name="RevokedAt">Revocation time, or null for an active association.</param>
public sealed record DeviceMetadata(Guid Id, string Name, DeviceRole Role,
    DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);

/// <summary>Temporarily carries an issued credential for one private pairing-file export.</summary>
public sealed class CreatedDevice
{
    /// <summary>Creates an issuance result; only the device store may issue it.</summary>
    internal CreatedDevice(DeviceMetadata device, string token)
    {
        Device = device;
        Token = token;
    }

    /// <summary>Gets safe device administration metadata.</summary>
    public DeviceMetadata Device { get; }

    /// <summary>Gets the temporary cleartext credential; never serialize or log this value.</summary>
    [JsonIgnore]
    public string Token { get; }

    /// <summary>Returns a safe description that deliberately omits the credential.</summary>
    public override string ToString() => $"Created device {Device.Id:D} (credential redacted)";
}
