using PiloSync.Protocol;
using static PiloSync.Core.Tests.DbFixtures;
using Xunit;
namespace PiloSync.Core.Tests;

public class InstallQueueTests
{
    [Fact]
    public void CancelRecyclesFilesForEachAccount()
    {
        using var folder = new Folder(); var store = new AccountStore(folder.Path);
        var accounts = new[] { new PalmAccount { Name = "Prism", Folder = "Prism" }, new PalmAccount { Name = "Prism2", Folder = "Prism2" } };
        var items = accounts.Select(a => new PendingInstall(a, "app.pdb", 1)).ToArray();
        var paths = accounts.Select(a => Path.Combine(store.GetInstallFolder(a), "app.pdb")).ToArray();
        foreach (string path in paths) File.WriteAllBytes(path, [1]);
        var recycled = new List<string>();
        var results = InstallQueue.Cancel(store, items, path => { recycled.Add(path); File.Delete(path); });
        Assert.Equal(paths, recycled); Assert.All(results, r => { Assert.True(r.Cancelled); Assert.Null(r.Error); });
    }
    [Fact]
    public void CancelSkipsMissingFile()
    {
        using var folder = new Folder(); var store = new AccountStore(folder.Path);
        var item = new PendingInstall(new PalmAccount { Name = "Z22", Folder = "Z22" }, "gone.pdb", 0);
        var result = Assert.Single(InstallQueue.Cancel(store, [item], _ => throw new Exception("unexpected")));
        Assert.True(result.Skipped); Assert.Null(result.Error);
    }
    [Theory]
    [InlineData("../outside.pdb")] [InlineData("sub\\file.pdb")] [InlineData("sub/file.pdb")] [InlineData("a..pdb")]
    public void CancelRejectsInvalidNames(string name)
    {
        using var folder = new Folder(); var store = new AccountStore(folder.Path);
        var item = new PendingInstall(new PalmAccount { Name = "Z22", Folder = "Z22" }, name, 0);
        var result = Assert.Single(InstallQueue.Cancel(store, [item], _ => throw new Exception("unexpected")));
        Assert.False(result.Cancelled); Assert.NotNull(result.Error);
    }
    [Fact]
    public void CancelReportsFailureAndContinues()
    {
        using var folder = new Folder(); var store = new AccountStore(folder.Path);
        var account = new PalmAccount { Name = "Z22", Folder = "Z22" };
        string dir = store.GetInstallFolder(account);
        File.WriteAllBytes(Path.Combine(dir, "locked.pdb"), [1]); File.WriteAllBytes(Path.Combine(dir, "other.pdb"), [1]);
        var results = InstallQueue.Cancel(store, [new(account, "locked.pdb", 1), new(account, "other.pdb", 1)], path => { if (path.EndsWith("locked.pdb")) throw new IOException("locked"); File.Delete(path); });
        Assert.Equal("locked", results[0].Error); Assert.True(results[1].Cancelled);
        Assert.True(File.Exists(Path.Combine(dir, "locked.pdb"))); Assert.False(File.Exists(Path.Combine(dir, "other.pdb")));
    }
    [Fact]
    public void PendingListsSupportedFilesForSelectedAccountsOnly()
    {
        using var folder = new Folder(); var store = new AccountStore(folder.Path);
        var first = new PalmAccount { Name = "Prism", Folder = "Prism" };
        var second = new PalmAccount { Name = "Prism2", Folder = "Prism2" };
        string firstDir = store.GetInstallFolder(first), secondDir = store.GetInstallFolder(second);
        File.WriteAllBytes(Path.Combine(firstDir, "app.PRC"), [1, 2]);
        File.WriteAllBytes(Path.Combine(firstDir, "news.pdb"), [1]);
        File.WriteAllBytes(Path.Combine(firstDir, "web.pqa"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(firstDir, "ignore.txt"), [1]);
        Directory.CreateDirectory(Path.Combine(firstDir, "nested"));
        File.WriteAllBytes(Path.Combine(firstDir, "nested", "hidden.pdb"), [1]);
        File.WriteAllBytes(Path.Combine(secondDir, "other.pdb"), [1]);
        Assert.Empty(InstallQueue.Pending(store, []));
        var pending = InstallQueue.Pending(store, [first]);
        Assert.Equal(new[] { "app.PRC", "news.pdb", "web.pqa" }, pending.Select(p => p.FileName));
        Assert.Equal(new long[] { 2, 1, 3 }, pending.Select(p => p.Size));
        Assert.All(pending, p => Assert.Same(first, p.Account));
        Assert.Equal(4, InstallQueue.Pending(store, [first, second]).Count);
    }
    [Fact]
    public void PendingIgnoresMissingOrUnreadableFolders()
    {
        using var folder = new Folder(); var store = new AccountStore(folder.Path);
        var missing = new PalmAccount { Name = "Missing", Folder = "Missing" };
        var blocked = new PalmAccount { Name = "Blocked", Folder = "Blocked" };
        File.WriteAllText(Path.Combine(folder.Path, "Blocked"), "file blocks folder");
        Assert.Empty(InstallQueue.Pending(store, [missing, blocked]));
    }
    [Fact]
    public void CopiesToMultipleAccountsAndCreatesFolders()
    {
        using var folder = new Folder();
        var store = new AccountStore(folder.Path);
        var accounts = new[] { new PalmAccount { Name = "Prism", Folder = "Prism", Id = 300 }, new PalmAccount { Name = "Prism2", Folder = "Prism2", Id = 301 } };
        string source = Path.Combine(folder.Path, "app.PRc"); File.WriteAllBytes(source, Bytes(Sample()));
        var results = InstallQueue.Add(store, accounts, [source], (_, _) => throw new Exception());
        Assert.Equal(2, results.Count); Assert.All(results, r => Assert.True(r.Added));
        foreach (var account in accounts) { string target = Path.Combine(store.GetInstallFolder(account), "app.PRc"); Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(target)); Assert.Single(Directory.GetFiles(Path.GetDirectoryName(target)!)); }
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public void ConfirmsOverwrite(bool allow)
    {
        using var folder = new Folder(); var store = new AccountStore(folder.Path);
        var account = new PalmAccount { Name = "Vx", Folder = "Vx", Id = 302 };
        string source = Path.Combine(folder.Path, "app.pdb"), target = Path.Combine(store.GetInstallFolder(account), "app.pdb");
        byte[] bytes = Bytes(Sample()); File.WriteAllBytes(source, bytes); File.WriteAllBytes(target, [1, 2]); int calls = 0;
        var result = Assert.Single(InstallQueue.Add(store, [account], [source], (a, p) => { Assert.Same(account, a); Assert.Equal(target, p); calls++; return allow; }));
        Assert.Equal(1, calls); Assert.Equal(allow, result.Added); Assert.Equal(!allow, result.Skipped); Assert.Null(result.Error);
        Assert.Equal(allow ? bytes : new byte[] { 1, 2 }, File.ReadAllBytes(target)); Assert.Single(Directory.GetFiles(Path.GetDirectoryName(target)!));
    }
    [Theory] [InlineData("bad.txt")] [InlineData("bad.pdb")] [InlineData("bad.pqa")]
    public void RejectsExtensionAndBadHeader(string filename)
    {
        using var folder = new Folder(); var store = new AccountStore(folder.Path); var account = new PalmAccount { Name = "Z22", Folder = "Z22" };
        string source = Path.Combine(folder.Path, filename); File.WriteAllBytes(source, [1, 2]);
        Assert.NotNull(InstallQueue.Check(source)); var result = Assert.Single(InstallQueue.Add(store, [account], [source], (_, _) => true)); Assert.False(result.Added); Assert.NotNull(result.Error); Assert.False(Directory.Exists(Path.Combine(folder.Path, "Z22")));
    }
    [Fact]
    public void RejectsInvalidDatabaseBeforeQueueing()
    {
        using var folder = new Folder(); byte[] bytes = Bytes(Sample()); Array.Clear(bytes, 0, 32);
        string source = Path.Combine(folder.Path, "bad.prc"); File.WriteAllBytes(source, bytes); Assert.NotNull(InstallQueue.Check(source));
    }
    [Fact]
    public void RejectsBlocksTooLargeForDeviceInstaller()
    {
        using var folder = new Folder(); var db = Sample(); db.AppInfo = new byte[65526];
        string source = Path.Combine(folder.Path, "large.pdb"); File.WriteAllBytes(source, Bytes(db));
        Assert.NotNull(InstallQueue.Check(source));
    }
    [Fact]
    public void LockedDestinationFailsAndRemovesTemporaryFile()
    {
        using var folder = new Folder(); var store = new AccountStore(folder.Path); var account = new PalmAccount { Name = "Vx", Folder = "Vx" };
        string source = Path.Combine(folder.Path, "app.pdb"), target = Path.Combine(store.GetInstallFolder(account), "app.pdb"); File.WriteAllBytes(source, Bytes(Sample())); File.WriteAllBytes(target, [1]);
        using (var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None)) { var result = Assert.Single(InstallQueue.Add(store, [account], [source], (_, _) => true)); Assert.False(result.Added); Assert.NotNull(result.Error); }
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(target)!)); Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(target));
    }
}
