namespace StudyLife.App.Services;

/// <summary>
/// Hands native entry points (home screen quick actions, app links) over to the Blazor world.
/// Cold start: the quick action arrives before Blazor has initialized - in that case the
/// route is buffered and consumed by AppRoot after boot. Warm start: direct event.
/// </summary>
public sealed class DeepLinkService
{
    private readonly object _lock = new();
    private string? _pending;

    public event Action<string>? OnNavigate;

    public void Dispatch(string route)
    {
        Action<string>? handler;
        lock (_lock)
        {
            handler = OnNavigate;
            if (handler == null)
            {
                _pending = route;
                return;
            }
        }
        handler(route);
    }

    public string? ConsumePending()
    {
        lock (_lock)
        {
            var pending = _pending;
            _pending = null;
            return pending;
        }
    }
}
