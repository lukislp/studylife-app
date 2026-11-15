namespace StudyLife.App.Core;

/// <summary>The platform's network reachability, mirrored from MAUI's NetworkAccess so this project stays free of
/// MAUI types (the app layer maps one to the other).</summary>
public enum NetworkReach
{
    Unknown,
    None,
    /// <summary>Connected, but only partly (a captive portal, or a link without a route to the internet).</summary>
    Constrained,
    Local,
    Internet,
}

/// <summary>What a reachability report means for the server check.</summary>
public enum ConnectivityChange
{
    /// <summary>Nothing to act on (the platform does not know).</summary>
    Ignore,
    /// <summary>A usable network: ask the server again.</summary>
    Restored,
    /// <summary>No network, or only a constrained one: ask the server again right away so the answer (and the
    /// bar) follows the real state. Constrained counts here because a captive portal or a LAN without the
    /// server cannot be judged from the connectivity state alone; the server check stays the source of truth.</summary>
    Lost,
}

/// <summary>Maps reachability to <see cref="ConnectivityChange"/> and decides when to poke the server check. Pure
/// apart from the one remembered value that drops repeated "lost" reports (nothing changed, so nothing to ask).</summary>
public sealed class ConnectivityWatch
{
    private bool _lost;

    public static ConnectivityChange Classify(NetworkReach reach) => reach switch
    {
        NetworkReach.Internet or NetworkReach.Local => ConnectivityChange.Restored,
        NetworkReach.None or NetworkReach.Constrained => ConnectivityChange.Lost,
        _ => ConnectivityChange.Ignore,
    };

    /// <summary>True when the server check should run now. A restoration always re-asks (the network may have
    /// switched from Wi-Fi to mobile data); a loss only when the previous report was not already a loss.</summary>
    public bool ShouldAsk(NetworkReach reach)
    {
        switch (Classify(reach))
        {
            case ConnectivityChange.Restored:
                _lost = false;
                return true;
            case ConnectivityChange.Lost:
                if (_lost) return false;
                _lost = true;
                return true;
            default:
                return false;
        }
    }
}
