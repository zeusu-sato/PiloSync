using PiloSync.Cli;
using PiloSync.Protocol;
using Xunit;
using static PiloSync.Core.Tests.DbFixtures;
namespace PiloSync.Core.Tests;

[Collection("Accounts")]
public sealed class BackupTidyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PiloSync-tidy-" + Guid.NewGuid().ToString("N"));
    private string Backup => Path.Combine(root, "User", "Backup");
    private string Archive => Path.Combine(root, "User", "Backup Archive");
    public BackupTidyTests() => Directory.CreateDirectory(Backup);
    public void Dispose() => Directory.Delete(root, true);
    private string Save(string filename, string name = "Data", DateTime? modified = null)
    {
        string path = Path.Combine(Backup, filename); File.WriteAllBytes(path, Bytes(new PalmDbFile { Name = name, Modified = modified, Type = "DATA", Creator = "TEST" })); return path;
    }
    [Fact]
    public void HeaderNamesAreOrdinalAndArchiveIsCreatedOnlyWhenNeeded()
    {
        Save("a.pdb", "Data"); Save("b.pdb", "data");
        Assert.Equal(0, BackupTidy.Run(Backup, Archive, _ => { })); Assert.False(Directory.Exists(Archive));
    }
    [Fact]
    public void CommandReportsMissingAccountAndMissingStore()
    {
        var registry = new FakePalmRegistry { DataPath = root };
        Assert.Contains("users.dat", Assert.Throws<ArgumentException>(() => TidyBackupCommand.Run(["tidy-backup", "Missing"], registry, new StringWriter(), () => false)).Message);
        new AccountStore(root).Save();
        Assert.Equal("失敗：指定したアカウントがありません", Assert.Throws<ArgumentException>(() => TidyBackupCommand.Run(["tidy-backup", "Missing"], registry, new StringWriter(), () => false)).Message);
    }
    [Theory]
    [InlineData("1234-TESTPC.PRC")]
    [InlineData("Graffiti_ShortCuts_-TESTPC.PRC")]
    [InlineData("たっち__.PRC")]
    public void DuplicateUsesHeaderNameAndPreservesOtherHeadersAndUnreadableFiles(string old)
    {
        string keep = Save("Data.pdb"), duplicate = Save(old), other = Save("Data-PC.pdb", "Other");
        File.WriteAllBytes(Path.Combine(Backup, "bad.prc"), [1]); Save("ignored.txt"); Save(".temporary.pdb");
        var confirmed = new Dictionary<string, string> { ["Data"] = keep }; var lines = new List<string>();
        Assert.Equal(1, BackupTidy.Run(Backup, Archive, lines.Add, confirmed: confirmed));
        Assert.False(File.Exists(duplicate)); Assert.True(File.Exists(Path.Combine(Archive, old))); Assert.True(File.Exists(other)); Assert.Equal(5, Directory.GetFiles(Backup).Length);
        Assert.Contains($"整理：{old} → Backup Archive（古い重複）", lines);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void SelectionUsesModifiedThenFileTimeThenCanonicalThenOrdinal(int stage)
    {
        string canonical = BackupFileName.For("Data", false, "DATA", "TEST");
        string first = Save(stage == 2 ? canonical : "a.prc", modified: When), second = Save("z.pdb", modified: stage == 0 ? When.AddDays(1) : When);
        File.SetLastWriteTimeUtc(first, When); File.SetLastWriteTimeUtc(second, stage == 1 ? When.AddHours(1) : When);
        Assert.Equal(stage < 2 ? second : first, BackupTidy.Select(BackupTidy.Paths(Backup).Select(BackupTidy.Read)).Path);
    }
    [Fact]
    public void ArchiveCollisionPreservesAllCopies()
    {
        Save("old.prc"); Save("keep.pdb", modified: When); Directory.CreateDirectory(Archive); File.WriteAllText(Path.Combine(Archive, "old.prc"), "existing");
        for (int i = 0; i < 3; i++) { Save("old.prc"); Assert.Equal(1, BackupTidy.Run(Backup, Archive, _ => { })); }
        Assert.Equal(4, Directory.GetFiles(Archive).Length); Assert.Equal("existing", File.ReadAllText(Path.Combine(Archive, "old.prc")));
    }
    [Fact]
    public void MissingDeviceDatabaseMovesButRomAndUnconfirmedDuplicatesStay()
    {
        Save("gone.pqa", "Gone"); Save("rom.prc", "ROM"); Save("failed-a.pdb", "Failed"); Save("failed-b.pdb", "Failed");
        var names = new HashSet<string>(StringComparer.Ordinal) { "ROM", "Failed" };
        Assert.Equal(1, BackupTidy.Run(Backup, Archive, _ => { }, confirmed: new Dictionary<string, string>(), deviceNames: names));
        Assert.Equal(3, Directory.GetFiles(Backup).Length); Assert.True(File.Exists(Path.Combine(Archive, "gone.pqa")));
    }
    [Fact]
    public void MoveFailureLeavesSourceAndContinues()
    {
        string old = Save("a.pdb"); Save("b.pdb", modified: When); File.WriteAllText(Archive, "blocked"); var lines = new List<string>();
        Assert.Equal(0, BackupTidy.Run(Backup, Archive, lines.Add)); Assert.True(File.Exists(old)); Assert.Contains(lines, l => l.StartsWith("整理失敗 a.pdb:"));
    }
    [Theory]
    [InlineData(true, true, 0)] [InlineData(false, true, 1)] [InlineData(false, false, 0)]
    public void CommandDryRunAndTrayGuard(bool dryRun, bool running, int code)
    {
        var store = new AccountStore(root); store.Data.Users.Add(new() { Id = 300, Name = "User", Folder = "User" }); store.Save();
        Save("old.prc"); Save("new.pdb", modified: When); var output = new StringWriter();
        string[] args = dryRun ? ["tidy-backup", "User", "--dry-run"] : ["tidy-backup", "User"];
        Assert.Equal(code, TidyBackupCommand.Run(args, new FakePalmRegistry { DataPath = root }, output, () => running));
        Assert.Equal(dryRun || running ? 2 : 1, Directory.GetFiles(Backup).Length); Assert.Equal(!dryRun && !running, Directory.Exists(Archive));
        Assert.Contains(dryRun ? "整理予定：old.prc" : running ? "トレイから終了" : "整理完了：1 件", output.ToString());
    }
    [Theory]
    [InlineData("tidy-backup")]
    [InlineData("tidy-backup", "--dry-run")]
    [InlineData("tidy-backup", "User", "--unknown")]
    [InlineData("tidy-backup", "User", "extra")]
    [InlineData("tidy-backup", "User", "--log")]
    [InlineData("tidy-backup", "User", "--log", "--dry-run")]
    public void CommandRejectsMissingAccountAndUnknownArguments(params string[] args) => Assert.Throws<ArgumentException>(() => CommandLine.ParseTidyBackup(args));
    [Theory]
    [InlineData(true, false)] [InlineData(false, false)] [InlineData(false, true)]
    public void CommandLogAppendsUtf8AndMatchesOutput(bool dryRun, bool running)
    {
        var store = new AccountStore(root); store.Data.Users.Add(new() { Id = 300, Name = "User", Folder = "User" }); store.Save();
        Save("古い.prc"); Save("new.pdb", modified: When);
        string path = Path.Combine(root, "整理.log"); var utf8 = new System.Text.UTF8Encoding(false, true);
        File.WriteAllText(path, "前回\n", utf8); var output = new StringWriter();
        string[] args = dryRun ? ["tidy-backup", "User", "--log", path, "--dry-run"] : ["tidy-backup", "User", "--log", path];
        Assert.Equal(path, CommandLine.ParseTidyBackup(args).LogPath);
        Assert.Equal(running ? 1 : 0, TidyBackupCommand.Run(args, new FakePalmRegistry { DataPath = root }, output, () => running));
        Assert.Equal(utf8.GetBytes("前回\n" + output), File.ReadAllBytes(path));
        var usage = new StringWriter(); CommandLine.Usage(usage); Assert.Contains("tidy-backup <アカウント名> [--dry-run] [--log <path>]", usage.ToString());
    }
    [Fact]
    public void CommandClassifiesAndParsesDryRun()
    {
        Assert.Equal(CommandKind.Command, CommandLine.Classify(["tidy-backup"])); Assert.Equal(new TidyBackupOptions("User", true), CommandLine.ParseTidyBackup(["tidy-backup", "User", "--dry-run"]));
    }
}
