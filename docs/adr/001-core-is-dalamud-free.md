# ADR-001: Core engine is Dalamud-free; the plugin is a thin adapter

**Status:** Accepted
**Date:** 2026-09-20

## Context

The wire-side logic (packet id/layout tables, cast-window state machine, suppression and flush
policy) is the part of this project that can be designed and verified without the game. The
Dalamud plugin API targets Windows (`net10.0-windows`) and the game only runs on the user's
machine under Windows/Wine, while development and CI happen on Linux. If wire logic lived inside
the plugin assembly, every logic change would need a Windows build and a game launch to test.

## Decision

`SlidecastExtender.Core` is plain `Microsoft.NET.Sdk` `net10.0` with zero Dalamud references.
Everything testable lives there: `WirePackets` (byte codec), `CastWindowTracker` (state
machine), `WireExtender` (mode facade). The plugin assembly contains only adapters: hook
installation (`PacketSink`), the file log (`LogSink`), the `/slidecast` command and backstop
timer (`SlidecastExtender.cs`), and placeholder hook addresses (`HookTargets.cs`).

The boundary types are owned by core and are Dalamud-free: hooks call
`WireExtender.OnClientToSend(IntPtr, uint, int, long)` / `OnClientReceived(byte[], long)` /
`Tick(long)`; the engine emits flushes through `IFlushSink.Send(byte[])`, which the plugin
implements by calling the *original* send function.

## Consequences

- The full wire-side behavior runs under `dotnet test` on Linux (55 tests at time of writing).
- The plugin stays small and boring; logic bugs are found by tests, not in game.
- `IntPtr` appears in the core API surface (the send hook's buffer) — accepted: it is a BCL
  type, and the alternative (copying every outgoing packet to `byte[]` before the keep/drop
  decision) would tax the hot path for purity.
- The plugin assembly still compiles on Linux (`-p:EnableWindowsTargeting=true`), so API drift
  in the adapter is caught without a Windows box.

## Alternatives rejected

- **Logic inside the plugin project** — untestable without the game; rejected by the
  environment constraints.
- **Separate hooking library (e.g. Reloaded.Hooks) instead of Dalamud's
  `IGameInteropProvider`** — a second hooking stack inside a Dalamud process for no benefit;
  Dalamud's `Hook<T>` already gives trampolines, `Original`, and safe disposal.
