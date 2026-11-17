using PiloSync.Cli;
using Xunit;
namespace PiloSync.Core.Tests;
public class CommandLineTests
{
    [Fact]
    public void RestoreParsesAllOptions()
    {
        Assert.Equal(CommandKind.Command, CommandLine.Classify(["restore"]));
        var options = CommandLine.ParseRestore(["restore", "Prism 2", "--serial-only", "--ports", "COM3, COM17", "--baud", "57600", "--log", "restore.log", "--dry-run", "--usb-read", "65", "--trace"]);
        Assert.Equal("Prism 2", options.AccountName); Assert.True(options.DryRun); Assert.True(options.SerialOnly); Assert.False(options.UsbOnly); Assert.True(options.Trace);
        Assert.Equal(new[] { "COM3", "COM17" }, options.Ports); Assert.Equal(57600, options.Baud); Assert.Equal(128, options.UsbRead); Assert.Equal("restore.log", options.LogPath);
        using var output = new StringWriter(); CommandLine.Usage(output); Assert.Contains("restore <アカウント名>", output.ToString());
    }
    [Fact]
    public void RestoreDefaultsAndUsbOnly()
    {
        var options = CommandLine.ParseRestore(["restore", "User"]); Assert.Equal(115200, options.Baud); Assert.Equal(4096, options.UsbRead); Assert.False(options.DryRun); Assert.Null(options.Ports); Assert.Null(options.LogPath);
        Assert.True(CommandLine.ParseRestore(["restore", "User", "--usb-only"]).UsbOnly);
    }
    [Theory]
    [InlineData("restore")]
    [InlineData("restore", "--dry-run")]
    [InlineData("restore", "")]
    [InlineData("restore", "User", "--serial-only", "--usb-only")]
    [InlineData("restore", "User", "--once")]
    [InlineData("restore", "User", "--log")]
    [InlineData("restore", "User", "--log", "--trace")]
    [InlineData("restore", "User", "--ports", "COM0")]
    [InlineData("restore", "User", "--ports", "")]
    [InlineData("restore", "User", "--baud", "1200")]
    [InlineData("restore", "User", "--usb-read", "0")]
    public void RestoreRejectsInvalidArguments(params string[] args) => Assert.ThrowsAny<ArgumentException>(() => CommandLine.ParseRestore(args));
    [Fact] public void EmptyStartsTray() => Assert.Equal(CommandKind.Tray, CommandLine.Classify([]));
    [Fact] public void TrayAcceptsOptions() => Assert.Equal(CommandKind.Tray, CommandLine.Classify(["tray", "--baud", "57600"]));
    [Theory] [InlineData("help")] [InlineData("--help")] [InlineData("-h")] [InlineData("/?")]
    public void Help(string arg) => Assert.Equal(CommandKind.Help, CommandLine.Classify([arg]));
    [Fact]
    public void LegacyScheduleWritesRequireAccountsAndMigrate()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pilosync-{Guid.NewGuid():N}.json");
        const string original = "{\"times\":[\"1:00\"]}";
        try
        {
            File.WriteAllText(path, original);
            ScheduleCommand.Run(["schedule"], path, TextWriter.Null);
            Assert.Equal(original, File.ReadAllText(path));
            var error = Assert.Throws<ArgumentException>(() => ScheduleCommand.Run(["schedule", "off", "B"], path, TextWriter.Null, () => null));
            Assert.Equal("Core\\Path \u307e\u305f\u306f users.dat \u304c\u3042\u308a\u307e\u305b\u3093", error.Message);
            Assert.Equal(original, File.ReadAllText(path));
            ScheduleCommand.Run(["schedule", "off", "B"], path, TextWriter.Null, () => ["A", "B"]);
            var settings = HotSyncSchedule.LoadSettings(path);
            Assert.False(settings.Legacy); Assert.Equal(ScheduleDeviceState.Inherit, settings.StateFor("A")); Assert.Equal(ScheduleDeviceState.Off, settings.StateFor("B"));
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void ScheduleParsesWeekRangesAndLists()
    {
        var rows = CommandLine.ParseSchedule(["schedule", "set", "1:00", "13:00@mon-fri", "22:00@sat,sun"]).Rows!;
        Assert.Equal(7, rows[0].Days.Count); Assert.Equal(5, rows[1].Days.Count); Assert.Equal(2, rows[2].Days.Count);
        Assert.Throws<ArgumentException>(() => CommandLine.ParseSchedule(["schedule", "set", "1:00@nope"]));
    }
    [Theory] [InlineData("listen")] [InlineData("list")] [InlineData("migrate")] [InlineData("serial-diag")] [InlineData("install-startup")] [InlineData("uninstall-startup")] [InlineData("schedule")]
    public void Command(string arg) => Assert.Equal(CommandKind.Command, CommandLine.Classify([arg]));
    [Fact] public void Unknown() => Assert.Equal(CommandKind.Unknown, CommandLine.Classify(["bogus"]));
    [Fact] public void TrayDefaults() => Assert.Equal((115200, 4096), CommandLine.ParseTray([]));
    [Fact] public void ExplicitTrayDefaults() => Assert.Equal((115200, 4096), CommandLine.ParseTray(["tray"]));
    [Theory] [InlineData(9600)] [InlineData(19200)] [InlineData(38400)] [InlineData(57600)] [InlineData(115200)]
    public void TrayBaud(int baud) => Assert.Equal((baud, 4096), CommandLine.ParseTray(["tray", "--baud", baud.ToString()]));
    [Theory] [InlineData(1, 64)] [InlineData(64, 64)] [InlineData(65, 128)] [InlineData(65536, 65536)]
    public void TrayUsbRead(int value, int expected) => Assert.Equal((57600, expected), CommandLine.ParseTray(["tray", "--usb-read", value.ToString(), "--baud", "57600"]));
    [Theory] [InlineData("--foo")] [InlineData("--baud")] [InlineData("--usb-read")]
    public void TrayUnknownOrMissingValue(string option) => Assert.Throws<ArgumentException>(() => CommandLine.ParseTray(["tray", option]));
    [Theory] [InlineData("--baud", "1234")] [InlineData("--baud", "0")] [InlineData("--usb-read", "0")] [InlineData("--usb-read", "65537")]
    public void TrayOutOfRange(string option, string value) => Assert.ThrowsAny<ArgumentException>(() => CommandLine.ParseTray(["tray", option, value]));
    [Theory] [InlineData("--baud")] [InlineData("--usb-read")]
    public void TrayNonNumeric(string option) => Assert.Throws<FormatException>(() => CommandLine.ParseTray(["tray", option, "abc"]));
    [Fact]
    public void ScheduleParsesListSetAndClear()
    {
        Assert.Equal(new ScheduleOptions("list", []), CommandLine.ParseSchedule(["schedule"]));
        Assert.Equal(new ScheduleOptions("clear", []), CommandLine.ParseSchedule(["schedule", "clear"]));
        var set = CommandLine.ParseSchedule(["schedule", "set", "1:00", "5:00", "0900", "21:30"]);
        Assert.Equal("set", set.Action); Assert.Equal(new[] { 60, 300, 540, 1290 }, set.Minutes);
        using var output = new StringWriter(); CommandLine.Usage(output); Assert.Contains("schedule set <時刻>[@曜日]…", output.ToString());
    }
    [Theory]
    [InlineData("schedule", "set")]
    [InlineData("schedule", "set", "25:00")]
    [InlineData("schedule", "set", "1:00", "abc")]
    [InlineData("schedule", "set", "1:00", "2:00", "3:00", "4:00", "5:00", "6:00", "7:00", "8:00", "9:00")]
    [InlineData("schedule", "clear", "1:00")]
    [InlineData("schedule", "1:00")]
    public void ScheduleRejectsInvalidArguments(params string[] args) => Assert.Throws<ArgumentException>(() => CommandLine.ParseSchedule(args));
    [Fact]
    public void ScheduleParsesWeekdayRangesAndKeepsDeviceWhenCleared()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pilosync-{Guid.NewGuid():N}.json");
        try
        {
            ScheduleCommand.Run(["schedule", "on", "A"], path, TextWriter.Null);
            ScheduleCommand.Run(["schedule", "set", "13:00@mon-fri,sun"], path, TextWriter.Null);
            var settings = HotSyncSchedule.LoadSettings(path);
            Assert.Equal(6, Assert.Single(settings.Times).Days.Count);
            ScheduleCommand.Run(["schedule", "clear"], path, TextWriter.Null);
            Assert.Equal(ScheduleDeviceState.On, HotSyncSchedule.LoadSettings(path).StateFor("A"));
            Assert.Throws<ArgumentException>(() => CommandLine.ParseSchedule(["schedule", "set", "1:00@fri-mon"]));
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void ScheduleCommandShowsSetsAndClears()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pilosync-schedule-{Guid.NewGuid():N}.json");
        try
        {
            string Run(params string[] args) { using var output = new StringWriter(); Assert.Equal(0, ScheduleCommand.Run(args, path, output)); return output.ToString().TrimEnd(); }
            Assert.Equal("時刻は未設定です", Run("schedule"));
            Assert.Equal("1:00 月火水木金土日" + Environment.NewLine + "5:00 月火水木金土日" + Environment.NewLine + "21:00 月火水木金土日", Run("schedule", "set", "21:00", "1:00", "5:00"));
            Assert.Equal("1:00 月火水木金土日" + Environment.NewLine + "5:00 月火水木金土日" + Environment.NewLine + "21:00 月火水木金土日", Run("schedule"));
            Assert.Throws<ArgumentException>(() => Run("schedule", "set", "1:00", "x"));
            Assert.Equal(new[] { 60, 300, 1260 }, HotSyncSchedule.LoadSettings(path).Times.Select(t => t.Minutes));
            Assert.Equal("時刻は未設定です", Run("schedule", "clear"));
            Assert.Empty(HotSyncSchedule.LoadSettings(path).Times.Select(t => t.Minutes));
        }
        finally { File.Delete(path); }
    }
}
