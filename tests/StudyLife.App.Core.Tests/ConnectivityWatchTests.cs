using StudyLife.App.Core;

namespace StudyLife.App.Core.Tests;

/// <summary>Which reachability reports make the signed-out server check ask again, and which are dropped.</summary>
public class ConnectivityWatchTests
{
    [Theory]
    [InlineData(NetworkReach.Internet, ConnectivityChange.Restored)]
    [InlineData(NetworkReach.Local, ConnectivityChange.Restored)]
    [InlineData(NetworkReach.None, ConnectivityChange.Lost)]
    [InlineData(NetworkReach.Constrained, ConnectivityChange.Lost)]
    [InlineData(NetworkReach.Unknown, ConnectivityChange.Ignore)]
    public void Classify_MapsEveryReach(NetworkReach reach, ConnectivityChange expected) =>
        Assert.Equal(expected, ConnectivityWatch.Classify(reach));

    [Fact]
    public void EveryReachHasAClassification() =>
        Assert.All(Enum.GetValues<NetworkReach>(), r => Assert.True(Enum.IsDefined(ConnectivityWatch.Classify(r))));

    [Fact]
    public void ALoss_AsksOnce_NotForEveryRepeatedReport()
    {
        var watch = new ConnectivityWatch();
        Assert.True(watch.ShouldAsk(NetworkReach.None));
        Assert.False(watch.ShouldAsk(NetworkReach.None));
        Assert.True(watch.ShouldAsk(NetworkReach.Constrained));
        Assert.False(watch.ShouldAsk(NetworkReach.Constrained));
    }

    [Fact]
    public void AConstrainedLinkGoingAway_ReachesTheBar()
    {
        // iOS often reports a constrained link first while Wi-Fi goes down, then none: the second report must
        // still ask, so the bar switches to "no network" (before, it was dropped as a repeated loss).
        var watch = new ConnectivityWatch();
        Assert.True(watch.ShouldAsk(NetworkReach.Constrained));
        Assert.True(watch.ShouldAsk(NetworkReach.None));
    }

    [Fact]
    public void ARestoration_AlwaysAsks_AndRearmsTheLoss()
    {
        var watch = new ConnectivityWatch();
        Assert.True(watch.ShouldAsk(NetworkReach.Internet));
        Assert.True(watch.ShouldAsk(NetworkReach.Local)); // Wi-Fi to mobile data: the answer may differ
        Assert.True(watch.ShouldAsk(NetworkReach.None));
        Assert.True(watch.ShouldAsk(NetworkReach.Internet));
        Assert.True(watch.ShouldAsk(NetworkReach.None));
    }

    [Fact]
    public void Unknown_NeverAsks_AndKeepsTheState()
    {
        var watch = new ConnectivityWatch();
        Assert.True(watch.ShouldAsk(NetworkReach.None));
        Assert.False(watch.ShouldAsk(NetworkReach.Unknown));
        Assert.False(watch.ShouldAsk(NetworkReach.None));
        Assert.True(watch.ShouldAsk(NetworkReach.Constrained));
    }
}
