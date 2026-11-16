using PiloSync.Cli;
using Xunit;
namespace PiloSync.Core.Tests;
public class StartupRegistrationTests
{
    const string Cli = @"C:\Example Sync\pilosync-cli.exe", Launcher = @"C:\Example Sync\PiloSync.exe";
    [Theory]
    [InlineData("\"C:\\Example Sync\\PiloSync.exe\" tray", true)]
    [InlineData("\"C:\\Example Sync\\pilosync-cli.exe\" tray", true)]
    [InlineData("C:\\Example Sync\\pilosync-cli.exe tray", true)]
    [InlineData("c:\\EXAMPLE SYNC\\PILOSYNC.EXE tray", true)]
    [InlineData("C:\\Other\\PiloSync.exe tray", false)]
    [InlineData(null, false)]
    [InlineData("HotSync.exe", false)]
    [InlineData("\"C:\\Example Sync\\PiloSync.exe", false)]
    public void RegistrationMatchesExecutable(string? value, bool expected) => Assert.Equal(expected, StartupCommand.IsRegistered(value, [Launcher, Cli]));
    [Fact] public void ExpandsEnvironment() => Assert.True(StartupCommand.IsRegistered(@"%LOCALAPPDATA%\PiloSync.exe tray", [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PiloSync.exe")]));
    [Theory]
    [InlineData("\"C:\\Example Sync\\pilosync-cli.exe\" tray", true, true)]
    [InlineData("C:\\Example Sync\\PILOSYNC-CLI.EXE tray", true, true)]
    [InlineData("C:\\Example Sync\\pilosync-cli.exe tray", false, false)]
    [InlineData("C:\\Example Sync\\PiloSync.exe tray", true, false)]
    [InlineData("C:\\Other\\pilosync-cli.exe tray", true, false)]
    [InlineData(null, true, false)]
    public void MigratesOnlyOwnCli(string? value, bool exists, bool expected) => Assert.Equal(expected, StartupCommand.ShouldMigrate(value, Cli, Launcher, exists));
}
