using System.Net;
using StudyLife.App.Core;

namespace StudyLife.App.Core.Tests;

public class BeforeSignOutHandlerTests
{
    private static readonly Uri Base = new("https://studylife.example.com/app/");

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage>? respond = null) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string? Session)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var session = request.Headers.TryGetValues("X-Session-Token", out var values) ? values.Single() : null;
            Requests.Add((request.Method, request.RequestUri!, session));
            return Task.FromResult(respond?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static HttpClient Client(RecordingHandler inner, Func<Uri, Task<HttpRequestMessage?>> create) =>
        new(new BeforeSignOutHandler(Base, create) { InnerHandler = inner }) { BaseAddress = Base };

    private static Func<Uri, Task<HttpRequestMessage?>> Unregister => b =>
        Task.FromResult<HttpRequestMessage?>(new HttpRequestMessage(HttpMethod.Post, new Uri(b, "api/push/unsubscribe-apns")));

    private static HttpRequestMessage SignOut()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "api/auth/logout");
        request.Headers.Add("X-Session-Token", "session-1");
        return request;
    }

    [Fact]
    public async Task SigningOut_FirstUnregistersWithTheSameSession()
    {
        var inner = new RecordingHandler();

        var response = await Client(inner, Unregister).SendAsync(SignOut());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Collection(inner.Requests,
            r => { Assert.Equal("https://studylife.example.com/app/api/push/unsubscribe-apns", r.Uri.ToString()); Assert.Equal("session-1", r.Session); },
            r => { Assert.Equal("https://studylife.example.com/app/api/auth/logout", r.Uri.ToString()); Assert.Equal("session-1", r.Session); });
    }

    [Fact]
    public async Task OtherRequests_AreLeftAlone()
    {
        var inner = new RecordingHandler();
        var client = Client(inner, Unregister);

        await client.GetAsync("api/auth/logout");
        await client.PostAsync("api/notes", null);

        Assert.Equal(2, inner.Requests.Count);
        Assert.DoesNotContain(inner.Requests, r => r.Uri.AbsolutePath.EndsWith("unsubscribe-apns", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NothingToUnregister_SignsOutDirectly()
    {
        var inner = new RecordingHandler();

        await Client(inner, _ => Task.FromResult<HttpRequestMessage?>(null)).SendAsync(SignOut());

        Assert.Equal("/app/api/auth/logout", inner.Requests.Single().Uri.AbsolutePath);
    }

    [Fact]
    public async Task AFailingUnregistration_NeverBlocksTheSignOut()
    {
        var inner = new RecordingHandler(r => r.RequestUri!.AbsolutePath.EndsWith("unsubscribe-apns", StringComparison.Ordinal)
            ? throw new HttpRequestException("no route")
            : new HttpResponseMessage(HttpStatusCode.OK));

        var response = await Client(inner, Unregister).SendAsync(SignOut());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Requests.Count);
    }

    [Fact]
    public async Task AFailingRequestFactory_NeverBlocksTheSignOut()
    {
        var inner = new RecordingHandler();

        var response = await Client(inner, _ => throw new InvalidOperationException("no token store")).SendAsync(SignOut());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(inner.Requests);
    }

    [Fact]
    public async Task ASlowUnregistration_IsGivenUpAfterTheTimeout()
    {
        var inner = new RecordingHandler();
        var never = new TaskCompletionSource<HttpRequestMessage?>();

        var response = await Client(inner, _ => never.Task).SendAsync(SignOut())
            .WaitAsync(BeforeSignOutHandler.Timeout + TimeSpan.FromSeconds(10));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(inner.Requests);
    }
}
