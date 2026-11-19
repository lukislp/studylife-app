using System.Net;

namespace StudyLife.App.Core;

/// <summary>What a request to delete the Live Activity push token came back with.</summary>
public enum LiveActivityTokenDeletion
{
    /// <summary>The server deleted the token (or had none: the call is idempotent).</summary>
    Deleted,

    /// <summary>An older server without the endpoint (404/405): nothing to delete there, not an error.</summary>
    NotSupported,

    /// <summary>Another answer or no answer (network, timeout): logged by the caller, never retried.</summary>
    Failed,
}

/// <summary>
/// The server call that removes this account's ActivityKit push token again (DELETE
/// api/timerstate/liveactivity-token, session only). Sent when the Live Activity ends and right before a
/// sign-out (manual, account deletion, server switch) so the server stops holding a token it can no longer
/// use. Always best effort: older servers lack the endpoint and answer 404/405, which is tolerated silently.
/// </summary>
public static class LiveActivityToken
{
    public const string Path = "api/timerstate/liveactivity-token";

    /// <summary>The deletion as an absolute request, for <see cref="BeforeSignOutHandler"/>.</summary>
    public static Task<HttpRequestMessage?> CreateSignOutDeletion(Uri baseUri) =>
        Task.FromResult<HttpRequestMessage?>(new HttpRequestMessage(HttpMethod.Delete, new Uri(ServerAddress.WithTrailingSlash(baseUri), Path)));

    /// <summary>Sends the deletion through the session client; never throws (except for the caller's own cancellation).</summary>
    public static async Task<LiveActivityTokenDeletion> DeleteAsync(HttpClient http, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.DeleteAsync(Path, cancellationToken);
            return Classify(response.StatusCode);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return LiveActivityTokenDeletion.Failed;
        }
    }

    public static LiveActivityTokenDeletion Classify(HttpStatusCode status) => status switch
    {
        HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed => LiveActivityTokenDeletion.NotSupported,
        _ when (int)status is >= 200 and < 300 => LiveActivityTokenDeletion.Deleted,
        _ => LiveActivityTokenDeletion.Failed,
    };
}
