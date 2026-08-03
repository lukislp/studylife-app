#if IOS
using Foundation;
#endif

namespace StudyLife.App.Services;

/// <summary>
/// Detects at runtime whether the app was signed with a profile that contains the
/// Associated Domains entitlement (webcredentials:...) - that only exists in the
/// PAID Apple Developer Program; free-account ("Personal Team") profiles can never have it.
/// The native in-app passkey dialog (ASAuthorizationPlatformPublicKeyCredential) requires
/// exactly this entitlement - as long as it's missing, the system browser detour
/// (NativeAppAuth) remains the only working passkey path.
///
/// Mechanics: dev/ad-hoc/sideload builds carry their provisioning profile as
/// embedded.mobileprovision in the bundle (a CMS-signed plist); we search it for the
/// entitlement markers. App Store/TestFlight builds have no embedded profile - that case is
/// irrelevant here, because an App Store build requires a paid account anyway.
/// For Associated Domains, the mere presence of the key as a marker is enough (not its value) -
/// Apple never embeds the actual domain in the downloaded profile, it always just contains
/// the placeholder "*" (the real domain is only added during local codesigning, see
/// sign-and-install.sh); a free/Personal Team profile can never contain the key at all.
/// </summary>
public static class AppleSigningInfo
{
#if IOS
    private static bool? _hasAssociatedDomains;
    private static bool? _hasPushEntitlement;
#endif

    /// <summary>True = signed with the Associated Domains entitlement (only possible with a
    /// paid developer account); false = free-account signing or a non-Apple platform.</summary>
    public static bool HasAssociatedDomains
    {
        get
        {
#if IOS
            // Only check for the key's presence, NOT for "webcredentials:" in its value:
            // Apple never embeds the real domain in the downloaded profile (it always just
            // contains the placeholder "*", regardless of which domain the app was actually
            // signed with - see sign-and-install.sh) - a marker search for "webcredentials:"
            // would NEVER match here. The mere presence is enough as a signal: a
            // free/Personal Team profile can never obtain this capability.
            return _hasAssociatedDomains ??= ProfileContainsMarkers("com.apple.developer.associated-domains");
#else
            return false;
#endif
        }
    }

    /// <summary>True = signed with the aps-environment entitlement (remote push/APNs; also
    /// only possible with a paid developer account) - unlocks the app's APNs channel
    /// (NativePush); free-signed builds don't even attempt registration.</summary>
    public static bool HasPushEntitlement
    {
        get
        {
#if IOS
            return _hasPushEntitlement ??= ProfileContainsMarkers("aps-environment");
#else
            return false;
#endif
        }
    }

#if IOS
    private static bool ProfileContainsMarkers(params string[] markers)
    {
        try
        {
            var path = NSBundle.MainBundle.PathForResource("embedded", "mobileprovision");
            if (path == null)
                return false;

            // Latin1 = lossless byte-to-char mapping; the entitlements plist sits inside the
            // CMS container as plaintext XML, so a marker search is enough.
            var raw = File.ReadAllText(path, System.Text.Encoding.Latin1);
            return markers.All(raw.Contains);
        }
        catch
        {
            return false; // when in doubt: treat like a free account (web/browser paths always work)
        }
    }
#endif
}
