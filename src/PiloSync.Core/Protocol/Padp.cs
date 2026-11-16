using System.Buffers.Binary;
using PiloSync.Transport;
namespace PiloSync.Protocol;

public interface IPacketTransport
{
    void Send(ReadOnlySpan<byte> data);
    byte[] Receive();
    byte[] Receive(int timeoutMs) => Receive();
}
public sealed record PadpFragment(byte Type, byte Flags, uint Size, byte[] Data);
public static class PadpCodec
{
    public static byte[] Encode(PadpFragment f)
    {
        bool large = (f.Flags & 0x10) != 0;
        if (!large && f.Size > 65535) throw new ArgumentOutOfRangeException(nameof(f));
        byte[] b = new byte[(large ? 6 : 4) + f.Data.Length]; b[0] = f.Type; b[1] = f.Flags;
        if (large) BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(2), f.Size);
        else BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), (ushort)f.Size);
        f.Data.CopyTo(b, large ? 6 : 4); return b;
    }
    public static PadpFragment Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4) throw new ProtocolException("PADP short header");
        int h = (b[1] & 0x10) != 0 ? 6 : 4;
        if (b.Length < h) throw new ProtocolException("PADP short long header");
        return new(b[0], b[1], h == 6 ? BinaryPrimitives.ReadUInt32BigEndian(b[2..]) : BinaryPrimitives.ReadUInt16BigEndian(b[2..]), b[h..].ToArray());
    }
    public static IEnumerable<PadpFragment> Fragment(byte[] data, int mtu = 1024)
    {
        if (data.Length > 65535 || mtu < 1) throw new ArgumentOutOfRangeException(nameof(data));
        int offset = 0;
        do
        {
            int n = Math.Min(mtu, data.Length - offset); byte flags = (byte)((offset == 0 ? 0x80 : 0) | (offset + n == data.Length ? 0x40 : 0));
            yield return new(1, flags, (uint)(offset == 0 ? data.Length : offset), data.AsSpan(offset, n).ToArray()); offset += n;
        } while (offset < data.Length);
    }
}

public sealed class PadpTransport : IPacketTransport
{
    private readonly Wire wire;
    private byte transaction = 0xff;
    private bool transactionEstablished;
    private SlpPacket? pending;
    private SlpPacket? lastReceived;
    public int RetryCount { get; init; } = 3;
    public PadpTransport(IByteChannel channel, CancellationToken cancellation = default, int timeoutMs = 10000)
        : this(new Wire(channel, cancellation, timeoutMs)) { }
    internal PadpTransport(Wire wire) { this.wire = wire; }
    public byte Transaction { get => transaction; set { transaction = value; transactionEstablished = true; } }
    public void AdvanceTransaction() => transaction = transaction >= 0xfe ? (byte)1 : (byte)(transaction + 1);
    private SlpPacket Read(long deadline)
    {
        var p = pending; pending = null; p ??= SlpCodec.Read(wire, deadline);
        if (p.Type != 2 || p.Source != 3 || p.Destination != 3) throw new ProtocolException("SLP: unexpected socket/type");
        return p;
    }
    private void Ack(SlpPacket p, PadpFragment f)
    {
        wire.Write(SlpCodec.Encode(new(3, 3, 2, p.Transaction, PadpCodec.Encode(f with { Type = 2, Data = [] }))));
    }
    private bool Duplicate(SlpPacket p) => lastReceived is { } last && last.Transaction == p.Transaction && last.Data.AsSpan().SequenceEqual(p.Data);
    public void Send(ReadOnlySpan<byte> data)
    {
        transactionEstablished = true;
        foreach (var fragment in PadpCodec.Fragment(data.ToArray()))
        {
            var packet = SlpCodec.Encode(new(3, 3, 2, transaction, PadpCodec.Encode(fragment)));
            bool accepted = false;
            for (int attempt = 0; attempt < RetryCount && !accepted; attempt++)
            {
                wire.Write(packet); long deadline = wire.Deadline();
                try
                {
                    while (true)
                    {
                        var p = Read(deadline); var f = PadpCodec.Decode(p.Data);
                        if ((f.Flags & 0x20) != 0 || f.Type == 8) throw new ProtocolException("PADP memory error/abort");
                        if (f.Type == 4) continue;
                        if (f.Type == 1 && Duplicate(p)) { Ack(p, f); continue; }
                        if (p.Transaction != transaction) throw new ProtocolException("PADP transaction mismatch");
                        if (f.Type == 2 && f.Flags == fragment.Flags && f.Size == fragment.Size && f.Data.Length == 0) { accepted = true; break; }
                        // A reply to the same request can substitute for its final lost ACK.
                        if (f.Type == 1 && (fragment.Flags & 0x40) != 0) { pending = p; accepted = true; break; }
                        throw new ProtocolException("PADP unexpected ACK");
                    }
                }
                catch (TimeoutException) when (attempt + 1 < RetryCount) { }
            }
            if (!accepted) throw new TimeoutException("PADP ACK timeout");
        }
    }
    public byte[] Receive() => ReceiveCore(null);
    public byte[] Receive(int timeoutMs) => ReceiveCore(timeoutMs);
    private byte[] ReceiveCore(int? timeoutMs)
    {
        long deadline = wire.Deadline(timeoutMs); byte[]? result = null; int offset = 0;
        while (true)
        {
            var p = Read(deadline); var f = PadpCodec.Decode(p.Data);
            if (f.Type == 4 || f.Type == 2) continue;
            if (f.Type != 1 || (f.Flags & 0x20) != 0) throw new ProtocolException("PADP data/abort");
            if (Duplicate(p)) { Ack(p, f); continue; }
            if (!transactionEstablished) { transaction = p.Transaction; transactionEstablished = true; }
            if (p.Transaction != transaction) throw new ProtocolException("PADP sequence mismatch");
            bool first = (f.Flags & 0x80) != 0;
            if (result == null)
            {
                if (!first || f.Size > 1048576) throw new ProtocolException("PADP first/size");
                result = new byte[(int)f.Size];
            }
            else if (first || f.Size != offset) throw new ProtocolException("PADP fragment offset");
            if (f.Data.Length > result.Length - offset) throw new ProtocolException("PADP fragment overflow");
            f.Data.CopyTo(result, offset); offset += f.Data.Length; Ack(p, f); lastReceived = p;
            if ((f.Flags & 0x40) != 0)
            {
                if (offset != result.Length) throw new ProtocolException("PADP incomplete message"); return result;
            }
            if (f.Data.Length == 0) throw new ProtocolException("PADP empty fragment");
        }
    }
}
