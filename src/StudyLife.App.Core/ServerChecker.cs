using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace StudyLife.App.Core;

public enum ServerCheckResult
{
    /// <summary>Answers like a StudyAtrium server.</summary>
    StudyLife,
    /// <summary>Answers, but is something else (or a StudyAtrium version too old to tell).</summary>
    NotStudyLife,
    /// <summary>No usable answer: no connection, timeout, or a gateway error.</summary>
    Unreachable,
    /// <summary>The device does not trust the server's TLS certificate (see <see cref="ConnectionFailure"/>).</summary>
    UntrustedCertificate,
}

/// <summary>The answer of <see cref="IServerChecker"/>: the verdict and, from a StudyAtrium server that pinned
/// its passkey domain, that domain and its public origin (api/instance, null otherwise).</summary>
public sealed record ServerCheck(ServerCheckResult Result, string? PasskeyRpId = null, Uri? PasskeyOrigin = null)
{
    public static ServerCheck Of(ServerCheckResult result) => new(result);
}

public interface IServerChecker
{
    Task<ServerCheck> CheckAsync(Uri baseUri, CancellationToken cancellationToken = default);
}

/// <summary>
/// Asks <c>GET api/instance</c>. Every current StudyAtrium server answers it anonymously with
/// <c>{"id": "...", "version": "..."}</c> (InstanceController in the studylife repo; the id is the
/// installation id Home Assistant discovery uses). A random id plus a version string is a cheap and
/// specific signal: a login page or a router's admin page does not produce it. Servers older than that
/// endpoint answer 404 and are reported as <see cref="ServerCheckResult.NotStudyLife"/>; the first-start
/// screen lets the user continue anyway. A certificate the device does not trust is reported as such
/// (<see cref="ServerCheckResult.UntrustedCertificate"/>), never accepted.
/// <para>
/// The answer must come from the server now, never from a cache: the server sends
/// <c>Cache-Control: private, max-age=60</c> for api/instance, and Apple's NSUrlSession keeps such a response in
/// the app's shared URL cache (on disk, across launches) and serves it without the network for a minute. The
/// check then said "StudyAtrium server" with Wi-Fi off, so the sign-in bar never showed "cannot be reached". Every
/// request therefore says <c>Cache-Control: no-cache, no-store</c> (and <c>Pragma: no-cache</c> for HTTP/1.0
/// proxies); on Apple platforms the app also hands in a handler with the local cache switched off
/// (<c>NSUrlSessionHandler.DisableCaching</c>), because the request header alone does not bind the platform cache.
/// </para>
/// </summary>
public sealed class ServerChecker : IServerChecker
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(6);

    private readonly HttpMessageHandler? _handler;
    private readonly TimeSpan _timeout;
    private readonly ConnectionFailure _failures;

    public ServerChecker(HttpMessageHandler? handler = null, TimeSpan? timeout = null, ConnectionFailure? failures = null)
    {
        _handler = handler;
        _timeout = timeout ?? DefaultTimeout;
        _failures = failures ?? new ConnectionFailure();
    }

    public async Task<ServerCheck> CheckAsync(Uri baseUri, CancellationToken cancellationToken = default)
    {
        using var http = _handler is null
            ? new HttpClient()
            : new HttpClient(_handler, disposeHandler: false);
        http.Timeout = Timeout.InfiniteTimeSpan;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            // WaitAsync enforces the limit even if a handler ignores its cancellation token.
            return await AskAsync(http, baseUri, timeout.Token).WaitAsync(_timeout, cancellationToken);
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            return ServerCheck.Of(_failures.IsUntrustedCertificate(failure)
                ? ServerCheckResult.UntrustedCertificate
                : ServerCheckResult.Unreachable);
        }
    }

    private static async Task<ServerCheck> AskAsync(HttpClient http, Uri baseUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ServerAddress.WithTrailingSlash(baseUri), "api/instance"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        request.Headers.Pragma.Add(new NameValueHeaderValue("no-cache"));
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
            return ServerCheck.Of(ServerCheckResult.Unreachable);
        if (!response.IsSuccessStatusCode)
            return ServerCheck.Of(ServerCheckResult.NotStudyLife);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return ReadInstanceInfo(body);
    }

    private static ServerCheck ReadInstanceInfo(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "id") is null || Text(root, "version") is null)
                return ServerCheck.Of(ServerCheckResult.NotStudyLife);
            var origin = Uri.TryCreate(Text(root, "passkeyOrigin"), UriKind.Absolute, out var parsed)
                && parsed.Scheme == Uri.UriSchemeHttps
                ? parsed
                : null;
            return new ServerCheck(ServerCheckResult.StudyLife, Text(root, "passkeyRpId"), origin);
        }
        catch (JsonException)
        {
            return ServerCheck.Of(ServerCheckResult.NotStudyLife);
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;
}
