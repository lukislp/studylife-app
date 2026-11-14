#if IOS || MACCATALYST
using Foundation;
#endif

namespace StudyLife.App.Services;

/// <summary>
/// The platform part of <see cref="StudyLife.App.Core.ConnectionFailure"/>, handed to it in MauiProgram.
/// Apple's NSUrlSession reports a certificate it does not trust as an NSErrorException in NSURLErrorDomain
/// with a code from NSURLErrorClientCertificateRequired (-1206) to NSURLErrorSecureConnectionFailed (-1200);
/// only code that references Foundation can read it (and a direct reference keeps the members from being
/// trimmed). Everywhere else the plain library recognises the failure on its own.
/// </summary>
public static class PlatformTlsFailure
{
    public static bool IsTrustFailure(Exception exception)
    {
#if IOS || MACCATALYST
        return exception is NSErrorException { Error: { } error }
            && error.Domain == "NSURLErrorDomain"
            && error.Code is >= -1206 and <= -1200;
#else
        _ = exception;
        return false;
#endif
    }
}
