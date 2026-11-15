using System;
using System.IO;
using SlidecastExtender.Core;

namespace SlidecastExtender.Plugin;

/// <summary>
/// File logger for the wire-side engine. Writes each <c>WireLogEntry</c> (already formatted by
/// <see cref="WireLogEntry.ToString"/> ) to a per-run log in a <c>logs</c> folder under the
/// plugin's install directory (passed in by the entry point). Thread-safe: the engine may log
/// from the network thread and the backstop timer.
/// </summary>
internal sealed class LogSink : IDisposable
{
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private string? _logPath;

    /// <summary>Creates the per-run log file and writes the opening line. The base directory
    /// must come from the caller (the plugin entry point's
    /// <c>IDalamudPluginInterface.AssemblyLocation</c>) — Dalamud loads dev plugins in-memory,
    /// so <c>Assembly.Location</c> is empty and cannot locate the install folder.</summary>
    public LogSink(string baseDirectory)
    {
        var dir = Path.Combine(baseDirectory, "logs");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"wire-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _logPath = path;
        _writer = new StreamWriter(path, append: true)
        {
            AutoFlush = true,
        };
        LogInfo("log", $"log file: {path}");
    }

    /// <summary>Current run's log file path (empty before the file is created).</summary>
    public string LogPath => _logPath ?? string.Empty;

    public void Log(WireLogEntry entry)
    {
        lock (_lock)
        {
            // Wall-clock prefix: the entry's TsMs is a process tick, useless for correlating
            // with Dalamud's log (which is wall-clock).
            _writer?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {entry}");
        }
    }

    /// <summary>Convenience: log an Info entry with a fixed tag.</summary>
    public void LogInfo(string tag, string message) =>
        Log(new WireLogEntry(Environment.TickCount, LogSeverity.Info, tag, message));

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
