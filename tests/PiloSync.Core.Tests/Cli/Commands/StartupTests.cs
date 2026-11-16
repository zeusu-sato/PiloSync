using PiloSync.Cli;
using Xunit;
namespace PiloSync.Core.Tests;
public class StartupTests
{
    sealed class Fake : IStartupEnvironment
    {
        public string Executable => @"C:\Example Sync\PiloSync.exe";
        public bool StockRunning => true;
        public List<StartupItem>? State;
        public string? Command;
        public int Writes, Disabled, Restored;
        public List<StartupItem> Discovered = [new("Run", "HotSync", "HotSync.exe"), new("Link", "a.lnk", "backup")];
        public List<ProtectedStartup> Protected = [new("HKLM Run HotSync", false)];
        public List<StartupItem>? Load() => State;
        public void Save(List<StartupItem> items) { State = items; Writes++; }
        public void DeleteState() { State = null; Writes++; }
        public IEnumerable<StartupItem> Discover() => Discovered;
        public IEnumerable<ProtectedStartup> ProtectedItems() => Protected;
        public void Disable(StartupItem item) { Disabled++; Writes++; }
        public void Restore(StartupItem item) { Restored++; Writes++; }
        public string? GetPiloSync() => Command;
        public void SetPiloSync(string? command) { Command = command; Writes++; }
    }
    [Theory] [InlineData("PalmSync")] [InlineData("PiloSync")]
    public void OwnSourcesAreNeverDisabledOrRestored(string source)
    {
        var env = new Fake { Discovered = [new(source, source, "old.exe tray")], Protected = [] };
        using var output = new StringWriter();
        Assert.Equal(0, StartupCommand.Run(true, [], env, output)); Assert.Equal(0, env.Disabled);
        Assert.Contains("純正 HotSync Manager の自動起動は見つかりませんでした", output.ToString());
        Assert.Equal(0, StartupCommand.Run(false, [], env, output)); Assert.Equal(0, env.Restored);
    }
    [Fact] public void InstallTwicePreservesOriginalAndUninstallRestores()
    {
        var env = new Fake(); using var log = new StringWriter();
        Assert.Equal(0, StartupCommand.Run(true, [], env, log)); var original = env.State;
        Assert.Equal("\"C:\\Example Sync\\PiloSync.exe\" tray", env.Command);
        Assert.Equal(0, StartupCommand.Run(true, [], env, log)); Assert.Same(original, env.State);
        Assert.Contains("自動では停止できません", log.ToString());
        Assert.Equal(0, StartupCommand.Run(false, [], env, log)); Assert.Null(env.State); Assert.Null(env.Command); Assert.Equal(2, env.Restored);
    }
    [Fact] public void MenuInstallDisablesStockSavesStateAndReturnsOnlyEnabledProtectedItems()
    {
        var env = new Fake(); env.Protected.Add(new("disabled", true));
        var remaining = StartupCommand.Install(env, TextWriter.Null);
        Assert.Equal(2, env.Disabled); Assert.NotNull(env.State); Assert.Equal(4, env.Writes);
        Assert.Equal("\"C:\\Example Sync\\PiloSync.exe\" tray", env.Command);
        Assert.Equal(new ProtectedStartup("HKLM Run HotSync", false), Assert.Single(remaining));
    }
    [Fact] public void MenuDisableOnlyRemovesRegistration()
    {
        var env = new Fake { State = [new("Run", "HotSync", "HotSync.exe")], Command = "old" }; var state = env.State;
        StartupCommand.DisableRegistration(env);
        Assert.Null(env.Command); Assert.Same(state, env.State); Assert.Equal(1, env.Writes); Assert.Equal(0, env.Restored); Assert.Equal(0, env.Disabled);
    }
    [Fact] public void UninstallDoesNotRestoreExistingPiloSyncRegistration()
    {
        var env = new Fake();
        env.Discovered.Add(new("PalmSync", "PalmSync", "old.exe tray"));
        using var log = new StringWriter();
        Assert.Equal(0, StartupCommand.Run(true, [], env, log));
        Assert.Equal(2, env.Disabled);
        Assert.Equal(3, env.State!.Count);
        Assert.Equal(0, StartupCommand.Run(false, [], env, log));
        Assert.Equal(2, env.Restored);
        Assert.Null(env.Command);
        Assert.Null(env.State);
        Assert.DoesNotContain("元の状態を復元：PalmSync", log.ToString());
    }
    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    public void InstallReportsWhenNoStockStartupExists(bool piloSync, bool stock, bool protectedItem, bool expected)
    {
        var env = new Fake { Discovered = [], Protected = [] };
        if (piloSync) env.Discovered.Add(new("PalmSync", "PalmSync", "old.exe tray"));
        if (stock) env.Discovered.Add(new("Run", "HotSync", "HotSync.exe"));
        if (protectedItem) env.Protected.Add(new("HKLM Run HotSync", false));
        using var log = new StringWriter();
        Assert.Equal(0, StartupCommand.Run(true, [], env, log));
        const string message = "純正 HotSync Manager の自動起動は見つかりませんでした（ユーザーの Run・スタートアップフォルダ、共通スタートアップ、HKLM Run を確認）";
        Assert.Equal(expected ? 1 : 0, log.ToString().Split(Environment.NewLine).Count(line => line == message));
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public void ProtectedStartupReportsTaskManagerStateWithoutWriting(bool disabled)
    {
        var env = new Fake { Discovered = [], Protected = [new("HotSync Manager.lnk", disabled), new("HKLM (Registry32) Run\\HotSync", disabled)] };
        using var log = new StringWriter(); Assert.Equal(0, StartupCommand.Run(true, ["--dry-run"], env, log)); Assert.Equal(0, env.Writes);
        foreach (var item in env.Protected)
        {
            Assert.Contains(disabled ? $"純正の自動起動はタスクマネージャーで無効になっています：{item.Path}" : $"自動では停止できません：{item.Path}。タスクマネージャーの「スタートアップ アプリ」で無効にしてください。", log.ToString());
        }
        Assert.DoesNotContain(disabled ? "自動では停止できません" : "純正の自動起動はタスクマネージャーで無効になっています", log.ToString());
        Assert.DoesNotContain("純正 HotSync Manager の自動起動は見つかりませんでした", log.ToString());
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public void DryRunChangesNothing(bool install)
    {
        var env = new Fake { State = install ? null : [new("Run", "HotSync", "HotSync.exe")] };
        using var log = new StringWriter(); Assert.Equal(0, StartupCommand.Run(install, ["--dry-run"], env, log)); Assert.Equal(0, env.Writes);
    }
    [Fact] public void MissingRecordHasNothingToRestore()
    {
        var env = new Fake(); using var log = new StringWriter(); Assert.Equal(0, StartupCommand.Run(false, [], env, log)); Assert.Equal(1, env.Writes); Assert.Contains("戻すものはありません", log.ToString());
    }
    [Theory] [InlineData("\"C:\\Example\\HotSync.exe\" -x", true)] [InlineData("C:\\Example\\HotSync.exe", true)] [InlineData("C:\\Example\\Other.exe HotSync.exe", false)] [InlineData("HotSync.exe.bak", false)]
    public void DetectsExecutableRatherThanArgument(string command, bool expected) => Assert.Equal(expected, StartupEnvironment.IsHotSync(command));
    [Theory] [InlineData(0u, 123u, 123u)] [InlineData(0x22acu, 123u, 0x22acu)]
    public void StockPcIdHasPriority(uint stock, uint own, uint expected)
    {
        var registry = new FakePalmRegistry { StockPcId = stock, PcId = own }; Assert.Equal(expected, PalmRegistryOperations.GetOrCreatePcId(registry)); Assert.Equal(own, registry.PcId);
    }
    [Fact] public async Task ListenerRestartsAfterExceptionAndStopsOnCancellation()
    {
        using var stop = new CancellationTokenSource(); int attempts = 0, logged = 0;
        await ListenerSupervisor.Run(_ => { if (++attempts == 1) throw new IOException("test"); stop.Cancel(); return Task.CompletedTask; }, _ => logged++, stop.Token, 1);
        Assert.Equal(2, attempts); Assert.Equal(1, logged);
    }
}
