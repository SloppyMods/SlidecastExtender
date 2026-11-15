using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace SlidecastExtender.Core;

/// <summary>
/// Byte-level adapter between raw plaintext wire buffers and typed values.
///
/// <para>The two hook boundaries frame packets differently (ground truth: Dalamud's own
/// NetworkMonitorWidget detours): the send hook (<c>ZoneClient.SendPacket</c>) sees the opcode
/// at byte 0; the receive hook (<c>PacketDispatcher.OnReceivePacket</c>) sees it at byte 2 with
/// the IPC payload after a 16-byte header.</para>
///
/// <para>Constants marked <c>VERIFY</c> are structural guesses to confirm against an Observe-mode
/// capture (<c>docs/re/capture-checklist.md</c>) before Live mode is trusted. The logic above
/// this layer works on <see cref="Position"/>/floats, so a layout fix is a one-constant change.
/// ActorCast payload offsets are NOT guesses — they come from FFXIVClientStructs'
/// <c>ActorCastPacket</c> struct.</para>
/// </summary>
public static class WirePackets
{
    // ---- Boundary framing ----
    /// <summary>Opcode offset in a packet at the send hook.</summary>
    public const int SendOpcodeOffset = 0;
    /// <summary>Opcode offset in a packet at the receive hook.</summary>
    public const int ReceiveOpcodeOffset = 2;
    /// <summary>IPC header size on the receive path; payload follows. VERIFY.</summary>
    public const int ReceivePayloadOffset = 0x10;

    // ---- UpdatePositionHandler (send) layout ----
    // Confirmed against Observe captures (2026-09-20, two sessions, smooth diagonal walk):
    // rotation f32 @+32, position f32 x/y/z @+40/+44/+48 (+44 constant on flat ground =
    // Y/height; +40/+48 are the horizontal axes). A u32 @+8 looks like a content length.
    public const int MoveRotationOffset = 32; // f32, radians
    public const int MoveXOffset = 40;  // float32 world units
    public const int MoveYOffset = 44;  // float32 — vertical
    public const int MoveZOffset = 48;  // float32
    public const int MoveMinLength = MoveZOffset + 4;

    // ---- UpdatePositionInstance (send) layout — the Move used inside instanced duties ----
    // VERIFY against an Observe capture. Payload (after the 32-byte header, per Sapphire's
    // FFXIVIpcUpdatePositionInstance): rotation f32 @+0, interpolate rotation f32 @+4,
    // flags u32 @+8, position @+12, interpolate position @+24, u32 @+36.
    public const int MoveInstanceXOffset = 44;
    public const int MoveInstanceYOffset = 48;
    public const int MoveInstanceZOffset = 52;
    public const int MoveInstanceInterpXOffset = 56;
    public const int MoveInstanceInterpYOffset = 60;
    public const int MoveInstanceInterpZOffset = 64;
    public const int MoveInstanceMinLength = MoveInstanceInterpZOffset + 4;
    /// <summary>Max distance between position and interpolate position for a decode to be
    /// trusted. A wrong layout guess reads unrelated bytes that won't agree like this.</summary>
    public const float MoveInstanceSanityDistance = 10f;

    // ---- ActionRequest (send) layout ----
    // Confirmed 2026-09-20: u32 action id @+32 (Cure III = 131 observed in packet + game log).
    public const int ActionRequestActionIdOffset = 32; // u32 action id

    // ---- ActorCast (receive) layout — from FFXIVClientStructs ActorCastPacket, capture-confirmed ----
    // The match key for our window is SpellId (payload+0) — it equals the ActionRequest's
    // action id (e.g. Cure III 131). The u32 "ActionId" at payload+4 is a different id space
    // (observed 268 for the same cast) and is NOT used for matching.
    public const int ActorCastSpellIdOffset = ReceivePayloadOffset + 0;   // u16 spell id
    public const int ActorCastCastTimeOffset = ReceivePayloadOffset + 8;  // float, seconds

    // ---- ActorControl / ActorControlSelf (receive) layout — VERIFY ----
    public const int ActorControlCategoryOffset = ReceivePayloadOffset + 0; // VERIFY — u16 category

    // ---- Capture sizes ----
    // Neither hook hands us a packet length, so the engine copies bounded per-id prefixes
    // (enough for every field it reads) and a larger sample in Observe for offset hunting.
    // Reads stay within the packet's real allocation for correctly-IDed packets.
    public const int MoveCaptureBytes = 64;          // covers the real packet + slack; the flush template
    public const int MoveInstanceCaptureBytes = 72;  // 32-byte header + 40-byte payload
    public const int ActionRequestCaptureBytes = ActionRequestActionIdOffset + 4;
    public const int ActorCastCaptureBytes = ActorCastCastTimeOffset + 4;
    public const int ActorControlCaptureBytes = ActorControlCategoryOffset + 2;
    public const int ObserveCaptureBytes = 64;

    /// <summary>Opcode from a packet at the send hook. Returns 0 if too short.</summary>
    public static PacketId SendPacketIdFromBuffer(IntPtr data, uint size)
    {
        if (data == IntPtr.Zero || size < SendOpcodeOffset + 2) return (PacketId)0;
        var head = new byte[SendOpcodeOffset + 2];
        Marshal.Copy(data, head, 0, head.Length);
        return (PacketId)BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(SendOpcodeOffset));
    }

    /// <summary>Both client Move opcodes: overworld (<c>Handler</c>) and instanced duties (<c>Instance</c>).</summary>
    public static bool IsMove(PacketId id) =>
        id is PacketId.UpdatePositionHandler or PacketId.UpdatePositionInstance;

    public static int MoveCaptureBytesFor(PacketId id) =>
        id is PacketId.UpdatePositionInstance ? MoveInstanceCaptureBytes : MoveCaptureBytes;

    /// <summary>Instance Move decode with a sanity check (finite, position ≈ interpolate
    /// position). False → the layout guess doesn't fit this packet; callers pass it through.
    /// Handler packets always decode (their layout is capture-confirmed).</summary>
    public static bool TryDecodeMovePosition(byte[] data, out Position pos)
    {
        pos = default;
        if (data is null || data.Length < 2) return false;
        if (!IsInstanceMove(data))
        {
            if (data.Length < MoveMinLength) return false;
            pos = DecodeMovePosition(data);
            return true;
        }
        if (data.Length < MoveInstanceMinLength) return false;
        pos = ReadPos(data, MoveInstanceXOffset, MoveInstanceYOffset, MoveInstanceZOffset);
        var interp = ReadPos(data, MoveInstanceInterpXOffset, MoveInstanceInterpYOffset, MoveInstanceInterpZOffset);
        return IsFinite(pos) && IsFinite(interp) && pos != Position.Origin &&
               pos.IsCloseTo(interp, MoveInstanceSanityDistance);
    }

    /// <summary>Opcode from a packet at the receive hook. Returns 0 if too short.</summary>
    public static PacketId ReceivePacketIdFromBytes(byte[] data) =>
        data is null || data.Length < ReceiveOpcodeOffset + 2
            ? (PacketId)0
            : (PacketId)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(ReceiveOpcodeOffset));

    /// <summary>Action id from a full ActionRequest send packet. Returns 0 if too short
    /// (a mis-framed packet is a no-op, not a crash).</summary>
    public static uint ReadActionRequestActionId(byte[] data)
    {
        if (data is null || data.Length < ActionRequestActionIdOffset + 4) return 0;
        return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(ActionRequestActionIdOffset));
    }

    /// <summary>Decode the position of a full Move send packet (layout chosen by its opcode).</summary>
    public static Position DecodeMovePosition(byte[] data)
    {
        if (IsInstanceMove(data))
        {
            if (data.Length < MoveInstanceMinLength) throw new InvalidOperationException("Move payload too short");
            return ReadPos(data, MoveInstanceXOffset, MoveInstanceYOffset, MoveInstanceZOffset);
        }
        if (data.Length < MoveMinLength) throw new InvalidOperationException("Move payload too short");
        return ReadPos(data, MoveXOffset, MoveYOffset, MoveZOffset);
    }

    /// <summary>Decode an ActorCast receive packet → (spell id — the match key for the window,
    /// equal to the ActionRequest's action id; cast time in seconds). Returns (0, 0) if too
    /// short.</summary>
    public static (uint SpellId, float CastTimeSec) DecodeActorCast(byte[] data)
    {
        if (data.Length < ActorCastCastTimeOffset + 4) return (0, 0f);
        return (
            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(ActorCastSpellIdOffset)),
            BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(ActorCastCastTimeOffset)));
    }

    /// <summary>Decode the category of an ActorControl/ActorControlSelf receive packet.
    /// Returns -1 if too short.</summary>
    public static int DecodeActorControlCategory(byte[] data)
    {
        if (data.Length < ActorControlCategoryOffset + 2) return -1;
        return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(ActorControlCategoryOffset));
    }

    /// <summary>
    /// Clone <paramref name="template"/> (a full Move packet, either opcode) with the position
    /// bytes replaced by <paramref name="pos"/>. Used to build flush packets byte-compatible
    /// with what the client itself would send.
    /// </summary>
    public static byte[] BuildMovePacket(byte[] template, Position pos)
    {
        if (template is null) throw new ArgumentNullException(nameof(template));
        var min = IsInstanceMove(template) ? MoveInstanceMinLength : MoveMinLength;
        if (template.Length < min) throw new InvalidOperationException("Move template too short");
        var packet = (byte[])template.Clone();
        foreach (var (offset, value) in PositionWrites(IsInstanceMove(packet), pos))
            BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(offset), value);
        return packet;
    }

    /// <summary>
    /// Patch <paramref name="pos"/> into the position bytes of a Move packet (either opcode), in
    /// place. The caller owns <c>data</c> (the client's original buffer, about to be passed to
    /// the original send) — this mutates it just before the original send runs. The client
    /// rebuilds the packet from its own state on the next Move, so the wire-only patch is
    /// self-consistent.
    /// </summary>
    public static void PatchMovePosition(IntPtr data, Position pos)
    {
        if (data == IntPtr.Zero) throw new ArgumentNullException(nameof(data));
        var instance = SendPacketIdFromBuffer(data, 2) is PacketId.UpdatePositionInstance;
        foreach (var (offset, value) in PositionWrites(instance, pos))
            WriteFloat(data, offset, value);
    }

    private static bool IsInstanceMove(byte[] data) =>
        data.Length >= SendOpcodeOffset + 2 &&
        (PacketId)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(SendOpcodeOffset)) is PacketId.UpdatePositionInstance;

    /// <summary>Instance packets carry the position twice (current + interpolate target); both
    /// get the same value so the server sees a stationary player.</summary>
    private static (int Offset, float Value)[] PositionWrites(bool instance, Position pos) => instance
        ? [
            (MoveInstanceXOffset, pos.X), (MoveInstanceYOffset, pos.Y), (MoveInstanceZOffset, pos.Z),
            (MoveInstanceInterpXOffset, pos.X), (MoveInstanceInterpYOffset, pos.Y), (MoveInstanceInterpZOffset, pos.Z),
        ]
        : [(MoveXOffset, pos.X), (MoveYOffset, pos.Y), (MoveZOffset, pos.Z)];

    private static Position ReadPos(byte[] data, int x, int y, int z) => new(
        BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(x)),
        BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(y)),
        BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(z)));

    private static bool IsFinite(Position p) =>
        float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);

    private static void WriteFloat(IntPtr data, int offset, float value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(b, value);
        Marshal.Copy(b, 0, (IntPtr)((ulong)data + (uint)offset), 4);
    }
}
