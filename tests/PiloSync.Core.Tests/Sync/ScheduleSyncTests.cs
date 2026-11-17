using System.Buffers.Binary;
using PiloSync.Protocol;
using Xunit;
using static PiloSync.Core.Tests.DlpFixtures;

namespace PiloSync.Core.Tests;

public class ScheduleSyncTests
{
    [Fact]
    public void WeekdayBuildUsesSundayZeroMondayOneAndEnables()
    {
        var settings = new ScheduleSettings([new(60, [DayOfWeek.Monday]), new(780, [DayOfWeek.Sunday])], []);
        var data = ScheduleSync.Build(settings);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2)));
        Assert.Equal(780, BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(14)));
        Assert.Equal(60, BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(30)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(126)));
        Assert.True(ScheduleSync.Matches(data, settings));
        data[126] = 0; data[127] = 0;
        Assert.False(ScheduleSync.Matches(data, settings));
    }
    private static readonly int[] Times = [60, 300, 540];
    private static ScheduleSettings Settings(IEnumerable<int> times) => new(times.Select(t => new ScheduleTime(t, Enum.GetValues<DayOfWeek>())).ToArray(), []);
    private static byte[] Stored(int count, ushort filler, ushort enabled = 1)
    {
        var b = new byte[128]; BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(126), enabled);
        for (int day = 0; day < 7; day++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(day * 2), (ushort)count);
            for (int i = 0; i < 8; i++) BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(14 + (day * 8 + i) * 2), i < Times.Length ? (ushort)Times[i] : filler);
        }
        return b;
    }
    private static byte[] PreferenceReply(byte[] data, ushort version = 1)
    {
        var b = new byte[6 + data.Length]; BinaryPrimitives.WriteUInt16BigEndian(b, version);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), (ushort)data.Length); BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), (ushort)data.Length); data.CopyTo(b, 6);
        return Reply(0x34, b);
    }
    [Fact]
    public void BuildReplacesEveryWeekdayKeepsEnabledAndZeroesUnusedSlots()
    {
        var built = ScheduleSync.Build(Settings(Times));
        Assert.Equal(Stored(3, 0, 1), built);
        Assert.Equal(128, built.Length);
    }
    [Fact]
    public void BuildRejectsMoreThanEightTimes()
    {
        Assert.Throws<ArgumentException>(() => ScheduleSync.Build(Settings(Enumerable.Range(0, 9))));
    }
    [Fact]
    public void MatchesComparesCountAndUsedSlotsOnly()
    {
        Assert.True(ScheduleSync.Matches(Stored(3, 999), Settings(Times)));
        Assert.False(ScheduleSync.Matches(Stored(2, 999), Settings(Times)));
        Assert.False(ScheduleSync.Matches(Stored(3, 0), Settings([60, 300, 541])));
        var oneDay = Stored(3, 0); oneDay[14 + 6 * 16 + 2] ^= 1;
        Assert.False(ScheduleSync.Matches(oneDay, Settings(Times)));
    }
    [Theory]
    [InlineData(2.5, 3)] [InlineData(-2.5, -3)] [InlineData(1.4, 1)] [InlineData(0, 0)]
    public void SkewIsDeviceMinusPcRoundedToSeconds(double seconds, int expected)
    {
        var pc = new DateTime(2026, 11, 1, 12, 0, 0);
        Assert.Equal(expected, ScheduleSync.Skew(pc.AddSeconds(seconds), pc));
    }
    [Fact]
    public void EmptyScheduleSendsNothing()
    {
        var fake = new Fake(_ => throw new InvalidOperationException()); var lines = new List<string>();
        ScheduleSync.Apply(new DlpClient(fake), Settings([]), ScheduleDeviceState.On, lines.Add);
        Assert.Empty(fake.Requests); Assert.Empty(lines);
    }
    [Theory]
    [InlineData(100, 1, "HotSync の時刻を設定しませんでした：PSas の版 1、サイズ 100 は未対応です")]
    [InlineData(128, 2, "HotSync の時刻を設定しませんでした：PSas の版 2、サイズ 128 は未対応です")]
    public void UnsupportedPreferenceIsLeftAloneWithOneLine(int size, ushort version, string expected)
    {
        var fake = new Fake(_ => PreferenceReply(new byte[size], version)); var lines = new List<string>();
        ScheduleSync.Apply(new DlpClient(fake), Settings(Times), ScheduleDeviceState.On, lines.Add);
        Assert.Equal(new[] { expected }, lines); Assert.Single(fake.Requests);
    }
    [Fact]
    public void DlpAndProtocolErrorsAreLoggedButTransportErrorsEscape()
    {
        var lines = new List<string>();
        ScheduleSync.Apply(new DlpClient(new Fake(_ => Reply(0x34, error: 3))), Settings(Times), ScheduleDeviceState.On, lines.Add);
        ScheduleSync.Apply(new DlpClient(new Fake(_ => new byte[] { 0 })), Settings(Times), ScheduleDeviceState.On, lines.Add);
        Assert.Equal(2, lines.Count);
        Assert.Equal("HotSync の時刻の設定に失敗：DLP dlpErrMemory (0x0003)", lines[0]);
        Assert.StartsWith("HotSync の時刻の設定に失敗：", lines[1]);
        Assert.Throws<IOException>(() => ScheduleSync.Apply(new DlpClient(new Fake(_ => throw new IOException("timeout"))), Settings(Times), ScheduleDeviceState.On, lines.Add));
        Assert.Equal(2, lines.Count);
    }
    [Fact]
    public void ClockReadFailureAfterWriteIsLogged()
    {
        var lines = new List<string>();
        var fake = new Fake(req => req[0] switch { 0x34 => PreferenceReply(Stored(1, 0)), 0x13 => Reply(0x13, error: 3), _ => Reply(req[0]) });
        ScheduleSync.Apply(new DlpClient(fake), Settings(Times), ScheduleDeviceState.On, lines.Add);
        Assert.Equal(new[] { 0x34, 0x35, 0x13 }, fake.Requests.Select(r => (int)r[0]));
        Assert.Equal(new[] { "HotSync の時刻の設定に失敗：DLP dlpErrMemory (0x0003)" }, lines);
    }
    [Fact]
    public void ApplyReadsOnlyThePreferenceSize()
    {
        var fake = new Fake(req => req[0] == 0x34 ? PreferenceReply(Stored(1, 0)) : Reply(req[0]));
        ScheduleSync.Apply(new DlpClient(fake), Settings(Times), ScheduleDeviceState.On, _ => { });
        Assert.Equal(Convert.FromHexString("3401200A" + "50536173" + "0000" + "0080" + "80" + "00"), fake.Requests[0]);
    }
}
