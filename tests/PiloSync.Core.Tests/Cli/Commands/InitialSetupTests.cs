using PiloSync.Cli;
using Xunit;

namespace PiloSync.Core.Tests;

[Collection("Accounts")]
public sealed class InitialSetupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PiloSync-setup-" + Guid.NewGuid().ToString("N"));
    private string Target => Path.Combine(AppFolder.Resolve(root), "Palm OS Desktop");
    public InitialSetupTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExistingPathIsUnchangedEvenWithoutUsers(bool hasUsers)
    {
        string stock = Path.Combine(root, "stock"); Directory.CreateDirectory(stock);
        if (hasUsers) new AccountStore(stock).Save();
        var registry = new FakePalmRegistry { DataPath = stock }; var lines = new List<string>();
        InitialSetup.Run(registry, root, lines.Add);
        Assert.Equal(stock, registry.DataPath); Assert.Empty(lines); Assert.False(Directory.Exists(Target));
        Assert.Equal(hasUsers, File.Exists(Path.Combine(stock, "users.dat")));
    }
    [Fact]
    public void CreatesEmptyUsersOnce()
    {
        var registry = new FakePalmRegistry(); var lines = new List<string>();
        InitialSetup.Run(registry, root, lines.Add);
        Assert.Equal(Target, registry.DataPath);
        string path = Path.Combine(Target, "users.dat"); byte[] before = File.ReadAllBytes(path);
        Assert.Empty(UsersDat.Read(before).Users);
        Assert.Single(lines); Assert.StartsWith("初期設定を作成しました：", lines[0]);
        InitialSetup.Run(registry, root, lines.Add);
        Assert.Single(lines); Assert.Equal(before, File.ReadAllBytes(path));
    }
    [Fact]
    public void ExistingUsersWithoutPathAreUntouched()
    {
        Directory.CreateDirectory(Target); string path = Path.Combine(Target, "users.dat");
        byte[] before = [42, 17, 3]; File.WriteAllBytes(path, before);
        var registry = new FakePalmRegistry(); var lines = new List<string>();
        InitialSetup.Run(registry, root, lines.Add);
        Assert.Null(registry.DataPath); Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Single(lines); Assert.Contains("初期設定は作成しません：", lines[0]);
        Assert.Single(Directory.GetFiles(Target));
    }
    [Fact]
    public void RegistryWriteFailureRemovesCreatedUsers()
    {
        var registry = new FailingRegistry(); var lines = new List<string>();
        InitialSetup.Run(registry, root, lines.Add);
        Assert.Null(registry.DataPath); Assert.False(File.Exists(Path.Combine(Target, "users.dat")));
        Assert.Single(lines); Assert.Contains("警告：", lines[0]); Assert.Contains("書き込み失敗", lines[0]);
        var retry = new FakePalmRegistry(); InitialSetup.Run(retry, root, lines.Add);
        Assert.Equal(Target, retry.DataPath); Assert.Empty(new AccountStore(Target).Data.Users);
    }
    private sealed class FailingRegistry : IPalmRegistry
    {
        public string? DataPath { get => null; set => throw new IOException("書き込み失敗"); }
        public uint? StockPcId => null;
        public uint? PcId { get; set; }
        public MigrationRecord? Migration { get; set; }
        public uint? ReadInstallFlags(uint id) => null;
        public void WriteInstallFlags(uint id, uint flags) { }
    }
}
