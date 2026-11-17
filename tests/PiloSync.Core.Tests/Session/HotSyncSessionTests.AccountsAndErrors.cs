using System.Buffers.Binary;
using PiloSync.Cli;
using PiloSync.Protocol;
using Xunit;

namespace PiloSync.Core.Tests;

public sealed partial class HotSyncSessionTests
{
    [Fact]
    public void FirstSyncAfterInitialSetupCreatesAccountAndBackup()
    {
        var registry = new FakePalmRegistry(); var lines = new List<string>();
        InitialSetup.Run(registry, root, lines.Add);
        string target = Path.Combine(AppFolder.Resolve(root), "Palm OS Desktop");
        Assert.Equal(target, registry.DataPath);
        Assert.True(HotSyncSession.Run(new(new Device { TidyFailure = "ok" }), registry, lines.Add, () => Assert.Fail("abort"), schedulePath: Path.Combine(root, "schedule.json")));
        var store = new AccountStore(target); var account = Assert.Single(store.Data.Users);
        Assert.Equal("User", account.Name);
        string folder = Path.Combine(target, account.Folder);
        Assert.True(Directory.Exists(Path.Combine(folder, "Install")));
        Assert.True(Directory.Exists(Path.Combine(folder, "Backup")));
        Assert.True(File.Exists(Path.Combine(folder, "Backup", "Data.pdb")));
    }
    [Fact]
    public void UnregisteredDeviceDoesNotWriteUserOrCreateAccount()
    {
        var registry = Registry(); byte[] before = File.ReadAllBytes(Path.Combine(root, "users.dat"));
        var device = new Device(false); var lines = new List<string>();
        string? reason = null;
        Assert.False(HotSyncSession.Run(new(device), registry, lines.Add, () => Assert.Fail("abort"), completed: (ok, message) => { Assert.False(ok); reason = message; }, schedulePath: Path.Combine(root, "schedule.json")));
        Assert.Equal(HotSyncSession.UnregisteredDeviceMessage, reason);
        Assert.Contains(lines, l => l.EndsWith(HotSyncSession.UnregisteredDeviceMessage));
        Assert.DoesNotContain(device.Requests, r => r[0] is 0x11 or 0x2e);
        Assert.Equal(0x2f, device.Requests[^1][0]); Assert.Equal(before, File.ReadAllBytes(Path.Combine(root, "users.dat")));
    }
    [Theory]
    [InlineData(0x12)]
    [InlineData(0x2e)]
    [InlineData(0x2f)]
    public void CommunicationFailureIsCaughtAndAborted(byte command)
    {
        var registry = Registry(); var device = new Device { FailCommand = command }; var lines = new List<string>(); bool aborted = false;
        Assert.False(HotSyncSession.Run(new(device), registry, lines.Add, () => aborted = true, schedulePath: Path.Combine(root, "schedule.json")));
        Assert.True(aborted); Assert.Contains(lines, l => l.Contains("失敗：通信切断"));
        Assert.Single(lines, l => l.Contains("DLP 要求"));
        Assert.Contains("終了（失敗）", lines[^1]);
        if (command != 0x12) Assert.Contains("失敗：通信切断", File.ReadAllText(Path.Combine(root, "User", "HotSync.log")));
    }
    [Fact]
    public void AssignWritesExistingAccountBeforeOpeningConduit()
    {
        var registry = Registry(); var device = new Device(false);
        Assert.True(HotSyncSession.Run(new(device), registry, _ => { }, () => Assert.Fail("abort"), "User", schedulePath: Path.Combine(root, "schedule.json")));
        int write = device.Requests.FindIndex(r => r[0] == 0x11);
        Assert.True(write < device.Requests.FindIndex(r => r[0] == 0x2e));
        Assert.Equal(300u, BinaryPrimitives.ReadUInt32BigEndian(device.Requests[write].AsSpan(4)));
        Assert.Single(new AccountStore(root).Data.Users);
    }
    [Fact]
    public void FinalUserInfoWritesThisSyncNotPreviousSuccessfulSync()
    {
        var previous = new DateTime(2026, 10, 1, 8, 0, 0);
        var device = new Device { SuccessfulSync = previous, LastSync = new DateTime(2026, 10, 2, 9, 0, 0) };
        var before = DateTime.Now; before = before.AddTicks(-(before.Ticks % TimeSpan.TicksPerSecond));
        Assert.True(HotSyncSession.Run(new(device), Registry(), _ => { }, () => Assert.Fail("abort"), schedulePath: Path.Combine(root, "schedule.json")));
        var after = DateTime.Now;
        var write = device.Requests.Last(r => r[0] == 0x11); Assert.Equal(0x20, write[24] & 0x20);
        var date = write.AsSpan(16, 8);
        var written = new DateTime(BinaryPrimitives.ReadUInt16BigEndian(date), date[2], date[3], date[4], date[5], date[6]);
        Assert.InRange(written, before, after); Assert.NotEqual(previous, written);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingAccountDataEndsWithoutDeviceWrites(bool missingFile)
    {
        var registry = new FakePalmRegistry { DataPath = missingFile ? root : null }; var device = new Device(); var lines = new List<string>();
        Assert.False(HotSyncSession.Run(new(device), registry, lines.Add, () => Assert.Fail("abort"), schedulePath: Path.Combine(root, "schedule.json")));
        Assert.Contains(lines, l => l.Contains(missingFile ? "users.dat がありません" : "Core\\Path が設定されていません"));
        Assert.DoesNotContain(device.Requests, r => r[0] == 0x11); Assert.Equal(0x2f, device.Requests[^1][0]);
        Assert.Single(lines, l => l.Contains("DLP 要求"));
        Assert.Null(registry.PcId); Assert.Empty(Directory.GetFiles(root));
    }

}
