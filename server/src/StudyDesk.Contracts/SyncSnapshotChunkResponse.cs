namespace StudyDesk.Contracts;

/// <summary>Carries a bounded part of one coherent full workspace snapshot.</summary>
/// <param name="ServerId">The persistent identity of the selected data server.</param>
/// <param name="Generation">The history generation that prevents restore-related replay.</param>
/// <param name="Cursor">The committed workspace cursor shared by every part of this snapshot.</param>
/// <param name="NextOffset">The next global entity offset, or null after the last part.</param>
/// <param name="Spaces">The ordered spaces carried by this part.</param>
/// <param name="Pages">The ordered pages carried by this part.</param>
/// <remarks>
/// Clients must assemble every part with identical identity, generation and cursor before applying
/// the snapshot locally. An interrupted or changed snapshot does not acknowledge local operations.
/// </remarks>
public sealed record SyncSnapshotChunkResponse(
    Guid ServerId,
    Guid Generation,
    long Cursor,
    long? NextOffset,
    IReadOnlyList<SpaceDto> Spaces,
    IReadOnlyList<PageDto> Pages);
