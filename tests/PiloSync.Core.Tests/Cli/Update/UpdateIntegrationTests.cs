using System.Diagnostics;
using Microsoft.Win32;
using PiloSync.Cli.Update;
using Xunit;

namespace PiloSync.Core.Tests.Cli;

public class UpdateIntegrationTests
{
    [Fact]
    public void SettingsDefaultToEnabledAndPersistDisabled()
    {
        string path = @"Software\PiloSyncTests\" + Guid.NewGuid();
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(path))
            {
                var settings = new UpdateSettings(key);
                Assert.True(settings.Enabled);
                settings.SetEnabled(false);
                Assert.Equal(RegistryValueKind.DWord, key.GetValueKind("AutoUpdate"));
            }
            using var reopened = Registry.CurrentUser.OpenSubKey(path)!;
            Assert.False(new UpdateSettings(reopened).Enabled);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, false); }
    }

    [Theory]
    [InlineData(0, false, true, 0, false, true)]
    [InlineData(1, false, true, 0, false, false)]
    [InlineData(0, true, true, 0, false, false)]
    [InlineData(0, false, false, 0, false, false)]
    [InlineData(0, false, true, 1, false, false)]
    [InlineData(0, false, true, 0, true, false)]
    public void GateChecksEveryCondition(int count, bool paused, bool listening, int forms, bool menu, bool expected) =>
        Assert.Equal(expected, UpdateGate.CanInstall(count, paused, listening, forms, menu));

    [Fact]
    public void ChildDoesNotInheritLauncherVersion()
    {
        var info = new ProcessStartInfo();
        info.Environment["PILOSYNC_LAUNCHER_VERSION"] = "old";
        info.Environment["PILOSYNC_TEST_VALUE"] = "kept";
        UpdateHandover.PrepareTrayEnvironment(info);
        Assert.False(info.Environment.ContainsKey("PILOSYNC_LAUNCHER_VERSION"));
        Assert.Equal("kept", info.Environment["PILOSYNC_TEST_VALUE"]);
    }
}
