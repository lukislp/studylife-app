using Microsoft.AspNetCore.Components;
using StudyLife.App.Core;

namespace StudyLife.App.Services;

/// <summary>
/// Leaving the current server: runs the hand-over (the one sequence, in <see cref="ServerHandOver"/>) and
/// then reloads the web view, which starts at the first-start screen because no server is saved any more.
/// The reload also drops every scoped service, so nothing keeps talking to the old address.
/// </summary>
public sealed class ServerSwitcher(AppHandOverSteps steps, NavigationManager navigation)
{
    public async Task ChangeAsync()
    {
        await new ServerHandOver(steps).RunAsync();
        navigation.NavigateTo("/", forceLoad: true);
    }
}
