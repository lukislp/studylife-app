using System.Globalization;
#if IOS || MACCATALYST
using Foundation;
#endif
#if ANDROID
using Android.Content;
using Android.OS;
#endif

namespace StudyLife.App.Services;

/// <summary>
/// The app's language is the operating system's language setting for the app (owner decision 2026-11-14:
/// no picker inside the app). This reads that setting and opens it; which supported language it maps to is
/// decided by the shared rule (StudyLife.Client.Services.UiLanguage.FromSystem), not here.
/// <list type="bullet">
/// <item>iOS, iPadOS, Mac Catalyst: the bundle's preferred localizations (Settings, StudyLife, Language;
/// the list of languages comes from CFBundleLocalizations in Info.plist).</item>
/// <item>Android 13+: the per-app language (LocaleManager, declared in res/xml/locales_config.xml), else
/// the system languages.</item>
/// <item>Windows: the user's display language.</item>
/// </list>
/// </summary>
public static class SystemAppLanguage
{
    /// <summary>The language tags the system gives this app, in its order of preference.</summary>
    public static IReadOnlyList<string> Get()
    {
#if IOS || MACCATALYST
        var preferred = NSBundle.MainBundle.PreferredLocalizations;
        if (preferred is { Length: > 0 })
            return preferred;
        return NSLocale.PreferredLanguages;
#elif ANDROID
        LocaleList? locales = null;
        if (OperatingSystem.IsAndroidVersionAtLeast(33)
            && Platform.AppContext.GetSystemService(Context.LocaleService) is Android.App.LocaleManager manager)
            locales = manager.ApplicationLocales;
        if (locales is null || locales.IsEmpty)
            locales = LocaleList.Default;
        return (locales.ToLanguageTags() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
#else
        return [CultureInfo.CurrentUICulture.Name];
#endif
    }

    /// <summary>Opens the system's language setting for this app, or its settings page where the system has
    /// no per-app language page.</summary>
    public static Task OpenSettingsAsync()
    {
#if ANDROID
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            var intent = new Intent(Android.Provider.Settings.ActionAppLocaleSettings,
                Android.Net.Uri.Parse("package:" + Platform.AppContext.PackageName));
            intent.AddFlags(ActivityFlags.NewTask);
            Platform.AppContext.StartActivity(intent);
            return Task.CompletedTask;
        }
#endif
#if WINDOWS
        return Launcher.Default.OpenAsync(new Uri("ms-settings:regionlanguage"));
#else
        // iOS/iPadOS (the app's page, which has the Language entry), Mac Catalyst, Android before 13.
        AppInfo.Current.ShowSettingsUI();
        return Task.CompletedTask;
#endif
    }
}
