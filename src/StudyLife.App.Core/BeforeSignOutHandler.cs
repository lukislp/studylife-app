namespace StudyLife.App.Core;

/// <summary>
/// Sends one request of the app's own (the push unregistration) right before the shared client's sign-out
/// request (POST api/auth/logout) leaves, with the same session headers. The client's "logged out" hook runs
/// only after the server ended the session, when an authenticated request can no longer succeed, so this
/// is the last moment the device can still tell the server to stop pushing to it.
/// <para>Sits inside the session handler, so the sign-out request already carries the session header. The
/// extra request is best effort and bounded by <see cref="Timeout"/>: whatever happens to it (no request,
/// an error, a 401), the sign-out itself is sent and its answer returned unchanged.</para>
/// </summary>
public sealed class BeforeSignOutHandler(Uri baseUri, Func<Uri, Task<HttpRequestMessage?>> createRequest) : DelegatingHandler
{
    public const string SignOutPath = "api/auth/logout";

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly Uri _signOut = new(ServerAddress.WithTrailingSlash(baseUri), SignOutPath);
    private readonly Uri _base = ServerAddress.WithTrailingSlash(baseUri);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (IsSignOut(request))
            await SendBeforeSignOutAsync(request, cancellationToken);
        return await base.SendAsync(request, cancellationToken);
    }

    private bool IsSignOut(HttpRequestMessage request) =>
        request.Method == HttpMethod.Post
        && request.RequestUri is { } uri
        && Uri.Compare(uri, _signOut, UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    private async Task SendBeforeSignOutAsync(HttpRequestMessage signOut, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            using var extra = await createRequest(_base).WaitAsync(timeout.Token);
            if (extra is null) return;
            foreach (var header in signOut.Headers)
                extra.Headers.TryAddWithoutValidation(header.Key, header.Value);
            using var response = await base.SendAsync(extra, timeout.Token);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Best effort: no network, a slow server or a refused request must never block signing out.
        }
    }
}
