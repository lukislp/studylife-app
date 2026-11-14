using System.Net.Http;
using System.Security.Authentication;

namespace StudyLife.App.Core;

/// <summary>
/// Tells a failed connection whose TLS certificate the device does not trust (a private certificate
/// authority such as a homelab CA, a self-signed certificate, a name that is not on the certificate,
/// an expired one) apart from "nothing answered". The user can do something about the first one
/// (install the CA's profile, use the public address), so the app says so instead of "unreachable".
/// Nothing here ever relaxes the validation: it only reads the failure.
/// <para>
/// Each platform's HTTP stack reports it differently, so the whole exception chain is checked:
/// SocketsHttpHandler (Windows, tests) sets <see cref="HttpRequestError.SecureConnectionError"/> or wraps an
/// <see cref="AuthenticationException"/>; Android's handler wraps a <c>javax.net.ssl</c> /
/// <c>java.security.cert</c> exception (matched by its type name, so this stays a plain library). Apple's
/// NSUrlSession handler wraps an <c>NSErrorException</c>, which only the app can inspect: it passes that
/// check in as <paramref name="platformCheck"/>.
/// </para>
/// </summary>
public sealed class ConnectionFailure(Func<Exception, bool>? platformCheck = null)
{
    private static readonly string[] JavaTlsTypeNames =
    [
        "SSLHandshakeException",
        "SSLPeerUnverifiedException",
        "CertPathValidatorException",
        "CertificateException",
    ];

    /// <summary>True when the failure (or anything it wraps) is a TLS trust failure.</summary>
    public bool IsUntrustedCertificate(Exception? failure)
    {
        for (var current = failure; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError })
                return true;
            if (current is AuthenticationException)
                return true;
            var name = current.GetType().Name;
            if (JavaTlsTypeNames.Any(n => string.Equals(name, n, StringComparison.Ordinal)))
                return true;
            if (platformCheck?.Invoke(current) == true)
                return true;
        }
        return false;
    }
}
