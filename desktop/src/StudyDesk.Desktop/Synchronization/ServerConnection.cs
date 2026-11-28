namespace StudyDesk.Desktop.Synchronization;

/// <summary>Stores non-secret server identity separately from the protected device credential.</summary>
/// <param name="ServerUri">The deliberately configured server origin.</param>
/// <param name="ServerId">Verified installation identity.</param>
/// <param name="Generation">History generation recorded during pairing.</param>
public sealed record ServerConnection(Uri ServerUri, Guid ServerId, Guid Generation);
