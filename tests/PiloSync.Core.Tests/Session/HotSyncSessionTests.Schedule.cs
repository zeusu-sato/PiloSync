using System.Buffers.Binary;
using PiloSync.Cli;
using PiloSync.Protocol;
using Xunit;

namespace PiloSync.Core.Tests;

public sealed partial class HotSyncSessionTests
{
    private static readonly int[] Times = [60, 300, 540, 780, 1020, 1260];
    private static byte[] StoredSchedule(ushort enabled, int[] minutes, ushort filler = 0)
    {
        var b = new byte[128]; BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(126), enabled);
        for (int day = 0; day < 7; day++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(day * 2), (ushort)minutes.Length);
            for (int i = 0; i < 8; i++) BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(14 + (day * 8 + i) * 2), i < minutes.Length ? (ushort)minutes[i] : filler);
        }
        return b;
    }
    private static void AssertSchedule(byte[]? stored, int[] minutes, ushort enabled)
    {
        Assert.Equal(StoredSchedule(enabled, minutes), stored);
    }
    private bool RunWithSchedule(Device device, List<string> lines, IReadOnlyList<int> schedule, Action<bool, string>? completed = null) =>
        HotSyncSession.Run(new(device), Registry(), lines.Add, () => Assert.Fail("abort"), completed: completed, schedule: schedule);
    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void ScheduleIsWrittenKeepingEnabledAndClockIsSet(ushort enabled)
    {
        var device = new Device { Preference = StoredSchedule(enabled, [480], 999) }; var lines = new List<string>();
        var before = DateTime.Now.AddSeconds(-1);
        Assert.True(RunWithSchedule(device, lines, Times));
        var after = DateTime.Now.AddSeconds(1);
        AssertSchedule(device.Preference, Times, enabled);
        var write = Assert.Single(device.Requests, r => r[0] == 0x35);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(write.AsSpan(10))); Assert.Equal(0x80, write[14]);
        var set = Assert.Single(device.Requests, r => r[0] == 0x14);
        var clock = new DateTime(BinaryPrimitives.ReadUInt16BigEndian(set.AsSpan(4)), set[6], set[7], set[8], set[9], set[10]);
        Assert.InRange(clock, before, after);
        Assert.Single(lines, l => System.Text.RegularExpressions.Regex.IsMatch(l, @"HotSync の時刻を設定 1:00, 5:00, 9:00, 13:00, 17:00, 21:00 / 時計のずれ [+-]\d+ 秒$"));
    }
    [Fact]
    public void EmptyScheduleSendsNoScheduleRequests()
    {
        var device = new Device { Preference = StoredSchedule(1, [480]) }; var lines = new List<string>();
        Assert.True(RunWithSchedule(device, lines, []));
        Assert.DoesNotContain(device.Requests, r => r[0] is 0x13 or 0x14 or 0x34 or 0x35);
        Assert.DoesNotContain(lines, l => l.Contains("HotSync の時刻") || l.Contains("時計"));
    }
    [Fact]
    public void MissingPreferenceChangesNothing()
    {
        var device = new Device(); var lines = new List<string>();
        Assert.True(RunWithSchedule(device, lines, Times));
        Assert.Single(device.Requests, r => r[0] == 0x34);
        Assert.DoesNotContain(device.Requests, r => r[0] is 0x13 or 0x14 or 0x35);
        Assert.DoesNotContain(lines, l => l.Contains("HotSync の時刻") || l.Contains("時計"));
    }
    [Fact]
    public void UnchangedScheduleIgnoresUnusedSlotsAndStillSetsClock()
    {
        var device = new Device { Preference = StoredSchedule(1, Times, 777) }; var lines = new List<string>();
        Assert.True(RunWithSchedule(device, lines, Times));
        Assert.DoesNotContain(device.Requests, r => r[0] == 0x35);
        Assert.Single(device.Requests, r => r[0] == 0x13); Assert.Single(device.Requests, r => r[0] == 0x14);
        Assert.Equal(StoredSchedule(1, Times, 777), device.Preference);
        Assert.DoesNotContain(lines, l => l.Contains("HotSync の時刻") || l.Contains("時計"));
    }
    [Theory]
    [InlineData(60, @"\+(59|60) 秒を修正$")] [InlineData(-60, @"-(60|61) 秒を修正$")]
    public void UnchangedScheduleReportsLargeClockSkew(int seconds, string pattern)
    {
        var device = new Device { Preference = StoredSchedule(1, Times), ClockSkew = TimeSpan.FromSeconds(seconds) }; var lines = new List<string>();
        Assert.True(RunWithSchedule(device, lines, Times));
        Assert.DoesNotContain(device.Requests, r => r[0] == 0x35);
        Assert.Single(lines, l => System.Text.RegularExpressions.Regex.IsMatch(l, "時計のずれ " + pattern));
    }
    [Fact]
    public void ScheduleFailureDoesNotStopHotSync()
    {
        var device = new Device { Preference = StoredSchedule(1, [480]), PreferenceWriteError = 3, TidyFailure = "ok" }; var lines = new List<string>(); bool? completed = null;
        Assert.True(RunWithSchedule(device, lines, Times, (ok, _) => completed = ok));
        Assert.Contains(lines, l => l.EndsWith("HotSync の時刻の設定に失敗：DLP dlpErrMemory (0x0003)"));
        Assert.DoesNotContain(device.Requests, r => r[0] == 0x14);
        Assert.Contains(device.Requests, r => r[0] == 0x17); Assert.Equal(0x2f, device.Requests[^1][0]);
        Assert.True(completed); Assert.EndsWith("終了（正常）", lines[^1]);
    }
    [Fact]
    public void ScheduleRunsAfterInstallAndBeforeBackup()
    {
        var registry = Registry(); string install = Path.Combine(root, "User", "Install"); Directory.CreateDirectory(install);
        using (var output = File.Create(Path.Combine(install, "good.pdb"))) new PalmDbFile { Name = "Data", Type = "DATA", Creator = "TEST" }.Write(output);
        var device = new Device { Preference = StoredSchedule(1, [480]), TidyFailure = "ok" };
        Assert.True(HotSyncSession.Run(new(device), registry, _ => { }, () => Assert.Fail("abort"), schedule: Times));
        int created = device.Requests.FindIndex(r => r[0] == 0x18), closed = device.Requests.FindIndex(r => r[0] == 0x19);
        int first = device.Requests.FindIndex(r => r[0] == 0x34), clock = device.Requests.FindIndex(r => r[0] == 0x14);
        int backupList = Enumerable.Range(0, device.Requests.Count).Where(i => device.Requests[i][0] == 0x16).ElementAt(2), backup = device.Requests.FindIndex(r => r[0] == 0x17);
        Assert.True(created >= 0 && closed > created); Assert.True(first > closed); Assert.True(clock > first);
        Assert.True(backupList > clock); Assert.True(backup > backupList);
    }
    [Fact]
    public void RestoreDoesNotApplySchedule()
    {
        var registry = Registry(); var store = new AccountStore(root);
        store.Data.Users.Add(new() { Id = 301, Name = "Target", Folder = "Target" }); store.Save();
        var device = new Device { Preference = StoredSchedule(1, [480]), StorageUnavailable = true };
        Assert.True(HotSyncSession.Run(new(device), registry, _ => { }, () => Assert.Fail("abort"), restore: new("Target"), schedule: Times));
        Assert.DoesNotContain(device.Requests, r => r[0] is 0x13 or 0x14 or 0x34 or 0x35);
    }
}
