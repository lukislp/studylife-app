using StudyLife.App.Core;

namespace StudyLife.App.Core.Tests;

public class HealthAccessCoordinatorTests
{
    private sealed class FakePlatform(bool available, HealthPermission initial, HealthPermission afterPrompt) : IHealthPlatform
    {
        private HealthPermission _permission = initial;

        public int Prompts { get; private set; }
        public bool IsAvailable => available;
        public Task<HealthPermission> GetPermissionAsync() => Task.FromResult(_permission);

        public Task ShowPromptAsync()
        {
            Prompts++;
            _permission = afterPrompt;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ReadingTheState_NeverShowsThePrompt()
    {
        var platform = new FakePlatform(true, HealthPermission.NotDetermined, HealthPermission.Authorized);

        var access = await new HealthAccessCoordinator(platform).GetAccessAsync();

        Assert.Equal(HealthAccess.NotAsked, access);
        Assert.Equal(0, platform.Prompts);
    }

    [Theory]
    [InlineData(HealthPermission.NotDetermined, HealthAccess.NotAsked)]
    [InlineData(HealthPermission.Denied, HealthAccess.Declined)]
    [InlineData(HealthPermission.Authorized, HealthAccess.Granted)]
    public async Task TheStateFollowsTheSystem(HealthPermission permission, HealthAccess expected)
    {
        var platform = new FakePlatform(true, permission, permission);

        Assert.Equal(expected, await new HealthAccessCoordinator(platform).GetAccessAsync());
    }

    [Fact]
    public async Task WithoutHealth_NothingIsAvailableAndNothingIsAsked()
    {
        var platform = new FakePlatform(false, HealthPermission.NotDetermined, HealthPermission.Authorized);
        var coordinator = new HealthAccessCoordinator(platform);

        Assert.Equal(HealthAccess.Unavailable, await coordinator.GetAccessAsync());
        Assert.Equal(HealthAccess.Unavailable, await coordinator.RequestAccessAsync());
        Assert.Equal(0, platform.Prompts);
    }

    [Fact]
    public async Task TheFirstRequest_ShowsThePromptOnceAndReportsTheAnswer()
    {
        var platform = new FakePlatform(true, HealthPermission.NotDetermined, HealthPermission.Authorized);

        var access = await new HealthAccessCoordinator(platform).RequestAccessAsync();

        Assert.Equal(HealthAccess.Granted, access);
        Assert.Equal(1, platform.Prompts);
    }

    [Fact]
    public async Task ADeclinedAnswer_IsReportedAsDeclined()
    {
        var platform = new FakePlatform(true, HealthPermission.NotDetermined, HealthPermission.Denied);

        Assert.Equal(HealthAccess.Declined, await new HealthAccessCoordinator(platform).RequestAccessAsync());
    }

    [Fact]
    public async Task Declined_IsNeverAskedAgain()
    {
        var platform = new FakePlatform(true, HealthPermission.Denied, HealthPermission.Authorized);
        var coordinator = new HealthAccessCoordinator(platform);

        Assert.Equal(HealthAccess.Declined, await coordinator.RequestAccessAsync());
        Assert.Equal(HealthAccess.Declined, await coordinator.RequestAccessAsync());
        Assert.Equal(0, platform.Prompts);
    }

    [Fact]
    public async Task ExistingGrant_SeesNoPromptAndNoChange()
    {
        var platform = new FakePlatform(true, HealthPermission.Authorized, HealthPermission.Authorized);
        var coordinator = new HealthAccessCoordinator(platform);

        Assert.Equal(HealthAccess.Granted, await coordinator.GetAccessAsync());
        Assert.Equal(HealthAccess.Granted, await coordinator.RequestAccessAsync());
        Assert.Equal(0, platform.Prompts);
    }
}
