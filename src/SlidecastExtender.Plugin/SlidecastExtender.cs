using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using SlidecastExtender.Core;

namespace SlidecastExtender.Plugin;

/// <summary>
/// Dalamud plugin entry point. Installs the wire-side inline hooks, wires the flush sink to the
/// core <see cref="WireExtender"/>, registers <c>/slidecast</c>, and drives the 250 ms backstop
/// timer. No wire logic here — that lives in core.
/// </summary>
public sealed class SlidecastExtenderPlugin : IDalamudPlugin
{
    private const string Command = "/slidecast";
    private const int BackstopIntervalMs = 250;

    private readonly WireExtender _extender;
    private readonly PacketSink _sink;
    private readonly LogSink _logSink;
    private readonly ICommandManager _commands;
    private readonly IChatGui _chat;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly SlidecastIpc _ipc;
    private readonly PluginConfig _persist;
    private bool _uiVisible;
    private System.Threading.Timer? _backstop;
    private CommandInfo _commandInfo;

    public string Name => "SlidecastExtender";

    public SlidecastExtenderPlugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commands,
        IGameInteropProvider interop,
        IDataManager dataManager,
        IPlayerState playerState,
        IChatGui chat)
    {
        _commands = commands;
        _chat = chat;
        _pluginInterface = pluginInterface;
        _pluginInterface.UiBuilder.Draw += Draw;
        _persist = pluginInterface.GetPluginConfig() as PluginConfig ?? new PluginConfig();
        var config = new SlidecastConfig
        {
            Mode = _persist.Mode,
            Suppress = _persist.Suppress,
            Flush = _persist.Flush,
            GracePeriodMs = _persist.GracePeriodMs,
        };

        _logSink = new LogSink(pluginInterface.AssemblyLocation.DirectoryName ?? ".");
        _extender = new WireExtender(config, _logSink.Log);
        _sink = new PacketSink(_extender, interop, _logSink.Log);

        _extender.SetFlushSink(_sink);
        // Cast-time lookup from game data: action id → cast time in ms (0 = instant → no
        // window, so instant weaponskills are never suppressed). The server's ActorCast later
        // refines the deadline with the authoritative (spell-speed-adjusted) value.
        _extender.SetCastTimeProvider(actionId =>
        {
            try
            {
                var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
                if (sheet is null) return 0L;
                return sheet.GetRowOrDefault(actionId) is { } row ? row.Cast100ms * 100L : 0L;
            }
            catch
            {
                return 0L; // lookup failure = treat as instant (no suppression) — fail-open
            }
        });
        // Actor-scoped broadcasts (ActorCast, ActorControl) arrive for every actor in range;
        // the engine must only act on the ones about the local player (0 while unknown/loading).
        _extender.SetLocalEntityIdProvider(() => playerState.EntityId);
        _sink.Install();
        _ipc = new SlidecastIpc(pluginInterface, _extender);

        _commandInfo = new CommandInfo(OnCommand)
        {
            HelpMessage = "Wire-side slidecast (/slidecast [status | mode off|observe|live | suppress drop|replayfrozen | flush immediate|staged | grace <ms> | ui])",
            ShowInHelp = true,
        };
        _commands.AddHandler(Command, _commandInfo);

        _backstop = new System.Threading.Timer(
            _ => _extender.Tick(Environment.TickCount),
            null,
            TimeSpan.FromMilliseconds(BackstopIntervalMs),
            TimeSpan.FromMilliseconds(BackstopIntervalMs));

        Announce($"loaded — mode={config.Mode} (wire side {(config.Mode is ExtenderMode.Live ? "ACTIVE: Move suppression on" : "inert: logging only")}). /slidecast for status, /slidecast ui for the window.");
    }

    /// <summary>Command handler: <c>/slidecast</c> and its subcommands.</summary>
    private void OnCommand(string command, string args)
    {
        var parts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            LogStatus();
            return;
        }

        var sub = parts[0].Trim().ToLowerInvariant();
        var arg1 = parts.Length > 1 ? parts[1] : string.Empty;

        switch (sub)
        {
            case "":
            case "status":
                LogStatus();
                break;
            case "mode":
                if (ParseMode(arg1) is ExtenderMode m)
                {
                    _extender.SetMode(m);
                    _persist.Mode = m;
                    _pluginInterface.SavePluginConfig(_persist);
                    Announce(m switch
                    {
                        ExtenderMode.Live => "wire side ON (Live) — Move packets are suppressed during casts, position flushed on resolve.",
                        ExtenderMode.Observe => "wire side Observe — logging the wire, changing nothing.",
                        _ => "wire side OFF — hooks pass everything through.",
                    });
                }
                else
                {
                    Reply("usage: /slidecast mode off|observe|live");
                }
                break;
            case "suppress":
                if (ParseSuppress(arg1) is SuppressionPolicy s)
                {
                    _extender.SetSuppression(s);
                    _persist.Suppress = s;
                    _pluginInterface.SavePluginConfig(_persist);
                    Reply($"suppress → {s}");
                }
                else
                {
                    Reply("usage: /slidecast suppress drop|replayfrozen");
                }
                break;
            case "flush":
                if (ParseFlush(arg1) is FlushMode f)
                {
                    _extender.SetFlush(f);
                    _persist.Flush = f;
                    _pluginInterface.SavePluginConfig(_persist);
                    Reply($"flush → {f}");
                }
                else
                {
                    Reply("usage: /slidecast flush immediate|staged");
                }
                break;
            case "grace":
                if (long.TryParse(arg1, out var ms))
                {
                    _extender.SetGrace(ms);
                    _persist.GracePeriodMs = Math.Max(0, ms);
                    _pluginInterface.SavePluginConfig(_persist);
                    Reply($"grace → {ms} ms");
                }
                else
                {
                    Reply("usage: /slidecast grace <ms>");
                }
                break;
            case "ui":
                _uiVisible = !_uiVisible;
                Reply($"status window → {(_uiVisible ? "shown" : "hidden")}");
                break;
            default:
                Reply($"unknown subcommand '{sub}' — try: status | mode | suppress | flush | grace | ui");
                break;
        }
    }

    /// <summary>Minimal status window (<c>/slidecast ui</c>). Reads are unlocked and
    /// approximate — the network thread may be mid-update; fine for a display.</summary>
    private void Draw()
    {
        if (!_uiVisible) return;
        var s = _extender.Status();
        ImGui.SetNextWindowSizeConstraints(new Vector2(260, 0), new Vector2(float.MaxValue, float.MaxValue));
        if (ImGui.Begin("SlidecastExtender", ref _uiVisible))
        {
            ImGui.Text($"mode: {s.Mode}");
            ImGui.Text($"window: {(s.WindowActive ? "ACTIVE" : "idle")}");
            if (s.CastId is { } castId)
                ImGui.Text($"cast: {castId}  remaining: {s.RemainingMs ?? 0} ms");
            ImGui.Text($"suppressed: {s.SuppressedMoves} moves");
            ImGui.Text($"flushed: {s.FlushedPackets} packets");
            ImGui.Separator();
            ImGui.TextDisabled("/slidecast mode off|observe|live");
        }
        ImGui.End();
    }

    private void LogStatus()
    {
        var s = _extender.Status();
        Reply(
            $"mode={s.Mode} window={s.WindowActive} cast={s.CastId ?? 0} " +
            $"remaining={s.RemainingMs ?? 0} ms suppressed={s.SuppressedMoves} moves " +
            $"flushed={s.FlushedPackets} packets");
    }

    /// <summary>User-facing answer: game chat AND the file log (the file is the durable record).</summary>
    private void Reply(string message)
    {
        _chat.Print($"[SlidecastExtender] {message}");
        _logSink.Log(new WireLogEntry(Environment.TickCount, LogSeverity.Info, "command", message));
    }

    /// <summary>On/off-style state announcements. Same channel as replies, but never throws —
    /// a chat failure must not break load/unload.</summary>
    private void Announce(string message)
    {
        try
        {
            _chat.Print($"[SlidecastExtender] {message}");
        }
        catch
        {
            // chat unavailable during teardown — the file log still has it
        }
        _logSink.Log(new WireLogEntry(Environment.TickCount, LogSeverity.Info, "announce", message));
    }

    private static ExtenderMode? ParseMode(string s) => s.Trim().ToLowerInvariant() switch
    {
        "off" => ExtenderMode.Off,
        "observe" => ExtenderMode.Observe,
        "live" => ExtenderMode.Live,
        _ => null,
    };

    private static SuppressionPolicy? ParseSuppress(string s) => s.Trim().ToLowerInvariant() switch
    {
        "drop" => SuppressionPolicy.Drop,
        "replayfrozen" => SuppressionPolicy.ReplayFrozen,
        _ => null,
    };

    private static FlushMode? ParseFlush(string s) => s.Trim().ToLowerInvariant() switch
    {
        "immediate" => FlushMode.Immediate,
        "staged" => FlushMode.Staged,
        _ => null,
    };

    public void Dispose()
    {
        Announce("unloaded — wire side off, hooks removed.");
        _backstop?.Dispose();
        _backstop = null;
        _pluginInterface.UiBuilder.Draw -= Draw;
        _commands.RemoveHandler(Command);
        _ipc.Dispose();
        _sink.Dispose();
        _logSink.Dispose();
    }
}
