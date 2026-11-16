using static PiloSync.Core.Tests.DbFixtures;
using System.Buffers.Binary;
using PiloSync.Protocol;
using Xunit;
using static PiloSync.Core.Tests.DlpFixtures;
namespace PiloSync.Core.Tests;

public class DlpClientTests
{
    [Fact]
    public void DlpUserAndDbOffsets()
    {
        var u = DlpClient.ParseUserInfo(User()); Assert.Equal("IIIc", u.Name); Assert.Equal(26203u, u.UserId); Assert.Equal(0x12345678u, u.LastSyncPc);
        var db = Assert.Single(DlpClient.ParseDBList(DbPage(false, 7))); Assert.Equal("Test", db.Name); Assert.Equal("sysd", db.Type); Assert.Equal("graf", db.Creator); Assert.Equal(8, db.Attributes); Assert.Equal(7, db.Index);
    }
    [Fact]
    public void DatabaseModificationDateUsesPalmCalendarFields()
    {
        var b = DbPage(false, 0); Convert.FromHexString("07EA0A010C000000").CopyTo(b, 30);
        Assert.Equal(new DateTime(2026, 10, 1, 12, 0, 0), Assert.Single(DlpClient.ParseDBList(b)).Modified);
    }
    private sealed class Replies(params byte[][] replies) : IPacketTransport
    {
        private readonly Queue<byte[]> pending = new(replies);
        public List<byte[]> Requests { get; } = new();
        public void Send(ReadOnlySpan<byte> data) => Requests.Add(data.ToArray());
        public byte[] Receive() => pending.Dequeue();
    }
    [Fact]
    public void MultipleDbRequestParsesEntriesAndContinuesAtLastIndex()
    {
        var page = DbPage(true, 7).Concat(DbPage(false, 7)[4..]).ToArray(); page[3] = 2;
        var transport = new Replies(VersionReply(), Reply(0x16, page), Reply(0x16, DbPage(false, 8)));
        var dlp = new DlpClient(transport); dlp.ReadSysInfo(); Assert.Equal(3, dlp.ReadDBList().Count);
        Assert.Equal(Convert.FromHexString("16012004A0000000"), transport.Requests[1]);
        Assert.Equal(Convert.FromHexString("16012004A0000008"), transport.Requests[2]);
    }
    [Theory]
    [InlineData(4)]
    [InlineData(13)]
    public void MultipleDbUnsupportedRetriesWithoutFlag(byte error)
    {
        var transport = new Replies(VersionReply(), [0x96, 0, 0, error], Reply(0x16, DbPage(false, 0)));
        var dlp = new DlpClient(transport); dlp.ReadSysInfo(); Assert.Single(dlp.ReadDBList());
        Assert.Equal(0xa0, transport.Requests[1][4]); Assert.Equal(Convert.FromHexString("1601200480000000"), transport.Requests[2]);
    }
    [Fact]
    public void DlpCountersIncludeErrorAndMalformedResponses()
    {
        int calls = 0;
        var fake = new Fake(req => ++calls switch { 1 => Reply(req[0]), 2 => Reply(req[0], error: 5), _ => new byte[] { 0 } });
        var dlp = new DlpClient(fake);
        Assert.Equal(0, dlp.RequestCount); Assert.Equal(0, dlp.SentBytes); Assert.Equal(0, dlp.ReceivedBytes);
        dlp.OpenConduit();
        Assert.Throws<DlpException>(() => dlp.EndOfSync());
        Assert.Throws<ProtocolException>(() => dlp.OpenConduit());
        Assert.Equal(3, dlp.RequestCount); Assert.Equal(fake.Requests.Sum(r => r.Length), dlp.SentBytes); Assert.Equal(9, dlp.ReceivedBytes);
    }
    [Fact]
    public void DlpCountersRetainSentBytesOnReceiveFailure()
    {
        var fake = new Fake(_ => throw new IOException("disconnect")); var dlp = new DlpClient(fake);
        Assert.Throws<IOException>(() => dlp.OpenConduit());
        Assert.Equal(1, dlp.RequestCount); Assert.Equal(2, dlp.SentBytes); Assert.Equal(0, dlp.ReceivedBytes);
    }
    [Fact]
    public void RecordReadWriteWireFormat()
    {
        var fake = new Fake(req => req[0] == 0x20 ? Reply(0x20, Convert.FromHexString("001234560007000250030405")) : Reply(0x21, Convert.FromHexString("00123456")));
        var dlp = new DlpClient(fake); var r = dlp.ReadRecord(2, 7); Assert.Equal(0x123456u, r.UniqueId); Assert.Equal(3, r.Category); Assert.Equal(new byte[] {4,5}, r.Data); Assert.Equal(0x123456u, dlp.WriteRecord(2, r));
        Assert.Equal(Convert.FromHexString("200121080200000700008000"), fake.Requests[0]); Assert.Equal(Convert.FromHexString("2101200A02800012345650030405"), fake.Requests[1]);
    }
    [Fact]
    public void CreateAndWriteUserInfoWireFormatAndDate()
    {
        var fake = new Fake(req => Reply(req[0], req[0] == 0x18 ? [7] : null)); var dlp = new DlpClient(fake);
        Assert.Equal(7, dlp.CreateDB("DATA", "TEST", 0, 8, 2, "A"));
        Assert.Equal(Convert.FromHexString("1801201054455354444154410000000800024100"), fake.Requests[0]);
        dlp.WriteUserInfo(new("たっち", 1, 2, 3, When, When), UserInfoChanges.All);
        Assert.Equal(Convert.FromHexString("1101201D00000001000000020000000307EA0A190C223800F80782BD82C182BF00"), fake.Requests[1]);
        Assert.Equal(Convert.FromHexString("07EA0A190C223800"), DlpClient.EncodeDate(When)); Assert.Equal(new byte[8], DlpClient.EncodeDate(null));
        var user = new byte[37]; fake.Requests[1].AsSpan(4, 12).CopyTo(user); DlpClient.EncodeDate(When).CopyTo(user, 12); DlpClient.EncodeDate(When.AddDays(-1)).CopyTo(user, 20); user[28] = 7; PalmText.Encode("たっち\0").CopyTo(user, 30);
        var parsed = DlpClient.ParseUserInfo(user); Assert.Equal(When, parsed.SuccessfulSync); Assert.Equal(When.AddDays(-1), parsed.LastSync); Assert.Equal("たっち", parsed.Name);
    }
    [Fact]
    public void LargeRecordReadsInChunks()
    {
        var fake = new Fake(req => { int offset = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(8)); int n = offset == 0 ? 32768 : 2; var b = new byte[10+n]; b[3]=1; BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(6),32770); b.AsSpan(10).Fill(42); return Reply(0x20,b); });
        Assert.Equal(32770, new DlpClient(fake).ReadRecord(2,0).Data.Length); Assert.Equal(2, fake.Requests.Count);
    }
    [Fact]
    public void BadItemResponseThrowsProtocolException()
    { var fake = new Fake(_ => Reply(0x20, new byte[10])); Assert.Throws<ProtocolException>(() => new DlpClient(fake).ReadRecord(1,2)); }
    [Fact]
    public void StorageUnsupportedIsOptionalButTransportFailureEscapes()
    { Assert.Null(new DlpClient(new Fake(_ => Reply(0x15,error:13))).ReadStorageInfo()); Assert.Throws<IOException>(() => new DlpClient(new Fake(_ => throw new IOException("disconnect"))).ReadStorageInfo()); }

    [Fact]
    public void ResourceReadWriteWireFormat()
    {
        var fake = new Fake(req => req[0] == 0x23 ? Reply(0x23, Convert.FromHexString("636F64650005000200020405")) : Reply(0x24));
        var dlp = new DlpClient(fake); var r = dlp.ReadResource(1, 2); Assert.Equal("code", r.Type); Assert.Equal(5, r.Id); dlp.WriteResource(1, r);
        Assert.Equal(Convert.FromHexString("230120080100000200008000"), fake.Requests[0]);
        Assert.Equal(Convert.FromHexString("2401200C0100636F6465000500020405"), fake.Requests[1]);
    }
    [Fact]
    public void BlocksCanReturnPartialData()
    {
        int reads = 0;
        var fake = new Fake(req => req[0] == 0x1b ? Reply(0x1b, ++reads == 1 ? [0,4,1,2] : [0,4,3,4]) : Reply(req[0]));
        var dlp = new DlpClient(fake); Assert.Equal(new byte[]{1,2,3,4}, dlp.ReadAppBlock(1));
        Assert.Equal(Convert.FromHexString("1B012006010000028000")[..8], fake.Requests[1][..8]);
        dlp.WriteSortBlock(1,[5,6]); Assert.Equal(Convert.FromHexString("1E012006010000020506"),fake.Requests[^1]);
    }
    [Fact]
    public void UserInfoWritesOnlyLastSyncDate()
    {
        var fake=new Fake(_=>Reply(0x11)); var dlp=new DlpClient(fake);
        dlp.WriteUserInfo(new("A",1,2,3,When.AddDays(-1),When)); Assert.Single(fake.Requests); Assert.Equal(DlpClient.EncodeDate(When), fake.Requests[0][16..24]);
        dlp.WriteUserInfo(new("A",1,2,3,When,null),UserInfoChanges.SyncDate); Assert.Equal(new byte[8], fake.Requests[1][16..24]);
    }
    [Fact]
    public void ShortSysInfoReplyListsDatabasesWithoutMultipleFlag()
    {
        var shortReply = Reply(0x12, new byte[10]);
        var v11 = DlpCodec.Request(0x92, new DlpArgument(0x20, new byte[10]), new DlpArgument(0x21, [0, 1, 0, 1, 0, 1, 0, 1]));
        foreach (var sys in new[] { shortReply, v11[..2].Concat(new byte[] { 0, 0 }).Concat(v11[2..]).ToArray() })
        {
            var transport = new Replies(sys, Reply(0x16, DbPage(false, 0)));
            var dlp = new DlpClient(transport); dlp.ReadSysInfo(); Assert.Single(dlp.ReadDBList());
            Assert.Equal(Convert.FromHexString("1601200480000000"), transport.Requests[1]);
        }
    }
    [Fact]
    public void SysDateTimeReadAndSetWireFormat()
    {
        var fake = new Fake(req => req[0] == 0x13 ? Reply(0x13, DlpClient.EncodeDate(When)) : Reply(0x14)); var dlp = new DlpClient(fake);
        Assert.Equal(When, dlp.ReadSysDateTime()); dlp.SetSysDateTime(When);
        Assert.Equal(Convert.FromHexString("1300"), fake.Requests[0]);
        Assert.Equal(Convert.FromHexString("14012008" + "07EA0A190C223800"), fake.Requests[1]);
    }
    [Fact]
    public void EmptySysDateTimeThrowsProtocolException()
    { Assert.Throws<ProtocolException>(() => new DlpClient(new Fake(_ => Reply(0x13, new byte[8]))).ReadSysDateTime()); }
    [Theory]
    [InlineData(true, "80")] [InlineData(false, "00")]
    public void AppPreferenceReadWireFormatAndResponse(bool saved, string flags)
    {
        var fake = new Fake(_ => Reply(0x34, Convert.FromHexString("0001" + "0080" + "0004" + "AABBCCDD"))); var dlp = new DlpClient(fake);
        var p = dlp.ReadAppPreference("PSas", 0, saved);
        Assert.NotNull(p); Assert.Equal(1, p.Version); Assert.Equal(0x80, p.ActualSize); Assert.Equal(Convert.FromHexString("AABBCCDD"), p.Data);
        Assert.Equal(Convert.FromHexString("3401200A" + "50536173" + "0000" + "FFFF" + flags + "00"), fake.Requests[0]);
    }
    [Fact]
    public void AppPreferenceReadWireFormatWithMaxBytes()
    {
        var fake = new Fake(_ => Reply(0x34, Convert.FromHexString("0001" + "0080" + "0004" + "AABBCCDD"))); var dlp = new DlpClient(fake);
        Assert.NotNull(dlp.ReadAppPreference("PSas", 0, maxBytes: 128));
        Assert.Equal(Convert.FromHexString("3401200A" + "50536173" + "0000" + "0080" + "80" + "00"), fake.Requests[0]);
    }
    [Fact]
    public void AppPreferenceNotFoundIsNullButOtherErrorsThrow()
    {
        Assert.Null(new DlpClient(new Fake(_ => Reply(0x34, error: 5))).ReadAppPreference("PSas", 0));
        Assert.Throws<DlpException>(() => new DlpClient(new Fake(_ => Reply(0x34, error: 3))).ReadAppPreference("PSas", 0));
        Assert.Throws<ProtocolException>(() => new DlpClient(new Fake(_ => Reply(0x34, Convert.FromHexString("000100800005AABB")))).ReadAppPreference("PSas", 0));
    }
    [Fact]
    public void AppPreferenceWriteWireFormat()
    {
        var fake = new Fake(_ => Reply(0x35)); var dlp = new DlpClient(fake);
        dlp.WriteAppPreference("PSas", 0, 1, Convert.FromHexString("AABBCCDD"));
        Assert.Equal(Convert.FromHexString("35012010" + "50536173" + "0000" + "0001" + "0004" + "8000" + "AABBCCDD"), fake.Requests[0]);
    }
    [Fact]
    public void DbListParsesRemainingDatesAndMisc()
    {
        var b=DatabasePage("\u305f\u3063\u3061"); b[5]=0x40; DlpClient.EncodeDate(When).CopyTo(b,22); DlpClient.EncodeDate(When.AddDays(1)).CopyTo(b,38);
        var db=Assert.Single(DlpClient.ParseDBList(b)); Assert.Equal(0x40,db.MiscFlags); Assert.Equal(When,db.Created); Assert.Equal(When.AddDays(1),db.BackupDate);
    }

}
