using PiloSync.Cli;
using Xunit;

namespace PiloSync.Core.Tests;

public sealed class NextSyncRequestTests
{
    [Fact]
    public void ReserveAndClaimConsumesOnce()
    {
        var next = new NextSyncRequest<RestoreRequest>(); Assert.Null(next.Current); Assert.False(next.TryClaim(out _));
        var request = new RestoreRequest("Prism"); next.Reserve(request); Assert.Same(request, next.Current);
        Assert.True(next.TryClaim(out var claimed)); Assert.Same(request, claimed); Assert.Null(next.Current); Assert.False(next.TryClaim(out _));
    }
    [Fact]
    public void CancelPreventsClaim()
    {
        var next = new NextSyncRequest<RestoreRequest>(); next.Reserve(new("Vx")); next.Cancel(); next.Cancel();
        Assert.Null(next.Current); Assert.False(next.TryClaim(out var claimed)); Assert.Null(claimed);
    }
    [Fact]
    public void ReservationReplacesPreviousRequest()
    {
        var next = new NextSyncRequest<RestoreRequest>(); next.Reserve(new("Prism")); next.Reserve(new("Prism2"));
        Assert.Equal("Prism2", next.Current?.AccountName); Assert.True(next.TryClaim(out var claimed)); Assert.Equal("Prism2", claimed?.AccountName);
    }
    [Fact]
    public void ParallelSessionsClaimOnlyOnce()
    {
        var next = new NextSyncRequest<RestoreRequest>(); var request = new RestoreRequest("Vx"); next.Reserve(request); int count = 0;
        Parallel.For(0, 100, _ => { if (next.TryClaim(out var claimed)) { Assert.Same(request, claimed); Interlocked.Increment(ref count); } });
        Assert.Equal(1, count); Assert.Null(next.Current);
    }
}
