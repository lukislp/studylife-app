using StudyLife.App.Core;
using StudyLife.Client.Services;

namespace StudyLife.App.Services;

/// <summary>
/// What each step of <see cref="ServerHandOver"/> does on this device. Each step is a single call into
/// the service that already owns the thing: NativePush for the push token, the session HttpClient for the
/// server sign-out, SessionTokenStore for the session and the offline caches, NativeLocalCopies for the
/// widget/Watch/Spotlight wipe (the same function the account deletion uses), ServerUrlStore for the address.
/// </summary>
public sealed class AppHandOverSteps(
    NativePush push,
    HttpClient http,
    SessionTokenStore session,
    ServerUrlStore server) : IHandOverSteps
{
    public Task UnregisterPushAsync(CancellationToken cancellationToken) => push.UnregisterAsync();

    public async Task SignOutOnServerAsync(CancellationToken cancellationToken)
    {
        if (session.Token is null) return;
        using var response = await http.PostAsync("api/auth/logout", null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Clears the token and, through the store's logout hook, the account's offline caches.</summary>
    public Task ClearSessionAsync(CancellationToken cancellationToken) => session.ClearAsync();

    public Task WipeLocalCopiesAsync(CancellationToken cancellationToken) => NativeLocalCopies.WipeAsync();

    public Task ForgetServerAsync(CancellationToken cancellationToken)
    {
        server.Clear();
        return Task.CompletedTask;
    }
}
