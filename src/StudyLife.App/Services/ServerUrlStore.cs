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

    public void Save(Uri baseUri)
    {
        // Force a trailing slash, otherwise "api/..." resolves relative to the parent path
        // (https://host/app + api/x => https://host/api/x instead of https://host/app/api/x).
        var normalized = baseUri.ToString();
        if (!normalized.EndsWith('/'))
            baseUri = new Uri(normalized + "/");

        Preferences.Default.Set(PreferencesKey, baseUri.ToString());
        BaseUri = baseUri;
    }
}
