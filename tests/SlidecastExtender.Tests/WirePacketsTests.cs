using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SlidecastExtender.Core;
using Xunit;

namespace SlidecastExtender.Tests;

public class WirePacketsTests
{
    [Fact]
    public void SendPacketIdFromBuffer_ReadsOpcodeAtSendOffset()
    {
        var bytes = TestPackets.BuildMove(new Position(1f, 2f, 3f));
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            Assert.Equal(PacketId.UpdatePositionHandler,
                WirePackets.SendPacketIdFromBuffer(handle.AddrOfPinnedObject(), 2));
        }
        finally
        {
            handle.Free();
        }
    }

    [Fact]
    public void SendPacketIdFromBuffer_ZeroPointerOrTiny_ReturnsZero()
    {
        Assert.Equal((PacketId)0, WirePackets.SendPacketIdFromBuffer(IntPtr.Zero, 2));
        Assert.Equal((PacketId)0, WirePackets.SendPacketIdFromBuffer(IntPtr.Zero, 0));
    }

    [Fact]
    public void ReceivePacketIdFromBytes_ReadsOpcodeAtReceiveOffset()
    {
        Assert.Equal(PacketId.ActorCast, WirePackets.ReceivePacketIdFromBytes(TestPackets.BuildReceiveRaw(PacketId.ActorCast)));
        Assert.Equal(PacketId.Effect, WirePackets.ReceivePacketIdFromBytes(TestPackets.BuildReceiveRaw(PacketId.Effect)));
    }

    [Fact]
    public void ReceivePacketIdFromBytes_ShortOrNull_ReturnsZero()
    {
        Assert.Equal((PacketId)0, WirePackets.ReceivePacketIdFromBytes(Array.Empty<byte>()));
        Assert.Equal((PacketId)0, WirePackets.ReceivePacketIdFromBytes(new byte[] { 0x00, 0x30 }));
        Assert.Equal((PacketId)0, WirePackets.ReceivePacketIdFromBytes(null!));
    }

    [Fact]
    public void ReadActionRequestActionId_ReadsPayload()
    {
        Assert.Equal(4242u, WirePackets.ReadActionRequestActionId(TestPackets.BuildActionRequest(4242)));
        Assert.Equal(4242u, WirePackets.ReadActionRequestActionId(TestPackets.BuildActionRequest(4242, groundTargeted: true)));
    }

    [Fact]
    public void ReadActionRequestActionId_TooShort_ReturnsZero()
    {
        Assert.Equal(0u, WirePackets.ReadActionRequestActionId(new byte[WirePackets.ActionRequestActionIdOffset]));
        Assert.Equal(0u, WirePackets.ReadActionRequestActionId(null!));
    }

    [Fact]
    public void DecodeMovePosition_RoundTrips()
    {
        var expected = new Position(2.5f, -4.25f, 100.125f);
        Assert.Equal(expected, WirePackets.DecodeMovePosition(TestPackets.BuildMove(expected)));
    }

    [Fact]
    public void DecodeMovePosition_InstanceLayout_RoundTrips()
    {
        var expected = new Position(-60.25f, 8.5f, 36.75f);
        Assert.Equal(expected, WirePackets.DecodeMovePosition(TestPackets.BuildMoveInstance(expected)));
        Assert.True(WirePackets.TryDecodeMovePosition(TestPackets.BuildMoveInstance(expected), out var pos));
        Assert.Equal(expected, pos);
    }

    [Fact]
    public void TryDecodeMovePosition_InstanceLayout_RejectsImplausibleDecodes()
    {
        var p = new Position(10f, 0f, 10f);
        Assert.False(WirePackets.TryDecodeMovePosition(TestPackets.BuildMoveInstance(p, new Position(500f, 0f, 10f)), out _));
        Assert.False(WirePackets.TryDecodeMovePosition(TestPackets.BuildMoveInstance(new Position(float.NaN, 0f, 0f)), out _));
        Assert.False(WirePackets.TryDecodeMovePosition(TestPackets.BuildMoveInstance(Position.Origin), out _));
    }

    [Fact]
    public void BuildMovePacket_InstanceTemplate_WritesBothPositions()
    {
        var template = TestPackets.BuildMoveInstance(new Position(1f, 2f, 3f), new Position(1.5f, 2f, 3f));
        var target = new Position(7f, 8f, 9f);
        var packet = WirePackets.BuildMovePacket(template, target);
        Assert.Equal(PacketId.UpdatePositionInstance, (PacketId)BinaryPrimitives.ReadUInt16LittleEndian(packet));
        Assert.Equal(target, WirePackets.DecodeMovePosition(packet));
        Assert.Equal(target.X, BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(WirePackets.MoveInstanceInterpXOffset)));
    }

    [Fact]
    public void DecodeMovePosition_TooShort_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            WirePackets.DecodeMovePosition(new byte[WirePackets.MoveMinLength - 1]));
    }

    [Fact]
    public void DecodeActorCast_ReadsActionIdAndCastTime()
    {
        var (actionId, castTimeSec) = WirePackets.DecodeActorCast(TestPackets.BuildActorCast(155, 2.5f));
        Assert.Equal(155u, actionId);
        Assert.Equal(2.5f, castTimeSec);
    }

    [Fact]
    public void DecodeActorCast_TooShort_ReturnsZero()
    {
        Assert.Equal((0u, 0f), WirePackets.DecodeActorCast(new byte[WirePackets.ActorCastCastTimeOffset]));
    }

    [Fact]
    public void DecodeActorControlCategory_ReadsCategory()
    {
        Assert.Equal(15, WirePackets.DecodeActorControlCategory(TestPackets.BuildActorControl(15)));
        Assert.Equal(15, WirePackets.DecodeActorControlCategory(TestPackets.BuildActorControl(15, self: true)));
    }

    [Fact]
    public void DecodeActorControlCategory_TooShort_ReturnsMinusOne()
    {
        Assert.Equal(-1, WirePackets.DecodeActorControlCategory(new byte[2]));
    }

    [Fact]
    public void BuildMovePacket_PatchesPositionAndPreservesEverythingElse()
    {
        var template = TestPackets.BuildMove(new Position(1f, 1f, 1f));
        template[9] = 0xAA; // sentinel outside the position fields
        var original = (byte[])template.Clone();

        var target = new Position(9.5f, -8.25f, 7f);
        var packet = WirePackets.BuildMovePacket(template, target);

        Assert.Equal(target, WirePackets.DecodeMovePosition(packet));
        Assert.Equal(original, template); // template untouched (clone, not in-place)
        Assert.Equal(0xAA, packet[9]);    // unrelated bytes preserved
        Assert.Equal(original[0], packet[0]); // opcode preserved
    }

    [Fact]
    public void BuildMovePacket_NullOrShortTemplate_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => WirePackets.BuildMovePacket(null!, new Position()));
        Assert.Throws<InvalidOperationException>(() =>
            WirePackets.BuildMovePacket(new byte[WirePackets.MoveMinLength - 1], new Position()));
    }

    [Fact]
    public void PatchMovePosition_WritesInPlaceOnTheOriginalBuffer()
    {
        var bytes = TestPackets.BuildMove(new Position(1f, 1f, 1f));
        var original0 = bytes[0];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var target = new Position(-3.5f, 0.25f, 42f);
            WirePackets.PatchMovePosition(handle.AddrOfPinnedObject(), target);

            Assert.Equal(target, WirePackets.DecodeMovePosition(bytes));
            Assert.Equal(original0, bytes[0]); // opcode untouched
        }
        finally
        {
            handle.Free();
        }
    }

    [Fact]
    public void PatchMovePosition_ZeroPointer_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => WirePackets.PatchMovePosition(IntPtr.Zero, new Position()));
    }
}
