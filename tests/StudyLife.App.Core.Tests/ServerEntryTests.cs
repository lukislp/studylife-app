using System.Net;
using System.Text;
using StudyLife.App.Core;

namespace StudyLife.App.Core.Tests;

public class ServerAddressTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_IsEmpty(string? input) =>
        Assert.Equal(ServerAddressProblem.Empty, ServerAddress.Parse(input).Problem);

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://studylife.example.com")]
    [InlineData("https://")]
    [InlineData("studylife example.com")]
    public void Garbage_IsNotAUrl(string input) =>
        Assert.Equal(ServerAddressProblem.NotAUrl, ServerAddress.Parse(input).Problem);

    [Theory]
    [InlineData("studylife.example.com", "https://studylife.example.com/")]
    [InlineData("  https://studylife.example.com  ", "https://studylife.example.com/")]
    [InlineData("http://192.168.1.20:8080", "http://192.168.1.20:8080/")]
    [InlineData("https://example.com/studylife", "https://example.com/studylife/")]
    [InlineData("https://example.com/app/?x=1#top", "https://example.com/app/")]
    public void Addresses_AreNormalisedToABaseWithTrailingSlash(string input, string expected)
    {
        var parsed = ServerAddress.Parse(input);

        Assert.Equal(ServerAddressProblem.None, parsed.Problem);
        Assert.Equal(expected, parsed.Uri!.ToString());
    }

    [Theory]
    [InlineData("https://studylife.example.com/", "studylife.example.com")]
    [InlineData("http://192.168.1.20:8080/", "192.168.1.20:8080")]
    public void TheHostIsShownWithANonDefaultPort(string uri, string expected) =>
        Assert.Equal(expected, ServerAddress.DisplayHost(new Uri(uri)));
}

public class ServerEntryTests
{
    private sealed class FakeChecker(ServerCheckResult result) : IServerChecker
    {
        public int Calls { get; private set; }

        public Task<ServerCheckResult> CheckAsync(Uri baseUri, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task Empty_NeverAsksTheNetwork()
    {
        var checker = new FakeChecker(ServerCheckResult.StudyLife);

        var result = await new ServerEntry(checker).SubmitAsync("  ", useAnyway: false);

        Assert.Equal(ServerEntryOutcome.Empty, result.Outcome);
        Assert.False(result.CanUseAnyway);
        Assert.Equal(0, checker.Calls);
    }

    [Fact]
    public async Task NotAUrl_NeverAsksTheNetworkAndCannotBeForced()
    {
        var checker = new FakeChecker(ServerCheckResult.StudyLife);

        var result = await new ServerEntry(checker).SubmitAsync("not a url", useAnyway: true);

        Assert.Equal(ServerEntryOutcome.NotAUrl, result.Outcome);
        Assert.False(result.CanUseAnyway);
        Assert.Equal(0, checker.Calls);
    }

    [Fact]
    public async Task AStudyLifeServer_IsAccepted()
    {
        var result = await new ServerEntry(new FakeChecker(ServerCheckResult.StudyLife)).SubmitAsync("studylife.example.com", false);

        Assert.True(result.IsAccepted);
        Assert.Equal("https://studylife.example.com/", result.Uri!.ToString());
    }

    [Theory]
    [InlineData(ServerCheckResult.Unreachable, ServerEntryOutcome.Unreachable)]
    [InlineData(ServerCheckResult.NotStudyLife, ServerEntryOutcome.NotStudyLife)]
    public async Task ASuspiciousServer_IsReportedButMayBeUsedAnyway(ServerCheckResult check, ServerEntryOutcome expected)
    {
        var result = await new ServerEntry(new FakeChecker(check)).SubmitAsync("studylife.example.com", false);

        Assert.Equal(expected, result.Outcome);
        Assert.True(result.CanUseAnyway);
    }

    [Fact]
    public async Task UseAnyway_SkipsTheCheck()
    {
        var checker = new FakeChecker(ServerCheckResult.Unreachable);

        var result = await new ServerEntry(checker).SubmitAsync("studylife.example.com", useAnyway: true);

        Assert.True(result.IsAccepted);
        Assert.Equal(0, checker.Calls);
    }
}

public class ServerCheckerTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return await respond(request);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Task<ServerCheckResult> CheckAsync(StubHandler handler, TimeSpan? timeout = null) =>
        new ServerChecker(handler, timeout).CheckAsync(new Uri("https://studylife.example.com/app/"));

    [Fact]
    public async Task InstanceInfo_IsAStudyLifeServer()
    {
        var handler = new StubHandler(_ => Task.FromResult(Json("""{"id":"3f2c","version":"3.29.1"}""")));

        Assert.Equal(ServerCheckResult.StudyLife, await CheckAsync(handler));
        Assert.Equal("https://studylife.example.com/app/api/instance", handler.Requests.Single().ToString());
    }

    [Theory]
    [InlineData("<html>router login</html>")]
    [InlineData("""{"version":"3.29.1"}""")]
    [InlineData("""{"id":"","version":"3.29.1"}""")]
    [InlineData("""["id","version"]""")]
    public async Task AnythingElseThatAnswers_IsNotStudyLife(string body)
    {
        var handler = new StubHandler(_ => Task.FromResult(Json(body)));

        Assert.Equal(ServerCheckResult.NotStudyLife, await CheckAsync(handler));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task AnErrorFromThePage_IsNotStudyLife(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => Task.FromResult(Json("{}", status)));

        Assert.Equal(ServerCheckResult.NotStudyLife, await CheckAsync(handler));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task AGatewayError_IsUnreachable(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => Task.FromResult(Json("{}", status)));

        Assert.Equal(ServerCheckResult.Unreachable, await CheckAsync(handler));
    }

    [Fact]
    public async Task ANetworkError_IsUnreachable()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("no route"));

        Assert.Equal(ServerCheckResult.Unreachable, await CheckAsync(handler));
    }

    [Fact]
    public async Task ASilentServer_IsUnreachableAfterTheTimeout()
    {
        var handler = new StubHandler(async request =>
        {
            await Task.Delay(Timeout.Infinite, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            return Json("{}");
        });

        // The handler ignores cancellation on purpose; the checker's own timeout must still end the call.
        var result = await CheckAsync(handler, TimeSpan.FromMilliseconds(100)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ServerCheckResult.Unreachable, result);
    }
}
