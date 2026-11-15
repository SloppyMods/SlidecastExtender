namespace SlidecastExtender.Core;

/// <summary>Snapshot of engine state for <c>/slidecast status</c> and the IPC.</summary>
public readonly record struct WireStatus(
    ExtenderMode Mode,
    bool WindowActive,
    uint? CastId,
    long? RemainingMs,
    long SuppressedMoves,
    long FlushedPackets)
{
    /// <summary>True while a cast window is open and unresolved (State == Casting). This is the
    /// IPC signal rotation plugins hold on: a new action now would cancel the server-side cast.
    /// (A staged flush in flight does NOT hold — the cast has already resolved.)</summary>
    public bool CastWindowOpen => WindowActive && CastId is not null;
}
