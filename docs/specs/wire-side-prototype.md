# Spec: M0 wire-side prototype

**Status:** implemented + unit-tested (58 green); installed to devPlugins; awaiting the first
in-game Observe run.
**Date:** 2026-09-20 (revised for the real zone protocol — opcodes FFXIVOpcodes Global 7.56h)

## Goal

Validate the wire half of slidecast extension: during a cast, keep the server's belief that the
character is stationary so its cast timer completes, then converge the server's view with a
position flush on resolve.

Explicit non-goal for M0: the local cast bar. The client cancels its own bar on movement before
any server traffic, so M0 produces "character slides, effect fires, bar dies early" — expected,
documented, fixed in M1 (`docs/re/local-side.md`).

## Components

| Piece | File(s) | Responsibility |
|---|---|---|
| Packet tables | `Core/PacketIds.cs`, `Core/WirePackets.cs` | Real 7.56h opcodes; layouts marked `VERIFY` where not FFXIVClientStructs-sourced. |
| Window state machine | `Core/CastWindowTracker.cs` | Cast window, Move gate, finish/interrupt/timeout flush, staged flush. |
| Facade | `Core/WireExtender.cs` | Mode routing (Off/Observe/Live), lock serialization, status. |
| Flush delivery | `Core/IFlushSink.cs` → `Plugin/PacketSink.cs` | Engine-built packet through the *original* `ZoneClient.SendPacket`. |
| Hooks | `Plugin/PacketSink.cs` + `Plugin/HookTargets.cs` | `IGameInteropProvider.HookFromAddress` at `ZoneClient.SendPacket` / `PacketDispatcher.OnReceivePacket`, resolved via FFXIVClientStructs at runtime (no hardcoded addresses). |
| Cast-time lookup | `Plugin/SlidecastExtender.cs` | `IDataManager` Action sheet → `Cast100ms`; injected into core as a delegate. |
| Entry | `Plugin/SlidecastExtender.cs` | `/slidecast` commands, 250 ms backstop timer, wiring. |
| Logging | `Plugin/LogSink.cs` | `logs/wire-*.log` next to the plugin DLL. |

## Behavior

### Modes

- **Off** — hooks pass everything through; zero work, zero logging.
- **Observe** — every packet logged (`observe`, throttled by `ObserveLogEveryNth`); Cast/Move
  detail lines (`observe-cast`, `observe-move`) when logging every packet. Nothing is dropped,
  patched, or flushed. This is the first in-game run.
- **Live** — full logic below.

### Live sequence (real zone protocol)

1. Send `ActionRequest` / `ActionRequestGroundTargeted`: the plugin's cast-time provider
   (Lumina Action sheet, `Cast100ms`) decides — cast time > 0 → open the window; 0 (instant)
   → no window, nothing is suppressed. The frozen position is the last Move the engine let
   through to the server (what the server currently believes).
2. Receive `ActorCast` (server confirm): refine the deadline with the authoritative cast time
   (spell-speed/latency-adjusted; the sheet value is the base). Broadcast per-actor like
   `ActorControl` — honored only for the local player's `targetId` (spell-id matching alone can't
   tell our cast from a nearby player casting the same spell).
3. Send `UpdatePositionHandler` during the window: record the real position, then per policy:
   - `Drop` — the original send is never called (the hook returns success without it).
   - `ReplayFrozen` — x/y/z of the client's own buffer are patched to the frozen position in
     place, then the original send runs.
4. Resolve, any of:
   - Receive `Effect` → flush. (M0: any Effect resolves the window — no source correlation yet;
     fine solo, sloppy in parties. Follow-up: match the caster entity id.)
   - Receive `ActorControl`/`ActorControlSelf` with `InterruptActorControlCategory` → flush
     (fail-open, ADR-002). `ActorControl` is broadcast per-actor: honored only when the packet's
     `targetId` is the local player (live bug 2026-09-25: foreign interrupts closed our window
     mid-cast and the premature flush really killed the cast). `ActorControlSelf` needs no
     correlation.
   - `castTime + GracePeriodMs` elapses with no resolve → flush (backstop; also driven by the
     250 ms timer when no packets flow).
5. Flush:
   - Zero-drift (real ≈ frozen within 0.01 u) → send nothing.
   - `Immediate` → one Move packet with the final real position.
   - `Staged` → N interpolated steps (`StagedSteps`, clamped up so no step exceeds
     `StagedMaxStepUnits`, max 64), one per `StagedStepIntervalMs`; remaining steps burst at
     `StagedFlushTimeoutMs` (fail-open).
   - Flush packets are clones of the last suppressed Move capture with x/y/z rewritten —
     byte-compatible with the client's own traffic — sent via the captured original
     `ZoneClient.SendPacket` `this`/`a3`/`a4`/`a5`.
6. Sink failure at any point → abandon flush, close window, log. The engine never stays in a
   window (ADR-002).

### Threading

Hooks run on the client network thread; the backstop timer calls `Tick` on a thread-pool thread.
`WireExtender` serializes all tracker access with a lock, held short. `IFlushSink.Send` may
therefore run on either thread.

## Verification

- **Unit tests (Linux):** `dotnet test SlidecastExtender.slnx` — 58 tests: packet codec
  round-trips, in-place patch, window lifecycle, both policies, both flush modes, staged
  clamping/timeout-burst, zero-drift skip, sink-failure abort, mode routing, cast-time-provider
  gating, ActorCast refine, interrupt-category routing, backstop tick.
- **In game:** Observe run per `docs/re/capture-checklist.md` confirms every `VERIFY` constant;
  only then a Live run in a safe spot. Success = effect fires after walking; log shows
  `move-suppressed ×N` then exactly one `flush`; no `cast-timeout` warns.

## Known risks (accepted for M0)

- Server snap on flush (large single-packet jump) — mitigated by `Staged` flush if observed.
- Server speed/distance clamp may reject a large flush — unknown until observed.
- Collision resolves against the frozen position until the flush.
- Server may re-validate finish vs cast-start delta and drop the effect — unknown until observed.
