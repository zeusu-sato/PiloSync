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
        Assert.Single(messages); Assert.False(Directory.Exists(root));
    }

    private sealed class Fetcher : IUpdateFetcher
    {
        public int Calls;
        public string? Baseline;
        public StagedUpdate? Result;
        public Task<StagedUpdate?> FetchAsync(string currentTag, Action<string> log, CancellationToken token) { Calls++; Baseline = currentTag; return Task.FromResult(Result); }
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
