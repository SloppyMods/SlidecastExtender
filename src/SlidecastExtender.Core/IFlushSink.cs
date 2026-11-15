namespace SlidecastExtender.Core;

/// <summary>
/// Delivers one plaintext wire packet the engine built (a flush packet) through the client's
/// ORIGINAL send path — i.e. byte-compatible with what the client would send, and delivered by the
/// original send function rather than a separate channel.
/// </summary>
public interface IFlushSink
{
    /// <summary>Send a fully-formed plaintext packet on the client's wire.</summary>
    void Send(byte[] packet);
}
