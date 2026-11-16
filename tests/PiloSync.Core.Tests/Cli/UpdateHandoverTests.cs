using PiloSync.Cli.Update;
using Xunit;

namespace PiloSync.Core.Tests.Cli;

public class UpdateHandoverTests
{
    private sealed class Platform : IUpdateHandoverPlatform
    {
        public bool Ready, ThrowRestore, Restored, Stopped;
        public int Starts;
        public string? Updated, Failed;
        public void ResetReady() { }
        public void SetUpdatedFrom(string tag) => Updated = tag;
        public void ClearUpdatedFrom() => Updated = null;
        public void SetFailedTag(string tag) => Failed = tag;
        public object StartTray(string folder) { Starts++; return new object(); }
        public bool WaitReady(object process, TimeSpan timeout) => Ready;
        public void Stop(object process) => Stopped = true;
        public void Restore(string folder) { Restored = true; if (ThrowRestore) throw new IOException(); }
    }
    [Fact]
    public void ReadyKeepsUpdatedMarker()
    {
        var p = new Platform { Ready = true };
        Assert.True(new UpdateHandover(p).Run("folder", new("new", "", "staged"), "old", _ => { }));
        Assert.Equal("old", p.Updated);
        Assert.Equal(1, p.Starts);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailureRestartsPreviousEvenWhenRestoreFails(bool throwRestore)
    {
        var p = new Platform { ThrowRestore = throwRestore };
        Assert.False(new UpdateHandover(p).Run("folder", new("new", "", "staged"), "old", _ => { }));
        Assert.True(p.Restored); Assert.True(p.Stopped); Assert.Equal(2, p.Starts);
        Assert.Equal("new", p.Failed); Assert.Null(p.Updated);
    }
}
