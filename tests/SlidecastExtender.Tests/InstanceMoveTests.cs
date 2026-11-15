using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SlidecastExtender.Core;
using Xunit;

namespace SlidecastExtender.Tests;

/// <summary>Inside instanced duties the client sends Move as <c>UpdatePositionInstance</c>, not
/// <c>UpdatePositionHandler</c>; the engine must gate both.</summary>
public class InstanceMoveTests
{
    private const long T0 = 100_000;
    private const uint CastAction = 100;

    private static WireExtender NewLiveExtender(List<WireLogEntry> logs)
    {
        var extender = new WireExtender(new SlidecastConfig { Mode = ExtenderMode.Live }, logs.Add);
        extender.SetCastTimeProvider(id => id == CastAction ? 2500L : 0L);
        return extender;
    }

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

    [Fact]
    public void Live_InstanceMoves_AreSuppressedAndFlushedLikeOverworldMoves()
    {
        var logs = new List<WireLogEntry>();
        var extender = NewLiveExtender(logs);
        var sink = new FakeFlushSink();
        extender.SetFlushSink(sink);
        Assert.True(Send(extender, TestPackets.BuildMoveInstance(new Position(10f, 0f, 10f)), T0 - 10));

        Send(extender, TestPackets.BuildActionRequest(CastAction), T0);
        var real = new Position(13f, 0f, 10f);
        Assert.False(Send(extender, TestPackets.BuildMoveInstance(real), T0 + 10));
        Assert.Equal(1, extender.Status().SuppressedMoves);

        extender.Tick(T0 + 2500);
        var packet = Assert.Single(sink.Sent);
        Assert.Equal(PacketId.UpdatePositionInstance, (PacketId)BitConverter.ToUInt16(packet, 0));
        Assert.Equal(real, WirePackets.DecodeMovePosition(packet));
    }

    [Fact]
    public void Live_ImplausibleInstanceMove_FailsOpen()
    {
        var logs = new List<WireLogEntry>();
        var extender = NewLiveExtender(logs);
        Send(extender, TestPackets.BuildActionRequest(CastAction), T0);

        var bad = TestPackets.BuildMoveInstance(new Position(10f, 0f, 10f), new Position(900f, 0f, 10f));
        Assert.True(Send(extender, bad, T0 + 10));
        Assert.True(Send(extender, bad, T0 + 20));
        Assert.Equal(0, extender.Status().SuppressedMoves);
        Assert.Single(logs, e => e.Tag == "move-layout");
    }
}
