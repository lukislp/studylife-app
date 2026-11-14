namespace StudyLife.App.Core;

/// <summary>
/// Makes every sign-out remove the copies the app keeps outside the web view (widget snapshot, Apple Watch
/// copy, Spotlight index, Live Activity). The shared client has one "logged out" hook that fires for a
/// manual sign-out, for a session the server declared invalid (401) and for the sign-out an account
/// deletion or a server switch ends in; this composes the app's wipe behind whatever the client already
/// put there (its offline cache purge), so the wipe itself stays the one function the deletion uses.
/// </summary>
public static class SignOutLocalCopies
{
    /// <summary>
    /// Returns a hook that runs <paramref name="existing"/> first (the client's own cache purge), then
    /// <paramref name="wipe"/>. A failure in one never prevents the other and never reaches the caller:
    /// signing out must always work.
    /// </summary>
    public static Func<Task> Compose(Func<Task>? existing, Func<Task> wipe) => async () =>
    {
        if (existing is not null)
        {
            try { await existing(); }
            catch { /* the client's purge is best effort, the wipe below must still run */ }
        }
        try { await wipe(); }
        catch { /* best effort, a sign-out never fails because a device copy could not be removed */ }
    };
}
