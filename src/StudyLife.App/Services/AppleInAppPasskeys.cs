#if IOS
using System.Text.Json;
using System.Text.Json.Nodes;
using AuthenticationServices;
using Foundation;
using UIKit;

namespace StudyLife.App.Services;

/// <summary>
/// Native passkey ceremony directly in the app (Face ID, no browser sheet) via the
/// ASAuthorization APIs. Translates between the base64url JSON from Fido2NetLib (server)
/// and the NSData fields of the Apple APIs - exactly the counterpart to createPasskey/
/// getPasskeyAssertion in Login.razor.js, just native. Works ONLY if the app is signed
/// with the Associated Domains entitlement (paid developer program) and the server
/// domain serves a matching apple-app-site-association - callers check
/// AppleSigningInfo.HasAssociatedDomains beforehand.
/// </summary>
internal static class AppleInAppPasskeys
{
    /// <summary>Console.WriteLine ends up in native stdout (visible via "xcrun devicectl
    /// device process launch --console") - the only way to see diagnostics without
    /// Safari Web Inspector access to the WKWebView. Explicit flush because Console.Out is
    /// buffered for non-TTY output - a hard process kill (e.g. during console capture
    /// with a time limit) would otherwise silently discard unflushed lines.</summary>
    private static void LogDiag(string message)
    {
        Console.WriteLine($"[AppleInAppPasskeys] {message}");
        Console.Out.Flush();
    }

    /// <summary>optionsJson = CredentialCreateOptions.ToJson(); return value in the format of
    /// AuthenticatorAttestationRawResponse, null on cancel/error.</summary>
    public static async Task<string?> CreateAsync(string optionsJson)
    {
        LogDiag("CreateAsync started");
        try
        {
            using var doc = JsonDocument.Parse(optionsJson);
            var root = doc.RootElement;
            var rpId = root.GetProperty("rp").GetProperty("id").GetString()!;
            var user = root.GetProperty("user");
            var userName = user.GetProperty("name").GetString() ?? "";
            var userId = FromBase64Url(user.GetProperty("id").GetString()!);
            var challenge = FromBase64Url(root.GetProperty("challenge").GetString()!);

            var provider = new ASAuthorizationPlatformPublicKeyCredentialProvider(rpId);
            var request = provider.CreateCredentialRegistrationRequest(
                NSData.FromArray(challenge), userName, NSData.FromArray(userId));

            if (root.TryGetProperty("authenticatorSelection", out var selection)
                && selection.ValueKind == JsonValueKind.Object
                && selection.TryGetProperty("userVerification", out var uv))
            {
                request.UserVerificationPreference = MapUserVerification(uv.GetString());
            }

            // excludeCredentials (duplicate-registration protection) deliberately omitted: the
            // corresponding iOS 17.4 API isn't exposed in the current .NET binding, and
            // the server rejects already-registered credentials on its own anyway
            // (excludeCredentials is a convenience, not a security feature).

            var authorization = await PerformRequestAsync(request);
            if (authorization?.GetCredential<ASAuthorizationPlatformPublicKeyCredentialRegistration>()
                is not { } registration)
                return null;

            var credentialId = ToBase64Url(registration.CredentialId);
            var result = new JsonObject
            {
                ["id"] = credentialId,
                ["rawId"] = credentialId,
                ["type"] = "public-key",
                ["response"] = new JsonObject
                {
                    ["attestationObject"] = ToBase64Url(registration.RawAttestationObject!),
                    ["clientDataJSON"] = ToBase64Url(registration.RawClientDataJson),
                    // Platform authenticator: fixed "internal" (like getTransports() in the browser)
                    ["transports"] = new JsonArray("internal"),
                },
                ["clientExtensionResults"] = new JsonObject(),
            };
            return result.ToJsonString();
        }
        catch (Exception ex)
        {
            LogDiag($"CreateAsync failed: {ex}");
            return null; // same semantics as cancellation: the page shows the generic error message
        }
    }

    /// <summary>optionsJson = AssertionOptions.ToJson(); return value in the format of
    /// AuthenticatorAssertionRawResponse, null on cancel/error.</summary>
    public static async Task<string?> GetAssertionAsync(string optionsJson)
    {
        LogDiag("GetAssertionAsync started");
        try
        {
            using var doc = JsonDocument.Parse(optionsJson);
            var root = doc.RootElement;
            var rpId = root.GetProperty("rpId").GetString()!;
            var challenge = FromBase64Url(root.GetProperty("challenge").GetString()!);
            LogDiag($"rpId={rpId}");

            var provider = new ASAuthorizationPlatformPublicKeyCredentialProvider(rpId);
            var request = provider.CreateCredentialAssertionRequest(NSData.FromArray(challenge));

            if (root.TryGetProperty("userVerification", out var uv))
                request.UserVerificationPreference = MapUserVerification(uv.GetString());

            if (root.TryGetProperty("allowCredentials", out var allow)
                && allow.ValueKind == JsonValueKind.Array)
            {
                var descriptors = allow.EnumerateArray()
                    .Select(c => new ASAuthorizationPlatformPublicKeyCredentialDescriptor(
                        NSData.FromArray(FromBase64Url(c.GetProperty("id").GetString()!))))
                    .ToArray();
                if (descriptors.Length > 0)
                    request.AllowedCredentials = descriptors;
            }

            LogDiag("calling PerformRequestAsync (ASAuthorizationController.PerformRequests)");
            var authorization = await PerformRequestAsync(request);
            if (authorization?.GetCredential<ASAuthorizationPlatformPublicKeyCredentialAssertion>()
                is not { } assertion)
                return null;

            var credentialId = ToBase64Url(assertion.CredentialId);
            var result = new JsonObject
            {
                ["id"] = credentialId,
                ["rawId"] = credentialId,
                ["type"] = "public-key",
                ["response"] = new JsonObject
                {
                    ["authenticatorData"] = ToBase64Url(assertion.RawAuthenticatorData!),
                    ["clientDataJSON"] = ToBase64Url(assertion.RawClientDataJson),
                    ["signature"] = ToBase64Url(assertion.Signature!),
                    ["userHandle"] = assertion.UserId is { } userId ? ToBase64Url(userId) : null,
                },
                ["clientExtensionResults"] = new JsonObject(),
            };
            return result.ToJsonString();
        }
        catch (Exception ex)
        {
            LogDiag($"GetAssertionAsync failed: {ex}");
            return null;
        }
    }

    private static async Task<ASAuthorization?> PerformRequestAsync(ASAuthorizationRequest request)
    {
        var callback = new AuthorizationCallback();
        var controller = new ASAuthorizationController(new[] { request })
        {
            Delegate = callback,
            PresentationContextProvider = callback,
        };
        LogDiag("controller.PerformRequests is being called on the main thread");
        MainThread.BeginInvokeOnMainThread(controller.PerformRequests);

        // 60s timeout: without this, a hang in a delegate that never fires (e.g. because
        // iOS hasn't finished Associated Domain verification yet) would freeze the sign-in
        // forever in a loading state, with no error message at all - same pattern as
        // ApnsTokenStore.GetTokenAsync.
        var completed = await Task.WhenAny(callback.Completion.Task, Task.Delay(TimeSpan.FromSeconds(60)));
        if (completed != callback.Completion.Task)
        {
            LogDiag("PerformRequestAsync: timeout after 60s - no delegate callback received");
            return null;
        }
        LogDiag("Delegate callback received");
        return await callback.Completion.Task;
    }

    // In the binding, ...UserVerificationPreference is a static class with NSString constants.
    private static NSString MapUserVerification(string? value)
        => value switch
        {
            "required" => ASAuthorizationPublicKeyCredentialUserVerificationPreference.Required,
            "discouraged" => ASAuthorizationPublicKeyCredentialUserVerificationPreference.Discouraged,
            _ => ASAuthorizationPublicKeyCredentialUserVerificationPreference.Preferred,
        };

    private static byte[] FromBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 += (base64.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(base64);
    }

    private static string ToBase64Url(NSData data)
        => Convert.ToBase64String(data.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Delegate + presentation anchor in one: cancellation and error both end up as
    /// null (the auth pages then show the same message as on browser cancellation).</summary>
    private sealed class AuthorizationCallback : NSObject,
        IASAuthorizationControllerDelegate, IASAuthorizationControllerPresentationContextProviding
    {
        public TaskCompletionSource<ASAuthorization?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        [Export("authorizationController:didCompleteWithAuthorization:")]
        public void DidComplete(ASAuthorizationController controller, ASAuthorization authorization)
            => Completion.TrySetResult(authorization);

        [Export("authorizationController:didCompleteWithError:")]
        public void DidComplete(ASAuthorizationController controller, NSError error)
        {
            LogDiag($"ASAuthorization error: domain={error.Domain} code={error.Code} description={error.LocalizedDescription}");
            Completion.TrySetResult(null);
        }

        public UIWindow GetPresentationAnchor(ASAuthorizationController controller)
            => UIApplication.SharedApplication.ConnectedScenes
                   .OfType<UIWindowScene>()
                   .SelectMany(scene => scene.Windows)
                   .FirstOrDefault(window => window.IsKeyWindow)
               ?? UIApplication.SharedApplication.ConnectedScenes
                   .OfType<UIWindowScene>()
                   .SelectMany(scene => scene.Windows)
                   .First();
    }
}
#endif
