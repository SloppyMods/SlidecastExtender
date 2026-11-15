namespace SlidecastExtender.Core;

/// <summary>
/// Engine configuration. All values are sane defaults for the wire-side prototype; every knob has
/// a command in <c>/slidecast</c> (mode/suppress/flush/grace) or a unit-test override.
/// </summary>
public sealed class SlidecastConfig
{
    /// <summary>
    /// <c>Off</c> — hooks pass everything through (no work).
    /// <c>Observe</c> — hooks log the wire but change nothing; used to confirm ids/layouts.
    /// <c>Live</c> — full suppression + flush logic.
    /// </summary>
    public ExtenderMode Mode { get; set; } = ExtenderMode.Observe;

    /// <summary>
    /// <c>Drop</c> (default) — during the cast window the client's Move packets are simply not sent;
    /// the server keeps believing you are at the cast-start position. <c>ReplayFrozen</c> — the
    /// packet still goes out with x/y/z overwritten to the frozen (cast-start) position.
    /// </summary>
    public SuppressionPolicy Suppress { get; set; } = SuppressionPolicy.Drop;

    /// <summary>
    /// <c>Immediate</c> (default) — one Move packet with the final real position on resolve;
    /// the server snap / speed-clamp risk is the documented one. <c>Staged</c> — interpolate over
    /// <c>StagedSteps</c> with per-step distance capped at <c>StagedMaxStepUnits</c>.
    /// </summary>
    public FlushMode Flush { get; set; } = FlushMode.Immediate;

    /// <summary>Number of interpolated steps for <c>FlushMode.Staged</c> (1 ≡ immediate). Clamped
    /// up by the max-step constraint (and to 64) when the walk distance requires it.</summary>
    public int StagedSteps { get; set; } = 6;

    /// <summary>Per-step distance cap for staged flushes, in world units. Start small; the server's
    /// speed clamp is unknown (VERIFY).</summary>
    public float StagedMaxStepUnits { get; set; } = 16f;

    /// <summary>Minimum interval between staged flush steps.</summary>
    public long StagedStepIntervalMs { get; set; } = 100;

    /// <summary>If the staged flush is not complete by this many ms after start, emit the
    /// remaining steps in one burst (fail-open) instead of stalling.</summary>
    public long StagedFlushTimeoutMs { get; set; } = 2000;

    /// <summary>Grace beyond the server cast duration before the engine fail-opens the window
    /// (flushes whatever real position it holds, returns to normal).</summary>
    public long GracePeriodMs { get; set; } = 2000;

    /// <summary>0 = off; log every Nth suppressed Move.</summary>
    public long LogEveryNthSuppressedMove { get; set; } = 1;

    /// <summary>Observe mode: 0 = off; log every Nth packet (per direction).</summary>
    public long ObserveLogEveryNth { get; set; } = 1;

    /// <summary>Observe mode: also log decoded details (positions, action ids, cast times,
    /// ActorControl categories) — the confirm-or-discard data for the VERIFY layouts. Turn off
    /// once the layouts are confirmed; the raw id/size lines are enough afterwards.</summary>
    public bool ObserveLogDetails { get; set; } = true;

    /// <summary>The ActorControl(Self) category that means "cast interrupted" (server→client).
    /// Observed = <b>15</b> (2026-09-20 capture: fired exactly when a walk-away killed the cast,
    /// twice). -1 disables interrupt detection (timeout backstop then covers it).</summary>
    public int InterruptActorControlCategory { get; set; } = 15;
}
