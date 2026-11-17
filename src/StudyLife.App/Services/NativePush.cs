using System.Security.Cryptography;
using System.Text;
using System.Net.Http.Json;
using StudyLife.Client.Services;
using StudyLife.Shared;

namespace StudyLife.App.Services;

/// <summary>
/// APNs registration for the app (counterpart to INativePush in the client): fetches the
/// device token from the OS (AppDelegate delivers it via ApnsTokenStore) and registers it
/// with the server (api/push/subscribe-apns). Only active if the app is signed with the
/// aps-environment entitlement (paid developer profile, AppleSigningInfo.HasPushEntitlement) -
/// with free signing, IsAvailable stays false and NotificationService behaves like on the
/// web (subscribePush returns null there, see js/native-interop.js).
/// </summary>
public sealed class NativePush : INativePush
{
    private readonly HttpClient _http;
    private readonly ServerUrlStore _urls;

    public NativePush(HttpClient http, ServerUrlStore urls)
    {
        _http = http;
        _urls = urls;
    }

    private const string UnsubscribePath = "api/push/unsubscribe-apns";

    public bool IsAvailable => _urls.IsConfigured && CanUsePush;

    private static bool CanUsePush => DeviceInfo.Platform == DevicePlatform.iOS && AppleSigningInfo.HasPushEntitlement;

    /// <summary>The unregistration sent right before a plain sign-out (BeforeSignOutHandler, wired in
    /// MauiProgram): the client's logged-out hook runs only after the server ended the session, too late for an
    /// authenticated request. Built from the token this process already holds, never by asking APNs for one,
    /// so signing out is not held up by a registration; without a token there is nothing to unregister.</summary>
    public static Task<HttpRequestMessage?> CreateSignOutUnregistration(Uri baseUri)
    {
        if (!CanUsePush || ApnsTokenStore.CachedToken is not { } token)
            return Task.FromResult<HttpRequestMessage?>(null);
        return Task.FromResult<HttpRequestMessage?>(new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, UnsubscribePath))
        {
            Content = JsonContent.Create(UnsubscribeBody(token)),
        });
    }

    private static object UnsubscribeBody(string token) => new { Token = token, DeviceName = DeviceInfo.Current.Name };

    public async Task<bool> RegisterAsync()
    {
        var token = await ApnsTokenStore.GetTokenAsync();
        if (token == null) return false;

        try
        {
            var response = await _http.PostAsJsonAsync("api/push/subscribe-apns",
                new { Token = token, DeviceName = DeviceInfo.Current.Name });
            var success = response.IsSuccessStatusCode;
            if (success)
                NativeTelemetry.Enqueue(new TelemetryEventDto { Type = "push", Event = "registered" });
            return success;
        }
        catch (HttpRequestException)
        {
            return false; // no network - next InitializeAsync call (app start) tries again
        }
    }

    /// <summary>Tells the server to stop sending pushes to this device (api/push/unsubscribe-apns), used when
    /// the user leaves the server. Best effort: no token, no push entitlement or a failing request all
    /// report false, and the caller carries on.</summary>
    public async Task<bool> UnregisterAsync()
    {
        if (!IsAvailable) return false;
        var token = await ApnsTokenStore.GetTokenAsync();
        if (token == null) return false;
        try
        {
            var response = await _http.PostAsJsonAsync(UnsubscribePath, UnsubscribeBody(token));
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    public async Task<string?> GetEndpointHashAsync()
    {
        var token = await ApnsTokenStore.GetTokenAsync();
        if (token == null) return null;
        // Identical to the server-side computation (PushController.HashEndpoint over "apns:<token>").
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"apns:{token}")));
    }
}

/// <summary>
/// Handoff point between AppDelegate (delivers the APNs token via a delegate callback) and
/// NativePush (needs it as an awaitable result). RegisterForRemoteNotifications is triggered
/// lazily on the first GetTokenAsync call; the token stays cached for the process lifetime
/// (Apple delivers it stably per launch).
/// </summary>
public static class ApnsTokenStore
{
    private static readonly object Lock = new();
    private static TaskCompletionSource<string?>? _pending;
    private static string? _token;

    /// <summary>The token delivered in this process, without registering for one.</summary>
    public static string? CachedToken
    {
        get { lock (Lock) return _token; }
    }

    public static Task<string?> GetTokenAsync()
    {
#if IOS
        TaskCompletionSource<string?> pending;
        lock (Lock)
        {
            if (_token != null) return Task.FromResult<string?>(_token);
            if (_pending != null) return _pending.Task;
            pending = _pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        MainThread.BeginInvokeOnMainThread(() =>
            UIKit.UIApplication.SharedApplication.RegisterForRemoteNotifications());

        // 30s timeout: without network/Apple reachability neither a success nor a failure
        // callback ever arrives. IMPORTANT: the lambda below must use "pending" (captured
        // locally), NOT the static field "_pending" - SetToken/SetFailed set "_pending = null"
        // as soon as the APNs response arrives, and that can happen exactly between the
        // WhenAny completion and the ContinueWith execution. With the static field this crashed
        // live with a NullReferenceException (_pending.Task on an already-null field) -
        // unhandled all the way into the Blazor renderer, because GetTokenAsync runs from
        // MainLayout.OnInitializedAsync via NotificationService.InitializeAsync on EVERY
        // authenticated cold start.
        return Task.WhenAny(pending.Task, Task.Delay(TimeSpan.FromSeconds(30)))
            .ContinueWith(t => t.Result == pending.Task ? pending.Task.Result : null);
#else
        return Task.FromResult<string?>(null);
#endif
    }

    /// <summary>Called by AppDelegate on successful registration (token as hex).</summary>
    public static void SetToken(string tokenHex)
    {
        lock (Lock)
        {
            _token = tokenHex;
            _pending?.TrySetResult(tokenHex);
            _pending = null;
        }
    }

    /// <summary>Called by AppDelegate on failed registration.</summary>
    public static void SetFailed()
    {
        lock (Lock)
        {
            _pending?.TrySetResult(null);
            _pending = null;
        }
    }
}
