using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using SlidecastExtender.Core;

namespace SlidecastExtender.Tests;

/// <summary>
/// Builds wire-format packets using the same layout constants as <c>WirePackets</c>, so tests
/// track a layout re-confirmation automatically. Send-side packets frame the opcode at
/// <c>SendOpcodeOffset</c>; receive-side at <c>ReceiveOpcodeOffset</c> with the payload after
/// the IPC header.
/// </summary>
internal static class TestPackets
{
    /// <summary>Client→server UpdatePositionHandler ("Move"): opcode + float32 x/y/z.</summary>
    public static byte[] BuildMove(Position pos)
    {
        var data = new byte[WirePackets.MoveCaptureBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(data, (ushort)PacketId.UpdatePositionHandler);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(WirePackets.MoveXOffset), pos.X);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(WirePackets.MoveYOffset), pos.Y);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(WirePackets.MoveZOffset), pos.Z);
        return data;
    }

    /// <summary>Client→server UpdatePositionInstance (Move inside duties): opcode + position +
    /// interpolate position (defaults to the same point, as a real stationary/slow step would).</summary>
    public static byte[] BuildMoveInstance(Position pos, Position? interp = null)
    {
        var i = interp ?? pos;
        var data = new byte[WirePackets.MoveInstanceCaptureBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(data, (ushort)PacketId.UpdatePositionInstance);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(WirePackets.MoveInstanceXOffset), pos.X);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(WirePackets.MoveInstanceYOffset), pos.Y);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(WirePackets.MoveInstanceZOffset), pos.Z);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(WirePackets.MoveInstanceInterpXOffset), i.X);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(WirePackets.MoveInstanceInterpYOffset), i.Y);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(WirePackets.MoveInstanceInterpZOffset), i.Z);
        return data;
    }

    /// <summary>Client→server ActionRequest: opcode + u32 action id.</summary>
    public static byte[] BuildActionRequest(uint actionId, bool groundTargeted = false)
    {
        var data = new byte[WirePackets.ActionRequestCaptureBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(data,
            (ushort)(groundTargeted ? PacketId.ActionRequestGroundTargeted : PacketId.ActionRequest));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(WirePackets.ActionRequestActionIdOffset), actionId);
        return data;
    }

    /// <summary>Server→client ActorCast: opcode @ +2, spell id and cast time in the payload.</summary>
    public static byte[] BuildActorCast(uint spellId, float castTimeSec)
    {
        var data = ReceiveShell(PacketId.ActorCast);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(WirePackets.ActorCastSpellIdOffset), (ushort)spellId);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(WirePackets.ActorCastCastTimeOffset), castTimeSec);
        return data;
    }

    /// <summary>Server→client ActorControl(Self) with a category.</summary>
    public static byte[] BuildActorControl(int category, bool self = false)
    {
        var data = ReceiveShell(self ? PacketId.ActorControlSelf : PacketId.ActorControl);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(WirePackets.ActorControlCategoryOffset), (ushort)category);
        return data;
    }

    /// <summary>Server→client packet carrying only an opcode (enough for receive routing).</summary>
    public static byte[] BuildReceiveRaw(PacketId id) => ReceiveShell(id);

    private static byte[] ReceiveShell(PacketId id)
    {
        var data = new byte[WirePackets.ObserveCaptureBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(WirePackets.ReceiveOpcodeOffset), (ushort)id);
        return data;
    }
}

/// <summary>Captures flush packets the engine emits. Can be told to throw to exercise fail-open paths.</summary>
internal sealed class FakeFlushSink : IFlushSink
{
    public List<byte[]> Sent { get; } = new();
    public int Calls { get; private set; }
    public bool Throw { get; set; }

    public void Send(byte[] packet)
    {
        Calls++;
        if (Throw) throw new InvalidOperationException("sink failure (test)");
        Sent.Add(packet);
    }

    public Position LastPosition => WirePackets.DecodeMovePosition(Sent[^1]);

    public List<Position> Positions => Sent.Select(WirePackets.DecodeMovePosition).ToList();
}

internal static class TestAssert
{
    public static void PositionClose(Position expected, Position actual, float eps = 0.01f) =>
        Xunit.Assert.True(
            expected.DistanceTo(actual) <= eps,
            $"expected {expected}, actual {actual}");

    public static bool HasLog(List<WireLogEntry> logs, string tag) =>
        logs.Any(e => e.Tag == tag);
}
