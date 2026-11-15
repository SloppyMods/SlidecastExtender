# Capture checklist: Observe run → Live run

**State after the 2026-09-20 Observe run:** hooks confirmed installed (FFXIVClientStructs
runtime resolution); both framings confirmed (send opcode @+0, receive opcode @+2); Move layout
confirmed (rotation f32 @+32, position f32 x/y/z @+40/+44/+48); ActionRequest action id
confirmed (u32 @+32 — Cure III = 131 matched the game log); ActorCast confirmed (SpellId @
payload+0 = the request's action id, CastTime f32 s @ payload+8 — matched the cast bar);
interrupt category confirmed = **15** (ActorControl, fired exactly at walk-away cast death,
twice). Log file: `devPlugins/SlidecastExtender/logs/wire-*.log`.

## Remaining Observe items

- [ ] **Completed-cast resolve:** cast a spell standing still, do NOT move. Confirm
      `observe-recv id=0x313` (Effect) arrives at completion. (So far only interrupted casts
      were captured; Effect=0x313 traffic was seen but not yet tied to a completed cast.)
- [ ] Optional, cosmetic: axis order — walk due north/east and note which of the first/third
      position components tracks. The engine patches all three as a set, so order doesn't affect
      function, only log readability.

## Live, safe spot

- [ ] `/slidecast mode live`. Cast a long spell, take a few steps, stop before the cast ends.
- [ ] Success = the effect fires after you walked; the log shows `cast-start` →
      `move-suppressed ×N` → exactly one `flush`; no `cast-timeout` warn.
- [ ] Expected M0 limitation: **the local cast bar still dies when you move** (that's M1,
      `docs/re/local-side.md`) — the win to look for is the effect firing despite movement.
- [ ] Watch for: server snap on flush (try `/slidecast flush staged` if violent), the send
      hook's drop semantics misbehaving (caller retrying = duplicated casts?), anything odd after
      a flush (malformed-packet symptoms — see docs/re/hook-targets.md).

## Failure handling

- Any `send-hook`/`receive-hook`/`flush-*` errors in the log → stop, file the hex + the error.
- tshark is shape/timing sanity only (the wire is encrypted): e.g. "did the move stream actually
  pause during the window".
- Dev-plugin hygiene: after toggling the plugin off, wait ~2 s before toggling on (Dalamud
  disposes asynchronously); a rebuild while the game runs can race the auto-reload — toggle once
  more if that happens.
