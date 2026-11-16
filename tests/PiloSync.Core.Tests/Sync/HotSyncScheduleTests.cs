using Xunit;

namespace PiloSync.Core.Tests;

public class HotSyncScheduleTests
{
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
        HotSyncSchedule.Save(path, [300, 60, 60, 1260]);
        Assert.Equal(new[] { 60, 300, 1260 }, HotSyncSchedule.Load(path));
        string json = File.ReadAllText(path); Assert.Contains("\"times\"", json); Assert.Contains("\"1:00\"", json); Assert.Contains("\"21:00\"", json);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }
    [Fact]
    public void SaveAcceptsEightTimesAfterRemovingDuplicates()
    {
        using var folder = new Folder(); string path = Path.Combine(folder.Path, "schedule.json");
        HotSyncSchedule.Save(path, [..Enumerable.Range(0, 8).Select(i => i * 60), 0]);
        Assert.Equal(HotSyncSchedule.MaxTimes, HotSyncSchedule.Load(path).Count);
    }
    [Theory]
    [InlineData(9)] [InlineData(-1)] [InlineData(1440)]
    public void SaveRejectsInvalidAndKeepsExistingFile(int bad)
    {
        using var folder = new Folder(); string path = Path.Combine(folder.Path, "schedule.json");
        HotSyncSchedule.Save(path, [60]);
        var minutes = bad == 9 ? Enumerable.Range(0, 9).Select(i => i * 60) : new[] { bad };
        Assert.Throws<ArgumentException>(() => HotSyncSchedule.Save(path, minutes));
        Assert.Equal(new[] { 60 }, HotSyncSchedule.Load(path)); Assert.Single(Directory.GetFiles(folder.Path));
    }
    [Theory]
    [InlineData(null)] [InlineData("not json")] [InlineData("")] [InlineData("[]")] [InlineData("{}")] [InlineData("{\"times\":[\"25:00\"]}")]
    [InlineData("{\"times\":[null]}")] [InlineData("{\"times\":[\"1:00\",\"x\"]}")] [InlineData("{\"times\":[\"0:00\",\"1:00\",\"2:00\",\"3:00\",\"4:00\",\"5:00\",\"6:00\",\"7:00\",\"8:00\"]}")]
    public void LoadReturnsEmptyForMissingOrBrokenFile(string? content)
    {
        using var folder = new Folder(); string path = Path.Combine(folder.Path, "schedule.json");
        if (content is not null) File.WriteAllText(path, content);
        Assert.Empty(HotSyncSchedule.Load(path));
    }
    [Fact]
    public void LoadSortsAndRemovesDuplicates()
    {
        using var folder = new Folder(); string path = Path.Combine(folder.Path, "schedule.json");
        File.WriteAllText(path, "{ \"times\": [\"5:00\", \"1:00\", \"0100\"] }");
        Assert.Equal(new[] { 60, 300 }, HotSyncSchedule.Load(path));
    }
    [Fact]
    public void DefaultPathIsInAppFolder()
    {
        Assert.Equal(Path.Combine(AppFolder.Current, "schedule.json"), HotSyncSchedule.DefaultPath);
    }
}
