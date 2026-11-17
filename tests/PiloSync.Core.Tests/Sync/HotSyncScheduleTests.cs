using Xunit;

namespace PiloSync.Core.Tests;

public class HotSyncScheduleTests
{
    private static void Save(string path, IEnumerable<int> minutes) => HotSyncSchedule.SaveSettings(path, new(minutes.Select(m => new ScheduleTime(m, Enum.GetValues<DayOfWeek>())).ToArray(), []));
    private static int[] Load(string path) => HotSyncSchedule.LoadSettings(path).Times.Select(t => t.Minutes).ToArray();
    [Fact]
    public void UpdateRejectsUnmigratedLegacyWithoutWriting()
    {
        using var folder = new Folder(); string path = Path.Combine(folder.Path, "schedule.json");
        const string original = "{\"times\":[\"1:00\"]}"; File.WriteAllText(path, original);
        Assert.Throws<InvalidOperationException>(() => HotSyncSchedule.Update(path, current => current));
        Assert.Equal(original, File.ReadAllText(path)); Assert.Single(Directory.GetFiles(folder.Path));
    }


    [Fact]
    public void VersionTwoMergesDaysAndPreservesDevicesOnUpdate()
    {
        using var folder = new Folder(); string path = Path.Combine(folder.Path, "schedule.json");
        HotSyncSchedule.SaveSettings(path, new([new(60, [DayOfWeek.Monday]), new(60, [DayOfWeek.Sunday])], [new("A", ScheduleDeviceState.On)]));
        var loaded = HotSyncSchedule.LoadSettings(path);
        Assert.Single(loaded.Times); Assert.Equal(2, loaded.Times[0].Days.Count);
        HotSyncSchedule.Update(path, current => current with { Times = [] });
        Assert.Equal(ScheduleDeviceState.On, HotSyncSchedule.LoadSettings(path).StateFor("A"));
    }
    [Fact]
    public void LegacyMigratesAndTooManyTimesAreRejected()
    {
        using var folder = new Folder(); string path = Path.Combine(folder.Path, "schedule.json");
        File.WriteAllText(path, "{\"times\":[\"1:00\"]}");
        var old = HotSyncSchedule.LoadSettings(path); Assert.True(old.Legacy);
        Assert.Equal(ScheduleDeviceState.Inherit, old.MigrateLegacy(["A"]).StateFor("A"));
        Assert.Throws<ArgumentException>(() => HotSyncSchedule.SaveSettings(path, new(Enumerable.Range(0, 9).Select(n => new ScheduleTime(n, [DayOfWeek.Monday])).ToArray(), [])));
        Assert.True(HotSyncSchedule.LoadSettings(path).Legacy);
    }
    [Theory]
    [InlineData("{\"version\":2,\"times\":[{\"time\":\"1:00\",\"days\":[\"bad\"]}],\"devices\":[]}")]
    [InlineData("{\"version\":2,\"times\":[],\"devices\":[{\"name\":\"A\",\"state\":\"bad\"}]}")]
    public void UnknownDayOrStateLoadsEmpty(string json)
    {
        using var folder = new Folder(); string path = Path.Combine(folder.Path, "schedule.json"); File.WriteAllText(path, json);
        var settings = HotSyncSchedule.LoadSettings(path); Assert.Empty(settings.Times); Assert.Empty(settings.Devices);
    }
    [Theory]
    [InlineData(0, "0:00")] [InlineData(60, "1:00")] [InlineData(785, "13:05")] [InlineData(1439, "23:59")]
    public void FormatUsesHourWithoutPadding(int minutes, string text) => Assert.Equal(text, HotSyncSchedule.Format(minutes));
    [Theory]
    [InlineData("1:00", 60)] [InlineData("13:05", 785)] [InlineData("0130", 90)] [InlineData(" 9:30 ", 570)] [InlineData("0:00", 0)] [InlineData("23:59", 1439)]
    public void TryParseAcceptsClockAndFourDigits(string text, int minutes)
    {
        Assert.True(HotSyncSchedule.TryParse(text, out int actual)); Assert.Equal(minutes, actual);
    }
    [Theory]
    [InlineData("")] [InlineData("abc")] [InlineData("24:00")] [InlineData("1:60")] [InlineData("1:0")] [InlineData("130")] [InlineData("2400")] [InlineData("1:00:00")]
    public void TryParseRejectsOthers(string text) => Assert.False(HotSyncSchedule.TryParse(text, out _));
    [Fact]
    public void SaveNormalizesAndLoadRoundTrips()
    {
        using var folder = new Folder(); string path = Path.Combine(folder.Path, "nested", "schedule.json");
        Save(path, [300, 60, 60, 1260]);
        Assert.Equal(new[] { 60, 300, 1260 }, Load(path));
        string json = File.ReadAllText(path); Assert.Contains("\"times\"", json); Assert.Contains("\"1:00\"", json); Assert.Contains("\"21:00\"", json);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }
    [Fact]
    public void SaveAcceptsEightTimesAfterRemovingDuplicates()
    {
        using var folder = new Folder(); string path = Path.Combine(folder.Path, "schedule.json");
        Save(path, [..Enumerable.Range(0, 8).Select(i => i * 60), 0]);
        Assert.Equal(HotSyncSchedule.MaxTimes, Load(path).Length);
    }
    [Theory]
    [InlineData(9)] [InlineData(-1)] [InlineData(1440)]
    public void SaveRejectsInvalidAndKeepsExistingFile(int bad)
    {
        using var folder = new Folder(); string path = Path.Combine(folder.Path, "schedule.json");
        Save(path, [60]);
        var minutes = bad == 9 ? Enumerable.Range(0, 9).Select(i => i * 60) : new[] { bad };
        Assert.Throws<ArgumentException>(() => Save(path, minutes));
        Assert.Equal(new[] { 60 }, Load(path)); Assert.Single(Directory.GetFiles(folder.Path));
    }
    [Theory]
    [InlineData(null)] [InlineData("not json")] [InlineData("")] [InlineData("[]")] [InlineData("{}")] [InlineData("{\"times\":[\"25:00\"]}")]
    [InlineData("{\"times\":[null]}")] [InlineData("{\"times\":[\"1:00\",\"x\"]}")] [InlineData("{\"times\":[\"0:00\",\"1:00\",\"2:00\",\"3:00\",\"4:00\",\"5:00\",\"6:00\",\"7:00\",\"8:00\"]}")]
    public void LoadReturnsEmptyForMissingOrBrokenFile(string? content)
    {
        using var folder = new Folder(); string path = Path.Combine(folder.Path, "schedule.json");
        if (content is not null) File.WriteAllText(path, content);
        Assert.Empty(Load(path));
    }
    [Fact]
    public void LoadSortsAndRemovesDuplicates()
    {
        using var folder = new Folder(); string path = Path.Combine(folder.Path, "schedule.json");
        File.WriteAllText(path, "{ \"times\": [\"5:00\", \"1:00\", \"0100\"] }");
        Assert.Equal(new[] { 60, 300 }, Load(path));
    }
    [Fact]
    public void DefaultPathIsInAppFolder()
    {
        Assert.Equal(Path.Combine(AppFolder.Current, "schedule.json"), HotSyncSchedule.DefaultPath);
    }
}
