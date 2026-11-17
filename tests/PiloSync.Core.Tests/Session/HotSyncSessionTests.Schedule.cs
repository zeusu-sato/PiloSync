using System.Buffers.Binary;
using PiloSync.Cli;
using PiloSync.Protocol;
using Xunit;

namespace PiloSync.Core.Tests;

public sealed partial class HotSyncSessionTests
{
    [Theory]
    [InlineData(0, ScheduleDeviceState.Off)] [InlineData(1, ScheduleDeviceState.On)]
    public void LegacyStateIsResolvedAndRecorded(ushort enabled, ScheduleDeviceState expected)
    {
        var registry = Registry(); string path = Path.Combine(root, "schedule.json");
        File.WriteAllText(path, "{\"times\":[\"1:00\"]}");
        var device = new Device { Preference = StoredSchedule(enabled, [480]), TidyFailure = "ok" };
        Assert.True(HotSyncSession.Run(new(device), registry, _ => { }, () => Assert.Fail("abort"), schedulePath: path));
        var settings = HotSyncSchedule.LoadSettings(path);
        Assert.False(settings.Legacy); Assert.Equal(expected, settings.StateFor("User"));
        Assert.Equal(expected == ScheduleDeviceState.On ? 1 : 0, BinaryPrimitives.ReadUInt16BigEndian(device.Preference!.AsSpan(126)));
        if (expected == ScheduleDeviceState.Off) Assert.Equal(480, BinaryPrimitives.ReadUInt16BigEndian(device.Preference.AsSpan(14)));
    }
    [Fact]
    public void UnlistedDeviceIsOffAndKeepsTimes()
    {
        var registry = Registry(); string path = Path.Combine(root, "schedule.json");
        HotSyncSchedule.SaveSettings(path, new([new(60, [DayOfWeek.Monday])], []));
        var device = new Device { Preference = StoredSchedule(1, [480]), TidyFailure = "ok" };
        Assert.True(HotSyncSession.Run(new(device), registry, _ => { }, () => Assert.Fail("abort"), schedulePath: path));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(device.Preference!.AsSpan(126)));
        Assert.Equal(480, BinaryPrimitives.ReadUInt16BigEndian(device.Preference.AsSpan(14)));
    }
    [Fact]
    public void LegacyMigrationPrecedesNewAccountCreation()
    {
        var store = new AccountStore(root); store.Save();
        string path = Path.Combine(root, "schedule.json"); File.WriteAllText(path, "{\"times\":[\"1:00\"]}");
        var device = new Device { Preference = StoredSchedule(1, [480]), TidyFailure = "ok" };
        Assert.True(HotSyncSession.Run(new(device), new FakePalmRegistry { DataPath = root, PcId = 123 }, _ => { }, () => Assert.Fail("abort"), schedulePath: path));
        var settings = HotSyncSchedule.LoadSettings(path);
        Assert.Empty(settings.Devices); Assert.Equal(ScheduleDeviceState.Off, settings.StateFor("User"));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(device.Preference!.AsSpan(126)));
    }
    [Theory]
    [InlineData(0, ScheduleDeviceState.Off)] [InlineData(2, ScheduleDeviceState.On)]
    public void InheritReadsEnabledState(ushort enabled, ScheduleDeviceState expected)
    {
        var device = new Device { Preference = StoredSchedule(enabled, [480]) };
        var settings = new ScheduleSettings([new(60, Enum.GetValues<DayOfWeek>())], []);
        Assert.Equal(expected, ScheduleSync.Apply(new(device), settings, ScheduleDeviceState.Inherit, _ => { }));
    }
    [Fact]
    public void OffOnlyChangesEnabledAndAlreadyOffDoesNotWrite()
    {
        var initial = StoredSchedule(1, [480]); var device = new Device { Preference = (byte[])initial.Clone() };
        var settings = new ScheduleSettings([new(60, [DayOfWeek.Monday])], []);
        ScheduleSync.Apply(new(device), settings, ScheduleDeviceState.Off, _ => { });
        initial[126] = initial[127] = 0; Assert.Equal(initial, device.Preference);
        Assert.Single(device.Requests, r => r[0] == 0x35);
        device.Requests.Clear(); ScheduleSync.Apply(new(device), settings, ScheduleDeviceState.Off, _ => { });
        Assert.DoesNotContain(device.Requests, r => r[0] == 0x35);
    }
    [Fact]
    public void RowsWithoutDaysSendNoRequests()
    {
        var device = new Device(); var settings = new ScheduleSettings([new(60, [])], []);
        Assert.Null(ScheduleSync.Apply(new(device), settings, ScheduleDeviceState.On, _ => { })); Assert.Empty(device.Requests);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void ScheduleLogIncludesTimesDaysOrOff(int kind)
    {
        var device = new Device { Preference = StoredSchedule(1, [480]), ClockSkew = TimeSpan.FromSeconds(3) };
        var settings = new ScheduleSettings(kind == 1 ? [new(60, Enum.GetValues<DayOfWeek>()), new(780, [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday])] : [new(60, Enum.GetValues<DayOfWeek>()), new(300, Enum.GetValues<DayOfWeek>())], []);
        var lines = new List<string>(); ScheduleSync.Apply(new(device), settings, kind == 2 ? ScheduleDeviceState.Off : ScheduleDeviceState.On, lines.Add);
        string expected = kind switch { 0 => "HotSync \u306e\u6642\u523b\u3092\u8a2d\u5b9a 1:00, 5:00", 1 => "HotSync \u306e\u6642\u523b\u3092\u8a2d\u5b9a 1:00, 13:00 \u6708\u706b\u6c34\u6728\u91d1", _ => "HotSync \u306e\u6642\u523b\u3092\u30aa\u30d5" };
        Assert.Matches("^" + System.Text.RegularExpressions.Regex.Escape(expected + " / \u6642\u8a08\u306e\u305a\u308c +") + "[23] \u79d2$", Assert.Single(lines));
    }
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
    private string SchedulePath(IReadOnlyList<int> times)
    {
        string path = Path.Combine(root, "schedule.json");
        HotSyncSchedule.SaveSettings(path, new(times.Select(t => new ScheduleTime(t, Enum.GetValues<DayOfWeek>())).ToArray(), [new("User", ScheduleDeviceState.On)]));
        return path;
    }
    private bool RunWithSchedule(Device device, List<string> lines, IReadOnlyList<int> schedule, Action<bool, string>? completed = null) =>
        HotSyncSession.Run(new(device), Registry(), lines.Add, () => Assert.Fail("abort"), completed: completed, schedulePath: SchedulePath(schedule));
    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void ScheduleIsWrittenKeepingEnabledAndClockIsSet(ushort enabled)
    {
        var device = new Device { Preference = StoredSchedule(enabled, [480], 999) }; var lines = new List<string>();
        var before = DateTime.Now.AddSeconds(-1);
        Assert.True(RunWithSchedule(device, lines, Times));
        var after = DateTime.Now.AddSeconds(1);
        AssertSchedule(device.Preference, Times, 1);
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
        Assert.True(HotSyncSession.Run(new(device), registry, _ => { }, () => Assert.Fail("abort"), schedulePath: SchedulePath(Times)));
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
        Assert.True(HotSyncSession.Run(new(device), registry, _ => { }, () => Assert.Fail("abort"), restore: new("Target"), schedulePath: SchedulePath(Times)));
        Assert.DoesNotContain(device.Requests, r => r[0] is 0x13 or 0x14 or 0x34 or 0x35);
    }
}
