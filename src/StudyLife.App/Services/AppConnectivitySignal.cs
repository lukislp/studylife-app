using StudyLife.Client.Services;

namespace StudyLife.App.Services;

/// <summary>
/// The native shell's IConnectivitySignal (studylife repo): "the network may be back". Raised when MAUI
/// reports a connection (<see cref="NetworkAccess.Internet"/> or <see cref="NetworkAccess.Local"/>) while the app
/// is in the foreground, and when the app comes back to the foreground (App.Resumed). The shared
/// sign-in pages and AppRoot's server check re-ask on it instead of waiting for their next retry. The platform
/// events are only hooked while someone listens and unhooked with the scope, so nothing keeps running behind
/// the UI. <see cref="Raise"/> lets the shell tell the pages about a result it found out itself.
/// </summary>
public sealed class AppConnectivitySignal : IConnectivitySignal, IDisposable
{
    private readonly object _gate = new();
    private Action? _handlers;
    private bool _hooked;

    public event Action? Restored
    {
        add
        {
            lock (_gate)
            {
                _handlers += value;
                Hook();
            }
        }
        remove
        {
            lock (_gate)
            {
                _handlers -= value;
                if (_handlers is null) Unhook();
            }
        }
    }

    public void Raise() => _handlers?.Invoke();

    private void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        Connectivity.ConnectivityChanged += OnConnectivityChanged;
        global::StudyLife.App.App.Resumed += Raise;
    }

    private void Unhook()
    {
        if (!_hooked) return;
        _hooked = false;
        Connectivity.ConnectivityChanged -= OnConnectivityChanged;
        global::StudyLife.App.App.Resumed -= Raise;
    }

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        // Not while backgrounded: the next Resumed asks instead.
        if (global::StudyLife.App.App.InForeground && e.NetworkAccess is NetworkAccess.Internet or NetworkAccess.Local) Raise();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _handlers = null;
            Unhook();
        }
    }
}
