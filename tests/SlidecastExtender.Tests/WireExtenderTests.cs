using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using SlidecastExtender.Core;
using Xunit;

namespace SlidecastExtender.Tests;

public class WireExtenderTests
{
    private const long T0 = 100_000;
    private const uint CastAction = 100;   // provider says: 2500 ms cast
    private const uint InstantAction = 200; // provider says: instant

    private static WireExtender NewExtender(
        List<WireLogEntry> logs,
        Action<SlidecastConfig>? configure = null)
    {
        var cfg = new SlidecastConfig();
        configure?.Invoke(cfg);
        var extender = new WireExtender(cfg, logs.Add);
        extender.SetCastTimeProvider(actionId => actionId switch
        {
            CastAction => 2500L,
            _ => 0L,
        });
        return extender;
    }

    /// <summary>Runs the send path against a pinned copy of <paramref name="packet"/>; mutations
    /// the hook makes in place (ReplayFrozen) are visible in <paramref name="packet"/> after.</summary>
    private static bool Send(WireExtender extender, byte[] packet, long tsMs)
    {
        var handle = GCHandle.Alloc(packet, GCHandleType.Pinned);
        try
        {
            return extender.OnClientToSend(handle.AddrOfPinnedObject(), tsMs);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>A pass-through move in Live mode outside any window, so the next cast freezes
    /// this position as what the server believes.</summary>
    private static void EstablishServerPosition(WireExtender extender, Position pos, long tsMs) =>
        Assert.True(Send(extender, TestPackets.BuildMove(pos), tsMs));

    [Fact]
    public void Off_PassesEverythingThrough_AndDoesNoWork()
    {
        var logs = new List<WireLogEntry>();
        var extender = NewExtender(logs, c => c.Mode = ExtenderMode.Off);

        Assert.True(Send(extender, TestPackets.BuildActionRequest(CastAction), T0));
        Assert.True(Send(extender, TestPackets.BuildMove(new Position(11f, 0f, 0f)), T0 + 10));
        extender.OnClientReceived(TestPackets.BuildReceiveRaw(PacketId.Effect), 0u, T0 + 2500);
        extender.Tick(T0 + 99_000);

        Assert.Empty(logs);
        Assert.False(extender.Status().WindowActive);
        Assert.Equal(ExtenderMode.Off, extender.Status().Mode);
    }

    [Fact]
    public void Observe_LogsSendAndReceive_ButNeverDropsOrOpensWindows()
    {
        var logs = new List<WireLogEntry>();
        var extender = NewExtender(logs, c => c.Mode = ExtenderMode.Observe);

        Assert.True(Send(extender, TestPackets.BuildActionRequest(CastAction), T0));
        Assert.True(Send(extender, TestPackets.BuildMove(new Position(11f, 0f, 0f)), T0 + 10));
        extender.OnClientReceived(TestPackets.BuildReceiveRaw(PacketId.Effect), 0u, T0 + 2500);

        Assert.Equal(2, logs.Count(e => e.Tag == "observe-send"));
        Assert.Single(logs.Where(e => e.Tag == "observe-recv"));
        Assert.Single(logs.Where(e => e.Tag == "observe-effect")); // hex capture for the correlation hunt
        Assert.False(extender.Status().WindowActive); // Observe never opens a window
    }

    [Fact]
    public void Observe_LogsDecodedDetailsForLayoutVerification()
    {
        var logs = new List<WireLogEntry>();
        var extender = NewExtender(logs, c => c.Mode = ExtenderMode.Observe);

        Send(extender, TestPackets.BuildActionRequest(CastAction), T0);
        Send(extender, TestPackets.BuildMove(new Position(11f, 0f, 0f)), T0 + 10);
        extender.OnClientReceived(TestPackets.BuildActorCast(CastAction, 2.5f), 0u, T0 + 20);
        extender.OnClientReceived(TestPackets.BuildActorControl(15), 0u, T0 + 30);

        var actionLog = Assert.Single(logs.Where(e => e.Tag == "observe-action"));
        Assert.Contains($"actionId={CastAction}", actionLog.Message);
        Assert.Contains("hex=", actionLog.Message); // raw bytes for offset hunting
        Assert.True(TestAssert.HasLog(logs, "observe-move"));
        var castLog = Assert.Single(logs.Where(e => e.Tag == "observe-cast"));
        Assert.Contains("castTimeSec=2.5", castLog.Message);
        var acLog = Assert.Single(logs.Where(e => e.Tag == "observe-actorcontrol"));
        Assert.Contains("category=15", acLog.Message);
    }

    [Fact]
    public void Observe_ThrottlesLoggingToEveryNthPacket()
    {
        var logs = new List<WireLogEntry>();
        var extender = NewExtender(logs, c =>
        {
            c.Mode = ExtenderMode.Observe;
            c.ObserveLogEveryNth = 2;
            c.ObserveLogDetails = false;
        });

        Send(extender, TestPackets.BuildMove(new Position(1f, 0f, 0f)), T0);
        Send(extender, TestPackets.BuildMove(new Position(2f, 0f, 0f)), T0 + 1);

        Assert.Single(logs.Where(e => e.Tag == "observe-send"));
    }

    [Fact]
    public void Live_CastRequestThenMove_DropsTheMove()
    {
        var logs = new List<WireLogEntry>();
        var extender = NewExtender(logs, c => c.Mode = ExtenderMode.Live);
        EstablishServerPosition(extender, new Position(10f, 0f, 0f), T0 - 10);

        Assert.True(Send(extender, TestPackets.BuildActionRequest(CastAction), T0));
        Assert.False(Send(extender, TestPackets.BuildMove(new Position(11f, 0f, 0f)), T0 + 10));

        var status = extender.Status();
        Assert.True(status.WindowActive);
        Assert.Equal(CastAction, status.CastId);
        Assert.Equal(1, status.SuppressedMoves);
    }

    [Fact]
    public void Live_InstantAction_OpensNoWindow_MovesPassThrough()
    {
        var logs = new List<WireLogEntry>();
        var extender = NewExtender(logs, c => c.Mode = ExtenderMode.Live);
        EstablishServerPosition(extender, new Position(10f, 0f, 0f), T0 - 10);

        Assert.True(Send(extender, TestPackets.BuildActionRequest(InstantAction), T0));
        Assert.True(Send(extender, TestPackets.BuildMove(new Position(11f, 0f, 0f)), T0 + 10));

        Assert.False(extender.Status().WindowActive);
        Assert.Equal(0, extender.Status().SuppressedMoves);
    }

    [Fact]
    public void Live_GroundTargetedCastRequest_AlsoOpensWindow()
    {
        var logs = new List<WireLogEntry>();
        var extender = NewExtender(logs, c => c.Mode = ExtenderMode.Live);
        EstablishServerPosition(extender, new Position(10f, 0f, 0f), T0 - 10);

        Assert.True(Send(extender, TestPackets.BuildActionRequest(CastAction, groundTargeted: true), T0));
        Assert.True(extender.Status().WindowActive);
    }

    [Fact]
    public void Live_WithoutCastTimeProvider_NeverOpensWindows()
    {
        var logs = new List<WireLogEntry>();
        var cfg = new SlidecastConfig { Mode = ExtenderMode.Live };
        var extender = new WireExtender(cfg, logs.Add); // no provider wired

        Assert.True(Send(extender, TestPackets.BuildActionRequest(CastAction), T0));
        Assert.False(extender.Status().WindowActive);
    }

    [Fact]
    public void Live_ReplayFrozen_PatchesTheOriginalBufferInPlace()
    {
        var logs = new List<WireLogEntry>();
        var extender = NewExtender(logs, c =>
        {
            c.Mode = ExtenderMode.Live;
            c.Suppress = SuppressionPolicy.ReplayFrozen;
        });
        var frozen = new Position(10f, 0f, 0f);
        EstablishServerPosition(extender, frozen, T0 - 10);

        Assert.True(Send(extender, TestPackets.BuildActionRequest(CastAction), T0));

        var move = TestPackets.BuildMove(new Position(14f, 0f, 0f));
        Assert.True(Send(extender, move, T0 + 10)); // kept…

        // …but the buffer the original send is about to transmit now reads the frozen position.
        Assert.Equal(frozen, WirePackets.DecodeMovePosition(move));
    }

    [Fact]
    public void Live_ActorCast_RefinesTheWindowDeadline()
    {
        var logs = new List<WireLogEntry>();
        var extender = NewExtender(logs, c => c.Mode = ExtenderMode.Live);
        var sink = new FakeFlushSink();
        extender.SetFlushSink(sink);
        EstablishServerPosition(extender, new Position(10f, 0f, 0f), T0 - 10);

        Send(extender, TestPackets.BuildActionRequest(CastAction), T0); // sheet: 2500 ms
        extender.OnClientReceived(TestPackets.BuildActorCast(CastAction, 4.0f), 0u, T0 + 50);

        Assert.True(TestAssert.HasLog(logs, "cast-refine"));
        // Sheet deadline (T0+2500) would have resolved; the refined 4 s window must not.
        extender.Tick(T0 + 3000);
        Assert.True(extender.Status().WindowActive);
        Assert.Empty(sink.Sent);
    }

    [Fact]
    public void Live_Effect_DoesNotCloseTheWindow()
    {
        // Live evidence 2026-09-21: the caster's own completion didn't arrive as Effect, and in
        // combat the Effect flood closed windows ~30 ms in. Effects are validation logging now;
        // the window closes on the cast deadline.
        var logs = new List<WireLogEntry>();
        var extender = NewExtender(logs, c => c.Mode = ExtenderMode.Live);
        var sink = new FakeFlushSink();
        extender.SetFlushSink(sink);
        EstablishServerPosition(extender, new Position(10f, 0f, 0f), T0 - 10);

        Send(extender, TestPackets.BuildActionRequest(CastAction), T0);
        Send(extender, TestPackets.BuildMove(new Position(12f, 0f, 0f)), T0 + 10);
        extender.OnClientReceived(TestPackets.BuildReceiveRaw(PacketId.Effect), 0u, T0 + 100);
        extender.OnClientReceived(TestPackets.BuildReceiveRaw(PacketId.EffectResult), 0u, T0 + 150);

        Assert.True(extender.Status().WindowActive); // still open
        Assert.Empty(sink.Sent);                     // nothing flushed
        Assert.True(TestAssert.HasLog(logs, "effect-seen"));
    }

    [Fact]
    public void Live_Resolve_AtCastEnd_FlushesRealPosition()
    {
        var logs = new List<WireLogEntry>();
        var extender = NewExtender(logs, c => c.Mode = ExtenderMode.Live);
        var sink = new FakeFlushSink();
        extender.SetFlushSink(sink);
        EstablishServerPosition(extender, new Position(10f, 0f, 0f), T0 - 10);

        Send(extender, TestPackets.BuildActionRequest(CastAction), T0); // 2500 ms cast
        var real = new Position(15f, 0f, -2f);
        Send(extender, TestPackets.BuildMove(real), T0 + 10);

        extender.Tick(T0 + 2499); // not yet
        Assert.Empty(sink.Sent);

        extender.Tick(T0 + 2500); // cast end → resolve → flush
        var packet = Assert.Single(sink.Sent);
        Assert.Equal(real, WirePackets.DecodeMovePosition(packet));
        Assert.False(extender.Status().WindowActive);
        Assert.True(TestAssert.HasLog(logs, "cast-resolve"));
    }

    [Fact]
    public void Live_ChainCast_FlushesThePreviousWindowFirst()
    {
        var logs = new List<WireLogEntry>();
        var extender = NewExtender(logs, c => c.Mode = ExtenderMode.Live);
        var sink = new FakeFlushSink();
        extender.SetFlushSink(sink);
        EstablishServerPosition(extender, new Position(10f, 0f, 0f), T0 - 10);

        Send(extender, TestPackets.BuildActionRequest(CastAction), T0);
        var real = new Position(13f, 0f, 0f);
        Send(extender, TestPackets.BuildMove(real), T0 + 10);

        // Server resolved early; the user chain-casts before the old window's deadline.
        Send(extender, TestPackets.BuildActionRequest(CastAction), T0 + 2000);

        var packet = Assert.Single(sink.Sent); // old window flushed before the new freeze
        Assert.Equal(real, WirePackets.DecodeMovePosition(packet));
        Assert.True(TestAssert.HasLog(logs, "cast-restart"));
        Assert.True(extender.Status().WindowActive); // new window open
    }

    [Fact]
    public void Live_ActorControlInterrupt_FailOpenFlushes_WhenCategoryMatches()
    {
        var logs = new List<WireLogEntry>();
        var extender = NewExtender(logs, c =>
        {
            c.Mode = ExtenderMode.Live;
            c.InterruptActorControlCategory = 15; // observed on the wire (walk-away interrupt)
        });
        var sink = new FakeFlushSink();
        extender.SetFlushSink(sink);
        EstablishServerPosition(extender, new Position(10f, 0f, 0f), T0 - 10);

        Send(extender, TestPackets.BuildActionRequest(CastAction), T0);
        var real = new Position(13f, 0f, 0f);
        Send(extender, TestPackets.BuildMove(real), T0 + 10);

        extender.OnClientReceived(TestPackets.BuildActorControl(99), 0u, T0 + 400); // some other category
        Assert.Empty(sink.Sent);
        Assert.True(extender.Status().WindowActive);

        extender.OnClientReceived(TestPackets.BuildActorControl(15), 0u, T0 + 500); // the interrupt
        Assert.Single(sink.Sent);
        Assert.False(extender.Status().WindowActive);
        Assert.True(TestAssert.HasLog(logs, "cast-interrupt"));
    }

    [Fact]
    public void Live_ActorControlInterrupt_Ignored_WhenCategoryDisabled()
    {
        var logs = new List<WireLogEntry>();
        var extender = NewExtender(logs, c =>
        {
            c.Mode = ExtenderMode.Live;
            c.InterruptActorControlCategory = -1; // explicitly disabled
        });
        var sink = new FakeFlushSink();
        extender.SetFlushSink(sink);
        EstablishServerPosition(extender, new Position(10f, 0f, 0f), T0 - 10);

        Send(extender, TestPackets.BuildActionRequest(CastAction), T0);
        Send(extender, TestPackets.BuildMove(new Position(13f, 0f, 0f)), T0 + 10);
        extender.OnClientReceived(TestPackets.BuildActorControl(15), 0u, T0 + 500);

        Assert.Empty(sink.Sent);
        Assert.True(extender.Status().WindowActive); // deadline/backstop will close it instead
    }

    [Fact]
    public void Live_Tick_BackstopTimesOutWhenDeadlineNeverRefined()
    {
        var logs = new List<WireLogEntry>();
        var extender = NewExtender(logs, c =>
        {
            c.Mode = ExtenderMode.Live;
            c.GracePeriodMs = 500;
        });
        var sink = new FakeFlushSink();
        extender.SetFlushSink(sink);
        EstablishServerPosition(extender, new Position(10f, 0f, 0f), T0 - 10);

        // No ActorCast refine: the sheet duration drives the resolve; grace is the backstop.
        Send(extender, TestPackets.BuildActionRequest(CastAction), T0); // 2500 ms + 500 grace
        Send(extender, TestPackets.BuildMove(new Position(12f, 0f, 0f)), T0 + 10);

        extender.Tick(T0 + 2499);
        Assert.Empty(sink.Sent);
        extender.Tick(T0 + 2500); // resolve at cast end
        Assert.Single(sink.Sent);
    }

    [Fact]
    public void Live_FinishWithoutSink_DoesNotThrowAndClosesWindow()
    {
        var logs = new List<WireLogEntry>();
        var extender = NewExtender(logs, c => c.Mode = ExtenderMode.Live);
        // No SetFlushSink — the plugin wires it after construction; a resolve must not hang.
        EstablishServerPosition(extender, new Position(10f, 0f, 0f), T0 - 10);

        Send(extender, TestPackets.BuildActionRequest(CastAction), T0);
        Send(extender, TestPackets.BuildMove(new Position(12f, 0f, 0f)), T0 + 10);
        extender.Tick(T0 + 2500); // resolve at cast end

        Assert.False(extender.Status().WindowActive);
        Assert.True(TestAssert.HasLog(logs, "flush-sink-not-ready"));
    }

    [Fact]
    public void Live_UnknownPacket_PassesThroughUntouched()
    {
        var logs = new List<WireLogEntry>();
        var extender = NewExtender(logs, c => c.Mode = ExtenderMode.Live);

        Assert.True(Send(extender, new byte[] { 0xEE, 0x7E, 1, 2, 3 }, T0));
        Assert.False(extender.Status().WindowActive);
    }

    [Fact]
    public void Tick_OutsideLiveMode_IsANoOp()
    {
        var logs = new List<WireLogEntry>();
        var extender = NewExtender(logs, c => c.Mode = ExtenderMode.Observe);
        var sink = new FakeFlushSink();
        extender.SetFlushSink(sink);

        extender.Tick(T0);
        extender.Tick(T0 + 10_000);

        Assert.Empty(sink.Sent);
        Assert.Empty(logs);
    }

    [Fact]
    public void SetMode_LogsTheChange()
    {
        var logs = new List<WireLogEntry>();
        var extender = NewExtender(logs, c => c.Mode = ExtenderMode.Observe);

        extender.SetMode(ExtenderMode.Live);

        Assert.Equal(ExtenderMode.Live, extender.Mode);
        var entry = Assert.Single(logs);
        Assert.Equal("mode", entry.Tag);
        Assert.Contains("Live", entry.Message);
    }

    // ---- Actor correlation (live bug 2026-09-25: foreign ActorControl/ActorCast broadcasts
    // in crowded content closed/moved our window mid-cast, killing real casts) ----

    private const uint LocalId = 0xE0000001u;
    private const uint ForeignId = 0xE0000002u;

    [Fact]
    public void Live_ForeignActorControlInterrupt_DoesNotCloseTheWindow()
    {
        var logs = new List<WireLogEntry>();
        var extender = NewExtender(logs, c =>
        {
            c.Mode = ExtenderMode.Live;
            c.InterruptActorControlCategory = 15;
        });
        extender.SetLocalEntityIdProvider(() => LocalId);
        var sink = new FakeFlushSink();
        extender.SetFlushSink(sink);
        EstablishServerPosition(extender, new Position(10f, 0f, 0f), T0 - 10);

        Send(extender, TestPackets.BuildActionRequest(CastAction), T0);
        Send(extender, TestPackets.BuildMove(new Position(13f, 0f, 0f)), T0 + 10);

        // Another actor's cast died nearby (ActorControl is broadcast for every actor in range):
        // must neither close our window nor flush our real position mid-cast.
        extender.OnClientReceived(TestPackets.BuildActorControl(15), ForeignId, T0 + 500);
        Assert.True(extender.Status().WindowActive);
        Assert.Empty(sink.Sent);

        // The broadcast about OUR actor still interrupts.
        extender.OnClientReceived(TestPackets.BuildActorControl(15), LocalId, T0 + 600);
        Assert.False(extender.Status().WindowActive);
        Assert.Single(sink.Sent);
    }

    [Fact]
    public void Live_ActorControlSelfInterrupt_IsHonoredWithoutCorrelation()
    {
        var logs = new List<WireLogEntry>();
        var extender = NewExtender(logs, c =>
        {
            c.Mode = ExtenderMode.Live;
            c.InterruptActorControlCategory = 15;
        });
        extender.SetLocalEntityIdProvider(() => LocalId);
        var sink = new FakeFlushSink();
        extender.SetFlushSink(sink);
        EstablishServerPosition(extender, new Position(10f, 0f, 0f), T0 - 10);

        Send(extender, TestPackets.BuildActionRequest(CastAction), T0);
        Send(extender, TestPackets.BuildMove(new Position(13f, 0f, 0f)), T0 + 10);

        // The Self variant is about us by definition — the targetId is not consulted.
        extender.OnClientReceived(TestPackets.BuildActorControl(15, self: true), 0u, T0 + 500);
        Assert.False(extender.Status().WindowActive);
        Assert.Single(sink.Sent);
    }

    [Fact]
    public void Live_UnknownLocalEntityId_IgnoresBroadcastInterrupts()
    {
        var logs = new List<WireLogEntry>();
        var extender = NewExtender(logs, c =>
        {
            c.Mode = ExtenderMode.Live;
            c.InterruptActorControlCategory = 15;
        });
        extender.SetLocalEntityIdProvider(() => 0u); // e.g. loading screen
        var sink = new FakeFlushSink();
        extender.SetFlushSink(sink);
        EstablishServerPosition(extender, new Position(10f, 0f, 0f), T0 - 10);

        Send(extender, TestPackets.BuildActionRequest(CastAction), T0);
        Send(extender, TestPackets.BuildMove(new Position(12f, 0f, 0f)), T0 + 10);
        extender.OnClientReceived(TestPackets.BuildActorControl(15), ForeignId, T0 + 500);

        Assert.True(extender.Status().WindowActive); // deadline backstop closes it instead
        Assert.Empty(sink.Sent);
        extender.Tick(T0 + 2500);
        Assert.False(extender.Status().WindowActive);
    }

    [Fact]
    public void Live_ForeignActorCast_SameSpell_DoesNotRefineTheWindow()
    {
        // Crowded-content case: another player casting the SAME spell as us. Spell-id matching
        // alone can't tell the packets apart; the entity id can.
        var logs = new List<WireLogEntry>();
        var extender = NewExtender(logs, c => c.Mode = ExtenderMode.Live);
        extender.SetLocalEntityIdProvider(() => LocalId);
        var sink = new FakeFlushSink();
        extender.SetFlushSink(sink);
        EstablishServerPosition(extender, new Position(10f, 0f, 0f), T0 - 10);

        Send(extender, TestPackets.BuildActionRequest(CastAction), T0); // sheet: 2500 ms
        extender.OnClientReceived(TestPackets.BuildActorCast(CastAction, 4.0f), ForeignId, T0 + 50);

        extender.Tick(T0 + 2600); // past the sheet deadline
        Assert.False(extender.Status().WindowActive); // resolved unrefined; foreign cast ignored

        // A refine about us still works.
        Send(extender, TestPackets.BuildActionRequest(CastAction), T0 + 3000);
        extender.OnClientReceived(TestPackets.BuildActorCast(CastAction, 4.0f), LocalId, T0 + 3050);
        extender.Tick(T0 + 5600); // past the sheet deadline, inside the refined window
        Assert.True(extender.Status().WindowActive);
    }
}
