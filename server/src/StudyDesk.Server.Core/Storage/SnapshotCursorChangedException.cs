namespace StudyDesk.Server.Core.Storage;

/// <summary>Signals that a snapshot continuation can no longer describe the initial committed state.</summary>
/// <remarks>The transport may request a fresh transfer without treating an internal storage failure as a retryable conflict.</remarks>
public sealed class SnapshotCursorChangedException : InvalidOperationException
{
    /// <summary>Creates the safe restart guidance used for a changed snapshot cursor.</summary>
    public SnapshotCursorChangedException()
        : base("The snapshot cursor changed; restart the transfer without applying partial data.")
    {
    }

    /// <summary>Creates a cursor mismatch exception with caller-provided recovery guidance.</summary>
    public SnapshotCursorChangedException(string? message) : base(message)
    {
    }

    /// <summary>Preserves an underlying exception while identifying a snapshot continuation mismatch.</summary>
    public SnapshotCursorChangedException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}
