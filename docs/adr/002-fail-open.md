# ADR-002: Fail-open on every exit path

**Status:** Accepted
**Date:** 2026-09-20

## Context

The engine suppresses the client's real position from the server for the length of a cast
window. Any bug or surprise that leaves a window open — a wrong packet id in the table, a silent
server, a flush that throws, a game update that shifts an offset — would permanently desync the
server's position for the character: the server keeps the frozen position while the player keeps
moving. That is worse than the stock behavior the plugin is trying to improve, and it is the
kind of failure that looks like cheating to server-side sanity checks.

## Decision

Every window-exit path restores normal operation, and the exit set is exhaustive:

- `ActionEffect` / `CastFinish` → flush real position, close window.
- `CastInterrupt` / `Interrupt` → flush real position, close window (the server already knows
  the cast died; converging positions is strictly safer).
- No resolve within `durationMs + GracePeriodMs` → flush, close window (`cast-timeout` warn).
  Driven both by packet flow and by a 250 ms backstop timer, so a silent network cannot wedge it.
- Flush sink missing or throwing → abandon the (staged) flush, close the window, log; never
  retry a failing sink inside the same window.

Additionally, the zero-drift fast path skips the flush entirely when the real position is within
0.01 u of the frozen one — no snap into your own position.

## Consequences

- The worst case of any engine bug is "one odd position jump, then normal play" — the same shape
  as an accepted stock-FFXIV correction.
- Wrong `VERIFY` constants degrade to logged no-ops or a timeout flush, never a stuck window.
- The timeout path means a *correct* id table is not load-bearing for safety, only for
  functionality.
- Fail-open is a bias toward server convergence: after any exit, the server's position for the
  character is the true one.

## Alternatives rejected

- **Fail-closed (keep suppressing until a resolve is certain)** — a missed resolve would desync
  the server indefinitely; unacceptable.
- **No timeout (trust the id table)** — the ids are `VERIFY` guesses until the Observe run;
  safety cannot depend on them.
- **Retry a failed flush sink** — a throwing send function will keep throwing; retrying inside
  the window just delays convergence.
