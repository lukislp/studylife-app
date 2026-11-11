using System.Net;
using System.Text.Json;

namespace StudyLife.App.Core;

public enum ServerCheckResult
{
    /// <summary>Answers like a StudyLife server.</summary>
    StudyLife,
    /// <summary>Answers, but is something else (or a StudyLife version too old to tell).</summary>
    NotStudyLife,
    /// <summary>No usable answer: no connection, timeout, or a gateway error.</summary>
    Unreachable,
}

public interface IServerChecker
{
    Task<ServerCheckResult> CheckAsync(Uri baseUri, CancellationToken cancellationToken = default);
}

/// <summary>
/// Asks <c>GET api/instance</c>. Every current StudyLife server answers it anonymously with
/// <c>{"id": "...", "version": "..."}</c> (InstanceController in the studylife repo; the id is the
/// installation id Home Assistant discovery uses). A random id plus a version string is a cheap and
/// specific signal: a login page or a router's admin page does not produce it. Servers older than that
/// endpoint answer 404 and are reported as <see cref="ServerCheckResult.NotStudyLife"/>; the first-start
/// screen lets the user continue anyway.
/// </summary>
public sealed class ServerChecker : IServerChecker
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(6);

    private readonly HttpMessageHandler? _handler;
    private readonly TimeSpan _timeout;

    public ServerChecker(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _handler = handler;
        _timeout = timeout ?? DefaultTimeout;
    }

    public async Task<ServerCheckResult> CheckAsync(Uri baseUri, CancellationToken cancellationToken = default)
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
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return ServerCheckResult.Unreachable;
        }
    }

    private static async Task<ServerCheckResult> AskAsync(HttpClient http, Uri baseUri, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(new Uri(ServerAddress.WithTrailingSlash(baseUri), "api/instance"), cancellationToken);
        if (response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
            return ServerCheckResult.Unreachable;
        if (!response.IsSuccessStatusCode)
            return ServerCheckResult.NotStudyLife;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return LooksLikeInstanceInfo(body) ? ServerCheckResult.StudyLife : ServerCheckResult.NotStudyLife;
    }

    private static bool LooksLikeInstanceInfo(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && HasText(root, "id")
                && HasText(root, "version");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasText(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString());
}
