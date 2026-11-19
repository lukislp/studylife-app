using System.Net;
using StudyLife.App.Core;

namespace StudyLife.App.Core.Tests;

public class LiveActivityTokenTests
{
    private static readonly Uri Base = new("https://studylife.example.com/app/");

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage>? respond = null) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string? Session)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var session = request.Headers.TryGetValues("X-Session-Token", out var values) ? values.Single() : null;
            Requests.Add((request.Method, request.RequestUri!, session));
            return Task.FromResult(respond?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    private static HttpClient Client(HttpMessageHandler handler) => new(handler) { BaseAddress = Base };

    [Fact]
    public async Task Delete_SendsADeleteToTheTokenEndpoint()
    {
        var inner = new RecordingHandler();

        var outcome = await LiveActivityToken.DeleteAsync(Client(inner));

        Assert.Equal(LiveActivityTokenDeletion.Deleted, outcome);
        var request = Assert.Single(inner.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal("https://studylife.example.com/app/api/timerstate/liveactivity-token", request.Uri.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public async Task AnOlderServerWithoutTheEndpoint_IsTolerated(HttpStatusCode status)
    {
        var inner = new RecordingHandler(_ => new HttpResponseMessage(status));

        Assert.Equal(LiveActivityTokenDeletion.NotSupported, await LiveActivityToken.DeleteAsync(Client(inner)));
    }

    [Fact]
    public async Task AServerError_IsReportedAsFailedWithoutThrowing()
    {
        var inner = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        Assert.Equal(LiveActivityTokenDeletion.Failed, await LiveActivityToken.DeleteAsync(Client(inner)));
    }

    [Fact]
    public async Task ANetworkError_IsReportedAsFailedWithoutThrowing()
    {
        var inner = new RecordingHandler(_ => throw new HttpRequestException("offline"));

        Assert.Equal(LiveActivityTokenDeletion.Failed, await LiveActivityToken.DeleteAsync(Client(inner)));
    }

    [Fact]
    public async Task SigningOut_DeletesTheTokenWithTheSessionBeforeTheLogout()
    {
        var inner = new RecordingHandler();
        var handler = new BeforeSignOutHandler(Base, LiveActivityToken.CreateSignOutDeletion) { InnerHandler = inner };
        var signOut = new HttpRequestMessage(HttpMethod.Post, "api/auth/logout");
        signOut.Headers.Add("X-Session-Token", "session-1");

        await Client(handler).SendAsync(signOut);

        Assert.Collection(inner.Requests,
            r =>
            {
                Assert.Equal(HttpMethod.Delete, r.Method);
                Assert.Equal("/app/api/timerstate/liveactivity-token", r.Uri.AbsolutePath);
                Assert.Equal("session-1", r.Session);
            },
            r => Assert.Equal("/app/api/auth/logout", r.Uri.AbsolutePath));
    }

    [Fact]
    public async Task SigningOut_IsNotBlockedWhenTheServerRefusesTheDeletion()
    {
        var inner = new RecordingHandler(r => r.Method == HttpMethod.Delete
            ? new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)
            : new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new BeforeSignOutHandler(Base, LiveActivityToken.CreateSignOutDeletion) { InnerHandler = inner };

        var response = await Client(handler).PostAsync("api/auth/logout", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Requests.Count);
    }

    [Fact]
    public async Task ChainedWithThePushUnregistration_BothRunBeforeTheLogout()
    {
        var inner = new RecordingHandler();
        var handler = new BeforeSignOutHandler(Base, b => Task.FromResult<HttpRequestMessage?>(new HttpRequestMessage(HttpMethod.Post, new Uri(b, "api/push/unsubscribe-apns"))))
        {
            InnerHandler = new BeforeSignOutHandler(Base, LiveActivityToken.CreateSignOutDeletion) { InnerHandler = inner },
        };

        await Client(handler).PostAsync("api/auth/logout", null);

        Assert.Equal(
            ["/app/api/push/unsubscribe-apns", "/app/api/timerstate/liveactivity-token", "/app/api/auth/logout"],
            inner.Requests.Select(r => r.Uri.AbsolutePath).ToArray());
    }
}
