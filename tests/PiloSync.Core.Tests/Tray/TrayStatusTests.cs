using PiloSync.Cli;
using PiloSync.Protocol;
using Xunit;

namespace PiloSync.Core.Tests;

public sealed class TrayStatusTests
{
    [Theory]
    [InlineData(false, "待ち受け中")]
    [InlineData(true, "一時停止中")]
    public void ReservationShowsWhileIdleOrPaused(bool paused, string state)
    {
        var status = new TrayStatus();
        Assert.Equal($"PiloSync：{state}（次の HotSync でPrismを復元）", status.Snapshot(paused, "Prism").Text);
        Assert.Equal($"PiloSync：{state}", status.Snapshot(paused).Text);
    }
    [Fact]
    public void ReservationPreservesLiveProgressAndTextLimit()
    {
        var status = new TrayStatus(); using var session = status.Begin("Vx"); session.Report(new(SyncPhase.Backup, 1, 2, "Data"));
        Assert.Equal("PiloSync：Vx バックアップ中 1/2 Data（次の HotSync でPrismを復元）", status.Snapshot(restoreAccount: "Prism").Text);
        Assert.Equal(TrayStatus.TextLimit, status.Snapshot(restoreAccount: new string('a', 200)).Text.Length);
    }
    [Fact]
    public void RestoreShowsDatabaseOnlyForSingleDevice()
    {
        var status = new TrayStatus(); using var first = status.Begin("Prism"); first.Report(new(SyncPhase.Restore, 12, 80, "Saved Preferences"));
        Assert.Equal("PiloSync：Prism 復元中 12/80 Saved Preferences", status.Snapshot().Text);
        using var second = status.Begin("Vx"); second.Report(new(SyncPhase.Restore, 1, 20, "Data"));
        Assert.Equal("PiloSync：2 台 HotSync 中 / Prism 復元 12/80 / Vx 復元 1/20", status.Snapshot().Text);
    }
    [Fact]
    public void IdleAndPaused()
    {
        var status = new TrayStatus();
        Assert.Equal("PiloSync：待ち受け中", status.Snapshot().Text);
        using var session = status.Begin("Vx");
        Assert.Equal("PiloSync：一時停止中", status.Snapshot(true).Text);
        session.Dispose(); Assert.Equal("PiloSync：一時停止中", status.Snapshot(true).Text);
    }
    [Theory]
    [InlineData(SyncPhase.Backup, 12, 36, "Pokemon Red", "バックアップ中 12/36 Pokemon Red")]
    [InlineData(SyncPhase.Install, 2, 5, "PalmNewsData.pdb", "インストール中 2/5 PalmNewsData.pdb")]
    [InlineData(SyncPhase.Backup, 12, 0, "Pokemon Red", "バックアップ中 12 件目 Pokemon Red")]
    [InlineData(SyncPhase.Install, 2, 0, "PalmNewsData.pdb", "インストール中 2 件目 PalmNewsData.pdb")]
    public void SingleProgress(SyncPhase phase, int index, int total, string name, string expected)
    {
        var status = new TrayStatus(); using var session = status.Begin("Vx");
        Assert.Equal("PiloSync：Vx HotSync 中", status.Snapshot().Text);
        session.Report(new(phase, index, total, name)); Assert.Equal("PiloSync：Vx " + expected, status.Snapshot().Text);
    }
    [Fact]
    public void MultipleRenameEndAndRegistrationOrder()
    {
        var status = new TrayStatus(); using var first = status.Begin("COM3"); using var second = status.Begin("Z22");
        first.Rename("Vx"); second.Report(new(SyncPhase.Backup, 40, 165, "ignored")); first.Report(new(SyncPhase.Backup, 3, 63, "ignored"));
        Assert.Equal("PiloSync：2 台 HotSync 中 / Vx 保存 3/63 / Z22 保存 40/165", status.Snapshot().Text);
        using var third = status.Begin("IIIc");
        Assert.Equal("PiloSync：3 台 HotSync 中 / Vx 保存 3/63 / Z22 保存 40/165 / IIIc 開始", status.Snapshot().Text);
        first.Report(new(SyncPhase.Install, 3, 0, "ignored"));
        Assert.Contains("Vx インストール 3 件目", status.Snapshot().Text);
        first.Report(new(SyncPhase.Backup, 3, 0, "ignored")); Assert.Contains("Vx 保存 3 件目", status.Snapshot().Text);
        second.Dispose(); third.Dispose(); Assert.Equal("PiloSync：Vx バックアップ中 3 件目 ignored", status.Snapshot().Text);
        first.Dispose(); Assert.Equal("PiloSync：待ち受け中", status.Snapshot().Text);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LongTextIsTruncated(bool many)
    {
        var status = new TrayStatus(); status.Begin(new string('a', 200));
        if (many) for (int i = 0; i < 20; i++) status.Begin("Z22");
        string text = status.Snapshot().Text; Assert.True(text.Length <= TrayStatus.TextLimit); Assert.EndsWith("…", text);
    }
    [Fact]
    public void ExactLimitAndSurrogateBoundary()
    {
        var status = new TrayStatus(); using var session = status.Begin(new string('a', 127 - "PiloSync： HotSync 中".Length));
        Assert.Equal(127, status.Snapshot().Text.Length); Assert.DoesNotContain("…", status.Snapshot().Text);
        session.Rename(new string('a', 125 - "PiloSync：".Length) + "😀" + new string('b', 20));
        string text = status.Snapshot().Text; Assert.Equal(126, text.Length); Assert.EndsWith("a…", text);
    }
    [Fact]
    public void ConcurrentSessionsReturnToZero()
    {
        var status = new TrayStatus();
        Parallel.For(0, 1000, i => { using var session = status.Begin("COM" + i); session.Rename("Vx"); session.Report(new(SyncPhase.Backup, 1, 0, "Data")); _ = status.Snapshot(); });
        Assert.Equal(0, status.Snapshot().Count); Assert.Equal("PiloSync：待ち受け中", status.Snapshot().Text);
    }
}
