using System.Buffers.Binary;
using PiloSync.Cli;
using PiloSync.Protocol;
using Xunit;

namespace PiloSync.Core.Tests;

public sealed partial class HotSyncSessionTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void RestoreUsesSpecifiedAccountAndDryRunNeverWrites(bool dryRun, bool registered)
    {
        var registry = Registry(); var store = new AccountStore(root); store.Data.Users.Add(new() { Id = 301, Name = "Target", Folder = "Target" }); store.Save();
        var account = store.Data.Users.Single(a => a.Name == "Target"); string backup = store.GetBackupFolder(account), install = store.GetInstallFolder(account);
        var db = new PalmDbFile { Name = "Restored", Type = "DATA", Creator = "TEST" };
        using (var output = File.Create(Path.Combine(backup, "restore.pdb"))) db.Write(output);
        File.WriteAllBytes(Path.Combine(install, "keep.pdb"), [1, 2]); byte[] original = File.ReadAllBytes(Path.Combine(backup, "restore.pdb"));
        var device = new Device(registered) { StorageUnavailable = true }; var lines = new List<string>(); bool? completed = null;
        Assert.True(HotSyncSession.Run(new(device), registry, lines.Add, () => Assert.Fail("abort"), completed: (ok, _) => completed = ok, restore: new("Target", dryRun), schedule: []));
        Assert.True(completed); Assert.True(File.Exists(Path.Combine(root, "Target", "HotSync.log"))); Assert.True(File.Exists(Path.Combine(install, "keep.pdb")));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(backup, "restore.pdb"))); Assert.Single(Directory.GetFiles(backup)); Assert.Equal(2, device.Requests.Count(r => r[0] == 0x16));
        Assert.DoesNotContain(device.Requests, r => r[0] is 0x17 or 0x2b or 0x1b or 0x1d); Assert.False(registry.Flags.ContainsKey(301));
        if (registered) Assert.Contains(lines, l => l.Contains("端末の名前 User をアカウント Target で上書きします"));
        if (dryRun) Assert.All(device.Requests, r => Assert.Contains(r[0], new byte[] { 0x12, 0x10, 0x2e, 0x16, 0x15, 0x2a, 0x2f }));
        else
        {
            var write = Assert.Single(device.Requests, r => r[0] == 0x11);
            Assert.Equal(301u, BinaryPrimitives.ReadUInt32BigEndian(write.AsSpan(4))); Assert.Equal(123u, BinaryPrimitives.ReadUInt32BigEndian(write.AsSpan(12)));
            Assert.Equal("Target", PalmText.Decode(write.AsSpan(26))); Assert.Equal(0xf0, write[24]);
            Assert.Equal(new byte[] { 0x29, 0x2f }, device.Requests.TakeLast(2).Select(r => r[0]));
        }
    }
    [Fact]
    public void RestoreInsufficientMemoryAndMissingAccountFinishAsFailureWithoutWrites()
    {
        var registry = Registry(); var store = new AccountStore(root); var account = store.Data.Users.Single();
        using (var output = File.Create(Path.Combine(store.GetBackupFolder(account), "restore.pdb"))) new PalmDbFile { Name = "Data" }.Write(output);
        foreach (string name in new[] { "User", "Missing" })
        {
            var device = new Device(false); bool? completed = null; var lines = new List<string>();
            Assert.False(HotSyncSession.Run(new(device), registry, lines.Add, () => Assert.Fail("abort"), completed: (ok, _) => completed = ok, restore: new(name), schedule: []));
            Assert.False(completed); Assert.DoesNotContain(device.Requests, r => r[0] is 0x11 or 0x18 or 0x1a or 0x29); Assert.Equal(0x2f, device.Requests[^1][0]);
            Assert.Contains(lines, l => l.Contains(name == "User" ? "空きメモリーが足りません" : "指定したアカウントがありません"));
        }
    }
    [Theory]
    [InlineData("netl", 1, false, true)] [InlineData("TEST", 1, false, false)]
    [InlineData("netl", 3, false, false)] [InlineData("netl", 1, true, false)]
    public void RestoreCompletionDistinguishesInUseAndOrdinaryFailures(string creator, ushort error, bool mixed, bool expected)
    {
        var registry = Registry(); var store = new AccountStore(root);
        string backup = store.GetBackupFolder(store.Data.Users.Single());
        using (var output = File.Create(Path.Combine(backup, "restore.pdb"))) new PalmDbFile { Name = "Net Prefs", Type = "rsrc", Creator = creator, Attributes = 1 }.Write(output);
        if (mixed) using (var output = File.Create(Path.Combine(backup, "ordinary.pdb"))) new PalmDbFile { Name = "Ordinary", Type = "DATA", Creator = "TEST" }.Write(output);
        var device = new Device { RestoreError = error, StorageUnavailable = true };
        bool? completed = null; string? message = null; var lines = new List<string>();
        Assert.Equal(expected, HotSyncSession.Run(new(device), registry, lines.Add, () => Assert.Fail("abort"),
            completed: (ok, reason) => { completed = ok; message = reason; }, restore: new("User"), schedule: []));
        Assert.Equal(expected, completed);
        Assert.EndsWith(expected ? "終了（正常・一部書けず）" : "終了（失敗）", lines[^1]);
        if (expected) { Assert.Contains("Net Prefs", message); Assert.Contains("使用中", message); }
    }
}
