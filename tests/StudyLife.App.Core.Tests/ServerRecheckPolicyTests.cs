using StudyLife.App.Core;

namespace StudyLife.App.Core.Tests;

/// <summary>Which answers of the sign-in bar's server check are retried on a backoff and which only on
/// connectivity changes and resume.</summary>
public class ServerRecheckPolicyTests
{
    [Fact]
    public void Unreachable_IsPolled_BecauseTheNetworkCanComeBack() =>
        Assert.Equal(ServerRecheck.Poll, ServerRecheckPolicy.For(ServerCheckResult.Unreachable));

    [Theory]
    [InlineData(ServerCheckResult.UntrustedCertificate)]
    [InlineData(ServerCheckResult.NotStudyLife)]
    public void AnswersThatWillNotFixThemselves_AreOnlyRecheckedOnEvents(ServerCheckResult result) =>
        Assert.Equal(ServerRecheck.OnEventsOnly, ServerRecheckPolicy.For(result));

    [Fact]
    public void AStudyLifeServer_IsSettled() =>
        Assert.Equal(ServerRecheck.Settled, ServerRecheckPolicy.For(ServerCheckResult.StudyLife));

    [Fact]
    public void EveryResultHasADecision() =>
        Assert.All(Enum.GetValues<ServerCheckResult>(), r => Assert.True(Enum.IsDefined(ServerRecheckPolicy.For(r))));
}
