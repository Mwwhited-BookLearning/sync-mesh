using System.Buffers.Binary;

namespace SyncMesh.Daemon.Ipc;

// Simple length-prefixed message framing shared by the IPC server and
// client: a 4-byte little-endian length, followed by that many UTF-8 bytes.
internal static class IpcFraming
{
    public static async Task WriteMessageAsync(Stream stream, string json, CancellationToken ct)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(json);
        var lengthPrefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthPrefix, payload.Length);

        await stream.WriteAsync(lengthPrefix, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<string> ReadMessageAsync(Stream stream, CancellationToken ct)
    {
        var lengthPrefix = new byte[4];
        await ReadExactAsync(stream, lengthPrefix, ct);
        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthPrefix);

        var payload = new byte[length];
        await ReadExactAsync(stream, payload, ct);
        return System.Text.Encoding.UTF8.GetString(payload);
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..], ct);
            if (n == 0)
            {
                throw new IOException("Pipe closed before the expected number of bytes was read.");
            }
            read += n;
        }
    }
}
