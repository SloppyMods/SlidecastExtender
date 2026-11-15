namespace SlidecastExtender.Core;

public enum LogSeverity
{
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>One log line for the wire-side engine. <c>TsMs</c> is the client tick (ms from process
/// start); callers use <c>Environment.TickCount</c>.</summary>
public sealed record WireLogEntry(long TsMs, LogSeverity Sev, string Tag, string Message)
{
    public override string ToString() =>
        $"{TsMs / 1000:000:00:00} {TsMs % 1000:000} [{Sev,-5}] [{Tag}] {Message}";
}
