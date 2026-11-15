using System;
using System.Runtime.InteropServices;

namespace SlidecastExtender.Core;

/// <summary>
/// Facade the plugin's native hooks call. Owns the mode (<c>Off</c>/<c>Observe</c>/<c>Live</c>)
/// and routes the real zone protocol (opcodes: FFXIVOpcodes Global 7.56h):
/// send <c>ActionRequest</c> → window start (if the cast-time provider says it's a cast),
/// send <c>UpdatePositionHandler</c> / <c>UpdatePositionInstance</c> → suppression gate,
/// receive <c>ActorCast</c> → refine deadline, <c>Effect</c> → finish,
/// <c>ActorControl(Self)</c> with the interrupt category → interrupt.
/// Contains no Dalamud types.
/// </summary>
public sealed class WireExtender
{
    private readonly SlidecastConfig _cfg;
    private readonly CastWindowTracker _tracker;
    private readonly Action<WireLogEntry> _log;
    private readonly object _lock = new();
    private volatile ExtenderMode _mode;
    private int _observeCounter;
    private bool _warnedBadInstanceMove;

    private IFlushSink? _sink;
    private Func<uint, long>? _castTimeProvider;
    private Func<uint>? _localEntityIdProvider;

    public WireExtender(SlidecastConfig cfg, Action<WireLogEntry> log)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _tracker = new CastWindowTracker(cfg, null, log);
        _mode = cfg.Mode;
    }

    /// <summary>Wired by the plugin after construction (the sink needs this extender, the tracker
    /// needs the sink — construction-order cycle, broken here).</summary>
    public void SetFlushSink(IFlushSink sink)
    {
        _sink = sink;
        _tracker.SetFlushSink(sink);
    }

    /// <summary>Wired by the plugin: action id → cast time in ms from game data (0 = instant /
    /// not a cast → no window). Without a provider no windows open (safe: nothing suppressed).</summary>
    public void SetCastTimeProvider(Func<uint, long>? provider) => _castTimeProvider = provider;

    /// <summary>Wired by the plugin: the local player's entity id (0 = unknown, e.g. during a
    /// loading screen). Actor-scoped server packets (<c>ActorCast</c>, <c>ActorControl</c>) are
    /// broadcast for every actor in range — live 2026-09-25, in a crowded FATE other players'
    /// cast-interrupts closed our window mid-cast (the premature flush then told the server we
    /// moved, really killing the cast). Without a provider all packets are trusted (legacy/test
    /// behavior); with one, an unknown (0) local id trusts nothing broadcast — the deadline
    /// backstop still closes the window (ADR-002). <c>ActorControlSelf</c> is exempt: the Self
    /// variant is about us by definition.</summary>
    public void SetLocalEntityIdProvider(Func<uint>? provider) => _localEntityIdProvider = provider;

    public ExtenderMode Mode => _mode;

    public void SetMode(ExtenderMode mode)
    {
        _mode = mode;
        LogEntry(Environment.TickCount, LogSeverity.Info, "mode", $"mode={mode}");
    }

    public void SetSuppression(SuppressionPolicy p) => _cfg.Suppress = p;
    public void SetFlush(FlushMode f) => _cfg.Flush = f;
    public void SetGrace(long ms) => _cfg.GracePeriodMs = Math.Max(0, ms);

    /// <summary>
    /// From the client→server hook (<c>ZoneClient.SendPacket</c>: no explicit length — the
    /// engine copies bounded per-id prefixes), before the original send.
    /// Returns <code>false</code> → drop the packet (the hook pretends success without calling
    /// the original). ReplayFrozen patches the original buffer in place before returning
    /// <code>true</code>.
    /// </summary>
    public bool OnClientToSend(IntPtr data, long tsMs)
    {
        var mode = _mode;
        if (mode is ExtenderMode.Off) return true;
        var id = WirePackets.SendPacketIdFromBuffer(data, 2);

        if (mode is ExtenderMode.Observe)
        {
            LogObservedSend(CopyBuffer(data, WirePackets.IsMove(id)
                ? WirePackets.MoveCaptureBytesFor(id)
                : WirePackets.ObserveCaptureBytes), id, tsMs);
            return true;
        }

        bool keep = true;
        lock (_lock)
        {
            if (id is PacketId.ActionRequest or PacketId.ActionRequestGroundTargeted)
            {
                var payload = CopyBuffer(data, WirePackets.ActionRequestCaptureBytes);
                var actionId = WirePackets.ReadActionRequestActionId(payload);
                var castTimeMs = _castTimeProvider?.Invoke(actionId) ?? 0;
                if (castTimeMs > 0)
                    _tracker.StartCast(actionId, castTimeMs, tsMs);
                else
                    LogEntry(tsMs, LogSeverity.Debug, "cast-start",
                        $"id={actionId} castTimeMs={castTimeMs}; instant/unknown, no window");
            }
            else if (WirePackets.IsMove(id))
            {
                var payload = CopyBuffer(data, WirePackets.MoveCaptureBytesFor(id));
                if (WirePackets.TryDecodeMovePosition(payload, out var pos))
                {
                    var (k, patchTo) = _tracker.HandleMove(payload, pos, 0, tsMs);
                    keep = k;
                    if (patchTo is not null)
                        WirePackets.PatchMovePosition(data, patchTo.Value);
                }
                else if (!_warnedBadInstanceMove)
                {
                    // Fail open (ADR-002): an untrusted decode is never suppressed or flushed.
                    _warnedBadInstanceMove = true;
                    LogEntry(tsMs, LogSeverity.Warn, "move-layout",
                        $"id=0x{(int)id:X2} failed the layout sanity check; passing instance moves through " +
                        $"(capture in Observe to fix offsets) hex={Convert.ToHexString(payload)}");
                }
            }

            _tracker.TickFlush(tsMs);
            _tracker.MaybeResolve(tsMs);
            _tracker.MaybeTimeout(tsMs);
        }
        return keep;
    }

    /// <summary>From the server→client hook (<c>PacketDispatcher.OnReceivePacket</c>), after
    /// decryption (plaintext in). <paramref name="data"/> is a bounded prefix of the packet;
    /// <paramref name="targetId"/> is the entity the packet is about (from the hook — the
    /// engine correlates actor-scoped packets against the local player).</summary>
    public void OnClientReceived(byte[] data, uint targetId, long tsMs)
    {
        var id = WirePackets.ReceivePacketIdFromBytes(data);
        if (_mode is ExtenderMode.Observe)
        {
            LogObservedReceive(data, id, targetId, tsMs);
            return;
        }
        if (_mode is not ExtenderMode.Live) return;

        lock (_lock)
        {
            switch (id)
            {
                case PacketId.ActorCast:
                    if (!IsAboutLocalPlayer(targetId))
                    {
                        LogEntry(tsMs, LogSeverity.Debug, "cast-refine",
                            $"ignored: ActorCast about foreign actor 0x{targetId:X8}");
                        break;
                    }
                    var (actionId, castTimeSec) = WirePackets.DecodeActorCast(data);
                    _tracker.RefineCast(actionId, castTimeSec, tsMs);
                    break;
                case PacketId.Effect:
                case PacketId.EffectResult:
                    // Not a close signal: the caster's own completion didn't arrive as Effect
                    // live, and in combat the Effect flood closes windows instantly. The window
                    // closes on the cast deadline (MaybeResolve) — this is logged for validation.
                    if (_tracker.WindowActive)
                        LogEntry(tsMs, LogSeverity.Debug, "effect-seen",
                            $"id=0x{(int)id:X2} during window cast={_tracker.CastId} (not closing; deadline-driven)");
                    break;
                case PacketId.ActorControl:
                    // Broadcast for every actor in range: a foreign cast's interrupt must not
                    // close our window (observed live 2026-09-25 — it killed real casts).
                    if (_cfg.InterruptActorControlCategory >= 0 &&
                        WirePackets.DecodeActorControlCategory(data) == _cfg.InterruptActorControlCategory)
                    {
                        if (IsAboutLocalPlayer(targetId))
                            _tracker.OnCastInterrupt(tsMs);
                        else
                            LogEntry(tsMs, LogSeverity.Debug, "cast-interrupt",
                                $"ignored: foreign actor 0x{targetId:X8}");
                    }
                    break;
                case PacketId.ActorControlSelf:
                    // The Self variant is about the local player by definition — no correlation needed.
                    if (_cfg.InterruptActorControlCategory >= 0 &&
                        WirePackets.DecodeActorControlCategory(data) == _cfg.InterruptActorControlCategory)
                        _tracker.OnCastInterrupt(tsMs);
                    break;
                default:
                    // Unknown/other traffic: pass through untouched.
                    break;
            }
            _tracker.TickFlush(tsMs);
            _tracker.MaybeResolve(tsMs);
            _tracker.MaybeTimeout(tsMs);
        }
    }

    /// <summary>True when an actor-scoped broadcast packet concerns the local player. No provider
    /// wired → trust everything (legacy/test); provider says unknown (0) → trust nothing (the
    /// deadline backstop still closes the window, ADR-002).</summary>
    private bool IsAboutLocalPlayer(uint targetId)
    {
        var provider = _localEntityIdProvider;
        if (provider is null) return true;
        var localId = provider();
        return localId != 0 && targetId == localId;
    }

    /// <summary>Backstop timer callback: advances staged flushes and enforces timeouts even when
    /// no packets flow.</summary>
    public void Tick(long tsMs)
    {
        if (_mode is not ExtenderMode.Live) return;
        lock (_lock)
        {
            _tracker.TickFlush(tsMs);
            _tracker.MaybeResolve(tsMs);
            _tracker.MaybeTimeout(tsMs);
        }
    }

    public WireStatus Status() => new(
        _mode,
        _tracker.WindowActive,
        _tracker.CastId,
        _tracker.RemainingMsAt(Environment.TickCount),
        _tracker.SuppressedMoves,
        _tracker.FlushedPackets);

    private void LogObservedSend(byte[] data, PacketId id, long tsMs)
    {
        if (!Throttle()) return;
        LogEntry(tsMs, LogSeverity.Info, "observe-send", $"id=0x{(int)id:X2} len={data.Length}");
        if (!_cfg.ObserveLogDetails) return;
        switch (id)
        {
            case PacketId.ActionRequest:
            case PacketId.ActionRequestGroundTargeted:
                LogEntry(tsMs, LogSeverity.Info, "observe-action",
                    $"actionId={WirePackets.ReadActionRequestActionId(data)} (VERIFY offset {WirePackets.ActionRequestActionIdOffset}) hex={Convert.ToHexString(data)}");
                break;
            case PacketId.UpdatePositionHandler when data.Length >= WirePackets.MoveMinLength:
                LogEntry(tsMs, LogSeverity.Info, "observe-move",
                    $"pos={WirePackets.DecodeMovePosition(data)} (VERIFY offsets {WirePackets.MoveXOffset}/{WirePackets.MoveYOffset}/{WirePackets.MoveZOffset}) hex={Convert.ToHexString(data)}");
                break;
            case PacketId.UpdatePositionInstance when data.Length >= WirePackets.MoveInstanceMinLength:
                var trusted = WirePackets.TryDecodeMovePosition(data, out var instancePos);
                LogEntry(tsMs, LogSeverity.Info, "observe-move-instance",
                    $"pos={instancePos} sane={trusted} (VERIFY offsets {WirePackets.MoveInstanceXOffset}/{WirePackets.MoveInstanceInterpXOffset}) hex={Convert.ToHexString(data)}");
                break;
        }
    }

    private void LogObservedReceive(byte[] data, PacketId id, uint targetId, long tsMs)
    {
        if (!Throttle()) return;
        LogEntry(tsMs, LogSeverity.Info, "observe-recv", $"id=0x{(int)id:X2} len={data.Length}");
        if (!_cfg.ObserveLogDetails) return;
        switch (id)
        {
            case PacketId.ActorCast:
                var (actionId, castTimeSec) = WirePackets.DecodeActorCast(data);
                LogEntry(tsMs, LogSeverity.Info, "observe-cast",
                    $"actionId={actionId} castTimeSec={castTimeSec:0.###} targetId=0x{targetId:X8} hex={Convert.ToHexString(data)}");
                break;
            case PacketId.ActorControl:
            case PacketId.ActorControlSelf:
                LogEntry(tsMs, LogSeverity.Info, "observe-actorcontrol",
                    $"category={WirePackets.DecodeActorControlCategory(data)} targetId=0x{targetId:X8} hex={Convert.ToHexString(data)}");
                break;
            case PacketId.Effect:
            case PacketId.EffectResult:
                // Hex captured for the completion-signal hunt: which packet (if any) marks OUR
                // cast's completion, and which field carries the spell id / source entity.
                LogEntry(tsMs, LogSeverity.Info, "observe-effect",
                    $"id=0x{(int)id:X2} hex={Convert.ToHexString(data)}");
                break;
        }
    }

    /// <summary>Observe throttle: log every Nth packet per direction.</summary>
    private bool Throttle()
    {
        if (_cfg.ObserveLogEveryNth <= 0) return false;
        _observeCounter++;
        return _observeCounter % _cfg.ObserveLogEveryNth == 0;
    }

    private byte[] CopyBuffer(IntPtr data, int bytes)
    {
        var buf = new byte[bytes];
        Marshal.Copy(data, buf, 0, bytes);
        return buf;
    }

    private void LogEntry(long tsMs, LogSeverity sev, string tag, string msg) =>
        _log(new WireLogEntry(tsMs, sev, tag, msg));
}
