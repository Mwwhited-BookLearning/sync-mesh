using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;

namespace SyncMesh.Contracts.Tunnel;

// Control-connection signaling only — the tunneled byte stream itself is
// never framed, so the tunnel stays protocol-agnostic (works identically
// for RDP/VNC/raw TCP/anything else forwarded to LocalTargetEndpoint).
// See docs/adr/0007-custom-reverse-tunnel-mechanism.md.
public enum TunnelFrameType : byte
{
    Hello = 0x01,            // agent -> relay, control conn: identity {siteId, instanceId}
    Heartbeat = 0x02,        // agent -> relay, control conn: empty
    OpenDataChannel = 0x03,  // relay -> agent, control conn: empty
    // Reserved, not currently sent or handled anywhere: TunnelRelay
    // rejects a busy agent by simply closing the client connection
    // (HandleClientConnectionAsync) without ever notifying the agent, and
    // TunnelAgent does the equivalent for its own local session lock
    // (HandleDataChannelRequestAsync). Keeping the wire value reserved
    // rather than repurposing/removing it, since a future "tell the
    // client explicitly why it was rejected" enhancement would want
    // exactly this value.
    Busy = 0x04,             // relay -> agent, control conn: empty (informational)
    DataChannelHello = 0x05, // agent -> relay, NEW data conn: identity
    ClientHello = 0x06,      // tunnel client -> relay, NEW conn: identity (target)
}

public sealed record TunnelIdentity(string SiteId, string InstanceId);

public static class TunnelFraming
{
    // [1-byte type][4-byte big-endian length][payload]
    public static async Task WriteFrameAsync(Stream stream, TunnelFrameType type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var header = new byte[5];
        header[0] = (byte)type;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(1), (uint)payload.Length);
        await stream.WriteAsync(header, ct);
        if (payload.Length > 0)
        {
            await stream.WriteAsync(payload, ct);
        }
        await stream.FlushAsync(ct);
    }

    // Every real frame payload is a small JSON identity blob (TunnelIdentity)
    // or empty (Heartbeat/OpenDataChannel/Busy) — this cap is generous
    // headroom over that, not a real protocol limit. It exists so an
    // unauthenticated peer's length prefix can't force either a negative-
    // length array allocation (a raw uint above int.MaxValue reinterprets
    // as negative once cast to int) or an unbounded multi-GB allocation
    // before a single byte of the claimed payload has even been read.
    private const int MaxFramePayloadLength = 64 * 1024;

    public static async Task<(TunnelFrameType Type, byte[] Payload)> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[5];
        await ReadExactAsync(stream, header, ct);
        var type = (TunnelFrameType)header[0];
        var rawLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1));
        if (rawLength > MaxFramePayloadLength)
        {
            throw new IOException($"Tunnel frame payload length {rawLength} exceeds the {MaxFramePayloadLength}-byte limit.");
        }

        var length = (int)rawLength;
        var payload = length == 0 ? [] : new byte[length];
        if (length > 0)
        {
            await ReadExactAsync(stream, payload, ct);
        }
        return (type, payload);
    }

    public static byte[] EncodeIdentity(string siteId, string instanceId) =>
        JsonSerializer.SerializeToUtf8Bytes(new TunnelIdentity(siteId, instanceId));

    public static TunnelIdentity DecodeIdentity(byte[] payload) =>
        JsonSerializer.Deserialize<TunnelIdentity>(payload)
            ?? throw new InvalidOperationException("Empty tunnel identity payload.");

    // Bidirectional raw-byte forwarding between two already-established
    // connections — the tunneled data itself, deliberately unframed so the
    // mechanism stays protocol-agnostic (see the type doc comment above).
    // Used identically by TunnelAgent (direct + data-channel sessions) and
    // TunnelRelay (client<->data-channel splicing).
    //
    // Takes NetworkStream (not the general Stream every caller happens to
    // pass today) specifically so each direction can half-close the OTHER
    // stream's send side the moment its own read side hits EOF. This
    // matters for any protocol that legitimately half-closes one
    // direction first (e.g. plain HTTP: send the request, shutdown-send,
    // then read the response) — WhenAll alone waits for both copies to
    // finish, but without forwarding the actual EOF as a real TCP FIN on
    // the other leg, the downstream peer just blocks waiting for more
    // input that will never arrive. The original WhenAny + immediate
    // dispose of both streams was worse: it truncated whichever direction
    // was still mid-copy the instant the other one finished.
    public static async Task SpliceAsync(NetworkStream a, NetworkStream b, CancellationToken ct)
    {
        var aToB = CopyThenShutdownSendAsync(a, b, ct);
        var bToA = CopyThenShutdownSendAsync(b, a, ct);
        await Task.WhenAll(aToB, bToA);
    }

    private static async Task CopyThenShutdownSendAsync(NetworkStream source, NetworkStream destination, CancellationToken ct)
    {
        await source.CopyToAsync(destination, ct);
        try
        {
            destination.Socket.Shutdown(SocketShutdown.Send);
        }
        catch (SocketException)
        {
            // The peer may have already torn down the connection from its
            // side (e.g. the other direction faulted) — shutdown on an
            // already-closed socket isn't a new failure worth surfacing.
        }
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0)
            {
                throw new IOException("Tunnel control connection closed mid-frame.");
            }
            offset += read;
        }
    }
}
