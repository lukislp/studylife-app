using StudyLife.App.Core;

namespace StudyLife.App.Services;

/// <summary>
/// Persists the server URL from the first-launch dialog to the native app preferences
/// (not to WebView localStorage - the URL must be fixed BEFORE the first HttpClient,
/// i.e. before the Blazor page can even do any JS interop).
/// </summary>
public sealed class ServerUrlStore
{
    private const string PreferencesKey = "studylife-server-url";

    public Uri? BaseUri { get; private set; }

    public ServerUrlStore()
    {
        var stored = Preferences.Default.Get(PreferencesKey, "");
        // Env var as fallback (only if nothing has been saved yet): allows automated
        // tests and dev setups without clicking through the first-launch dialog.
        if (string.IsNullOrEmpty(stored))
            stored = Environment.GetEnvironmentVariable("STUDYLIFE_SERVER_URL") ?? "";
        if (Uri.TryCreate(stored, UriKind.Absolute, out var uri))
            BaseUri = uri;
    }

    public bool IsConfigured => BaseUri != null;

    /// <summary>Forgets the saved address (changing server): the next start shows the first-start screen.
    /// The STUDYLIFE_SERVER_URL fallback is only read when the app process starts, so it cannot bring
    /// the old address back within the session.</summary>
    public void Clear()
    {
        Preferences.Default.Remove(PreferencesKey);
        BaseUri = null;
    }

    public void Save(Uri baseUri)
    {
        // Force a trailing slash, otherwise "api/..." resolves relative to the parent path
        // (https://host/app + api/x => https://host/api/x instead of https://host/app/api/x).
        baseUri = ServerAddress.WithTrailingSlash(baseUri);

        Preferences.Default.Set(PreferencesKey, baseUri.ToString());
        BaseUri = baseUri;
    }
}
