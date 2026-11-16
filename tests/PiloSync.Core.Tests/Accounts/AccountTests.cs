using PiloSync.Core;
using Xunit;

namespace PiloSync.Core.Tests;

[CollectionDefinition("Accounts", DisableParallelization = true)]
public sealed class AccountCollection { }

[Collection("Accounts")]
public sealed class AccountTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PiloSync-tests-" + Guid.NewGuid().ToString("N"));
    public AccountTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    [Fact]
    public void ResolveAndAssignPreserveAccountIdentity()
    {
        var store = new AccountStore(root);
        var created = store.Resolve("Prism", 123);
        Assert.Equal(AccountResolutionKind.Created, created.Kind);
        var user = created.Account!;
        Assert.NotEqual(0u, user.Id);
        Assert.True(created.WriteDeviceUserId);
        var byId = store.Resolve("Renamed", user.Id);
        Assert.Equal(AccountResolutionKind.IdMatch, byId.Kind);
        Assert.NotNull(byId.Warning);
        Assert.Equal("Prism", byId.Account!.Name);
        Assert.False(byId.WriteDeviceUserId);
        var byName = store.Resolve("Prism", 0);
        Assert.Equal(AccountResolutionKind.NameMatch, byName.Kind);
        Assert.True(byName.WriteDeviceUserId);
        Assert.Equal(AccountResolutionKind.Unregistered, store.Resolve("", 0).Kind);
        Assert.Single(store.Data.Users);
        Assert.Same(user, store.Assign("Prism").Account);
        Assert.Equal(AccountResolutionKind.Created, store.Resolve("prism", 0).Kind);
        Assert.Equal(2, new AccountStore(root).Data.Users.Count);
    }
    [Fact]
    public void SafeSaveCreatesDirectoriesAndLeavesNoTemporaryFiles()
    {
        var store = new AccountStore(root);
        var user = store.Assign("Tungsten C").Account!;
        Assert.Equal("Tungst", user.Folder);
        Assert.True(Path.IsPathFullyQualified(store.GetUserFolder(user)));
        Assert.True(Directory.Exists(store.GetInstallFolder(user)));
        Assert.True(Directory.Exists(store.GetBackupFolder(user)));
        user.Flags = 0xfffa; store.Save();
        Assert.Equal((ushort)0xfffa, new AccountStore(root).Data.Users[0].Flags);
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        Assert.Equal("Tungs2", store.Assign("Tungsten D").Account!.Folder);
        Assert.DoesNotContain(':', store.Assign("CON:").Account!.Folder);
        Assert.StartsWith("_", store.Assign("CON").Account!.Folder);
    }
    [Fact]
    public void ConcurrentChangesAreNotOverwritten()
    {
        var first = new AccountStore(root);
        var second = new AccountStore(root);
        first.Assign("a");
        Assert.Throws<IOException>(() => second.Assign("b"));
        Assert.Empty(second.Data.Users);
        Assert.Equal("a", new AccountStore(root).Data.Users.Single().Name);
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }
    [Fact]
    public void FailedReplacePreservesOriginalAndCleansTemporaryFile()
    {
        var store = new AccountStore(root); store.Assign("a");
        string path = Path.Combine(root, "users.dat"); byte[] original = File.ReadAllBytes(path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            store.Data.Users[0].Name = "changed";
            Assert.Throws<IOException>(() => store.Save());
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
    }
    [Fact]
    public void StockMutexTimeoutDoesNotWrite()
    {
        using var ready = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var task = Task.Run(() => { using var gate = new Mutex(false, AccountStore.SemaphoreName); gate.WaitOne(); ready.Set(); release.Wait(); gate.ReleaseMutex(); });
        ready.Wait();
        try
        {
            var store = new AccountStore(root, TimeSpan.FromMilliseconds(40));
            Assert.Throws<TimeoutException>(() => store.Save());
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { release.Set(); }
        Assert.True(SpinWait.SpinUntil(() => task.IsCompleted, TimeSpan.FromSeconds(5)));
        Assert.True(task.IsCompletedSuccessfully);
    }
    [Theory]
    [InlineData("..")][InlineData("../escape")][InlineData("C:\\escape")][InlineData("CON")][InlineData("name.")]
    public void UnsafeFolderIsRejected(string folder) => Assert.Throws<InvalidDataException>(() => new AccountStore(root).GetUserFolder(new() { Folder = folder }));
}
