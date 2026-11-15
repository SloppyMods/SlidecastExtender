# Hook targets — RESOLVED (no Ghidra needed)

**Status: solved at runtime.** The two plaintext-boundary functions are the same ones Dalamud's
own Network Monitor hooks, resolved through FFXIVClientStructs' per-patch signature data — so
there are no hardcoded addresses and nothing to re-find after a game update. See
`src/SlidecastExtender.Plugin/HookTargets.cs`.

| Target | Function | Boundary | Signature |
|---|---|---|---|
| CoreSend | `ZoneClient.SendPacket` (`ZoneClient.MemberFunctionPointers.SendPacket`) | client→server, plaintext, before encryption | `bool SendPacket(ZoneClient* self, nint packet, uint a3, uint a4, bool a5)` — opcode at `packet+0` |
| CoreReceive | `PacketDispatcher.OnReceivePacket` (`PacketDispatcher.StaticVirtualTablePointer->OnReceivePacket`) | server→client, after decryption | `void OnReceivePacket(PacketDispatcher* self, uint targetId, nint packet)` — opcode at `packet+2` |

Ground truth: `Dalamud/Interface/Internal/Windows/Data/Widgets/NetworkMonitorWidget.cs` hooks
exactly these and reads opcodes from the buffers — opcodes are only readable in plaintext, so
both are at the plaintext boundary by construction.

## Still to confirm at runtime (first Live run)

- **Drop semantics on send.** The hook drops a packet by returning `true` (pretend success)
  without calling the original. Watch for the caller retrying or erroring.
- **What `a3`/`a4`/`a5` are.** Possibly a length and flags; the flush reuses the values captured
  from the last Move. Observe logs will show whether they vary.
- **Send-side packet length.** Neither hook carries an explicit length; the engine copies
  bounded prefixes (`WirePackets.*CaptureBytes`) and the flush template is a 64-byte capture.
  If a real `UpdatePositionHandler` is longer than that, the flush packet is a prefix + slack —
  watch for malformed-packet symptoms (disconnects, server complaints) on the first flush.

## If a game patch breaks these

It breaks Dalamud's Network Monitor too, and FFXIVClientStructs updates the signatures — the
plugin picks the fix up with the Dalamud/CS update. No action needed here.
