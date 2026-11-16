using System.Buffers.Binary;
using System.Text;
using PiloSync.Protocol;
namespace PiloSync.Core.Tests;

internal static class DlpFixtures
{
    internal static byte[] User()
    { var b = new byte[35]; BinaryPrimitives.WriteUInt32BigEndian(b, 26203); BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), 0x12345678); b[28] = 5; Encoding.ASCII.GetBytes("IIIc").CopyTo(b, 30); return b; }
    internal static byte[] DbPage(bool more, ushort index)
    {
        var b = new byte[54]; BinaryPrimitives.WriteUInt16BigEndian(b, index); b[2] = (byte)(more ? 0x80 : 0); b[3] = 1; b[4] = 50;
        b[7] = 8; Encoding.ASCII.GetBytes("sysdgraf").CopyTo(b, 8); BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(46), index);
        Encoding.ASCII.GetBytes("Test").CopyTo(b, 48); return b;
    }
    internal static byte[] NetBytes(byte tx, byte[] data) => NetCodec.Header(1, tx, data.Length).Concat(data).ToArray();
    internal static byte[] HandshakeBytes(byte tx = 1)
    {
        var hello = new byte[22]; hello[0] = 0x90;
        var middle = new byte[50]; middle[0] = 0x92;
        return NetBytes(tx, hello).Concat(NetBytes(tx, middle)).Concat(NetBytes(tx, [0x93, 0, 0, 0, 0, 0, 0, 0])).ToArray();
    }
    internal static byte[] VersionReply()
    {
        var response = DlpCodec.Request(0x92, new DlpArgument(0x20, new byte[10]), new DlpArgument(0x21, [0, 1, 0, 2, 0, 1, 0, 2]));
        return response[..2].Concat(new byte[] { 0, 0 }).Concat(response[2..]).ToArray();
    }
    internal static byte[] Reply(byte cmd, byte[]? data = null, ushort error = 0)
    {
        if (data == null) return [(byte)(cmd | 0x80), 0, (byte)(error >> 8), (byte)error];
        return new byte[] { (byte)(cmd | 0x80), 1, 0, 0 }.Concat(DlpCodec.Request(0, new DlpArgument(0x20, data))[2..]).ToArray();
    }
}
