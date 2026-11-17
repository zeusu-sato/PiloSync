using System.Buffers.Binary;
using PiloSync.Cli;
using PiloSync.Protocol;
using Xunit;

namespace PiloSync.Core.Tests;

public sealed partial class HotSyncSessionTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void InstallSummaryReportsOnlyNonzeroSkips(int skipped)
    {
        var registry = Registry(); string install = Path.Combine(root, "User", "Install"); Directory.CreateDirectory(install);
        for (int i = 0; i < skipped; i++)
            using (var output = File.Create(Path.Combine(install, $"{i}.pdb"))) new PalmDbFile { Name = $"Install{i}" }.Write(output);
        var device = new Device { StorageLow = true }; var lines = new List<string>(); bool? completed = null; string? reason = null;
        Assert.True(HotSyncSession.Run(new(device), registry, lines.Add, () => Assert.Fail("abort"), completed: (ok, message) => { completed = ok; reason = message; }, schedulePath: Path.Combine(root, "schedule.json")));
        string expected = $"インストール：成功 0{(skipped > 0 ? $"、見送り {skipped}" : "")}、失敗 0";
        Assert.Contains(lines, line => line.Contains(expected));
        Assert.Contains(File.ReadAllLines(Path.Combine(root, "User", "HotSync.log")), line => line.Contains(expected));
        string message = System.Text.Encoding.ASCII.GetString(device.Requests.Last(r => r[0] == 0x2a).AsSpan(4)).TrimEnd('\0');
        Assert.Contains($"install 0 OK{(skipped > 0 ? $", {skipped} skipped (no space)" : "")}, 0 failed;", message);
        Assert.True(completed); Assert.Equal("", reason); Assert.EndsWith("終了（正常）", lines[^1]);
        Assert.Equal(new byte[] { 0, 0 }, device.Requests.Last(r => r[0] == 0x2f)[4..]);
    }
    [Theory]
    [InlineData(true, false, "COM3")] [InlineData(false, false, "COM3")]
    [InlineData(true, true, "COM3")] [InlineData(false, true, "COM3")] [InlineData(true, false, null)]
    public void LogPrefixUsesConnectionThenOriginalDeviceName(bool registered, bool restore, string? connection)
    {
        var registry = Registry(); var store = new AccountStore(root);
        store.Data.Users.Add(new() { Id = 301, Name = "Target", Folder = "Target" }); store.Save();
        using (var output = File.Create(Path.Combine(store.GetBackupFolder(store.Data.Users.Single(a => a.Name == "Target")), "restore.pdb"))) new PalmDbFile { Name = "Restored" }.Write(output);
        var lines = new List<string>();
        Assert.True(HotSyncSession.Run(new(new Device(registered) { StorageUnavailable = true }), registry, lines.Add, () => Assert.Fail("abort"),
            assign: "User", restore: restore ? new("Target") : null, connection: connection, schedulePath: Path.Combine(root, "schedule.json")));
        string first = connection is null ? "" : $"[{connection}] ";
        string known = connection is null ? "" : registered ? "[User] " : $"[{connection}] ";
        Assert.StartsWith(first + "[", lines[0]); Assert.EndsWith("HotSync 開始", lines[0]);
        Assert.All(lines.Skip(1), line => Assert.StartsWith(known + "[", line));
        string[] saved = File.ReadAllLines(Path.Combine(root, restore ? "Target" : "User", "HotSync.log"));
        Assert.Equal(lines.Select((line, index) => line[(index == 0 ? first.Length : known.Length)..]), saved);
        Assert.All(saved, line => Assert.Matches(@"^\[\d{4}-", line));
    }
    [Theory]
    [InlineData(0x12)] [InlineData(0x10)]
    public void EarlyFailureKeepsConnectionLogPrefix(byte command)
    {
        var lines = new List<string>();
        Assert.False(HotSyncSession.Run(new(new Device { FailCommand = command }), Registry(), lines.Add, () => { }, connection: "COM3", schedulePath: Path.Combine(root, "schedule.json")));
        Assert.All(lines, line => Assert.StartsWith("[COM3] [", line));
        Assert.Contains(lines, line => line.Contains("失敗：通信切断"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProgressReportsInstallThenBackupAndExceptionsDoNotStopHotSync(bool throws)
    {
        var registry = Registry(); string install = Path.Combine(root, "User", "Install"); Directory.CreateDirectory(install);
        using (var output = File.Create(Path.Combine(install, "good.pdb"))) new PalmDbFile { Name = "Data", Type = "DATA", Creator = "TEST" }.Write(output);
        var progress = new List<SyncProgress>(); var lines = new List<string>(); bool? success = null;
        var status = new TrayStatus(); using var session = status.Begin("COM3"); var texts = new List<string>();
        Assert.True(HotSyncSession.Run(new(new Device { TidyFailure = "ok" }), registry, lines.Add, () => Assert.Fail("abort"),
            userKnown: session.Rename, completed: (ok, _) => success = ok,
            progress: p => { progress.Add(p); session.Report(p); texts.Add(status.Snapshot().Text); if (throws) throw new InvalidOperationException("progress"); }, schedulePath: Path.Combine(root, "schedule.json")));
        Assert.Equal(new[] { new SyncProgress(SyncPhase.Install, 1, 1, "good.pdb"), new(SyncPhase.Backup, 1, 1, "Data") }, progress);
        Assert.True(success); Assert.Contains(lines, l => l.Contains("終了（正常）"));
        Assert.Equal(new[] { "PiloSync：User インストール中 1/1 good.pdb", "PiloSync：User バックアップ中 1/1 Data" }, texts);
    }
    [Fact]
    public void CompletionReportsSuccessAndDeviceUserName()
    {
        var registry = Registry(); string? user = null; bool? success = null; string? reason = null;
        Assert.True(HotSyncSession.Run(new(new Device()), registry, _ => { }, () => Assert.Fail("abort"),
            userKnown: name => user = name, completed: (ok, message) => { success = ok; reason = message; }, schedulePath: Path.Combine(root, "schedule.json")));
        Assert.Equal("User", user); Assert.True(success); Assert.Equal("", reason);
    }
    [Fact]
    public void CompletionReportsCommunicationFailure()
    {
        bool? success = null; string? reason = null;
        Assert.False(HotSyncSession.Run(new(new Device { FailCommand = 0x12 }), Registry(), _ => { }, () => { },
            completed: (ok, message) => { success = ok; reason = message; }, schedulePath: Path.Combine(root, "schedule.json")));
        Assert.False(success); Assert.Equal("通信切断", reason);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public void ExistingUserInstallsBacksUpAndLogsEvenIfResetFails(ushort resetError)
    {
        var registry = Registry(); registry.Flags[300] = 3;
        string install = Path.Combine(root, "User", "Install"); Directory.CreateDirectory(install);
        using (var output = File.Create(Path.Combine(install, "good.pdb"))) new PalmDbFile { Name = "Data", Type = "DATA", Creator = "TEST", Attributes = 0x20 }.Write(output);
        File.WriteAllBytes(Path.Combine(install, "bad.pdb"), [1, 2]);
        var device = new Device { ResetError = resetError, StorageAvailable = true }; var lines = new List<string>(); bool aborted = false;
        Assert.True(HotSyncSession.Run(new(device), registry, lines.Add, () => aborted = true, schedulePath: Path.Combine(root, "schedule.json")));
        Assert.False(aborted); Assert.True(File.Exists(Path.Combine(root, "User", "Backup", "Data.pdb")));
        string logPath = Path.Combine(root, "User", "HotSync.log");
        Assert.Equal(new byte[] { 239, 187, 191 }, File.ReadAllBytes(logPath)[..3]);
        Assert.Equal(lines, File.ReadAllLines(logPath));
        int stats = lines.FindIndex(l => l.Contains("所要時間"));
        Assert.True(stats > lines.FindIndex(l => l.Contains("空きメモリー")));
        Assert.True(stats < lines.FindIndex(l => l.Contains("終了（正常）")));
        Assert.Single(lines, l => l.Contains("DLP 要求"));
        Assert.DoesNotContain(lines, l => l.Contains("ReadFile") || l.Contains("WriteFile") || l.Contains("Win32=0"));
        Assert.Contains(lines, l => System.Text.RegularExpressions.Regex.IsMatch(l, @"保存 Data \(\d+ バイト, \d+\.\d 秒\)"));
        Assert.Contains(lines, l => System.Text.RegularExpressions.Regex.IsMatch(l, @"インストール成功 good.pdb \(\d+ バイト, \d+\.\d 秒\)"));
        Assert.Contains(lines, l => l.Contains("インストール成功 good.pdb")); Assert.Contains(lines, l => l.Contains("解析失敗 bad.pdb"));
        Assert.Equal(3u, registry.Flags[300]);
        Assert.Equal(4, device.Requests.Count(r => r[0] == 0x16));
        Assert.Equal(new byte[] { 0x29, 0x2f }, device.Requests.TakeLast(2).Select(r => r[0]));
        Assert.All(device.Requests.Single(r => r[0] == 0x2a).Skip(4), b => Assert.True(b < 128));
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "PiloSync-session-example.log"), lines);
    }
}
