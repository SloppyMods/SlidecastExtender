using System;

namespace SlidecastExtender.Core;

/// <summary>
/// Owns the "cast is active" window: the cast-start position snapshot, the running log of the
/// character's real position (from suppressed Move packets), and the resolve / interrupt /
/// timeout flush machinery.
///
/// <para>Window close: at <c>CastEndTs</c> — the cast's completion time, known from the Action
/// sheet and refined by the server's <c>ActorCast</c>. (Effect packets proved unreliable as a
/// completion signal live: the caster's own completion didn't arrive as <c>Effect</c>, and in
/// combat the Effect flood closed windows instantly.) The interrupt category closes early;
/// the deadline + grace timeout is the fail-open backstop (ADR-002).</para>
///
/// <para>Thread model: all methods are invoked from the client's network thread (the hooks). The
/// backstop timer (<see cref="WireExtender.Tick"/>) may also call <c>MaybeResolve</c>,
/// <c>MaybeTimeout</c> and <c>TickFlush</c>; <c>WireExtender</c> serializes both with a lock.
/// No other threading.</para>
/// </summary>
public sealed class CastWindowTracker
{
    private sealed class CastWindow
    {
        public readonly uint ActionId;
        public readonly long StartedTs;
        // Mutable: ActorCast refines both with the server's authoritative cast time.
        public long CastEndTs;
        public long DeadlineTs;
        public readonly Position Frozen;

        public CastWindow(uint actionId, long startedTs, long castEndTs, long deadlineTs, Position frozen)
        {
            ActionId = actionId;
            StartedTs = startedTs;
            CastEndTs = castEndTs;
            DeadlineTs = deadlineTs;
            Frozen = frozen;
        }
    }

    private sealed class StagedFlush
    {
        public readonly Position[] Steps;
        public int Next;
        public long LastEmitTs;
        public long DeadlineTs;

        public StagedFlush(Position[] steps, int next, long lastEmitTs, long deadlineTs)
        {
            Steps = steps;
            Next = next;
            LastEmitTs = lastEmitTs;
            DeadlineTs = deadlineTs;
        }
    }

    private readonly SlidecastConfig _cfg;
    // Not readonly: the sink is wired after construction (the sink needs the extender, the
    // tracker needs the sink — construction-order cycle, broken by SetFlushSink).
    private IFlushSink? _sink;
    private readonly Action<WireLogEntry> _log;
    private bool _sinkFailed;

    private CastWindow? _cast;
    private byte[]? _moveTemplate;
    private Position _lastReal = Position.Origin;
    private bool _hasRealMove;
    private Position _lastSentPos = Position.Origin;
    private bool _hasSentPos;
    private long _suppressedMoves;
    private long _flushedPackets;
    private StagedFlush? _staged;

    public CastWindowTracker(SlidecastConfig cfg, IFlushSink? sink, Action<WireLogEntry> log)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        _sink = sink;
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public void SetFlushSink(IFlushSink sink) => _sink = sink;

    public TrackerState State =>
        _staged is not null ? TrackerState.Flushing :
        _cast is not null ? TrackerState.Casting : TrackerState.Idle;

    /// <summary>True while the engine is doing something with the wire: the cast window is open
    /// (or a staged flush is in flight). This is the <c>window</c>, not the client's cast bar
    /// (that's M1).</summary>
    public bool WindowActive => State is TrackerState.Casting or TrackerState.Flushing;

    public uint? CastId => _cast?.ActionId;
    public Position? FrozenPosition => _cast?.Frozen;
    public Position? LastRealPosition => _hasRealMove ? _lastReal : null;
    public long? RemainingMsAt(long tsMs) => _cast is { } w ? Math.Max(0, w.CastEndTs - tsMs) : null;
    public long SuppressedMoves => _suppressedMoves;
    public long FlushedPackets => _flushedPackets;

    /// <summary>
    /// Opens a new window for an action the plugin's cast-time lookup says is a cast
    /// (<paramref name="durationMs"/> &gt; 0; zero means instant/non-castable and is ignored).
    /// The frozen position is the last position the server was made to believe
    /// (<c>_lastSentPos</c>: last pass-through Move, or the last flush). If a window is still
    /// open (chain-casting under the server's early resolution), the old window is flushed
    /// first so the server converges before the new freeze.
    /// </summary>
    public void StartCast(uint actionId, long durationMs, long tsMs)
    {
        if (durationMs <= 0)
        {
            Log(tsMs, LogSeverity.Debug, "cast-start", $"id={actionId} durationMs={durationMs}; skipping suppression");
            return;
        }
        if (_cast is not null)
        {
            Log(tsMs, LogSeverity.Info, "cast-restart", $"window {Id} replaced by {actionId}; flushing first");
            EmitFlush(tsMs, "cast-restart-flush");
        }

        var frozen = _hasSentPos ? _lastSentPos : Position.Origin;
        if (!_hasSentPos)
            Log(tsMs, LogSeverity.Warn, "cast-start", "no position seen yet; frozen=origin (move once after zoning)");

        _cast = new(actionId, tsMs, tsMs + durationMs, tsMs + durationMs + Math.Max(0, _cfg.GracePeriodMs), frozen);
        _moveTemplate = null;
        _lastReal = frozen;
        _hasRealMove = false;
        _suppressedMoves = 0;
        _sinkFailed = false;
        _staged = null;
        Log(tsMs, LogSeverity.Info, "cast-start", $"id={actionId} durationMs={durationMs} frozen={frozen}");
    }

    /// <summary>
    /// The server's <c>ActorCast</c> confirm arrived: refine the window deadline with the
    /// authoritative cast time (the sheet value and the server value can differ under
    /// spell-speed / latency). Ignored when no window is open — and when the spell id doesn't
    /// match the window's (ActorCast is broadcast for every actor in range; someone else's cast
    /// must not move our deadline — observed live 2026-09-21).
    /// </summary>
    public void RefineCast(uint actionId, float castTimeSec, long tsMs)
    {
        if (_cast is null) return;
        if (actionId != _cast.ActionId)
        {
            Log(tsMs, LogSeverity.Debug, "cast-refine",
                $"ignored: server spell {actionId} != window {Id} (another actor's cast)");
            return;
        }
        var castTimeMs = Math.Max(0, (long)(castTimeSec * 1000f));
        _cast.CastEndTs = tsMs + castTimeMs;
        _cast.DeadlineTs = _cast.CastEndTs + Math.Max(0, _cfg.GracePeriodMs);
        Log(tsMs, LogSeverity.Info, "cast-refine", $"id={Id} serverCastTimeMs={castTimeMs}");
    }

    /// <summary>
    /// A Move arrives while a window is open (position already decoded by <c>WirePackets</c>).
    /// Records the character's real position (the later flush target) and decides the gate:
    /// <list>
    /// <item><description><code>(false, null)</code> — Drop policy: the hook must NOT call the
    /// original send. The packet is dropped on the wire; the server keeps the frozen position.</description></item>
    /// <item><description><code>(true, frozen)</code> — ReplayFrozen policy: the hook must rewrite
    /// the original buffer's x/y/z in place (via <c>WirePackets.PatchMovePosition</c>) and forward.</description></item>
    /// <item><description>Outside the window: <code>(true, null)</code> — pass through untouched,
    /// and recorded as the position the server now believes.</description></item>
    /// </list>
    /// </summary>
    public (bool Keep, Position? PatchTo) HandleMove(byte[] movePayload, Position pos, int flags, long tsMs)
    {
        if (_cast is null)
        {
            // Outside a window the packet goes out untouched — this is the position the server
            // currently believes, which a later cast freezes.
            _lastSentPos = pos;
            _hasSentPos = true;
            return (true, null);
        }

        _moveTemplate = movePayload;
        _lastReal = pos;
        _hasRealMove = true;
        _suppressedMoves++;
        if (_cfg.LogEveryNthSuppressedMove > 0 && _suppressedMoves % _cfg.LogEveryNthSuppressedMove == 0)
            Log(tsMs, LogSeverity.Info, "move-suppressed", $"pos={pos} count={_suppressedMoves}");

        return _cfg.Suppress == SuppressionPolicy.Drop
            ? (false, null)
            : (true, _cast.Frozen);
    }

    /// <summary>
    /// The cast's completion time arrived (<c>tsMs &gt;= CastEndTs</c>): the server resolves the
    /// cast here (a touch earlier in practice — the stock slidecast early-window — so by the
    /// deadline the resolve has certainly happened). Emits the pending flush, then returns to
    /// normal. Called from every hook path and the backstop timer.
    /// </summary>
    public void MaybeResolve(long tsMs)
    {
        if (_cast is null || tsMs < _cast.CastEndTs) return;
        EmitFlush(tsMs, "cast-resolve");
    }

    /// <summary>A finish signal arrived (currently unused by the facade — the deadline drives
    /// the resolve — but kept for a future verified completion packet). Flushes and closes.</summary>
    public void OnCastFinish(long tsMs) => EmitFlush(tsMs, "cast-finish");

    /// <summary>Server says the cast was interrupted (ActorControl interrupt category).
    /// Fail-open (ADR-002): flush the pending real position so the server's view converges, then
    /// return to normal. The client resumes sending its real position on its next Move regardless.</summary>
    public void OnCastInterrupt(long tsMs)
    {
        if (_cast is null)
        {
            Log(tsMs, LogSeverity.Debug, "cast-interrupt", "no active window; ignored");
            return;
        }
        Log(tsMs, LogSeverity.Info, "cast-interrupt", $"id={Id}");
        EmitFlush(tsMs, "cast-interrupt");
    }

    /// <summary>
    /// Backstop: the window is past deadline (cast end + grace) without resolving — e.g. a
    /// mis-tabled opcode or a silent server. Cheap to call from every hook path and the timer.
    /// </summary>
    public void MaybeTimeout(long tsMs)
    {
        if (_cast is null || tsMs < _cast.DeadlineTs) return;
        Log(tsMs, LogSeverity.Warn, "cast-timeout", $"id={Id} no finish/interrupt in window+grace; fail-open flush");
        EmitFlush(tsMs, "cast-timeout");
    }

    /// <summary>
    /// Advances a staged flush: one step per <c>StagedStepIntervalMs</c>, or all remaining steps at
    /// once once past <c>StagedFlushTimeoutMs</c> (fail-open). No-op when nothing is staged.
    /// </summary>
    public void TickFlush(long tsMs)
    {
        if (_staged is null) return;
        if (_sinkFailed)
        {
            _staged = null;
            _moveTemplate = null;
            Log(tsMs, LogSeverity.Warn, "staged-flush-aborted", "flush sink failed; abandoning staged flush (ADR-002)");
            return;
        }
        if (tsMs >= _staged.DeadlineTs)
        {
            EmitStagedRemaining(tsMs);
            _staged = null;
            _moveTemplate = null;
            Log(tsMs, LogSeverity.Info, "staged-flush-done", "timeout; remaining steps emitted at once");
            return;
        }
        if (tsMs - _staged.LastEmitTs < _cfg.StagedStepIntervalMs) return;
        if (_staged.Next < _staged.Steps.Length)
        {
            EmitStagedStep(tsMs, _staged.Next++);
            _staged.LastEmitTs = tsMs;
        }
        if (_staged.Next >= _staged.Steps.Length)
        {
            _staged = null;
            _moveTemplate = null;
            Log(tsMs, LogSeverity.Info, "staged-flush-done", "completed");
        }
    }

    private void EmitFlush(long tsMs, string reason)
    {
        if (_cast is null) return;
        Log(tsMs, LogSeverity.Info, reason, $"id={Id}");

        var drift = _hasRealMove && !_lastReal.IsCloseTo(_cast.Frozen, 0.01f);
        if (drift)
        {
            if (_cfg.Flush == FlushMode.Staged)
            {
                _staged = NewStagedFlush(tsMs);
                Log(tsMs, LogSeverity.Info, "staged-flush-start",
                    $"from={_cast.Frozen} to={_lastReal} steps={_staged.Steps.Length}");
                if (_staged.Steps.Length > 0)
                {
                    EmitStagedStep(tsMs, 0);
                    _staged.Next = 1;
                    _staged.LastEmitTs = tsMs;
                }
            }
            else
            {
                EmitStepPosition(tsMs, _lastReal, $"{reason} pos={_lastReal}");
            }
        }
        else
        {
            Log(tsMs, LogSeverity.Debug, "flush-no-drift", $"id={Id} lastReal={_lastReal} ≈ frozen; nothing flushed");
        }

        ResetWindow();
    }

    private StagedFlush NewStagedFlush(long tsMs)
    {
        var steps = ComputeStagedSteps(_cast!.Frozen, _lastReal);
        return new(steps, 0, tsMs, tsMs + Math.Max(1, _cfg.StagedFlushTimeoutMs));
    }

    private Position[] ComputeStagedSteps(Position from, Position to)
    {
        var dist = from.DistanceTo(to);
        if (dist <= 0.001f) return Array.Empty<Position>();
        var maxStep = MathF.Max(0.001f, _cfg.StagedMaxStepUnits);
        var needed = (int)MathF.Ceiling(dist / maxStep);
        var n = Math.Clamp(Math.Max(_cfg.StagedSteps, needed), 1, 64);
        var stepDist = MathF.Min(maxStep, dist / n);
        var unit = (to - from) / dist;
        var steps = new Position[n];
        for (int i = 0; i < n; i++)
            steps[i] = i == n - 1 ? to : from + unit * (i + 1) * stepDist;
        return steps;
    }

    private void EmitStagedStep(long tsMs, int index)
    {
        if (_staged is not null) EmitStepPosition(tsMs, _staged.Steps[index], $"staged {index + 1}/{_staged.Steps.Length}");
    }

    private void EmitStagedRemaining(long tsMs)
    {
        if (_staged is not null)
            while (_staged.Next < _staged.Steps.Length)
                EmitStagedStep(tsMs, _staged.Next++);
    }

    private void EmitStepPosition(long tsMs, Position pos, string context)
    {
        if (_sinkFailed) return;
        if (_sink is null)
        {
            Log(tsMs, LogSeverity.Warn, "flush-sink-not-ready", context);
            _sinkFailed = true;
            return;
        }
        if (_moveTemplate is null)
        {
            Log(tsMs, LogSeverity.Warn, "flush-no-template", context);
            _sinkFailed = true;
            return;
        }
        var packet = WirePackets.BuildMovePacket(_moveTemplate, pos);
        try
        {
            _sink.Send(packet);
            _flushedPackets++;
            // The server now believes the flushed position — the next cast freezes this.
            _lastSentPos = pos;
            _hasSentPos = true;
            Log(tsMs, LogSeverity.Info, "flush", $"{context} bytes={packet.Length}");
        }
        catch (Exception e)
        {
            _sinkFailed = true;
            Log(tsMs, LogSeverity.Error, "flush-send-failed", $"{context}: {e}");
        }
    }

    private void ResetWindow()
    {
        _cast = null;
        _lastReal = Position.Origin;
        _hasRealMove = false;
        _suppressedMoves = 0;
        // When a staged flush is in flight it still reads _moveTemplate (to build step packets)
        // and _sinkFailed (to abort on the next TickFlush); both are cleared when the staged
        // flush completes/aborts, and StartCast resets everything on the next window regardless.
        if (_staged is null)
        {
            _moveTemplate = null;
            _sinkFailed = false;
        }
        // _staged is on purpose NOT cleared here when a staged flush is active; it clears in
        // TickFlush when complete.
    }

    private void Log(long tsMs, LogSeverity sev, string tag, string msg) => _log(new WireLogEntry(tsMs, sev, tag, msg));

    private uint Id => _cast?.ActionId ?? 0;
}
