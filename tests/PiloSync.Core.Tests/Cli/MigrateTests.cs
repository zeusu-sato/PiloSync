using PiloSync.Core;
using PiloSync.Cli;
using Xunit;

namespace PiloSync.Core.Tests;

internal sealed class FakePalmRegistry : IPalmRegistry
{
    public string? DataPath { get; set; }
    public uint? StockPcId { get; set; }
    public uint? PcId { get; set; }
    public MigrationRecord? Migration { get; set; }
    public Dictionary<uint, uint> Flags { get; } = [];
    public uint? ReadInstallFlags(uint id) => Flags.TryGetValue(id, out uint value) ? value : null;
    public void WriteInstallFlags(uint id, uint flags) { if (Flags.ContainsKey(id)) Flags[id] = flags; }
}

[Collection("Accounts")]
public sealed class MigrateTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PiloSync-migrate-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(root, "source");
    private string Target => Path.Combine(root, "target");
    private string Local => Path.Combine(root, "local");
    public MigrateTests() { Directory.CreateDirectory(Source); }
    public void Dispose() => Directory.Delete(root, true);
    private void Users(string file, params uint[] ids)
    {
        var data = new UsersDat();
        foreach (uint id in ids) data.Users.Add(new() { Id = id, Name = "User" + id, Folder = "U" + id });
        File.WriteAllBytes(Path.Combine(Source, file), data.Write());
    }
    private int Run(FakePalmRegistry reg, StringWriter output, params string[] options) => MigrateCommand.Run(options, reg, output, Local);
    [Fact]
    public void DryRunWritesNothingThenMigrateAndUndoPreservePendingInstalls()
    {
        Users("users.dat", 1);
        string backup = Path.Combine(Source, "U1", "Backup");
        string install = Path.Combine(Source, "U1", "Install");
        Directory.CreateDirectory(backup); Directory.CreateDirectory(install);
        File.WriteAllBytes(Path.Combine(backup, "data.pdb"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(install, "same.pdb"), "original");
        var reg = new FakePalmRegistry { DataPath = Source };
        using var output = new StringWriter();
        Assert.Equal(0, Run(reg, output, "--dry-run", "--to", Target));
        Assert.False(Directory.Exists(Target)); Assert.False(Directory.Exists(Local)); Assert.Null(reg.Migration); Assert.Equal(Source, reg.DataPath);
        Assert.Contains("User1", output.ToString()); Assert.Contains("バイト", output.ToString());
        Assert.Equal(0, Run(reg, output, "--to", Target));
        Assert.Equal(Target, reg.DataPath);
        Assert.Equal(Source, reg.Migration!.PreviousPath);
        Assert.True(File.Exists(Path.Combine(Local, "PiloSync", "migration.json")));
        Assert.True(File.Exists(Path.Combine(backup, "data.pdb")));
        string newInstall = Path.Combine(Target, "U1", "Install");
        File.WriteAllText(Path.Combine(newInstall, "pending.PQA"), "pending");
        File.WriteAllText(Path.Combine(newInstall, "same.pdb"), "new");
        Assert.Equal(0, Run(reg, output, "--undo", "--dry-run"));
        Assert.Equal(Target, reg.DataPath); Assert.False(File.Exists(Path.Combine(install, "pending.PQA")));
        Assert.Equal(0, Run(reg, output, "--undo")); Assert.Equal(Source, reg.DataPath);
        Assert.Equal("pending", File.ReadAllText(Path.Combine(install, "pending.PQA")));
        Assert.Equal("original", File.ReadAllText(Path.Combine(install, "same.pdb")));
        Assert.True(Directory.Exists(Target));
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void ChoosesSupersetOrUnion(bool superset)
    {
        Users("users.dat", 1, 2);
        Users("users-older.dat", superset ? [1u] : [2u, 3u]);
        File.SetLastWriteTimeUtc(Path.Combine(Source, "users.dat"), DateTime.UtcNow);
        File.SetLastWriteTimeUtc(Path.Combine(Source, "users-older.dat"), DateTime.UtcNow.AddDays(-1));
        File.WriteAllText(Path.Combine(Source, "users-broken.dat"), "broken");
        var reg = new FakePalmRegistry { DataPath = Source };
        using var output = new StringWriter();
        Assert.Equal(0, Run(reg, output, "--to", Target));
        Assert.Equal(superset ? 2 : 3, UsersDat.Read(File.ReadAllBytes(Path.Combine(Target, "users.dat"))).Users.Count);
        Assert.Empty(Directory.GetFiles(Target, "users-*.dat"));
        Assert.Contains(superset ? "全候補" : "和集合", output.ToString());
        Assert.Contains("除外", output.ToString());
    }
    [Fact]
    public void LatestSupersetWinsAndUnknownFieldsRemain()
    {
        Users("users.dat", 1); Users("users-new.dat", 1);
        string path = Path.Combine(Source, "users-new.dat");
        var data = UsersDat.Read(File.ReadAllBytes(path)); data.Users[0].Name = "Latest"; data.Users[0].Password = [3, 2, 1];
        File.WriteAllBytes(path, data.Write()); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(1));
        var reg = new FakePalmRegistry { DataPath = Source }; using var output = new StringWriter();
        Assert.Equal(0, Run(reg, output, "--to", Target));
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(Path.Combine(Target, "users.dat")));
    }
    [Fact]
    public void CopyFailureListsFileAndDoesNotChangeRegistry()
    {
        Users("users.dat", 1); Directory.CreateDirectory(Path.Combine(Source, "U1"));
        string locked = Path.Combine(Source, "U1", "online.pdb"); File.WriteAllText(locked, "test");
        using var file = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var reg = new FakePalmRegistry { DataPath = Source }; using var output = new StringWriter();
        Assert.Equal(1, Run(reg, output, "--to", Target)); Assert.Equal(Source, reg.DataPath); Assert.Null(reg.Migration);
        Assert.Contains("online.pdb", output.ToString());
        Assert.False(File.Exists(Path.Combine(Target, "users.dat")));
    }
    [Fact]
    public void UnreadableConflictCopyAbortsRatherThanLosingItsUsers()
    {
        Users("users.dat", 1); Users("users-locked.dat", 1, 2);
        using var locked = new FileStream(Path.Combine(Source, "users-locked.dat"), FileMode.Open, FileAccess.Read, FileShare.None);
        var reg = new FakePalmRegistry { DataPath = Source }; using var output = new StringWriter();
        Assert.Equal(1, Run(reg, output, "--to", Target));
        Assert.Equal(Source, reg.DataPath); Assert.False(Directory.Exists(Target));
        Assert.Contains("users-locked.dat", output.ToString());
    }
    [Fact]
    public void UndoCanReadJsonWhenRegistryHistoryIsMissing()
    {
        Users("users.dat", 1);
        var reg = new FakePalmRegistry { DataPath = Source }; using var output = new StringWriter();
        Assert.Equal(0, Run(reg, output, "--to", Target)); reg.Migration = null;
        Assert.Equal(0, Run(reg, output, "--undo")); Assert.Equal(Source, reg.DataPath);
    }
    [Fact]
    public void ExistingUsersAndNestedDestinationAreRefused()
    {
        Users("users.dat", 1); Directory.CreateDirectory(Target); File.WriteAllBytes(Path.Combine(Target, "users.dat"), [42]);
        var reg = new FakePalmRegistry { DataPath = Source }; using var output = new StringWriter();
        Assert.Equal(1, Run(reg, output, "--to", Target)); Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(Path.Combine(Target, "users.dat")));
        Assert.Equal(1, Run(reg, output, "--to", Path.Combine(Source, "nested")));
        Assert.False(Directory.Exists(Path.Combine(Source, "nested")));
    }
    [Fact]
    public void PcIdAndInstallFlagsUseFakeRegistryOnly()
    {
        var reg = new FakePalmRegistry(); uint pc = PalmRegistryOperations.GetOrCreatePcId(reg);
        Assert.NotEqual(0u, pc); Assert.Equal(pc, PalmRegistryOperations.GetOrCreatePcId(reg));
        reg.Flags[1] = 0x21;
        File.WriteAllText(Path.Combine(Source, "pending.PRC"), "test");
        PalmRegistryOperations.CompleteInstall(reg, 1, Source); Assert.Equal(0x21u, reg.Flags[1]);
        File.Delete(Path.Combine(Source, "pending.PRC"));
        PalmRegistryOperations.CompleteInstall(reg, 1, Source); Assert.Equal(0x20u, reg.Flags[1]);
        PalmRegistryOperations.CompleteInstall(reg, 2, Source); Assert.False(reg.Flags.ContainsKey(2));
        reg.Flags[1] = 1; PalmRegistryOperations.CompleteInstall(reg, 1, Source); Assert.Equal(0u, reg.Flags[1]);
    }
}
