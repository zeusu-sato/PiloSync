namespace PiloSync.Transport;
public sealed class TracingChannel(IByteChannel channel, Action<string> log) : IByteChannel
{
    public string Name => channel.Name;
    public bool IsSerial => channel.IsSerial;
    private void Trace(string direction, ReadOnlySpan<byte> bytes) => log($"{DateTime.Now:HH:mm:ss.fff} {direction} {bytes.Length} bytes: {string.Join(" ", bytes.ToArray().Select(b => b.ToString("X2")))}");
    public int Read(Span<byte> buffer, int timeoutMs) { int n = channel.Read(buffer, timeoutMs); if (n > 0) Trace("IN", buffer[..n]); return n; }
    public void Write(ReadOnlySpan<byte> data) { channel.Write(data); Trace("OUT", data); }
    public void SetBaudRate(int baud) => channel.SetBaudRate(baud);
    public void Dispose() => channel.Dispose();
}
