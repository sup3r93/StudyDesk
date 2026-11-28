namespace StudyDesk.Desktop.Core.Storage;

/// <summary>Describes a durable rejected upload that requires an explicit user resolution.</summary>
public sealed record LocalSyncIssue(Guid OperationId, Guid EntityId, string Status, long CurrentVersion, string? Message);
