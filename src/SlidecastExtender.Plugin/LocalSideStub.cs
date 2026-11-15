using System;
using SlidecastExtender.Core;

namespace SlidecastExtender.Plugin;

/// <summary>
/// Contract for the <b>local side</b> of slidecast — <b>M1 (deferred)</b>. The wire side (M0)
/// keeps the <b>server</b> believing the character is stationary so its cast timer completes.
/// The <b>local</b> side must additionally keep the <b>client's own cast bar</b> up while the
/// character moves. The interrupt is <b>local</b>: the client cancels its own cast bar the instant
/// it moves, <i>before</i> any <c>CastInterrupt</c> arrives, so this lives in native game logic
/// and needs reverse engineering (see <c>docs/re/local-side.md</c>).
/// </summary>
public interface ILocalCastGate
{
    /// <summary>True while a local gate is active (movement is being kept from canceling the
    /// local cast bar).</summary>
    bool IsGating { get; }

    /// <summary>True if a local gate is available. Always <c>false</c> in M0.</summary>
    bool Available { get; }

    /// <summary>Begin local gating for a cast window (called at cast start).</summary>
    void Begin(uint actionId, long tsMs);

    /// <summary>End local gating (cast resolve / interrupt / timeout).</summary>
    void End();
}

/// <summary>
/// M0 placeholder: <b>wire-side only</b>, so local gating is a no-op. Replaced by a Realscript
/// or native gate in M1. Kept as a single injectable dependency so the M0 code is already
/// shaped for M1.
/// </summary>
internal sealed class LocalSideStub : ILocalCastGate
{
    private readonly Action<string, string>? _log;

    public LocalSideStub(Action<string, string>? log = null)
    {
        _log = log;
        _log?.Invoke("local", "local-side gate: M0 no-op (M1 placeholder; see docs/re/local-side.md)");
    }

    public bool IsGating => false;
    public bool Available => false;

    public void Begin(uint actionId, long tsMs)
    {
        // M0: no local gating. In M1 this is where the local cast bar is protected from the
        // movement-triggered cancel.
        _log?.Invoke("local-gate", $"begin id={actionId} (no-op)");
    }

    public void End()
    {
        // M0: no local gating to end.
    }
}
