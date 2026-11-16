using StudyLife.App.Core;

namespace StudyLife.App.Core.Tests;

/// <summary>Which platforms hide the add-on registration and the marketplace in Setup.</summary>
public class AppStorePolicyTests
{
    [Theory]
    [InlineData("ios")]
    [InlineData("maccatalyst")]
    public void TheAppleStoreBuilds_HideTheDeveloperIntegrations(string platform) =>
        Assert.False(AppStorePolicy.ShowsDeveloperIntegrations(platform));

    [Theory]
    [InlineData("android")]
    [InlineData("windows")]
    [InlineData("web")]
    public void TheOtherPlatforms_KeepThem(string platform) =>
        Assert.True(AppStorePolicy.ShowsDeveloperIntegrations(platform));
}
