namespace SlidecastExtender.Core;

/// <summary>Engine operating mode. <c>Off</c> passes everything through; <c>Observe</c> logs the
/// wire without changing it (the first in-game run); <c>Live</c> is full suppression + flush.</summary>
public enum ExtenderMode
{
    Off,
    Observe,
    Live,
}

/// <summary>How the client's <c>Move</c> packets are treated while a cast window is open.</summary>
public enum SuppressionPolicy
{
    /// <summary><c>Move</c> packets are simply not sent; the server keeps the frozen position.</summary>
    Drop,
    /// <summary><c>Move</c> packets still go out, with x/y/z overwritten to the frozen (cast-start)
    /// position, patched in place on the client's buffer just before the original send.</summary>
    ReplayFrozen,
}

/// <summary>How the pending real-position flush is delivered on resolve.</summary>
public enum FlushMode
{
    /// <summary>One <c>Move</c> packet with the final real position on resolve.</summary>
    Immediate,
    /// <summary>N interpolated steps, per-step distance capped, one step per <c>StagedStepIntervalMs</c>.</summary>
    Staged,
}

/// <summary>Internal lifecycle state of the cast window / flush.</summary>
public enum TrackerState
{
    Idle,
    Casting,
    Flushing,
}
