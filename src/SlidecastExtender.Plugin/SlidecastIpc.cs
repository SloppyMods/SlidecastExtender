using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using SlidecastExtender.Core;

namespace SlidecastExtender.Plugin;

/// <summary>
/// Dalamud IPC for rotation plugins (PL5): while a Live cast window is open, the server is
/// still casting even if the local bar died (movement) — a new action now would cancel the
/// server-side cast. Subscribers hold their rotation until the window closes.
///
/// <para>Reads are unlocked and approximate (the network thread may be mid-update) — fine for
/// a rotation gate. When the mode isn't Live no windows open, so the IPC always reports
/// closed: subscribers behave stock.</para>
/// </summary>
internal sealed class SlidecastIpc : IDisposable
{
    private readonly ICallGateProvider<bool> _windowOpen;
    private readonly ICallGateProvider<long> _windowRemainingMs;

    public SlidecastIpc(IDalamudPluginInterface pluginInterface, WireExtender extender)
    {
        _windowOpen = pluginInterface.GetIpcProvider<bool>("SlidecastExtender.IsCastWindowOpen");
        _windowOpen.RegisterFunc(() => extender.Status().CastWindowOpen);
        _windowRemainingMs = pluginInterface.GetIpcProvider<long>("SlidecastExtender.CastWindowRemainingMs");
        _windowRemainingMs.RegisterFunc(() => extender.Status().RemainingMs ?? 0);
    }

    public void Dispose()
    {
        _windowOpen.UnregisterFunc();
        _windowRemainingMs.UnregisterFunc();
    }
}
