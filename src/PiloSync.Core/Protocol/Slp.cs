using System.Buffers.Binary;
namespace PiloSync.Protocol;

public sealed record SlpPacket(byte Destination, byte Source, byte Type, byte Transaction, byte[] Data);
public static class SlpCodec
{
    public static ushort Crc16(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0; // CRC-16/XMODEM: CCITT polynomial, initial value zero.
        foreach (byte b in bytes)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++) crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        return crc;
    }
    public static byte[] Encode(SlpPacket p)
    {
        if (p.Data.Length > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(p));
        var b = new byte[12 + p.Data.Length];
        b[0] = 0xbe; b[1] = 0xef; b[2] = 0xed; b[3] = p.Destination; b[4] = p.Source; b[5] = p.Type;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(6), (ushort)p.Data.Length); b[8] = p.Transaction;
        for (int i = 0; i < 9; i++) b[9] = unchecked((byte)(b[9] + b[i]));
        p.Data.CopyTo(b, 10);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(b.Length - 2), Crc16(b.AsSpan(0, b.Length - 2)));
        return b;
    }
    public static SlpPacket Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length < 12 || b[0] != 0xbe || b[1] != 0xef || b[2] != 0xed) throw new ProtocolException("SLP signature/length");
        byte sum = 0; for (int i = 0; i < 9; i++) sum = unchecked((byte)(sum + b[i]));
        if (sum != b[9]) throw new ProtocolException("SLP header checksum");
        int n = BinaryPrimitives.ReadUInt16BigEndian(b[6..]);
        if (b.Length != n + 12) throw new ProtocolException("SLP length");
        if (Crc16(b[..^2]) != BinaryPrimitives.ReadUInt16BigEndian(b[^2..])) throw new ProtocolException("SLP CRC-16-CCITT");
        return new(b[3], b[4], b[5], b[8], b.Slice(10, n).ToArray());
    }
    internal static SlpPacket Read(Wire wire, long deadline)
    {
        var h = wire.Read(10, deadline);
        if (h[0] != 0xbe || h[1] != 0xef || h[2] != 0xed) throw new ProtocolException("SLP signature");
        byte sum = 0; for (int i = 0; i < 9; i++) sum = unchecked((byte)(sum + h[i]));
        if (sum != h[9]) throw new ProtocolException("SLP header checksum");
        var tail = wire.Read(BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(6)) + 2, deadline);
        return Decode(h.Concat(tail).ToArray());
    }
}
