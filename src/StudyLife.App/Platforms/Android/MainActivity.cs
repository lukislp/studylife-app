using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using StudyLife.App.Services;

namespace StudyLife.App;

// Pin the name explicitly so shortcuts.xml (static home screen quick actions) can
// target the activity deterministically via targetClass. LaunchMode=SingleTop so a
// shortcut ends up in OnNewIntent while the app is running, instead of starting a second instance.
[Activity(Name = "app.studylife.mobile.MainActivity",
          Theme = "@style/Maui.SplashTheme",
          MainLauncher = true,
          LaunchMode = LaunchMode.SingleTop,
          Exported = true,
          ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[MetaData("android.app.shortcuts", Resource = "@xml/shortcuts")]
// Share-to-StudyLife: shared text/links end up as a note (SharedNoteIntake → AppRoot).
[IntentFilter(new[] { Intent.ActionSend },
              Categories = new[] { Intent.CategoryDefault },
              DataMimeType = "text/plain")]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        DispatchShortcut(Intent);
        DispatchSharedText(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        DispatchShortcut(intent);
        DispatchSharedText(intent);
    }

    /// <summary>ACTION_SEND (text/plain) from the share menu: put the text into the intake -
    /// AppRoot turns it into a note and navigates to /notes.</summary>
    private static void DispatchSharedText(Intent? intent)
    {
        if (intent?.Action != Intent.ActionSend || intent.Type != "text/plain") return;
        var text = intent.GetStringExtra(Intent.ExtraText);
        var subject = intent.GetStringExtra(Intent.ExtraSubject);
        if (string.IsNullOrWhiteSpace(text)) return;
        SharedNoteIntake.SetPending(string.IsNullOrWhiteSpace(subject) ? text : $"{subject}\n{text}");
    }

    /// <summary>Quick action intents carry the Blazor route as a studylife://shortcut/... data URI.
    /// Cold start: DeepLinkService buffers it, AppRoot consumes it after boot.</summary>
    private static void DispatchShortcut(Intent? intent)
    {
        var data = intent?.Data;
        if (data == null || data.Scheme != "studylife" || data.Host != "shortcut") return;

        var route = data.Path switch
        {
            "/focus" => "/focus",
            "/newnote" => "/notes?new=1",
            "/calendar" => "/calendar",
            "/stats" => "/auswertung",
            "/weekplan" => "/weekplan",
            _ => null,
        };
        if (route == null) return;

        IPlatformApplication.Current?.Services.GetService<DeepLinkService>()?.Dispatch(route);
    }
}
