using System;
using System.Collections.Generic;
using System.Linq;
using SlidecastExtender.Core;
using Xunit;

namespace SlidecastExtender.Tests;

public class CastWindowTrackerTests
{
    private const long T0 = 100_000;
    private static readonly Position StandingAt = new(10f, 0f, 0f);

    private static CastWindowTracker NewTracker(
        out FakeFlushSink sink,
        List<WireLogEntry>? logs = null,
        Action<SlidecastConfig>? configure = null)
    {
        var cfg = new SlidecastConfig();
        configure?.Invoke(cfg);
        sink = new FakeFlushSink();
        return new CastWindowTracker(cfg, sink, (logs ?? new List<WireLogEntry>()).Add);
    }

    /// <summary>A pass-through move outside any window: the position the server currently
    /// believes, which the next cast freezes.</summary>
    private static void EstablishServerPosition(CastWindowTracker tracker, Position pos, long tsMs) =>
        tracker.HandleMove(TestPackets.BuildMove(pos), pos, 0, tsMs);

    [Fact]
    public void StartCast_OpensWindow_FrozenAtLastSentPosition()
    {
        var tracker = NewTracker(out _);
        EstablishServerPosition(tracker, StandingAt, T0 - 10);
        tracker.StartCast(100, 2500, T0);

        Assert.True(tracker.WindowActive);
        Assert.Equal(TrackerState.Casting, tracker.State);
        Assert.Equal(100u, tracker.CastId);
        Assert.Equal(StandingAt, tracker.FrozenPosition);
        Assert.Equal(2500, tracker.RemainingMsAt(T0));
        Assert.Equal(1000, tracker.RemainingMsAt(T0 + 1500));
        Assert.Equal(0, tracker.RemainingMsAt(T0 + 9999));
    }

    [Fact]
    public void StartCast_WithoutAnyPriorMove_FrozenAtOriginAndWarns()
    {
        var logs = new List<WireLogEntry>();
        var tracker = NewTracker(out _, logs);
        tracker.StartCast(100, 2500, T0);

        Assert.True(tracker.WindowActive);
        Assert.Equal(Position.Origin, tracker.FrozenPosition);
        Assert.Contains(logs, e => e.Tag == "cast-start" && e.Sev == LogSeverity.Warn);
    }

    [Fact]
    public void StartCast_ZeroDuration_DoesNotOpenWindow()
    {
        var logs = new List<WireLogEntry>();
        var tracker = NewTracker(out _, logs);
        EstablishServerPosition(tracker, StandingAt, T0 - 10);
        tracker.StartCast(100, 0, T0);

        Assert.False(tracker.WindowActive);
        Assert.Null(tracker.CastId);

        // A following Move is outside any window: pass through, untouched.
        var (keep, patchTo) = tracker.HandleMove(TestPackets.BuildMove(new Position(5f, 0f, 0f)), new Position(5f, 0f, 0f), 0, T0 + 1);
        Assert.True(keep);
        Assert.Null(patchTo);
    }

    [Fact]
    public void StartCast_WithOpenWindow_FlushesPreviousFirst()
    {
        var logs = new List<WireLogEntry>();
        var tracker = NewTracker(out var sink, logs);
        EstablishServerPosition(tracker, StandingAt, T0 - 10);
        tracker.StartCast(100, 2500, T0);
        var real = new Position(13f, 0f, 0f);
        tracker.HandleMove(TestPackets.BuildMove(real), real, 0, T0 + 10);

        // Chain-cast before the first window's deadline: the server converges before the new freeze.
        tracker.StartCast(200, 4000, T0 + 2000);

        var packet = Assert.Single(sink.Sent);
        Assert.Equal(real, WirePackets.DecodeMovePosition(packet));
        Assert.Equal(200u, tracker.CastId);
        Assert.True(tracker.WindowActive);
        Assert.True(TestAssert.HasLog(logs, "cast-restart"));
        // …and the new window froze what the server believed after the flush (the real position).
        Assert.Equal(real, tracker.FrozenPosition);
    }

    [Fact]
    public void RefineCast_UpdatesDeadlineFromServerCastTime()
    {
        var tracker = NewTracker(out _);
        EstablishServerPosition(tracker, StandingAt, T0 - 10);
        tracker.StartCast(100, 2500, T0);

        tracker.RefineCast(100, 4.0f, T0 + 50); // server says 4 s, sheet said 2.5 s

        Assert.Equal(4000, tracker.RemainingMsAt(T0 + 50));
        // …and the timeout backstop follows the refined deadline (+ default 2000 ms grace).
        tracker.MaybeTimeout(T0 + 50 + 4000 + 2000 - 1);
        Assert.True(tracker.WindowActive);
    }

    [Fact]
    public void RefineCast_WithoutWindow_IsIgnored()
    {
        var tracker = NewTracker(out _);
        tracker.RefineCast(100, 4.0f, T0);
        Assert.False(tracker.WindowActive);
    }

    [Fact]
    public void RefineCast_FromAnotherActor_DoesNotMoveTheDeadline()
    {
        var logs = new List<WireLogEntry>();
        var tracker = NewTracker(out _, logs);
        EstablishServerPosition(tracker, StandingAt, T0 - 10);
        tracker.StartCast(100, 2500, T0);

        // ActorCast is broadcast for every actor in range — someone else's cast must not
        // refine ours (observed live: a mismatched ActorCast moved our deadline).
        tracker.RefineCast(826, 1.05f, T0 + 50);

        Assert.Equal(2500, tracker.RemainingMsAt(T0)); // unchanged
        Assert.Contains(logs, e => e.Tag == "cast-refine" && e.Sev == LogSeverity.Debug);
    }

    [Fact]
    public void MaybeResolve_BeforeCastEnd_KeepsWindow()
    {
        var tracker = NewTracker(out var sink);
        EstablishServerPosition(tracker, StandingAt, T0 - 10);
        tracker.StartCast(100, 2500, T0);

        tracker.MaybeResolve(T0 + 2499);

        Assert.True(tracker.WindowActive);
        Assert.Empty(sink.Sent);
    }

    [Fact]
    public void MaybeResolve_AtCastEnd_FlushesAndCloses()
    {
        var logs = new List<WireLogEntry>();
        var tracker = NewTracker(out var sink, logs);
        EstablishServerPosition(tracker, StandingAt, T0 - 10);
        tracker.StartCast(100, 2500, T0);
        var real = new Position(12f, 0f, 0f);
        tracker.HandleMove(TestPackets.BuildMove(real), real, 0, T0 + 10);

        tracker.MaybeResolve(T0 + 2500); // exactly at cast end

        var packet = Assert.Single(sink.Sent);
        Assert.Equal(real, WirePackets.DecodeMovePosition(packet));
        Assert.False(tracker.WindowActive);
        Assert.True(TestAssert.HasLog(logs, "cast-resolve"));
    }

    [Fact]
    public void MaybeResolve_ZeroDrift_SkipsFlush()
    {
        var tracker = NewTracker(out var sink);
        EstablishServerPosition(tracker, StandingAt, T0 - 10);
        tracker.StartCast(100, 2500, T0);

        tracker.MaybeResolve(T0 + 2500);

        Assert.Empty(sink.Sent);
        Assert.False(tracker.WindowActive);
    }

    [Fact]
    public void HandleMove_WithoutWindow_PassesThroughAndTracksServerPosition()
    {
        var tracker = NewTracker(out _);
        var pos = new Position(1f, 0f, 0f);
        var (keep, patchTo) = tracker.HandleMove(TestPackets.BuildMove(pos), pos, 0, T0);
        Assert.True(keep);
        Assert.Null(patchTo);
        Assert.Equal(0, tracker.SuppressedMoves);

        tracker.StartCast(100, 2500, T0 + 10);
        Assert.Equal(pos, tracker.FrozenPosition);
    }

    [Fact]
    public void HandleMove_DropPolicy_DropsAndRecordsRealPosition()
    {
        var tracker = NewTracker(out _, configure: c => c.Suppress = SuppressionPolicy.Drop);
        EstablishServerPosition(tracker, StandingAt, T0 - 10);
        tracker.StartCast(100, 2500, T0);

        var real = new Position(12.5f, 0f, 1f);
        var (keep, patchTo) = tracker.HandleMove(TestPackets.BuildMove(real), real, 0, T0 + 10);

        Assert.False(keep);          // hook must NOT call the original send
        Assert.Null(patchTo);        // nothing to patch — the packet is dropped
        Assert.Equal(1, tracker.SuppressedMoves);
        Assert.Equal(real, tracker.LastRealPosition);
        Assert.Equal(StandingAt, tracker.FrozenPosition); // frozen does not follow the real position
    }

    [Fact]
    public void HandleMove_ReplayFrozenPolicy_KeepsAndPatchesToFrozen()
    {
        var tracker = NewTracker(out _, configure: c => c.Suppress = SuppressionPolicy.ReplayFrozen);
        EstablishServerPosition(tracker, StandingAt, T0 - 10);
        tracker.StartCast(100, 2500, T0);

        var real = new Position(14f, 0f, 0f);
        var (keep, patchTo) = tracker.HandleMove(TestPackets.BuildMove(real), real, 0, T0 + 10);

        Assert.True(keep);                     // packet goes out…
        Assert.Equal(StandingAt, patchTo);     // …with x/y/z rewritten to frozen
        Assert.Equal(real, tracker.LastRealPosition);
    }

    [Fact]
    public void HandleMove_LogsEveryNthSuppressedMove()
    {
        var logs = new List<WireLogEntry>();
        var tracker = NewTracker(out _, logs, c => c.LogEveryNthSuppressedMove = 3);
        EstablishServerPosition(tracker, StandingAt, T0 - 10);
        tracker.StartCast(100, 2500, T0);

        for (var i = 1; i <= 3; i++)
            tracker.HandleMove(TestPackets.BuildMove(new Position(i, 0f, 0f)), new Position(i, 0f, 0f), 0, T0 + i);

        Assert.Equal(3, tracker.SuppressedMoves);
        Assert.Single(logs.Where(e => e.Tag == "move-suppressed"));
    }

    [Fact]
    public void OnCastFinish_ImmediateFlush_SendsOnePacketWithLastRealPosition()
    {
        var logs = new List<WireLogEntry>();
        var tracker = NewTracker(out var sink, logs);
        EstablishServerPosition(tracker, StandingAt, T0 - 10);
        tracker.StartCast(100, 2500, T0);

        tracker.HandleMove(TestPackets.BuildMove(new Position(11f, 0f, 0f)), new Position(11f, 0f, 0f), 0, T0 + 10);
        var real = new Position(15f, 0f, -2f);
        tracker.HandleMove(TestPackets.BuildMove(real), real, 0, T0 + 20);

        tracker.OnCastFinish(T0 + 2500);

        var packet = Assert.Single(sink.Sent);
        Assert.Equal(real, WirePackets.DecodeMovePosition(packet));
        Assert.Equal(1, tracker.FlushedPackets);
        Assert.False(tracker.WindowActive);
        Assert.True(TestAssert.HasLog(logs, "cast-finish"));
        Assert.True(TestAssert.HasLog(logs, "flush"));
    }

    [Fact]
    public void OnCastFinish_ZeroDrift_SkipsFlush()
    {
        var logs = new List<WireLogEntry>();
        var tracker = NewTracker(out var sink, logs);
        EstablishServerPosition(tracker, StandingAt, T0 - 10);
        tracker.StartCast(100, 2500, T0);

        // Moved, but ended back essentially on the frozen spot (< 0.01 u drift).
        tracker.HandleMove(TestPackets.BuildMove(new Position(10.001f, 0f, 0f)), new Position(10.001f, 0f, 0f), 0, T0 + 10);

        tracker.OnCastFinish(T0 + 2500);

        Assert.Empty(sink.Sent);
        Assert.Equal(0, tracker.FlushedPackets);
        Assert.False(tracker.WindowActive);
        Assert.True(TestAssert.HasLog(logs, "flush-no-drift"));
    }

    [Fact]
    public void OnCastFinish_WithoutWindow_IsANoOp()
    {
        var tracker = NewTracker(out var sink);
        tracker.OnCastFinish(T0);
        Assert.Empty(sink.Sent);
        Assert.False(tracker.WindowActive);
    }

    [Fact]
    public void OnCastInterrupt_FailOpen_FlushesAndCloses()
    {
        var logs = new List<WireLogEntry>();
        var tracker = NewTracker(out var sink, logs);
        EstablishServerPosition(tracker, StandingAt, T0 - 10);
        tracker.StartCast(100, 2500, T0);
        var real = new Position(13f, 0f, 0f);
        tracker.HandleMove(TestPackets.BuildMove(real), real, 0, T0 + 10);

        tracker.OnCastInterrupt(T0 + 500);

        var packet = Assert.Single(sink.Sent);
        Assert.Equal(real, WirePackets.DecodeMovePosition(packet));
        Assert.False(tracker.WindowActive);
        Assert.True(TestAssert.HasLog(logs, "cast-interrupt"));
    }

    [Fact]
    public void OnCastInterrupt_WithoutWindow_LogsAndIgnores()
    {
        var logs = new List<WireLogEntry>();
        var tracker = NewTracker(out var sink, logs);
        tracker.OnCastInterrupt(T0);

        Assert.Empty(sink.Sent);
        Assert.True(TestAssert.HasLog(logs, "cast-interrupt"));
    }

    [Fact]
    public void MaybeTimeout_BeforeDeadline_KeepsWindow()
    {
        var tracker = NewTracker(out var sink, configure: c => c.GracePeriodMs = 500);
        EstablishServerPosition(tracker, StandingAt, T0 - 10);
        tracker.StartCast(100, 1000, T0); // deadline = T0 + 1500

        tracker.MaybeTimeout(T0 + 1499);

        Assert.True(tracker.WindowActive);
        Assert.Empty(sink.Sent);
    }

    [Fact]
    public void MaybeTimeout_AtDeadline_FailOpenFlush()
    {
        var logs = new List<WireLogEntry>();
        var tracker = NewTracker(out var sink, logs, c => c.GracePeriodMs = 500);
        EstablishServerPosition(tracker, StandingAt, T0 - 10);
        tracker.StartCast(100, 1000, T0);
        var real = new Position(12f, 0f, 0f);
        tracker.HandleMove(TestPackets.BuildMove(real), real, 0, T0 + 10);

        tracker.MaybeTimeout(T0 + 1500); // exactly at deadline

        var packet = Assert.Single(sink.Sent);
        Assert.Equal(real, WirePackets.DecodeMovePosition(packet));
        Assert.False(tracker.WindowActive);
        Assert.True(TestAssert.HasLog(logs, "cast-timeout"));
    }

    [Fact]
    public void StagedFlush_EmitsOneStepPerInterval()
    {
        var logs = new List<WireLogEntry>();
        var tracker = NewTracker(out var sink, logs, c =>
        {
            c.Flush = FlushMode.Staged;
            c.StagedSteps = 4;
            c.StagedMaxStepUnits = 16f;
            c.StagedStepIntervalMs = 100;
            c.StagedFlushTimeoutMs = 10_000;
        });
        EstablishServerPosition(tracker, Position.Origin, T0 - 10);
        tracker.StartCast(100, 2500, T0);
        var real = new Position(4f, 0f, 0f);
        tracker.HandleMove(TestPackets.BuildMove(real), real, 0, T0 + 10);

        tracker.OnCastFinish(T0 + 2500); // step 1 of 4 emits immediately

        Assert.Equal(TrackerState.Flushing, tracker.State);
        Assert.True(tracker.WindowActive);
        TestAssert.PositionClose(new Position(1f, 0f, 0f), sink.LastPosition);

        tracker.TickFlush(T0 + 2500 + 50); // interval not elapsed
        Assert.Single(sink.Sent);

        tracker.TickFlush(T0 + 2500 + 100);
        Assert.Equal(2, sink.Sent.Count);
        TestAssert.PositionClose(new Position(2f, 0f, 0f), sink.LastPosition);

        tracker.TickFlush(T0 + 2500 + 200);
        TestAssert.PositionClose(new Position(3f, 0f, 0f), sink.LastPosition);

        tracker.TickFlush(T0 + 2500 + 300); // final step lands exactly on the real position
        Assert.Equal(4, sink.Sent.Count);
        TestAssert.PositionClose(real, sink.LastPosition);

        Assert.Equal(TrackerState.Idle, tracker.State);
        Assert.False(tracker.WindowActive);
        Assert.True(TestAssert.HasLog(logs, "staged-flush-done"));
    }

    [Fact]
    public void StagedFlush_ClampsStepSizeToMaxStep()
    {
        var tracker = NewTracker(out var sink, configure: c =>
        {
            c.Flush = FlushMode.Staged;
            c.StagedSteps = 4;          // asked for 4…
            c.StagedMaxStepUnits = 16f; // …but 100 u away needs ceil(100/16) = 7 steps
            c.StagedStepIntervalMs = 10;
            c.StagedFlushTimeoutMs = 10_000;
        });
        EstablishServerPosition(tracker, Position.Origin, T0 - 10);
        tracker.StartCast(100, 2500, T0);
        var real = new Position(100f, 0f, 0f);
        tracker.HandleMove(TestPackets.BuildMove(real), real, 0, T0 + 10);

        tracker.OnCastFinish(T0 + 2500);
        for (var i = 1; i < 16; i++)
            tracker.TickFlush(T0 + 2500 + i * 10);

        var positions = sink.Positions;
        Assert.Equal(7, positions.Count); // clamped up from the requested 4
        for (var i = 1; i < positions.Count; i++)
            Assert.True(positions[i - 1].DistanceTo(positions[i]) <= 16.001f,
                $"step {i} moved {positions[i - 1].DistanceTo(positions[i])} u");
        TestAssert.PositionClose(real, positions[^1]); // last step lands exactly
        Assert.False(tracker.WindowActive);
    }

    [Fact]
    public void StagedFlush_PastTimeout_BurstsRemainingSteps()
    {
        var logs = new List<WireLogEntry>();
        var tracker = NewTracker(out var sink, logs, c =>
        {
            c.Flush = FlushMode.Staged;
            c.StagedSteps = 4;
            c.StagedMaxStepUnits = 16f;
            c.StagedStepIntervalMs = 10_000; // interval so long only the timeout can advance it
            c.StagedFlushTimeoutMs = 500;
        });
        EstablishServerPosition(tracker, Position.Origin, T0 - 10);
        tracker.StartCast(100, 2500, T0);
        var real = new Position(4f, 0f, 0f);
        tracker.HandleMove(TestPackets.BuildMove(real), real, 0, T0 + 10);

        tracker.OnCastFinish(T0 + 2500); // step 1 emits
        Assert.Single(sink.Sent);

        tracker.TickFlush(T0 + 2500 + 499); // before the staged deadline
        Assert.Single(sink.Sent);

        tracker.TickFlush(T0 + 2500 + 500); // at the deadline: burst the remaining 3
        Assert.Equal(4, sink.Sent.Count);
        TestAssert.PositionClose(real, sink.LastPosition);
        Assert.False(tracker.WindowActive);
        Assert.True(TestAssert.HasLog(logs, "staged-flush-done"));
    }

    [Fact]
    public void StagedFlush_SinkThrows_AbandonsFlushFailOpen()
    {
        var logs = new List<WireLogEntry>();
        var tracker = NewTracker(out var sink, logs, c =>
        {
            c.Flush = FlushMode.Staged;
            c.StagedSteps = 4;
            c.StagedStepIntervalMs = 100;
        });
        EstablishServerPosition(tracker, Position.Origin, T0 - 10);
        tracker.StartCast(100, 2500, T0);
        tracker.HandleMove(TestPackets.BuildMove(new Position(4f, 0f, 0f)), new Position(4f, 0f, 0f), 0, T0 + 10);

        sink.Throw = true;
        tracker.OnCastFinish(T0 + 2500); // first staged step throws inside the sink

        Assert.True(TestAssert.HasLog(logs, "flush-send-failed"));

        tracker.TickFlush(T0 + 2500 + 100); // fail-open: abandon, do not keep retrying

        Assert.Equal(1, sink.Calls); // exactly one attempt, never retried
        Assert.False(tracker.WindowActive);
        Assert.True(TestAssert.HasLog(logs, "staged-flush-aborted"));
    }

    [Fact]
    public void ImmediateFlush_SinkThrows_ClosesWindowAndLogs()
    {
        var logs = new List<WireLogEntry>();
        var tracker = NewTracker(out var sink, logs);
        EstablishServerPosition(tracker, StandingAt, T0 - 10);
        tracker.StartCast(100, 2500, T0);
        tracker.HandleMove(TestPackets.BuildMove(new Position(12f, 0f, 0f)), new Position(12f, 0f, 0f), 0, T0 + 10);

        sink.Throw = true;
        tracker.OnCastFinish(T0 + 2500);

        Assert.Equal(1, sink.Calls);
        Assert.Empty(sink.Sent);
        Assert.Equal(0, tracker.FlushedPackets);
        Assert.False(tracker.WindowActive); // fail-open: never stuck in a window
        Assert.True(TestAssert.HasLog(logs, "flush-send-failed"));
    }

    [Fact]
    public void Finish_WithoutSink_LogsNotReadyAndCloses()
    {
        var logs = new List<WireLogEntry>();
        var tracker = new CastWindowTracker(new SlidecastConfig(), null, logs.Add); // no sink wired
        EstablishServerPosition(tracker, StandingAt, T0 - 10);
        tracker.StartCast(100, 2500, T0);
        tracker.HandleMove(TestPackets.BuildMove(new Position(12f, 0f, 0f)), new Position(12f, 0f, 0f), 0, T0 + 10);

        tracker.OnCastFinish(T0 + 2500);

        Assert.False(tracker.WindowActive);
        Assert.True(TestAssert.HasLog(logs, "flush-sink-not-ready"));
    }

    [Fact]
    public void RemainingMsAt_IsNullWhenIdle()
    {
        var tracker = NewTracker(out _);
        Assert.Null(tracker.RemainingMsAt(T0));
    }
}
