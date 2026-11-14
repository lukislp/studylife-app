using System.Net.Sockets;
using System.Security.Authentication;
using StudyLife.App.Core;

namespace StudyLife.App.Core.Tests;

/// <summary>How each platform's TLS trust failure is recognised (the exception shapes, no network).</summary>
public class ConnectionFailureTests
{
    private readonly ConnectionFailure _failures = new();

    [Fact]
    public void SocketsHttpHandler_SecureConnectionError_IsUntrusted() =>
        Assert.True(_failures.IsUntrustedCertificate(
            new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established")));

    [Fact]
    public void AWrappedAuthenticationException_IsUntrusted() =>
        Assert.True(_failures.IsUntrustedCertificate(
            new HttpRequestException("failed", new AuthenticationException("The remote certificate is invalid: UntrustedRoot"))));

    [Fact]
    public void AndroidsSslHandshakeException_IsUntrusted() =>
        Assert.True(_failures.IsUntrustedCertificate(new HttpRequestException("failed", new SSLHandshakeException())));

    [Fact]
    public void AnAppleCertificateError_IsUntrustedThroughThePlatformCheck()
    {
        var apple = new ConnectionFailure(e => e is NSErrorException { Domain: "NSURLErrorDomain", Code: -1202 });

        Assert.True(apple.IsUntrustedCertificate(new HttpRequestException("failed", new NSErrorException("NSURLErrorDomain", -1202))));
        Assert.False(apple.IsUntrustedCertificate(new HttpRequestException("failed", new NSErrorException("NSURLErrorDomain", -1003))));
    }

    [Fact]
    public void ANameResolutionFailure_IsNotATrustProblem() =>
        Assert.False(_failures.IsUntrustedCertificate(
            new HttpRequestException(HttpRequestError.NameResolutionError, "No such host", new SocketException(11001))));

    [Fact]
    public void ATimeout_IsNotATrustProblem() =>
        Assert.False(_failures.IsUntrustedCertificate(new TimeoutException()));

    [Fact]
    public void Nothing_IsNotATrustProblem() =>
        Assert.False(_failures.IsUntrustedCertificate(null));

    /// <summary>Same simple name as the Android binding of javax.net.ssl.SSLHandshakeException.</summary>
    private sealed class SSLHandshakeException : Exception;

    /// <summary>Shape of Foundation.NSErrorException (Domain, Code).</summary>
    private sealed class NSErrorException(string domain, long code) : Exception
    {
        public string Domain { get; } = domain;
        public long Code { get; } = code;
    }
}
