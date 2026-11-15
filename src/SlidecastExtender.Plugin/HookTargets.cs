using FFXIVClientStructs.FFXIV.Application.Network;
using FFXIVClientStructs.FFXIV.Client.Network;

namespace SlidecastExtender.Plugin;

/// <summary>
/// The two plaintext-boundary hook targets, resolved at runtime through FFXIVClientStructs —
/// which maintains the per-patch signatures itself, so there are no hardcoded addresses and no
/// Ghidra step; a game update that breaks these breaks Dalamud's own network monitor too.
///
/// <para><b>CoreSend</b> = <c>ZoneClient.SendPacket</c>: client→server, plaintext, before
/// encryption. Droppable (skipping the original call means the packet never queues) and
/// forwardable. <b>CoreReceive</b> = <c>PacketDispatcher.OnReceivePacket</c> (vtable slot):
/// server→client, after decryption.</para>
/// </summary>
internal static unsafe class HookTargets
{
    /// <summary>Resolve both targets. Returns false (wire side stays idle, plugin still loads)
    /// if FFXIVClientStructs hasn't resolved them — fail-open.</summary>
    public static bool TryResolve(out nint coreSend, out nint coreReceive)
    {
        coreSend = 0;
        coreReceive = 0;
        try
        {
            coreSend = (nint)ZoneClient.MemberFunctionPointers.SendPacket;
            coreReceive = (nint)PacketDispatcher.StaticVirtualTablePointer->OnReceivePacket;
        }
        catch
        {
            return false;
        }
        return coreSend != 0 && coreReceive != 0;
    }
}
