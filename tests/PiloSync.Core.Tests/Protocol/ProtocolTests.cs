using PiloSync.Protocol;
using Xunit;
namespace PiloSync.Core.Tests;

public class ProtocolTests
{
    [Fact] public void CrcHasIndependentKnownVector() => Assert.Equal(0x31c3, SlpCodec.Crc16("123456789"u8));
    [Fact]
    public void SlpRoundTripAndHeaderChecksum()
    {
        var packet = new SlpPacket(3, 3, 2, 0xff, [1, 2, 3]); var b = SlpCodec.Encode(packet);
        Assert.Equal(Convert.FromHexString("BEEFED0303020003FFA4"), b[..10]);
        var decoded = SlpCodec.Decode(b); Assert.Equal(packet.Transaction, decoded.Transaction); Assert.Equal(packet.Data, decoded.Data);
    }
    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(14)]
    public void SlpRejectsCorruption(int index)
    { var b = SlpCodec.Encode(new(3, 3, 2, 1, [4, 5, 6])); b[index] ^= 1; Assert.Throws<ProtocolException>(() => SlpCodec.Decode(b)); }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1024)]
    [InlineData(1025)]
    [InlineData(5000)]
    public void PadpFragmentsRoundTrip(int size)
    {
        var bytes = Enumerable.Range(0, size).Select(i => (byte)i).ToArray(); var pieces = PadpCodec.Fragment(bytes).ToArray();
        Assert.True((pieces[0].Flags & 0x80) != 0); Assert.True((pieces[^1].Flags & 0x40) != 0);
        int offset = 0; foreach (var f in pieces) { var d = PadpCodec.Decode(PadpCodec.Encode(f)); Assert.Equal((uint)(offset == 0 ? size : offset), d.Size); Assert.Equal(f.Data, d.Data); offset += d.Data.Length; }
        Assert.Equal(bytes, pieces.SelectMany(f => f.Data).ToArray());
    }
    [Fact]
    public void PadpLongHeaderRoundTrip()
    { var f = new PadpFragment(1, 0x90, 100000, [7]); var d = PadpCodec.Decode(PadpCodec.Encode(f)); Assert.Equal(f.Size, d.Size); Assert.Equal(f.Data, d.Data); }
    [Fact]
    public void NetHeaderUsesNetworkByteOrder()
    { Assert.Equal(Convert.FromHexString("012300001234"), NetCodec.Header(1, 0x23, 0x1234)); Assert.Equal(new byte[] { 8 }, NetCodec.Decode([1, 2, 0, 0, 0, 1, 8]).Data); }
    [Fact]
    public void NetRejectsLengthAndInvalidTickle()
    { Assert.Throws<ProtocolException>(() => NetCodec.Decode([1, 1, 0, 0, 0, 2, 8])); Assert.Throws<ProtocolException>(() => NetCodec.Decode([2, 255, 0, 0, 0, 1, 8])); }
    [Fact]
    public void DlpErrorsHaveNames()
    { var e = Assert.Throws<DlpException>(() => DlpCodec.ParseResponse(0x10, [0x90, 0, 0, 4])); Assert.Contains("dlpErrParam", e.Message); }
    [Theory]
    [InlineData(8)]
    [InlineData(300)]
    [InlineData(70000)]
    public void DlpArgumentWrappers(int size)
    { var arg = new DlpArgument(0x20, new byte[size]); var req = DlpCodec.Request(0x10, arg); var res = new byte[] { 0x90, 1, 0, 0 }.Concat(req[2..]).ToArray(); Assert.Equal(size, Assert.Single(DlpCodec.ParseResponse(0x10, res)).Data.Length); }
    [Fact] public void DlpRejectsTruncatedArgument() => Assert.Throws<ProtocolException>(() => DlpCodec.ParseResponse(0x10, [0x90, 1, 0, 0, 0x20, 10, 1]));
}
