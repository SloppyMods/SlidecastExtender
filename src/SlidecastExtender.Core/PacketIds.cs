namespace SlidecastExtender.Core;

/// <summary>
/// FFXIV zone IPC opcodes. Source: karashiiro/FFXIVOpcodes, <b>Global 7.58</b> tables
/// (game build 2026.11.03.0000.0000, synced 2026-11-11). Opcodes shift on most patches — re-sync from
/// FFXIVOpcodes after an update, and confirm in Observe mode
/// (<c>docs/re/capture-checklist.md</c>).
///
/// <para>The two hook boundaries read the opcode at different offsets: the send hook
/// (<c>ZoneClient.SendPacket</c>) sees it at byte 0; the receive hook
/// (<c>PacketDispatcher.OnReceivePacket</c>) at byte 2. <c>WirePackets</c> owns those
/// offsets.</para>
/// </summary>
public enum PacketId
{
    // ---- Client → server (send hook) ----
    /// <summary>Position update ("Move") in the open world. The packet this engine suppresses/patches.</summary>
    UpdatePositionHandler = 555,   // 0x022B
    /// <summary>Position update ("Move") inside instanced duties — same role as
    /// <see cref="UpdatePositionHandler"/>, different layout (see <c>WirePackets</c>).</summary>
    UpdatePositionInstance = 938,  // 0x03AA
    /// <summary>Use action (targeted). A cast begins here client-side.</summary>
    ActionRequest = 882,           // 0x0372
    /// <summary>Use action (ground-targeted). Also opens casts.</summary>
    ActionRequestGroundTargeted = 623, // 0x026F

    // ---- Server → client (receive hook) ----
    /// <summary>Cast confirmation: cast time + server-view position. Refines the window.</summary>
    ActorCast = 352,               // 0x0160
    /// <summary>Action effect applied, broadcast for actors in range. NOT reliable as our
    /// completion signal (observed 2026-09-21: the caster's own completion didn't arrive as
    /// Effect; in combat the Effect flood closes windows instantly). Observe/validation only —
    /// the window closes on the cast deadline instead.</summary>
    Effect = 400,                  // 0x0190
    /// <summary>Target-side effect result. Candidate completion signal — unconfirmed.</summary>
    EffectResult = 659,            // 0x0293
    /// <summary>Actor control (others). Carries the cast-interrupt category (VERIFY).</summary>
    ActorControl = 735,            // 0x02DF
    /// <summary>Actor control (self). Carries the cast-interrupt category (VERIFY).</summary>
    ActorControlSelf = 169,        // 0x00A9
}
