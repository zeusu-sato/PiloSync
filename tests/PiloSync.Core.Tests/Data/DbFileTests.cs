using System.Buffers.Binary;
using PiloSync.Protocol;
using static PiloSync.Core.Tests.DlpFixtures;
using static PiloSync.Core.Tests.DbFixtures;
using Xunit;
namespace PiloSync.Core.Tests;

public class DbFileTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsDatabaseWithoutTwoByteGap(bool resource)
    {
        var original = Bytes(Sample(resource));
        int count = 2, size = resource ? 10 : 8, end = 78 + count * size;
        var compact = original[..end].Concat(original[(end + 2)..]).ToArray();
        foreach (int field in new[] { 52, 56, 78 + (resource ? 6 : 0), 78 + size + (resource ? 6 : 0) })
            BinaryPrimitives.WriteUInt32BigEndian(compact.AsSpan(field), BinaryPrimitives.ReadUInt32BigEndian(original.AsSpan(field)) - 2);
        Assert.Equal(original, Bytes(PalmDbFile.Read(new MemoryStream(compact))));
    }
    [Fact]
    public void ReadsEmptyDatabaseWithoutGap()
    {
        var bytes = Bytes(new PalmDbFile { Name = "Empty" })[..78];
        Assert.Empty(PalmDbFile.Read(new MemoryStream(bytes)).Records);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LastItemCanStartAtEndOfFile(bool resource)
    {
        var bytes = Bytes(Sample(resource));
        var db = PalmDbFile.Read(new MemoryStream(bytes));
        Assert.Equal((uint)bytes.Length, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(78 + (resource ? 16 : 8))));
        Assert.Empty(resource ? db.Resources[^1].Data : db.Records[^1].Data);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrailingBytesBelongToLastItem(bool resource)
    {
        var bytes = Bytes(Sample(resource)).Concat(new byte[] { 9, 8, 7 }).ToArray();
        var db = PalmDbFile.Read(new MemoryStream(bytes));
        Assert.Equal(new byte[] { 9, 8, 7 }, resource ? db.Resources[^1].Data : db.Records[^1].Data);
    }
    [Theory]
    [InlineData(1u)]
    [InlineData(0xffffffffu)]
    public void OutOfRangeOffsetIdentifiesRecordAndBounds(uint value)
    {
        var bytes = Bytes(Sample());
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(86), value);
        string message = Assert.Throws<InvalidDataException>(() => PalmDbFile.Read(new MemoryStream(bytes))).Message;
        Assert.Contains($"record 1 offset 0x{value:X8}", message);
        Assert.Contains($"valid 0x0000005E..0x{bytes.Length:X8}", message);
    }
    [Theory]
    [InlineData(52, "AppInfo")]
    [InlineData(56, "SortInfo")]
    public void OutOfRangeInfoOffsetIdentifiesBlock(int field, string label)
    {
        var bytes = Bytes(Sample()); BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(field), 1);
        Assert.Contains(label, Assert.Throws<InvalidDataException>(() => PalmDbFile.Read(new MemoryStream(bytes))).Message);
    }
    [Fact]
    public void ReversedOffsetsIdentifyBothRecords()
    {
        var bytes = Bytes(Sample()); BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(86), 98);
        string message = Assert.Throws<InvalidDataException>(() => PalmDbFile.Read(new MemoryStream(bytes))).Message;
        Assert.Contains("record 0 offset 0x00000063 > record 1", message);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DatabaseRoundTripIsByteExact(bool resource)
    {
        var original = Bytes(Sample(resource)); var parsed = PalmDbFile.Read(new MemoryStream(original));
        Assert.Equal(original, Bytes(parsed)); Assert.Equal("たっち", parsed.Name); Assert.Equal(When, parsed.Modified); Assert.Equal(resource, parsed.IsResource);
    }
    [Fact]
    public void IndependentPdbFixtureHasBigEndianFieldsAndThreeByteId()
    {
        var b = new byte[91]; Convert.FromHexString("82BD82C182BF").CopyTo(b, 0);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(34), 0x1234); BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(36), 86400);
        "DATA"u8.CopyTo(b.AsSpan(60)); "TEST"u8.CopyTo(b.AsSpan(64)); b[77] = 1; b[81] = 88; b[82] = 0x53; b[83] = 0x12; b[84] = 0x34; b[85] = 0x56; b[88] = 42; b[89] = 43; b[90] = 44;
        var db = PalmDbFile.Read(new MemoryStream(b)); Assert.Equal("たっち", db.Name); Assert.Equal(0x1234, db.Version); Assert.Equal(new DateTime(1904, 1, 2), db.Created);
        var r = Assert.Single(db.Records); Assert.Equal(0x123456u, r.UniqueId); Assert.Equal(0x50, r.Attributes); Assert.Equal(3, r.Category); Assert.Equal(new byte[] {42,43,44}, r.Data);
    }
    [Theory]
    [InlineData(0)] [InlineData(77)] [InlineData(79)] [InlineData(85)]
    public void ShortFileThrows(int length) => Assert.Throws<InvalidDataException>(() => PalmDbFile.Read(new MemoryStream(Bytes(Sample())[..length])));
    [Theory]
    [InlineData(52, 1u)] [InlineData(56, 0xffffffffu)] [InlineData(72, 1u)] [InlineData(78, 1u)]
    public void BadOffsetsOrChainedListsThrow(int field, uint value)
    { var b = Bytes(Sample()); BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(field), value); Assert.Throws<InvalidDataException>(() => PalmDbFile.Read(new MemoryStream(b))); }
    [Fact]
    public void Cp932StopsAtNulAndEncodesJapanese()
    { var b = Convert.FromHexString("82BD82C182BF"); Assert.Equal("たっち", PalmText.Decode(b)); Assert.Equal(b, PalmText.Encode("たっち")); Assert.Equal("たっち", PalmText.Decode(b.Concat(new byte[] {0,65}).ToArray())); Assert.Equal(932, PalmText.Encoding.CodePage); }
    [Theory]
    [InlineData("a\\/:*?\"<>|b", false, "DATA", "a_________b.pdb")]
    [InlineData("CON", false, "DATA", "_CON.pdb")]
    [InlineData("con.txt", false, "DATA", "_con.txt.pdb")]
    [InlineData("LPT9", false, "DATA", "_LPT9.pdb")]
    [InlineData("COM1", true, "DATA", "_COM1.prc")]
    [InlineData("COM10", false, "DATA", "COM10.pdb")]
    [InlineData("... ", false, "DATA", "_.pdb")]
    [InlineData("a. ", false, "pqa ", "a.pqa")]
    [InlineData("たっち", true, "pqa ", "たっち.prc")]
    [InlineData("a\u0001b", false, "DATA", "a_b.pdb")]
    public void SafeBackupNames(string name, bool resource, string type, string expected) => Assert.Equal(expected, BackupFileName.For(name, resource, type, "TEST"));

    [Fact]
    public void IndependentPrcFixtureParsesTypeIdAndOffset()
    {
        var b=new byte[92]; b[0]=65; b[33]=1; "appl"u8.CopyTo(b.AsSpan(60)); "TEST"u8.CopyTo(b.AsSpan(64)); b[77]=1;
        "code"u8.CopyTo(b.AsSpan(78)); b[82]=0x12; b[83]=0x34; b[87]=90; b[90]=42; b[91]=43;
        var db=PalmDbFile.Read(new MemoryStream(b)); var r=Assert.Single(db.Resources); Assert.Equal("code",r.Type); Assert.Equal(0x1234,r.Id); Assert.Equal(new byte[]{42,43},r.Data); Assert.Equal(b,Bytes(db));
    }
}
