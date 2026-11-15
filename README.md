---
tags:
  - type/readme
  - project/slidecastextender
  - status/active
type: readme
project: slidecastextender
status: active
aliases: []
---
# SlidecastExtender

Extends FFXIV's slidecast window by faking stillness to the server: during a cast the plugin
suppresses outgoing `Move` packets at the plaintext boundary, so the server's cast timer completes
and `ActionEffect`/`CastFinish` arrive on schedule; the real position is flushed on resolve.

**Status:** M0 wire side code-complete, unit-tested (58 green on Linux), and **installed to
`~/.xlcore/devPlugins/SlidecastExtender/`** — ready for the first in-game Observe run. Hook
targets resolve at runtime via FFXIVClientStructs (`ZoneClient.SendPacket` /
`PacketDispatcher.OnReceivePacket` — the same functions Dalamud's Network Monitor hooks), so no
Ghidra step and no per-patch address maintenance (`docs/re/hook-targets.md`). Opcodes are synced
from FFXIVOpcodes Global 7.56h (game build 2026.09.15). The local side (keeping the client's own
cast bar alive while moving) is the hard 50% and is deferred to M1 (`docs/re/local-side.md`).

## Layout

- `src/SlidecastExtender.Core/` — pure C# engine, no Dalamud, unit-testable on Linux (ADR-001).
- `src/SlidecastExtender.Plugin/` — thin Dalamud adapter: hooks, file log, `/slidecast` command,
  250 ms backstop timer. No wire logic lives here.
- `tests/SlidecastExtender.Tests/` — xunit suite for the core (runs on Linux).
- `docs/specs/wire-side-prototype.md` — what M0 does and how it is verified.
- `docs/re/` — reverse-engineering task lists (hook targets, capture checklist, local side).
- `docs/adr/` — design decisions (001 core is Dalamud-free, 002 fail-open).

## Build & test (Linux)

Requires the .NET 10 SDK (`~/.dotnet` is fine).

```bash
export PATH="$HOME/.dotnet:$PATH"
./scripts/build.sh
```

The plugin (Windows target) also compiles on Linux for verification:

```bash
dotnet build SlidecastExtender.Plugin.slnx -p:EnableWindowsTargeting=true -p:OutputPath=/tmp/sce-out/
```

On the game machine, `scripts/build-plugin.ps1` builds straight into
`%AppData%\XIVLauncher\devPlugins\SlidecastExtender\`.

## In-game

The plugin is already in the devPlugins folder (visible in game at
`C:\Users\you\AppData\Roaming\XIVLauncher\devPlugins\SlidecastExtender\` via the shared link).
Enable it from Dalamud's dev-plugin list, then:

```
/slidecast mode observe    # first run: log the wire, change nothing
/slidecast mode live       # suppression + flush
/slidecast suppress drop|replayfrozen
/slidecast flush immediate|staged
/slidecast grace <ms>
/slidecast status
```

Logs land in `devPlugins/SlidecastExtender/logs/wire-*.log` (next to the DLL — works on both
Windows and XLCore/Wine). Run Observe first and walk `docs/re/capture-checklist.md` before any
Live run.
