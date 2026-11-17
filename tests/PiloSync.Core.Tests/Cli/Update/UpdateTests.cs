using PiloSync.Cli.Update;
using Xunit;

namespace PiloSync.Core.Tests.Cli;

public class UpdateTests
{
    private static readonly string[] Names = ["PiloSync.exe", "pilosync-cli.exe", "LICENSE"];

    [Fact]
    public void InstallMovesPreviousAndCopiesStaged()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        string staged = Path.Combine(root, "staged");
        Directory.CreateDirectory(staged);
        try
        {
            foreach (string name in Names) { File.WriteAllText(Path.Combine(root, name), "old"); File.WriteAllText(Path.Combine(staged, name), "new"); }
            UpdateInstaller.Install(root, staged);
            foreach (string name in Names)
            {
                Assert.Equal("new", File.ReadAllText(Path.Combine(root, name)));
                Assert.Equal("old", File.ReadAllText(Path.Combine(root, "previous", name)));
            }
            UpdateInstaller.Restore(root);
            foreach (string name in Names) Assert.Equal("old", File.ReadAllText(Path.Combine(root, name)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void InstallRestoresFilesWhenCopyFails()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        string staged = Path.Combine(root, "staged");
        Directory.CreateDirectory(staged);
        try
        {
            foreach (string name in Names) File.WriteAllText(Path.Combine(root, name), "old");
            File.WriteAllText(Path.Combine(staged, Names[0]), "new");
            Assert.Throws<FileNotFoundException>(() => UpdateInstaller.Install(root, staged));
            foreach (string name in Names) Assert.Equal("old", File.ReadAllText(Path.Combine(root, name)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RestorePreservesUnrelatedFilesAndRemovesNewFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        string staged = Path.Combine(root, "staged");
        Directory.CreateDirectory(staged);
        try
        {
            File.WriteAllText(Path.Combine(root, Names[0]), "old");
            foreach (string name in Names) File.WriteAllText(Path.Combine(staged, name), "new");
            UpdateInstaller.Install(root, staged);
            File.WriteAllText(Path.Combine(root, "other"), "keep");
            UpdateInstaller.Restore(root);
            Assert.Equal("old", File.ReadAllText(Path.Combine(root, Names[0])));
            Assert.False(File.Exists(Path.Combine(root, Names[1])));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(root, "other")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InstallExceptionIsLoggedAndStagingIsRemoved()
    {
        string root = Path.Combine(Path.GetTempPath(), "PiloSync-update-" + Guid.NewGuid());
        string staged = Path.Combine(root, "staged");
        Directory.CreateDirectory(staged);
        bool enabled = true;
        var messages = new List<string>();
        var scheduler = new UpdateScheduler(new Fetcher { Result = new("v2026.1117a", "", staged) }, (time, _) => { if (time == TimeSpan.FromMinutes(1) && Directory.Exists(root) && messages.Count > 0) enabled = false; return Task.CompletedTask; });
        await scheduler.RunAsync("v2026.1116a", () => enabled, () => null, (_, _) => throw new IOException("test"), messages.Add, CancellationToken.None);
        Assert.Single(messages); Assert.Contains("更新の確認: 失敗", messages[0]); Assert.DoesNotContain("??", messages[0]); Assert.False(Directory.Exists(root));
    }

    private sealed class Fetcher : IUpdateFetcher
    {
        public int Calls;
        public string? Baseline;
        public StagedUpdate? Result;
        public Task<StagedUpdate?> FetchAsync(string currentTag, Action<string> log, CancellationToken token) { Calls++; Baseline = currentTag; return Task.FromResult(Result); }
    }

    private sealed class BlockingFetcher : IUpdateFetcher
    {
        public TaskCompletionSource<StagedUpdate?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<StagedUpdate?> FetchAsync(string currentTag, Action<string> log, CancellationToken token) => Completion.Task;
    }

    [Fact]
    public async Task ManualLatestDoesNotInstallAndUsesFailedBaseline()
    {
        var fetcher = new Fetcher();
        int installs = 0;
        var result = await new UpdateScheduler(fetcher).CheckNowAsync("v2026.1116a", () => "v2026.1117a", (_, _) => { installs++; return Task.FromResult(true); }, _ => { }, CancellationToken.None);
        Assert.Equal(ManualCheckResult.Latest, result);
        Assert.Equal("v2026.1117a", fetcher.Baseline);
        Assert.Equal(0, installs);
    }

    [Fact]
    public async Task ManualRetriesUntilInstalled()
    {
        var fetcher = new Fetcher { Result = new("v2026.1117a", "", "staged") };
        var delays = new List<TimeSpan>();
        int installs = 0;
        var scheduler = new UpdateScheduler(fetcher, (time, _) => { delays.Add(time); return Task.CompletedTask; });
        var result = await scheduler.CheckNowAsync("v2026.1116a", () => null, (_, _) => Task.FromResult(++installs == 3), _ => { }, CancellationToken.None);
        Assert.Equal(ManualCheckResult.Installed, result);
        Assert.Equal(3, installs);
        Assert.Equal(new[] { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1) }, delays);
    }

    [Fact]
    public async Task ManualFetchFailureIsLogged()
    {
        var fetcher = new BlockingFetcher();
        fetcher.Completion.SetException(new IOException("test"));
        var messages = new List<string>();
        var result = await new UpdateScheduler(fetcher).CheckNowAsync("v2026.1116a", () => null, (_, _) => Task.FromResult(true), messages.Add, CancellationToken.None);
        Assert.Equal(ManualCheckResult.Failed, result);
        Assert.Contains("更新の確認: 失敗", Assert.Single(messages));
        Assert.DoesNotContain("??", messages[0]);
    }

    [Fact]
    public async Task ManualSecondCheckIsBusy()
    {
        var fetcher = new BlockingFetcher();
        var scheduler = new UpdateScheduler(fetcher);
        var first = scheduler.CheckNowAsync("v2026.1116a", () => null, (_, _) => Task.FromResult(true), _ => { }, CancellationToken.None);
        Assert.Equal(ManualCheckResult.Busy, await scheduler.CheckNowAsync("v2026.1116a", () => null, (_, _) => Task.FromResult(true), _ => { }, CancellationToken.None));
        fetcher.Completion.SetResult(null);
        Assert.Equal(ManualCheckResult.Latest, await first);
    }

    [Fact]
    public async Task ManualCancellationCleansStaging()
    {
        string root = Path.Combine(Path.GetTempPath(), "PiloSync-update-" + Guid.NewGuid());
        string staged = Path.Combine(root, "staged");
        Directory.CreateDirectory(staged);
        using var stop = new CancellationTokenSource();
        var scheduler = new UpdateScheduler(new Fetcher { Result = new("v2026.1117a", "", staged) }, (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
        var result = await scheduler.CheckNowAsync("v2026.1116a", () => null, (_, _) => { stop.Cancel(); return Task.FromResult(false); }, _ => { }, stop.Token);
        Assert.Equal(ManualCheckResult.Cancelled, result);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task ManualInvalidTagFails()
    {
        Assert.Equal(ManualCheckResult.Failed, await new UpdateScheduler(new Fetcher()).CheckNowAsync(null, () => null, (_, _) => Task.FromResult(true), _ => { }, CancellationToken.None));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("v2026.1116a", false)]
    public async Task DisabledOrDevelopmentDoesNotFetch(string? tag, bool enabled)
    {
        var fetcher = new Fetcher();
        await new UpdateScheduler(fetcher).RunAsync(tag, () => enabled, () => null, (_, _) => Task.FromResult(true), _ => { }, CancellationToken.None);
        Assert.Equal(0, fetcher.Calls);
    }

    [Fact]
    public async Task ChecksAfterOneMinuteAndAgainAfterSixHoursAndSkipsFailedTag()
    {
        var fetcher = new Fetcher { Result = new("v2026.1117a", "test", "staged") };
        var delays = new List<TimeSpan>();
        bool enabled = true;
        var scheduler = new UpdateScheduler(fetcher, (time, _) => { delays.Add(time); if (delays.Count == 3) enabled = false; return Task.CompletedTask; });
        int installs = 0;
        await scheduler.RunAsync("v2026.1116a", () => enabled, () => "v2026.1117a", (_, _) => { installs++; return Task.FromResult(true); }, _ => { }, CancellationToken.None);
        Assert.Equal(new[] { TimeSpan.FromMinutes(1), TimeSpan.FromHours(6), TimeSpan.FromHours(6) }, delays);
        Assert.Equal(2, fetcher.Calls);
        Assert.Equal("v2026.1117a", fetcher.Baseline);
        Assert.Equal(0, installs);
    }
}
