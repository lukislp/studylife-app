using Android.App;
using Android.Content;
using Android.Content.PM;

namespace StudyLife.App;

/// <summary>
/// Catches the studylife://auth redirect from the login/register/link page and hands it back
/// to MAUI's WebAuthenticator (standard pattern from the MAUI docs). The quick action intents
/// do use the same scheme (studylife://shortcut/...), but they go as explicit intents
/// directly to the MainActivity - this filter never sees them.
/// </summary>
[Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
[IntentFilter(new[] { Intent.ActionView },
              Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
              DataScheme = "studylife",
              DataHost = "auth")]
public class WebAuthenticationCallbackActivity : Microsoft.Maui.Authentication.WebAuthenticatorCallbackActivity
{
}
