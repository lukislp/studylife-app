using StudyLife.App.Core;
using StudyLife.Client.Services;

namespace StudyLife.App.Services;

/// <summary>
/// The native shell's IConnectivitySignal (studylife repo): "the network changed, ask again". Raised when MAUI
/// reports a connection (<see cref="NetworkAccess.Internet"/> or <see cref="NetworkAccess.Local"/>) or a loss
/// (<see cref="NetworkAccess.None"/> or <see cref="NetworkAccess.ConstrainedInternet"/>, see
/// <see cref="ConnectivityWatch"/>) while the app is in the foreground, and when the app comes back to the
/// foreground (App.Resumed). A loss only makes the listeners ask again: the answer (unreachable or not) always
/// comes from their own server check, never from the connectivity state. The shared
/// sign-in pages and AppRoot's server check re-ask on it instead of waiting for their next retry. The platform
/// events are only hooked while someone listens and unhooked with the scope, so nothing keeps running behind
/// the UI. <see cref="Raise"/> lets the shell tell the pages about a result it found out itself.
/// </summary>
public sealed class AppConnectivitySignal : IConnectivitySignal, IDisposable
{
    private readonly object _gate = new();
    private Action? _handlers;
    private readonly ConnectivityWatch _watch = new();
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

    /// <summary>False while the app is in the background: the shared pages and AppRoot's server check then
    /// neither ask nor keep a timer, and continue when <see cref="Restored"/> fires on resume.</summary>
    public bool CanAsk => global::StudyLife.App.App.InForeground;

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
        if (!CanAsk) return;
        bool ask;
        lock (_gate) ask = _watch.ShouldAsk(ToReach(e.NetworkAccess));
        if (ask) Raise();
    }

    private static NetworkReach ToReach(NetworkAccess access) => access switch
    {
        NetworkAccess.Internet => NetworkReach.Internet,
        NetworkAccess.Local => NetworkReach.Local,
        NetworkAccess.ConstrainedInternet => NetworkReach.Constrained,
        NetworkAccess.None => NetworkReach.None,
        _ => NetworkReach.Unknown,
    };

    public void Dispose()
    {
        lock (_gate)
        {
            _handlers = null;
            Unhook();
        }
    }
}
