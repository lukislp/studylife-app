using Microsoft.AspNetCore.Components;
using StudyLife.App.Core;

namespace StudyLife.App.Services;

/// <summary>
/// Leaving the current server: runs the hand-over (the one sequence, in <see cref="ServerHandOver"/>) and
/// then reloads the web view, which starts at the first-start screen because no server is saved any more.
/// The reload also drops every scoped service, so nothing keeps talking to the old address.
/// With <c>next</c> (the sign-in bar's "use this address" for a server whose passkeys live under its public
/// domain) the new address is saved right after the hand-over, so the reload starts on that server instead
/// of the first-start screen.
/// </summary>
public sealed class ServerSwitcher(AppHandOverSteps steps, ServerUrlStore server, NavigationManager navigation)
{
    public async Task ChangeAsync(Uri? next = null)
    {
        await new ServerHandOver(steps).RunAsync();
        if (next is not null)
            server.Save(next);
        navigation.NavigateTo("/", forceLoad: true);
    }
}
