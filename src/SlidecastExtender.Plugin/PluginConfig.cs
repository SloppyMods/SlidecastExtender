using Dalamud.Configuration;
using SlidecastExtender.Core;

namespace SlidecastExtender.Plugin;

/// <summary>
/// Persisted plugin settings (Dalamud plugin config JSON). The engine's own
/// <see cref="SlidecastConfig"/> stays the runtime knob set; this is just the durable copy,
/// loaded at construction and saved on every <c>/slidecast</c> change.
///
/// <para>Default mode is <b>Live</b> — Observe did its job (layouts confirmed on the wire);
/// if a game patch shifts opcodes, unrecognized ids mean no windows open, i.e. fail-open.</para>
/// </summary>
public sealed class PluginConfig : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public ExtenderMode Mode { get; set; } = ExtenderMode.Live;
    public SuppressionPolicy Suppress { get; set; } = SuppressionPolicy.Drop;
    public FlushMode Flush { get; set; } = FlushMode.Immediate;
    public long GracePeriodMs { get; set; } = 2000;
}
