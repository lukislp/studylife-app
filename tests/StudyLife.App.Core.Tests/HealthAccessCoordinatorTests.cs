using StudyLife.App.Core;

namespace StudyLife.App.Core.Tests;

public class HealthAccessCoordinatorTests
{
    private sealed class FakeFlag(bool set = false) : IHealthConnectFlag
    {
        public bool IsSet { get; private set; } = set;
        public void Set() => IsSet = true;
        public void Clear() => IsSet = false;
    }

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
        var flag = new FakeFlag();
        var platform = new FakePlatform(true, HealthPermission.NotDetermined, HealthPermission.Authorized);

        var access = await new HealthAccessCoordinator(platform, flag).GetAccessAsync();

        Assert.Equal(HealthAccess.NotAsked, access);
        Assert.Equal(0, platform.Prompts);
    }

    [Theory]
    [InlineData(HealthPermission.NotDetermined, HealthAccess.NotAsked)]
    [InlineData(HealthPermission.Denied, HealthAccess.Declined)]
    [InlineData(HealthPermission.Authorized, HealthAccess.Granted)]
    public async Task TheStateFollowsTheSystem(HealthPermission permission, HealthAccess expected)
    {
        var flag = new FakeFlag(set: true);
        var platform = new FakePlatform(true, permission, permission);

        Assert.Equal(expected, await new HealthAccessCoordinator(platform, flag).GetAccessAsync());
    }

    [Fact]
    public async Task WithoutHealth_NothingIsAvailableAndNothingIsAsked()
    {
        var flag = new FakeFlag();
        var platform = new FakePlatform(false, HealthPermission.NotDetermined, HealthPermission.Authorized);
        var coordinator = new HealthAccessCoordinator(platform, flag);

        Assert.Equal(HealthAccess.Unavailable, await coordinator.GetAccessAsync());
        Assert.Equal(HealthAccess.Unavailable, await coordinator.RequestAccessAsync());
        Assert.Equal(0, platform.Prompts);
    }

    [Fact]
    public async Task TheFirstRequest_ShowsThePromptOnceAndReportsTheAnswer()
    {
        var flag = new FakeFlag();
        var platform = new FakePlatform(true, HealthPermission.NotDetermined, HealthPermission.Authorized);

        var access = await new HealthAccessCoordinator(platform, flag).RequestAccessAsync();

        Assert.Equal(HealthAccess.Granted, access);
        Assert.Equal(1, platform.Prompts);
    }

    [Fact]
    public async Task ADeclinedAnswer_IsReportedAsDeclined()
    {
        var flag = new FakeFlag();
        var platform = new FakePlatform(true, HealthPermission.NotDetermined, HealthPermission.Denied);

        Assert.Equal(HealthAccess.Declined, await new HealthAccessCoordinator(platform, flag).RequestAccessAsync());
    }

    [Fact]
    public async Task Declined_IsNeverAskedAgain()
    {
        var flag = new FakeFlag(set: true);
        var platform = new FakePlatform(true, HealthPermission.Denied, HealthPermission.Authorized);
        var coordinator = new HealthAccessCoordinator(platform, flag);

        Assert.Equal(HealthAccess.Declined, await coordinator.RequestAccessAsync());
        Assert.Equal(HealthAccess.Declined, await coordinator.RequestAccessAsync());
        Assert.Equal(0, platform.Prompts);
    }

    [Fact]
    public async Task ExistingGrant_SeesNoPromptAndNoChange()
    {
        var flag = new FakeFlag();
        var platform = new FakePlatform(true, HealthPermission.Authorized, HealthPermission.Authorized);
        var coordinator = new HealthAccessCoordinator(platform, flag);

        // Allowed in the system earlier, but never connected here: not asked until the user taps.
        Assert.Equal(HealthAccess.NotAsked, await coordinator.GetAccessAsync());
        Assert.Equal(HealthAccess.Granted, await coordinator.RequestAccessAsync());
        Assert.True(flag.IsSet);
        Assert.Equal(0, platform.Prompts);
    }

    [Fact]
    public async Task OnlyTheConnectTap_RecordsTheOptIn()
    {
        var flag = new FakeFlag();
        var platform = new FakePlatform(true, HealthPermission.NotDetermined, HealthPermission.Authorized);
        var coordinator = new HealthAccessCoordinator(platform, flag);

        await coordinator.GetAccessAsync();
        Assert.False(flag.IsSet);

        await coordinator.RequestAccessAsync();
        Assert.True(flag.IsSet);
    }

    [Fact]
    public async Task WithoutHealth_TheTapRecordsNothing()
    {
        var flag = new FakeFlag();
        var platform = new FakePlatform(false, HealthPermission.NotDetermined, HealthPermission.Authorized);

        await new HealthAccessCoordinator(platform, flag).RequestAccessAsync();

        Assert.False(flag.IsSet);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void TheGate_NeedsHealthAndTheOptIn(bool available, bool optedIn, bool expected)
    {
        var gate = new HealthGate(new FakeFlag(optedIn), () => available);

        Assert.Equal(expected, gate.MayAccessHealth);
    }

    [Fact]
    public void ClearingTheOptIn_ClosesTheGate()
    {
        var flag = new FakeFlag(set: true);
        var gate = new HealthGate(flag, () => true);

        flag.Clear();

        Assert.False(gate.MayAccessHealth);
    }
}
