using System.Buffers.Binary;
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

    public static async Task<(TunnelFrameType Type, byte[] Payload)> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[5];
        await ReadExactAsync(stream, header, ct);
        var type = (TunnelFrameType)header[0];
        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1));
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
    public static async Task SpliceAsync(Stream a, Stream b, CancellationToken ct)
    {
        var aToB = a.CopyToAsync(b, ct);
        var bToA = b.CopyToAsync(a, ct);
        await Task.WhenAny(aToB, bToA);
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
