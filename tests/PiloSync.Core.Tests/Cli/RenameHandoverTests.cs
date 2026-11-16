using System.Text.Json;
using PiloSync.Cli;
using Xunit;
namespace PiloSync.Core.Tests;

[Collection("Accounts")]
public sealed class RenameHandoverTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "PiloSync-rename-" + Guid.NewGuid().ToString("N"));
    string Old => Path.Combine(root, "PalmSync");
    string Next => Path.Combine(root, "PiloSync");
    public RenameHandoverTests() { Directory.CreateDirectory(root); }
    public void Dispose() => Directory.Delete(root, true);
    sealed class Lease(Action release) : IDisposable { public void Dispose() => release(); }
    sealed class Fake(string local) : IHandoverEnvironment
    {
        public string LocalAppData => local;
        public bool OldRunning { get; set; }
        public bool StockRunning { get; set; }
        public bool LockAvailable = true, Locked, FailMove, FailCore, FailRollback, FailRun;
        public int Copies, Moves, RunWrites;
        public string? PathValue;
        public FakePalmRegistry Source = new(), Target = new();
        public (string?, string?)? Rename;
        public string? NewRun;
        public string? OldRun { get; set; }
        public string? CorePath { get => PathValue; set { if (FailCore) throw new IOException("Core write"); PathValue = value; } }
        public MigrationRecord? Migration { get => Target.Migration; set => Target.Migration = value; }
        public IDisposable? LockUsers() { if (!LockAvailable) return null; Locked = true; return new Lease(() => Locked = false); }
        public void Move(string from, string to) { Assert.True(Locked); Moves++; if (FailMove || (FailRollback && Moves > 1)) throw new IOException("Move"); Directory.Move(from, to); }
        public bool CopySettings() { Copies++; return RenameHandover.CopyMissingSettings(Source, Target); }
        public void RecordRename(string? previous, string? next) => Rename = (previous, next);
        public string Executable => @"C:\publish\PiloSync.exe";
        public void SetRun(string command) { if (FailRun) throw new IOException("Run write"); NewRun = command; RunWrites++; }
        public void DeleteOldRun() { Assert.NotNull(NewRun); OldRun = null; }
    }
    Fake OldFolder()
    {
        Directory.CreateDirectory(Path.Combine(Old, "Palm OS Desktop")); File.WriteAllText(Path.Combine(Old, "data"), "backup");
        return new(root) { PathValue = Path.Combine(Old, "Palm OS Desktop") };
    }
    [Fact] public void MovesContentsUpdatesCoreAndRecordsRename()
    {
        var env = OldFolder(); string previous = env.PathValue!;
        var result = RenameHandover.Run(env);
        Assert.Empty(result.Warnings); Assert.False(Directory.Exists(Old)); Assert.Equal("backup", File.ReadAllText(Path.Combine(Next, "data")));
        Assert.Equal(Path.Combine(Next, "Palm OS Desktop"), env.CorePath); Assert.Equal((previous, env.CorePath), env.Rename); Assert.False(env.Locked);
        RenameHandover.Run(env); Assert.Equal(1, env.Moves);
    }
    [Fact] public void MissingFoldersDoNotCreateAnything()
    {
        var env = new Fake(root); var result = RenameHandover.Run(env);
        Assert.Empty(result.Warnings); Assert.Empty(Directory.GetFileSystemEntries(root)); Assert.Equal(0, env.Moves); Assert.Null(env.Rename); Assert.Equal(0, env.RunWrites);
    }
    [Fact] public void BothFoldersLeaveCoreAndContentsAlone()
    {
        var env = OldFolder(); Directory.CreateDirectory(Next); string? previous = env.CorePath;
        var result = RenameHandover.Run(env); Assert.Equal(0, env.Moves); Assert.Equal(previous, env.CorePath); Assert.NotEmpty(result.Actions); Assert.Null(env.Rename);
    }
    [Fact] public void MoveFailureKeepsOldFolderAndCoreButTransfersRun()
    {
        var env = OldFolder(); env.FailMove = true; env.OldRun = "old"; string? previous = env.CorePath;
        var result = RenameHandover.Run(env); Assert.NotEmpty(result.Warnings); Assert.True(Directory.Exists(Old)); Assert.False(Directory.Exists(Next)); Assert.Equal(previous, env.CorePath); Assert.Equal(1, env.RunWrites);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public void HotSyncDefersMove(bool stock)
    {
        var env = OldFolder(); env.StockRunning = stock; env.LockAvailable = stock;
        Assert.NotEmpty(RenameHandover.Run(env).Warnings); Assert.Equal(0, env.Moves); Assert.True(Directory.Exists(Old));
    }
    [Fact] public void OldProcessPreventsAllHandoverActions()
    {
        var env = OldFolder(); env.OldRunning = true; env.OldRun = "old";
        var result = RenameHandover.Run(env); Assert.True(result.OldRunning); Assert.Contains(RenameHandover.OldRunningMessage, result.Warnings); Assert.Equal(0, env.Copies); Assert.Equal(0, env.Moves); Assert.Equal(0, env.RunWrites);
    }
    [Theory] [InlineData("stock")] [InlineData("PalmSyncX")]
    public void UnrelatedCorePathIsPreserved(string folder)
    {
        var env = OldFolder(); env.PathValue = Path.Combine(root, folder, "Palm OS Desktop"); string? previous = env.CorePath;
        RenameHandover.Run(env); Assert.Equal(previous, env.CorePath); Assert.True(Directory.Exists(Next));
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public void CoreWriteFailureAttemptsRollback(bool failRollback)
    {
        var env = OldFolder(); env.FailCore = true; env.FailRollback = failRollback; string? previous = env.CorePath;
        var result = RenameHandover.Run(env); Assert.Equal(previous, env.CorePath); Assert.Equal(2, env.Moves); Assert.Equal(!failRollback, Directory.Exists(Old)); Assert.Equal(failRollback, Directory.Exists(Next)); Assert.Equal(failRollback ? 2 : 1, result.Warnings.Count); Assert.Null(env.Rename);
    }
    [Fact] public void AlreadyMovedFolderRepairsOldCorePath()
    {
        var env = OldFolder(); Directory.Move(Old, Next);
        Assert.Empty(RenameHandover.Run(env).Warnings); Assert.Equal(Path.Combine(Next, "Palm OS Desktop"), env.CorePath); Assert.NotNull(env.Rename);
    }
    [Fact] public void MigrationRecordsKeepOriginalAndAllowUndoDryRun()
    {
        var env = OldFolder(); string stock = Path.Combine(root, "stock"); Directory.CreateDirectory(stock);
        var record = new MigrationRecord(stock, env.CorePath!, DateTimeOffset.UtcNow);
        env.Source.Migration = record; File.WriteAllText(Path.Combine(Old, "migration.json"), JsonSerializer.Serialize(record));
        Assert.Empty(RenameHandover.Run(env).Warnings);
        Assert.Equal(record with { NewPath = env.CorePath! }, env.Migration);
        Assert.Equal(env.Migration, JsonSerializer.Deserialize<MigrationRecord>(File.ReadAllText(Path.Combine(Next, "migration.json"))));
        env.Target.DataPath = env.CorePath;
        using var output = new StringWriter(); Assert.Equal(0, MigrateCommand.Run(["--undo", "--dry-run"], env.Target, output, root));
    }
    [Fact] public void PreviousMigrationPathUnderOldFolderAlsoChanges()
    {
        var env = OldFolder(); env.Source.Migration = new(Path.Combine(Old, "original"), env.CorePath!, DateTimeOffset.UtcNow);
        RenameHandover.Run(env); Assert.Equal(Path.Combine(Next, "original"), env.Migration!.PreviousPath);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public void TransfersOnlyExistingOldRun(bool exists)
    {
        var env = new Fake(root) { OldRun = exists ? "old" : null };
        RenameHandover.Run(env); Assert.Null(env.OldRun); Assert.Equal(exists ? "\"C:\\publish\\PiloSync.exe\" tray" : null, env.NewRun); Assert.Equal(exists ? 1 : 0, env.RunWrites);
    }
    [Fact] public void RunWriteFailurePreservesOldRegistration()
    {
        var env = new Fake(root) { OldRun = "old", FailRun = true };
        Assert.NotEmpty(RenameHandover.Run(env).Warnings); Assert.Equal("old", env.OldRun); Assert.Null(env.NewRun);
    }
    [Fact] public void InvalidMigrationFileReportsFailureAndStillTransfersRun()
    {
        var env = OldFolder(); env.OldRun = "old"; File.WriteAllText(Path.Combine(Old, "migration.json"), "broken");
        Assert.NotEmpty(RenameHandover.Run(env).Warnings); Assert.Equal(1, env.RunWrites); Assert.True(Directory.Exists(Next));
    }
    [Theory] [InlineData(true, true)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(false, false)]
    public void CopiesOnlyMissingSettings(bool pc, bool migration)
    {
        var source = new FakePalmRegistry { PcId = 123, Migration = new("source", "target", DateTimeOffset.UtcNow) };
        var original = new MigrationRecord("other", "next", DateTimeOffset.UtcNow);
        var target = new FakePalmRegistry { PcId = pc ? 456u : null, Migration = migration ? original : null };
        RenameHandover.CopyMissingSettings(source, target); Assert.Equal(pc ? 456u : 123u, target.PcId); Assert.Equal(migration ? original : source.Migration, target.Migration);
    }
    [Fact] public void MissingLegacySettingsAreNotCreated()
    {
        var target = new FakePalmRegistry(); RenameHandover.CopyMissingSettings(new FakePalmRegistry(), target); Assert.Null(target.PcId); Assert.Null(target.Migration);
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void AppFolderResolvesExistingFoldersWithoutCaching(int folders)
    {
        Assert.Equal(Next, AppFolder.Resolve(root));
        if ((folders & 1) != 0) Directory.CreateDirectory(Old);
        if ((folders & 2) != 0) Directory.CreateDirectory(Next);
        Assert.Equal(folders == 1 ? Old : Next, AppFolder.Resolve(root));
    }
    [Theory] [InlineData("")] [InlineData("\\Palm OS Desktop\\")] [InlineData("\\..\\PalmSync\\data")]
    public void PathReplacementUsesFullPathAndCaseInsensitiveBoundary(string suffix)
    {
        Assert.Equal(Next + Path.TrimEndingDirectorySeparator(Path.GetFullPath(Old + suffix))[Old.Length..], RenameHandover.ReplacePath(Old.ToUpperInvariant() + suffix, Old, Next));
        Assert.Null(RenameHandover.ReplacePath(null, Old, Next));
        Assert.Equal(Old + "X\\data", RenameHandover.ReplacePath(Old + "X\\data", Old, Next));
    }
}
