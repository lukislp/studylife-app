namespace StudyLife.App.Core;

/// <summary>Why a typed server address cannot be used.</summary>
public enum ServerAddressProblem
{
    None,
    /// <summary>Nothing was typed.</summary>
    Empty,
    /// <summary>Not an http(s) address with a host.</summary>
    NotAUrl,
}

public readonly record struct ServerAddressParse(Uri? Uri, ServerAddressProblem Problem);

/// <summary>Turns what the user typed into the base address of a server, or says why not.
/// Pure: no network, no device.</summary>
public static class ServerAddress
{
    public static ServerAddressParse Parse(string? input)
    {
        var text = input?.Trim() ?? "";
        if (text.Length == 0)
            return new(null, ServerAddressProblem.Empty);
        if (text.Any(char.IsWhiteSpace))
            return new(null, ServerAddressProblem.NotAUrl);

        // People type "studylife.example.com"; https is the sensible default.
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || uri.Host.Length == 0)
            return new(null, ServerAddressProblem.NotAUrl);

        return new(WithTrailingSlash(uri), ServerAddressProblem.None);
    }

    /// <summary>Relative API paths ("api/x") resolve against the base address only when it ends
    /// with a slash (https://host/app + api/x would otherwise become https://host/api/x).</summary>
    public static Uri WithTrailingSlash(Uri uri)
    {
        var text = uri.GetLeftPart(UriPartial.Path);
        return text.EndsWith('/') ? new Uri(text) : new Uri(text + "/");
    }

    /// <summary>Host (with the port when it is not the default one) as shown to the user.</summary>
    public static string DisplayHost(Uri uri) => uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
}
