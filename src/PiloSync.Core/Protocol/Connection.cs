using System.Buffers.Binary;
using PiloSync.Transport;
namespace PiloSync.Protocol;

public sealed class PalmConnection : IDisposable
{
    private readonly IByteChannel channel;
    private readonly IPacketTransport packets;
    public string Protocol { get; }
    public int BaudRate { get; }
    public int SkippedNoiseBytes { get; }
    public DlpClient Dlp { get; }
    private PalmConnection(IByteChannel channel, string protocol, int baud, IPacketTransport packets, int skippedNoiseBytes)
    { this.channel = channel; this.packets = packets; Protocol = protocol; BaudRate = baud; SkippedNoiseBytes = skippedNoiseBytes; Dlp = new DlpClient(packets); }
    /// <summary>Takes ownership of the channel, including on handshake failure.</summary>
    public static PalmConnection Accept(IByteChannel channel, int baud = 57600, CancellationToken cancellation = default, int timeoutMs = 10000)
    {
        try
        {
            if (!new[] { 9600, 19200, 38400, 57600, 115200 }.Contains(baud)) throw new ArgumentOutOfRangeException(nameof(baud));
            byte[] one = new byte[1];
            // Idle is unlimited; protocol operations are bounded after a recognized first byte.
            int skippedNoiseBytes = 0;
            while (true)
            {
                while (channel.Read(one, 200) == 0) cancellation.ThrowIfCancellationRequested();
                if (!channel.IsSerial || one[0] is 0xbe or 1 or 0x90) break;
                skippedNoiseBytes++;
                cancellation.ThrowIfCancellationRequested();
            }
            var wire = new Wire(channel, cancellation, timeoutMs, one[0]);
            if (one[0] == 0xbe)
            {
                var padp = new PadpTransport(wire); var wake = padp.Receive();
                if (wake.Length != 10 || wake[0] != 1 || (BinaryPrimitives.ReadUInt16BigEndian(wake.AsSpan(2)) & 0xff00) != 0x100) throw new ProtocolException("CMP invalid wakeup");
                uint maximum = BinaryPrimitives.ReadUInt32BigEndian(wake.AsSpan(6));
                int selected = new[] { 115200, 57600, 38400, 19200, 9600 }.FirstOrDefault(x => x <= baud && x <= maximum);
                if (selected == 0) throw new ProtocolException("CMP unsupported baud rate");
                byte[] init = new byte[10]; init[0] = 2; init[1] = (byte)(selected != 9600 ? 0x80 : 0);
                BinaryPrimitives.WriteUInt16BigEndian(init.AsSpan(2), (ushort)Math.Min(0x102, (int)BinaryPrimitives.ReadUInt16BigEndian(wake.AsSpan(2))));
                BinaryPrimitives.WriteUInt32BigEndian(init.AsSpan(6), (uint)selected);
                padp.Send(init);
                if (channel.IsSerial)
                {
                    channel.SetBaudRate(selected);
                    // Allow the handheld UART to settle before the first DLP request.
                    if (cancellation.WaitHandle.WaitOne(50)) cancellation.ThrowIfCancellationRequested();
                }
                padp.Transaction = 1;
                return new(channel, "SLP", channel.IsSerial ? selected : 0, padp, skippedNoiseBytes);
            }
            if (one[0] is 1 or 0x90)
            {
                var net = new NetTransport(wire); net.AcceptHandshake(); return new(channel, "NET", 0, net, skippedNoiseBytes);
            }
            throw new ProtocolException($"Unknown protocol first byte 0x{one[0]:X2}");
        }
        catch { channel.Dispose(); throw; }
    }
    public void TryAbort()
    {
        using var abortTimeout = new CancellationTokenSource(2000);
        using var abortClose = abortTimeout.Token.Register(() => { try { channel.Dispose(); } catch { } });
        try
        {
            if (packets is NetTransport net) net.AllowTransactionMismatch = true;
            Dlp.EndOfSync(3);
        }
        catch { }
        finally
        {
            if (packets is NetTransport net) net.AllowTransactionMismatch = false;
        }
    }
    public void Dispose() => channel.Dispose();
}
