using PiloSync.Cli;
using Xunit;
namespace PiloSync.Core.Tests;

public class InstallHandoffTests
{
    [Fact]
    public void TrayInstallParsesPathsAndOptions()
    {
        string[] args = ["tray", "--baud", "57600", "--install", @"C:\Example\日本語 app.prc", @"C:\Example\two.pdb"];
        Assert.Equal((57600, 4096), CommandLine.ParseTray(args)); Assert.Equal(args[4..], CommandLine.ParseTrayInstall(args)); Assert.Empty(CommandLine.ParseTrayInstall(["tray"]));
    }
    [Theory] [InlineData("tray", "--install")] [InlineData("tray", "--install", "--baud")]
    public void TrayInstallRejectsMissingPaths(params string[] args) => Assert.Throws<ArgumentException>(() => CommandLine.ParseTray(args));
    [Fact]
    public void PipeRoundTrip()
    {
        string[] paths = [@"C:\Example\日本語 a.prc", @"C:\Example\b.pdb"];
        Assert.Equal(paths, InstallPipe.Decode(InstallPipe.Encode(paths)));
        Assert.Contains(System.Diagnostics.Process.GetCurrentProcess().SessionId.ToString(), InstallPipe.Name);
    }
    [Theory] [InlineData("")] [InlineData("1\n")] [InlineData("2\nC:\\a.prc\n")] [InlineData("0\n")] [InlineData("257\n")] [InlineData("1\nrelative.prc\n")] [InlineData("1\nC:\\a.prc\nextra\n")] [InlineData("1\nC:\\a.prc")]
    public void RejectsMalformedPipe(string message) => Assert.Throws<InvalidDataException>(() => InstallPipe.Decode(System.Text.Encoding.UTF8.GetBytes(message)));
    [Fact]
    public void RejectsOversizedAndInvalidUtf8()
    {
        Assert.Throws<InvalidDataException>(() => InstallPipe.Decode(new byte[InstallPipe.MaxBytes + 1]));
        Assert.Throws<InvalidDataException>(() => InstallPipe.Decode([0xff]));
        Assert.Throws<InvalidDataException>(() => InstallPipe.Encode(Enumerable.Repeat(@"C:\a.prc", 257).ToArray()));
        Assert.Throws<InvalidDataException>(() => InstallPipe.Encode(["C:\\a\nb.prc"]));
        Assert.Throws<InvalidDataException>(() => InstallPipe.Encode(["C:\\" + new string('a', 32768)]));
    }
    [Fact]
    public void SendToPathsUseLauncherAndRequestedFolder()
    {
        var paths = SendToShortcut.Paths(@"C:\Example\bin", @"C:\Example\SendTo");
        Assert.Equal(@"C:\Example\bin\PiloSync.exe", paths.Target); Assert.Equal(@"C:\Example\SendTo\PiloSync（Palm にインストール）.lnk", paths.Shortcut);
    }
}
