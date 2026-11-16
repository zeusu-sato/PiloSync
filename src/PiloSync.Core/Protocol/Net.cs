using System.Buffers.Binary;
using PiloSync.Transport;
namespace PiloSync.Protocol;

public sealed record NetPacket(byte Type, byte Transaction, byte[] Data);
public static class NetCodec
{
    public static byte[] Header(byte type, byte transaction, int length)
    {
        if (length < 0 || length > 1048576) throw new ProtocolException("NET length");
        var h = new byte[6]; h[0] = type; h[1] = transaction; BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(2), (uint)length); return h;
    }
    public static NetPacket Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 6) throw new ProtocolException("NET short header");
        uint n = BinaryPrimitives.ReadUInt32BigEndian(bytes[2..]);
        if (n > 1048576 || n != bytes.Length - 6 || (bytes[0] != 1 && bytes[0] != 2) || (bytes[0] == 2 && n != 0)) throw new ProtocolException("NET type/length");
        return new(bytes[0], bytes[1], bytes[6..].ToArray());
    }
}
public sealed class NetTransport : IPacketTransport
{
    private const int MaxReceiveMs = 300000;
    private readonly Wire wire;
    internal int MaxReceiveTimeoutMs { get; set; } = MaxReceiveMs;
    private byte transaction;
    private (int Length, byte Transaction)? lastSent;
    private (byte Transaction, uint Length)? lastReceived;
    private bool handshake = true;
    private bool first = true;
    internal bool AllowTransactionMismatch { get; set; }
    public NetTransport(IByteChannel channel, CancellationToken cancellation = default, int timeoutMs = 10000) : this(new Wire(channel, cancellation, timeoutMs)) { }
    internal NetTransport(Wire wire) { this.wire = wire; }
    public void Send(ReadOnlySpan<byte> data)
    {
        var header = NetCodec.Header(1, transaction, data.Length);
        var packet = new byte[header.Length + data.Length];
        header.CopyTo(packet, 0);
        data.CopyTo(packet.AsSpan(6)); wire.Write(packet);
        lastSent = (data.Length, transaction);
    }
    public byte[] Receive() => ReceiveCore(null);
    public byte[] Receive(int timeoutMs) => ReceiveCore(timeoutMs);
    private byte[] ReceiveCore(int? timeoutMs)
    {
        long maximum = wire.Deadline(MaxReceiveTimeoutMs);
        long deadline = Math.Min(maximum, wire.Deadline(timeoutMs));
        while (true)
        {
            byte b = wire.Read(1, deadline)[0];
            if (first && b == 0x90) { first = false; transaction = 1; return new byte[] { b }.Concat(wire.Read(21, deadline)).ToArray(); }
            first = false;
            byte[] h = new byte[] { b }.Concat(wire.Read(5, deadline)).ToArray();
            uint n = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(2));
            if (n > 1048576 || (b != 1 && b != 2) || (b == 2 && n != 0))
            {
                var following = new List<byte>();
                long diagnosticDeadline = Math.Min(deadline, wire.Deadline(300));
                try { while (following.Count < 64) following.Add(wire.Read(1, diagnosticDeadline)[0]); }
                catch (TimeoutException) { }
                string sent = lastSent is { } s ? $"len {s.Length} tx {s.Transaction:X2}" : "none";
                string received = lastReceived is { } r ? $"tx {r.Transaction:X2} len {r.Length}" : "none";
                string tail = following.Count == 0 ? "none" : Convert.ToHexString(following.ToArray());
                throw new ProtocolException($"NET header type/length: {Convert.ToHexString(h)}; expected tx {transaction:X2}; last sent {sent}; last recv {received}; following: {tail}");
            }
            if (b == 2) { lastReceived = (h[1], n); deadline = Math.Min(maximum, wire.Deadline(timeoutMs)); continue; }
            if (!handshake && !AllowTransactionMismatch && h[1] != transaction) throw new ProtocolException("NET transaction mismatch");
            var payload = wire.Read((int)n, deadline);
            lastReceived = (h[1], n);
            if (handshake) transaction = h[1];
            else transaction = transaction >= 0xfe ? (byte)1 : (byte)(transaction + 1);
            return payload;
        }
    }
    public void AcceptHandshake()
    {
        Expect(Receive(), 0x90, 22);
        // NetSync negotiation fields from the public protocol traces (not program logic).
        var firstReply = new byte[50]; firstReply[0] = 0x12; firstReply[1] = 1; firstReply[9] = 0x20; firstReply[13] = 0x24;
        firstReply.AsSpan(14, 4).Fill(0xff); firstReply[18] = 0x3c; firstReply[20] = 0x3c;
        firstReply[30] = 192; firstReply[31] = 168; firstReply[32] = 165; firstReply[33] = 31; firstReply[34] = 4; firstReply[35] = 0x27;
        Send(firstReply); Expect(Receive(), 0x92, 50);
        var secondReply = new byte[46]; secondReply[0] = 0x13; secondReply[1] = 1; secondReply[9] = 0x20; secondReply[13] = 0x20;
        secondReply.AsSpan(14, 4).Fill(0xff); secondReply[19] = 0x3c; secondReply[21] = 0x3c; secondReply[29] = 1;
        Send(secondReply); Expect(Receive(), 0x93, 8); handshake = false;
    }
    private static void Expect(byte[] packet, byte type, int length)
    {
        if (packet.Length != length || packet[0] != type) throw new ProtocolException($"NET handshake: expected 0x{type:X2}, {length} bytes, received {packet.Length} bytes: {Convert.ToHexString(packet.AsSpan(0, Math.Min(8, packet.Length)))}");
    }
}
