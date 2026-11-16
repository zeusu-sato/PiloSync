using System.Buffers.Binary;
using PiloSync.Cli;
using PiloSync.Protocol;
using Xunit;

namespace PiloSync.Core.Tests;

public sealed partial class HotSyncSessionTests
{
    [Fact]
    public void TidyEnumerationFailureDoesNotStopHotSync()
    {
        var registry = Registry(); var store = new AccountStore(root);
        var backup = new DirectoryInfo(store.GetBackupFolder(store.Data.Users.Single()));
        var denied = System.IO.FileSystemAclExtensions.GetAccessControl(backup);
        var rule = new System.Security.AccessControl.FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.Security.AccessControl.FileSystemRights.ListDirectory, System.Security.AccessControl.AccessControlType.Deny);
        denied.AddAccessRule(rule);
        var device = new Device(); var lines = new List<string>(); bool? success = null; string? failure = null;
        try
        {
            Assert.True(HotSyncSession.Run(new(device), registry, line =>
            {
                lines.Add(line);
                if (line.Contains("インストール：成功")) System.IO.FileSystemAclExtensions.SetAccessControl(backup, denied);
            }, () => Assert.Fail("abort"), completed: (ok, reason) => { success = ok; failure = reason; }, schedule: []));
        }
        finally { denied.RemoveAccessRuleSpecific(rule); System.IO.FileSystemAclExtensions.SetAccessControl(backup, denied); }
        Assert.Contains(lines, line => line.Contains("整理失敗：")); Assert.True(success); Assert.Equal("", failure);
        Assert.Contains(device.Requests, r => r[0] == 0x11); Assert.Equal(0x2f, device.Requests[^1][0]);
        Assert.EndsWith("終了（正常）", lines[^1]);
    }
    [Theory]
    [InlineData("ok")] [InlineData("page")] [InlineData("read")] [InlineData("rom")] [InlineData("empty")] [InlineData("rom-name")] [InlineData("collision")]
    public void TidyMovesMissingOnlyAfterCompleteSuccessfulBackup(string mode)
    {
        var registry = Registry(); var store = new AccountStore(root); string backup = store.GetBackupFolder(store.Data.Users.Single());
        string name = mode == "rom-name" ? "ROM!" : "Gone";
        using (var output = File.Create(Path.Combine(backup, "old.prc"))) new PalmDbFile { Name = name }.Write(output);
        var device = new Device { TidyFailure = mode }; var lines = new List<string>(); bool aborted = false;
        bool result = HotSyncSession.Run(new(device), registry, lines.Add, () => aborted = true, schedule: []);
        Assert.Equal(mode != "page", result); Assert.Equal(mode == "page", aborted);
        Assert.Equal(mode != "ok", File.Exists(Path.Combine(backup, "old.prc")));
        if (mode is "read" or "rom" or "empty" or "collision") Assert.Contains(lines, l => l.Contains("端末にない DB の整理は行いません"));
        if (mode == "page") Assert.DoesNotContain(lines, l => l.Contains("整理："));
    }
    [Fact]
    public void TidyArchivesDuplicateOfUnchangedBackup()
    {
        var registry = Registry(); var store = new AccountStore(root); string backup = store.GetBackupFolder(store.Data.Users.Single());
        var db = new PalmDbFile { Name = "Data", Type = "DATA", Creator = "TEST" };
        foreach (string name in new[] { "Data.pdb", "Data-PC.pdb" }) using (var output = File.Create(Path.Combine(backup, name))) db.Write(output);
        var lines = new List<string>(); Assert.True(HotSyncSession.Run(new(new Device { TidyFailure = "ok" }), registry, lines.Add, () => Assert.Fail("abort"), schedule: []));
        Assert.True(File.Exists(Path.Combine(backup, "Data.pdb"))); Assert.False(File.Exists(Path.Combine(backup, "Data-PC.pdb")));
        Assert.Contains(lines, l => l.Contains("変化なし Data")); Assert.Contains(lines, l => l.Contains("Data-PC.pdb → Backup Archive（古い重複）"));
    }
    [Theory]
    [InlineData("psys", true, false)] [InlineData("TEST", false, false)] [InlineData("psys", false, true)]
    public void TidyContinuesOnlyForInUseBackupFailures(string creator, bool proceeds, bool mixed)
    {
        var registry = Registry(); var store = new AccountStore(root);
        string backup = store.GetBackupFolder(store.Data.Users.Single());
        using (var output = File.Create(Path.Combine(backup, "gone.pdb"))) new PalmDbFile { Name = "Gone" }.Write(output);
        var lines = new List<string>();
        Assert.True(HotSyncSession.Run(new(new Device { TidyFailure = mixed ? "mixed" : "ok", BackupError = 10, Creator = creator }), registry, lines.Add, () => Assert.Fail("abort"), schedule: []));
        Assert.Equal(!proceeds, File.Exists(Path.Combine(backup, "gone.pdb")));
        Assert.Equal(creator == "psys", lines.Any(l => l.Contains("整理を止める理由に数えません")));
    }
    [Theory]
    [InlineData("psys", false, 1, 0)] [InlineData("TEST", false, 0, 1)] [InlineData("psys", true, 1, 1)]
    public void BackupSummarySeparatesInUseFromFailures(string creator, bool mixed, int inUse, int failed)
    {
        var registry = Registry(); var device = new Device { TidyFailure = mixed ? "mixed" : "ok", BackupError = 10, Creator = creator };
        var lines = new List<string>(); bool? completed = null;
        Assert.True(HotSyncSession.Run(new(device), registry, lines.Add, () => Assert.Fail("abort"), completed: (ok, _) => completed = ok, schedule: []));
        Assert.Equal(failed == 0, completed);
        string message = System.Text.Encoding.ASCII.GetString(Assert.Single(device.Requests, r => r[0] == 0x2a).AsSpan(4)).TrimEnd('\0');
        Assert.Contains($"{failed} failed.\n", message);
        Assert.Equal(inUse > 0, message.Contains("1 skipped (in use)"));
        Assert.Equal(inUse > 0, lines.Any(l => l.Contains("使用中で読めず 1")));
        Assert.Contains(lines, l => l.Contains($"変化なし 0") && l.Contains($"失敗 {failed}"));
        Assert.Equal(new byte[] { 0, 0 }, device.Requests.Last(r => r[0] == 0x2f)[4..]);
    }
}
