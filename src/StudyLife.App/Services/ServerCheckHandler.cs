namespace StudyLife.App.Services;

/// <summary>
/// The HTTP handler of the server check (App.Core's ServerChecker), handed in by MauiProgram. On Apple platforms
/// it is an NSUrlSessionHandler with the local cache switched off: the default one uses the app's shared URL cache
/// and answered api/instance (sent with "Cache-Control: private, max-age=60") from it with Wi-Fi off, so the
/// signed-out bar never said "cannot be reached". DisableCaching makes every request ignore cached data and keeps
/// the answers out of the cache. Everywhere else the default handler has no response cache: null keeps it.
/// </summary>
public static class ServerCheckHandler
{
    public static HttpMessageHandler? Create()
    {
#if IOS || MACCATALYST
        return new System.Net.Http.NSUrlSessionHandler { DisableCaching = true };
#else
        return null;
#endif
    }
}
