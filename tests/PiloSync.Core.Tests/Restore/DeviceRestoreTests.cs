using System.Buffers.Binary;
using PiloSync.Protocol;
using Xunit;
using static PiloSync.Core.Tests.DlpFixtures;
using static PiloSync.Core.Tests.DbFixtures;
namespace PiloSync.Core.Tests;

public class DeviceRestoreTests
{
    private static Fake Device(uint free = 1000000, string? ram = null, string? rom = null, ushort deleteError = 0, bool failFirst = false, bool storageUnavailable = false)
    {
        int creates = 0;
        return new(req => req[0] switch
        {
            0x12 => VersionReply(),
            0x16 => ((req[4] & 0x80) != 0 ? ram : rom) is { } name ? Reply(0x16, DatabasePage(name)) : Reply(0x16, error: 5),
            0x15 => storageUnavailable ? Reply(0x15, error: 13) : Reply(0x15, Memory(free)),
            0x18 => failFirst && ++creates == 1 ? Reply(0x18, error: 3) : Reply(0x18, [1]),
            0x17 => Reply(0x17, [1]), 0x1a => Reply(0x1a, error: deleteError), 0x21 => Reply(0x21, [0, 0, 0, 1]),
            0x19 or 0x1c or 0x1e or 0x24 => Reply(req[0]), _ => throw new Exception($"Unexpected {req[0]:X2}")
        });
    }
    private static byte[] Memory(uint free) { var b = new byte[30]; BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(24), free); return b; }
    private static void Save(Folder folder, string filename, PalmDbFile file) => File.WriteAllBytes(Path.Combine(folder.Path, filename), Bytes(file));
    private static PalmDbFile Db(string name, string type = "DATA", string creator = "TEST", int size = 1)
    { var file = new PalmDbFile { Name = name, Type = type, Creator = creator }; file.Records.Add(new(1, 0, 0, new byte[size])); return file; }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void PlanSelectsNewestHeaderThenFileTimeThenCanonicalAndLogsFilename(int stage)
    {
        using var folder = new Folder(); var a = Db("Data"); var b = Db("Data"); a.Modified = When; b.Modified = stage == 0 ? When.AddDays(1) : When;
        string first = stage == 2 ? "Data.pdb" : "a.pdb"; Save(folder, first, a); Save(folder, "z.pdb", b);
        File.SetLastWriteTimeUtc(Path.Combine(folder.Path, first), When); File.SetLastWriteTimeUtc(Path.Combine(folder.Path, "z.pdb"), stage == 1 ? When.AddHours(1) : When);
        var lines = new List<string>(); var plan = DeviceRestore.Plan(new(Device()), folder.Path, lines.Add);
        Assert.Single(plan.Databases); string kept = stage == 2 ? first : "z.pdb", skipped = stage == 2 ? "z.pdb" : first;
        Assert.Contains($"復元しない {skipped}: 新しいほうを使用（{kept}）", lines);
    }
    [Fact]
    public void PlanSelectsDuplicateAndExcludesRomReadOnlyAndReportsParseFailure()
    {
        using var folder = new Folder();
        Save(folder, "rom.pdb", Db("ROM")); var readOnly = Db("ReadOnly"); readOnly.Attributes = 2; Save(folder, "ro.pdb", readOnly);
        Save(folder, "a.pdb", Db("Duplicate")); Save(folder, "b.prc", Db("Duplicate")); File.WriteAllBytes(Path.Combine(folder.Path, "bad.pqa"), [1]);
        var fake = Device(rom: "ROM"); var lines = new List<string>(); var plan = DeviceRestore.Plan(new(fake), folder.Path, lines.Add);
        Assert.Single(plan.Databases); Assert.Equal(3, plan.Skipped.Count); Assert.Single(plan.Failed);
        Assert.Contains(plan.Skipped, s => s.Reason.Contains("ROM")); Assert.Contains(plan.Skipped, s => s.Reason.Contains("ReadOnly")); Assert.Contains(plan.Skipped, s => s.Reason.Contains("新しいほうを使用"));
        Assert.All(fake.Requests, r => Assert.Contains(r[0], new byte[] { 0x16, 0x15 }));
    }
    [Fact]
    public void PlanOrdersStagesAppsAndMaximumBlockRatherThanTotalSize()
    {
        using var folder = new Folder();
        var files = new[] { Db("Library", "libr", size: 1), Db("Extension", "extn", size: 2), Db("Shared", "shlb", size: 3), Db("Overlay", "ovly", size: 4), Db("Data", size: 3), Db("App", "appl", size: 500), Db("Saved Preferences", size: 1000), Db("Unsaved Preferences", size: 1001), Db("psysLaunchDB", size: 1002) };
        foreach (var f in files) Save(folder, f.Name + ".pdb", f);
        var plan = DeviceRestore.Plan(new(Device()), folder.Path, _ => { });
        Assert.Equal(new[] { "Overlay", "Shared", "Extension", "Library", "Data", "App", "psysLaunchDB", "Unsaved Preferences", "Saved Preferences" }, plan.Databases.Select(d => d.File.Name));
        using var other = new Folder(); var many = Db("Many", creator: "aaaa", size: 4); many.Records.Add(new(2, 0, 0, new byte[4]));
        Save(other, "many.pdb", many); Save(other, "large.pdb", Db("Large", creator: "bbbb", size: 5)); Save(other, "hack.pdb", Db("Hack", "HACK", "cccc", 3));
        Assert.Equal(new[] { "Large", "Many", "Hack" }, DeviceRestore.Plan(new(Device()), other.Path, _ => { }).Databases.Select(d => d.File.Name));
    }
    [Fact]
    public void ReplacementDeletesThenCreatesAndWritesAllBlocksWithMaskedAttributes()
    {
        using var folder = new Folder(); var file = Sample(); file.Attributes = 0xc004; Save(folder, "a.pdb", file);
        var fake = Device(ram: file.Name); var dlp = new DlpClient(fake); var plan = DeviceRestore.Plan(dlp, folder.Path, _ => { }); var progress = new List<SyncProgress>();
        var result = DeviceRestore.Run(dlp, plan, _ => { }, progress.Add);
        Assert.Single(result.Succeeded); Assert.True(result.ResetRequired); Assert.Equal(5, result.WrittenBytes);
        Assert.Equal(new byte[] { 0x1a, 0x18, 0x1c, 0x1e, 0x21, 0x21, 0x19 }, fake.Requests.Skip(3).Select(r => r[0]));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(fake.Requests.Single(r => r[0] == 0x18).AsSpan(14)));
        Assert.Equal(new SyncProgress(SyncPhase.Restore, 1, 1, file.Name), Assert.Single(progress)); Assert.True(File.Exists(Path.Combine(folder.Path, "a.pdb")));
    }
    [Theory]
    [InlineData(7)] [InlineData(10)] [InlineData(12)]
    public void BusyResourceFallsBackToReadWriteWithoutDeletingOnFailure(ushort error)
    {
        using var folder = new Folder(); var file = Sample(true); file.Name = "Saved Preferences"; Save(folder, "prefs.prc", file);
        var fake = Device(ram: file.Name, deleteError: error); var dlp = new DlpClient(fake);
        var result = DeviceRestore.Run(dlp, DeviceRestore.Plan(dlp, folder.Path, _ => { }), _ => { });
        Assert.Single(result.Succeeded); Assert.Equal(0xc0, fake.Requests.Single(r => r[0] == 0x17)[5]);
        Assert.DoesNotContain(fake.Requests, r => r[0] is 0x18 or 0x1c or 0x1e); Assert.Equal(2, fake.Requests.Count(r => r[0] == 0x24)); Assert.Single(fake.Requests, r => r[0] == 0x1a);
    }
    [Fact]
    public void BusyRecordFailsAndOtherDatabaseContinues()
    {
        using var folder = new Folder(); Save(folder, "a.pdb", Db("A")); Save(folder, "b.pdb", Db("B")); var fake = Device(ram: "A", deleteError: 10); var dlp = new DlpClient(fake);
        var result = DeviceRestore.Run(dlp, DeviceRestore.Plan(dlp, folder.Path, _ => { }), _ => { });
        Assert.Equal("B", Assert.Single(result.Succeeded)); Assert.Equal("A", Assert.Single(result.Failed).Name); Assert.DoesNotContain(fake.Requests, r => r[0] == 0x17);
    }
    [Fact]
    public void InsufficientMemoryStopsBeforeAnyMutation()
    {
        using var folder = new Folder(); Save(folder, "a.pdb", Db("A")); var fake = Device(32768); var dlp = new DlpClient(fake);
        var plan = DeviceRestore.Plan(dlp, folder.Path, _ => { }); var result = DeviceRestore.Run(dlp, plan, _ => { });
        Assert.NotNull(result.StopReason); Assert.Empty(result.Succeeded); Assert.False(result.ResetRequired); Assert.Equal(3, fake.Requests.Count);
    }
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(true, true)]
    public void ReplacementCreditsDeviceSizeOrBackupEstimate(bool modern, bool unavailable)
    {
        using var folder = new Folder(); Save(folder, "a.pdb", Db("A", size: 1000));
        var normal = Device(32768, ram: "A");
        var fake = new Fake(req =>
        {
            if (req[0] == 0x39)
            {
                Assert.Equal(new byte[] { 0x39, 1, 0x20, 4, 0x40, 0, 65, 0 }, req);
                if (unavailable) return Reply(0x39, error: 13);
                var size = new byte[24]; BinaryPrimitives.WriteUInt32BigEndian(size.AsSpan(4), 2000);
                return new byte[] { 0xb9, 1, 0, 0, 0x21, 24 }.Concat(size).ToArray();
            }
            normal.Send(req); return normal.Receive();
        });
        var dlp = new DlpClient(fake); if (modern) dlp.ReadSysInfo();
        var lines = new List<string>(); var plan = DeviceRestore.Plan(dlp, folder.Path, lines.Add);
        long reclaimed = modern && !unavailable ? 2000 : new FileInfo(Path.Combine(folder.Path, "a.pdb")).Length;
        Assert.Equal(plan.Databases.Sum(d => d.Size) - reclaimed + 32768, plan.RequiredBytes);
        Assert.Contains($"置き換えで空く見込み：{reclaimed:N0} バイト", lines);
        Assert.Null(plan.StopReason); Assert.Single(DeviceRestore.Run(dlp, plan, lines.Add).Succeeded);
    }
    [Fact]
    public void CreateFailureCleansUpAndContinuesWithUnknownMemory()
    {
        using var folder = new Folder(); Save(folder, "a.pdb", Db("A")); Save(folder, "b.pdb", Db("B")); var fake = Device(failFirst: true, storageUnavailable: true); var dlp = new DlpClient(fake); var lines = new List<string>();
        var result = DeviceRestore.Run(dlp, DeviceRestore.Plan(dlp, folder.Path, lines.Add), lines.Add);
        Assert.Equal("B", Assert.Single(result.Succeeded)); Assert.Equal("A", Assert.Single(result.Failed).Name); Assert.Contains(lines, l => l.Contains("警告")); Assert.Single(fake.Requests, r => r[0] == 0x1a);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RecordsPreserveIdentityCategoryAndAttributesExceptBusyAndSkipEmptyDeleted(bool modern)
    {
        using var folder = new Folder(); var file = Db("A"); file.Records.Clear();
        file.Records.Add(new(0x123456, 0xf0, 3, [1])); file.Records.Add(new(2, 0x80, 4, [])); file.Records.Add(new(3, 0x60, 5, [2])); Save(folder, "a.pdb", file);
        var fake = Device(); var dlp = new DlpClient(fake); if (modern) dlp.ReadSysInfo();
        var result = DeviceRestore.Run(dlp, DeviceRestore.Plan(dlp, folder.Path, _ => { }), _ => { });
        var writes = fake.Requests.Where(r => r[0] == 0x21).ToArray(); Assert.Equal(modern ? 2 : 1, writes.Length);
        var last = writes[^1]; Assert.Equal(0x40, last[10]); Assert.Equal(5, last[11]);
        if (modern) { Assert.Equal(0xd0, writes[0][10]); Assert.Equal(3, writes[0][11]); Assert.Equal(0x123456u, BinaryPrimitives.ReadUInt32BigEndian(writes[0].AsSpan(6))); }
        Assert.True(result.ResetRequired);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void BodyFailureClosesAndCleansOnlyNewDatabaseThenContinues(bool overwrite)
    {
        using var folder = new Folder(); var file = Sample(overwrite); file.Name = "A"; Save(folder, "a.pdb", file); Save(folder, "b.pdb", Db("B", creator: "zzzz", size: 1));
        var normal = Device(ram: overwrite ? "A" : null, deleteError: overwrite ? (ushort)10 : (ushort)0); bool failed = false;
        var fake = new Fake(req =>
        {
            if (!failed && req[0] == (overwrite ? 0x24 : 0x21)) { failed = true; return Reply(req[0], error: 3); }
            normal.Send(req); return normal.Receive();
        });
        var dlp = new DlpClient(fake); var result = DeviceRestore.Run(dlp, DeviceRestore.Plan(dlp, folder.Path, _ => { }), _ => { });
        Assert.Equal("B", Assert.Single(result.Succeeded)); Assert.Equal("A", Assert.Single(result.Failed).Name);
        int failure = fake.Requests.FindIndex(r => r[0] == (overwrite ? 0x24 : 0x21)); Assert.Equal(0x19, fake.Requests[failure + 1][0]);
        Assert.Equal(overwrite ? 0x18 : 0x1a, fake.Requests[failure + 2][0]); Assert.True(result.ResetRequired);
    }
    [Fact]
    public void PlanRejectsOversizedRecordBeforeAnyWriteAndTiesUseOrdinalName()
    {
        using var folder = new Folder(); Save(folder, "huge.pdb", Db("Huge", size: 65522)); Save(folder, "z.pdb", Db("a")); Save(folder, "a.pdb", Db("Z"));
        var fake = Device(); var plan = DeviceRestore.Plan(new(fake), folder.Path, _ => { });
        Assert.Equal("Huge", Assert.Single(plan.Failed).Name);
        Assert.Equal(new[] { "Z", "a" }, plan.Databases.Select(d => d.File.Name)); Assert.Equal(3, fake.Requests.Count);
    }
    [Theory]
    [InlineData(1, "netl", 0, true)] [InlineData(7, "psys", 0, true)]
    [InlineData(10, "TEST", 0x8000, true)] [InlineData(12, "TEST", 0x8000, true)]
    [InlineData(3, "netl", 0x8000, false)] [InlineData(10, "TEST", 0, false)]
    public void RestoreClassifiesOnlyInUseSystemErrors(ushort error, string creator, ushort attributes, bool inUse)
    {
        var file = Db("Any name", creator: creator);
        var entry = Metadata(file) with { Attributes = attributes };
        var fake = new Fake(req => Reply(req[0], error: error));
        var plan = new DeviceRestorePlan([new(file, 80, true) { DeviceEntry = entry }], [], [], 0, null);
        var result = DeviceRestore.Run(new(fake), plan, _ => { });
        Assert.Equal(inUse ? 1 : 0, result.InUse.Count);
        Assert.Equal(inUse ? 0 : 1, result.Failed.Count);
    }
    [Theory]
    [InlineData(1000)] [InlineData(0)] [InlineData(100000)]
    public void PlanDisplaysNonnegativeRequiredFreeSpaceWithoutChangingEstimate(uint deviceSize)
    {
        using var folder = new Folder(); Save(folder, "a.pdb", Db("A"));
        if (deviceSize == 0) deviceSize = (uint)new FileInfo(Path.Combine(folder.Path, "a.pdb")).Length + 32768;
        var normal = Device(ram: "A");
        var fake = new Fake(req =>
        {
            if (req[0] == 0x39)
            {
                var size = new byte[24]; BinaryPrimitives.WriteUInt32BigEndian(size.AsSpan(4), deviceSize);
                return new byte[] { 0xb9, 1, 0, 0, 0x21, 24 }.Concat(size).ToArray();
            }
            normal.Send(req); return normal.Receive();
        });
        var dlp = new DlpClient(fake); dlp.ReadSysInfo(); var lines = new List<string>();
        var plan = DeviceRestore.Plan(dlp, folder.Path, lines.Add);
        Assert.Equal(plan.Databases.Sum(d => d.Size) - deviceSize + 32768, plan.RequiredBytes);
        Assert.Null(plan.StopReason);
        string total = Assert.Single(lines, l => l.StartsWith("復元合計："));
        if (plan.RequiredBytes <= 0) Assert.EndsWith("必要な空き：0 バイト（置き換えで空く分で足ります）", total);
        else Assert.EndsWith($"必要な空き（余裕込み）：{plan.RequiredBytes:N0} バイト", total);
    }
}
