using System;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using SlidecastExtender.Core;

namespace SlidecastExtender.Plugin;

/// <summary>
/// Native↔managed glue: installs the hooks at the plaintext boundary and forwards bytes to the
/// core <see cref="WireExtender"/>.
///
/// <para>Thread: the hooks run on the client's network thread. <c>_sendThis</c>/<c>_sendA3..A5</c>
/// are written from that thread and read by <see cref="Send"/> (invoked from the engine — the
/// network thread, or the backstop timer for a staged-flush step).</para>
/// </summary>
internal sealed class PacketSink : IFlushSink, IDisposable
{
    private readonly WireExtender _extender;
    private readonly IGameInteropProvider _interop;
    private readonly Action<WireLogEntry> _log;

    // Real unmanaged signatures (confirmed against Dalamud's NetworkMonitorWidget, which hooks
    // the same two functions). Neither carries an explicit packet length — the engine copies
    // bounded per-id prefixes (see WirePackets.*CaptureBytes).
    private delegate bool CallSend(IntPtr self, IntPtr packet, uint a3, uint a4, bool a5);
    private delegate void CallReceive(IntPtr self, uint targetId, IntPtr packet);

    private Hook<CallSend>? _sendHook;
    private Hook<CallReceive>? _receiveHook;
    private IntPtr _sendThis;
    private uint _sendA3;
    private uint _sendA4;
    private bool _sendA5;

    public PacketSink(WireExtender extender, IGameInteropProvider interop, Action<WireLogEntry> log)
    {
        _extender = extender ?? throw new ArgumentNullException(nameof(extender));
        _interop = interop ?? throw new ArgumentNullException(nameof(interop));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public bool Install()
    {
        if (!HookTargets.TryResolve(out var coreSend, out var coreReceive))
        {
            Log(LogSeverity.Warn, "hooks",
                "FFXIVClientStructs could not resolve the hook targets; wire side idle (plugin still loaded).");
            return false;
        }
        _sendHook = _interop.HookFromAddress<CallSend>(coreSend, SendHook);
        _receiveHook = _interop.HookFromAddress<CallReceive>(coreReceive, ReceiveHook);
        _sendHook.Enable();
        _receiveHook.Enable();
        Log(LogSeverity.Info, "hooks", $"installed send=0x{coreSend:X} receive=0x{coreReceive:X}");
        return true;
    }

    public void Dispose()
    {
        _sendHook?.Dispose();
        _receiveHook?.Dispose();
        _sendHook = null;
        _receiveHook = null;
    }

    /// <summary>Flush path: call the client's ORIGINAL send function with our constructed packet.</summary>
    public void Send(byte[] packet)
    {
        var hook = _sendHook;
        if (_sendThis == IntPtr.Zero || hook is null)
        {
            Log(LogSeverity.Warn, "flush-sink", "original send target not captured yet; dropping flush packet");
            return;
        }
        var handle = GCHandle.Alloc(packet, GCHandleType.Pinned);
        try
        {
            hook.Original(_sendThis, handle.AddrOfPinnedObject(), _sendA3, _sendA4, _sendA5);
        }
        catch (Exception e)
        {
            Log(LogSeverity.Error, "flush-sink", e.ToString());
        }
        finally
        {
            handle.Free();
        }
    }

    private bool SendHook(IntPtr self, IntPtr packet, uint a3, uint a4, bool a5)
    {
        try
        {
            // Capture the original send's this/a3/a4/a5 on the last Move — flush packets reuse them.
            if (WirePackets.IsMove(WirePackets.SendPacketIdFromBuffer(packet, 2)))
            {
                _sendThis = self;
                _sendA3 = a3;
                _sendA4 = a4;
                _sendA5 = a5;
            }

            bool keep = _extender.OnClientToSend(packet, Environment.TickCount);
            // Drop = pretend success without calling the original (VERIFY: the caller must not
            // retry or error on a "successful" send — watch the first Live run for oddities).
            if (!keep) return true;
            return _sendHook?.Original(self, packet, a3, a4, a5) ?? true;
        }
        catch (Exception e)
        {
            Log(LogSeverity.Error, "send-hook", e.ToString());
            return true; // never break the client's send path
        }
    }

    private void ReceiveHook(IntPtr self, uint targetId, IntPtr packet)
    {
        try
        {
            if (_extender.Mode is not ExtenderMode.Off)
            {
                var buf = new byte[WirePackets.ObserveCaptureBytes]; // bounded prefix
                Marshal.Copy(packet, buf, 0, buf.Length);
                _extender.OnClientReceived(buf, targetId, Environment.TickCount);
            }
        }
        catch (Exception e)
        {
            Log(LogSeverity.Error, "receive-hook", e.ToString());
        }
        // Inbound packets are never dropped — the original always runs.
        _receiveHook?.Original(self, targetId, packet);
    }

    private void Log(LogSeverity sev, string tag, string message) =>
        _log(new WireLogEntry(Environment.TickCount, sev, tag, message));
}
