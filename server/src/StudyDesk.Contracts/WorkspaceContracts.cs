using System.Text.Json;

namespace StudyDesk.Contracts;

/// <summary>Describes a space at its current local or authoritative server revision.</summary>
/// <param name="Id">Stable space identifier, preserved across synchronization.</param>
/// <param name="Title">User-visible title.</param>
/// <param name="Version">Revision in the context that supplied the record.</param>
/// <param name="IsTrashed">Whether the space is marked as removed.</param>
/// <param name="UpdatedAt">UTC revision timestamp.</param>
public sealed record SpaceDto(Guid Id, string Title, long Version, bool IsTrashed, DateTimeOffset UpdatedAt);

/// <summary>Describes a page without embedding device credentials or filesystem paths.</summary>
/// <param name="Id">Stable page identifier.</param>
/// <param name="SpaceId">Owning space identifier.</param>
/// <param name="ParentId">Optional parent page in the same space.</param>
/// <param name="Title">User-visible page title.</param>
/// <param name="ContentJson">Versioned editor document serialized as JSON.</param>
/// <param name="Version">Revision in the context that supplied the record.</param>
/// <param name="IsTrashed">Whether the page is in the trash.</param>
/// <param name="UpdatedAt">UTC revision timestamp.</param>
public sealed record PageDto(Guid Id, Guid SpaceId, Guid? ParentId, string Title, string ContentJson,
    long Version, bool IsTrashed, DateTimeOffset UpdatedAt);

/// <summary>Represents a retryable change whose identity must not be reused with different content.</summary>
/// <param name="OperationId">Stable operation identity retained until durably acknowledged.</param>
/// <param name="Kind">One of the explicit operation kinds in the protocol.</param>
/// <param name="EntityId">The entity being changed.</param>
/// <param name="ExpectedVersion">Last authoritative server revision known when the change was made.</param>
/// <param name="Payload">The complete proposed entity state.</param>
public sealed record SyncOperationDto(Guid OperationId, string Kind, Guid EntityId,
    long ExpectedVersion, JsonElement Payload);

/// <summary>Provides public server identity and compatibility metadata only.</summary>
/// <param name="ServerId">Stable server installation identity.</param>
/// <param name="Generation">History generation, changed after a rollback restore.</param>
/// <param name="ProtocolVersion">Supported protocol major version.</param>
public sealed record ServerInfoResponse(Guid ServerId, Guid Generation, int ProtocolVersion);

/// <summary>Returns one coherent authorized snapshot and its durable cursor.</summary>
/// <param name="ServerId">The installation that supplied the snapshot.</param>
/// <param name="Generation">History generation covered by the snapshot.</param>
/// <param name="Cursor">Monotonic committed server change sequence.</param>
/// <param name="Spaces">Authorized spaces.</param>
/// <param name="Pages">Authorized pages, including retained trash markers.</param>
public sealed record SyncSnapshotResponse(Guid ServerId, Guid Generation, long Cursor,
    IReadOnlyList<SpaceDto> Spaces, IReadOnlyList<PageDto> Pages);

/// <summary>Submits queued operations against a previously verified server generation.</summary>
/// <param name="Generation">Expected server history generation.</param>
/// <param name="Operations">Ordered retryable operations.</param>
public sealed record SyncPushRequest(Guid Generation, IReadOnlyList<SyncOperationDto> Operations);

/// <summary>Records the durable outcome of an operation without discarding the local draft.</summary>
/// <param name="OperationId">Submitted operation identity.</param>
/// <param name="Status">Accepted, duplicate, conflict, invalid or forbidden outcome.</param>
/// <param name="CurrentVersion">Authoritative version after processing.</param>
/// <param name="Message">Safe error or recovery guidance without credentials.</param>
/// <param name="CurrentEntity">Authorized current entity state for reconciliation.</param>
public sealed record OperationReceiptDto(Guid OperationId, string Status, long CurrentVersion,
    string? Message, JsonElement? CurrentEntity);

/// <summary>Returns only operations whose outcomes have been durably recorded.</summary>
/// <param name="Generation">Current server history generation.</param>
/// <param name="Cursor">Last committed change sequence.</param>
/// <param name="Receipts">Per-operation outcomes.</param>
public sealed record SyncPushResponse(Guid Generation, long Cursor,
    IReadOnlyList<OperationReceiptDto> Receipts);
