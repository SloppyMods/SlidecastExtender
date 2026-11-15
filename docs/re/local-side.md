# RE task list: M1 local side (the hard 50%)

**Problem.** The wire side (M0) keeps the *server* believing the character is stationary, so the
server's cast timer completes. But the *client* cancels its own cast bar the instant the
character moves — locally, before any `CastInterrupt` arrives from the network. The server's
interrupt is a sync/confirmation, not the cause of the bar dying.

Result with M0 only: the character slides and the effect fires server-side, but the local cast
bar dies the moment you move — a normal interrupted cast on your screen, not a slidecast.

**Goal of M1:** the local cast object survives local movement for the duration of the cast
window, so the bar stays up and the resolve (which M0 guarantees arrives) completes it.

## What to find (Ghidra, live client)

1. **The local cast / "is-casting" state owner.** The object or manager that owns the cast bar's
   lifecycle (start, progress, cancel). Likely hanging off the local player character or a UI
   adjacent action manager.
2. **The movement-triggered interrupt path.** The function that observes "local player moved" and
   cancels the in-progress cast. This is the gate point: M1 hooks it and suppresses the cancel
   while a wire-side window is open.
3. **The cancel's other callers.** The same cancel is surely invoked for real interrupts
   (server `CastInterrupt`, being hit, stun). The gate must only swallow the *movement* trigger,
   or real interrupts will stop breaking casts — detectable and wrong.

## Candidate lanes (cheap → deep)

1. **Realscript first.** The client's managed event bridge may expose the cast-cancel event
   early enough to defer/swallow it. Cheapest option; try before any native patching. Open
   question: does Realscript see the cancel *before* the bar dies, or only after?
2. **Native inline hook on the cancel function**, gated by `CastWindowTracker.WindowActive`
   (the engine already exposes exactly the predicate needed). Breaks on every client update;
   needs the RE above.
3. **Patch the movement check** (the predicate the cancel path consults) instead of the cancel
   itself — smaller surface, if the check is a distinct function.

## Integration shape (already prepared)

`src/SlidecastExtender.Plugin/LocalSideStub.cs` defines `ILocalCastGate`
(`Begin(actionId, ts)` / `End()` / `IsGating` / `Available`) with an M0 no-op implementation.
The M1 implementation replaces the stub; the wire side calls `Begin` at cast start and `End` at
every window exit. Keep the gate fail-open like the wire side (ADR-002): if the M1 hook is
missing or broken after a patch, the plugin must degrade to M0 behavior, never wedge casts.

## Risks

- **Detection surface.** Suppressing a local cancel is client-side only, but behavior that is
  impossible without it (full-length casts while moving) is exactly what observers and
  server-side sanity checks notice. M0 alone is deniable-ish; M1 is not.
- **Per-patch maintenance.** Native targets move every client update; budget for re-RE each
  patch or invest in signature scanning.
- **Desync visibility.** Other players' clients resolve your movement from the server's view
  (frozen, then flushed). Long slides may look like teleporting to others.
