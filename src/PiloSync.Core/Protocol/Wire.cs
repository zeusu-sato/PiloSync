using PiloSync.Transport;
namespace PiloSync.Protocol;

// Packet layouts: pilot-link libpisock slp.c, padp.c, cmp.c, net.c and pi-*.h.
// See docs/protocol-notes.md. This implementation is independently written.
public sealed class ProtocolException(string message) : IOException(message);

internal sealed class Wire(IByteChannel channel, CancellationToken cancellation, int timeoutMs, byte? prefix = null)
{
    private byte? first = prefix;
    public long Deadline(int? waitMs = null) => Environment.TickCount64 + (waitMs ?? timeoutMs);
    public byte[] Read(int count, long deadline)
    {
        var result = new byte[count];
        int offset = 0;
        if (count > 0 && first is byte b) { result[offset++] = b; first = null; }
        while (offset < count)
        {
            cancellation.ThrowIfCancellationRequested();
            int left = (int)Math.Clamp(deadline - Environment.TickCount64, 0, int.MaxValue);
            if (left == 0) throw new TimeoutException($"{channel.Name}: protocol timeout (6410 相当)");
            int n = channel.Read(result.AsSpan(offset), Math.Min(200, left));
            if (n < 0 || n > count - offset) throw new IOException("Invalid channel read count");
            offset += n;
        }
        return result;
    }
    public void Write(ReadOnlySpan<byte> bytes) { cancellation.ThrowIfCancellationRequested(); channel.Write(bytes); }
}
