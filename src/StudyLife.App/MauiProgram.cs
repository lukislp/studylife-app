using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
using StudyLife.App.Services;
using StudyLife.Client.Services;
using Toolbelt.Blazor.Extensions.DependencyInjection;
using Toolbelt.Blazor.I18nText;

namespace StudyLife.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddMauiBlazorWebView();

#if IOS
        // Drain the share extension's share-menu inbox on foreground transition (the
        // extension can't notify the app itself - it only writes the app group file).
        // MAUI lifecycle instead of an export selector, to avoid colliding with the
        // delegate methods MauiUIApplicationDelegate already implements.
        builder.ConfigureLifecycleEvents(events =>
            events.AddiOS(ios => ios.WillEnterForeground(_ => SharedNoteIntake.DrainIosInbox())));
#endif

#if IOS || MACCATALYST
        // Safe-area behavior matching the installed PWA: the WKWebView ScrollView's automatic
        // content inset would push the fixed bottom navbar up by the home indicator zone,
        // hiding the last list item. Insets are disabled here AND at the page level
        // (SafeAreaEdges="None" in MainPage.xaml, was "Container" until it left a visible gap
        // under the bottom navbar) - the WebView now gets the full physical screen, same as the
        // PWA in standalone mode, and handles every safe-area edge itself via CSS env(safe-area-inset-*)
        // (see --safe-top/--safe-bottom in wwwroot/css/base.css).
        // While we're here: remember the WebView reference for the native print dialog (NativeBridge.NativePrint).
        Microsoft.AspNetCore.Components.WebView.Maui.BlazorWebViewHandler.BlazorWebViewMapper
            .AppendToMapping("StudyLifeSafeArea", (handler, view) =>
            {
                handler.PlatformView.ScrollView.ContentInsetAdjustmentBehavior =
                    UIKit.UIScrollViewContentInsetAdjustmentBehavior.Never;
                NativeBridge.SetWebView(handler.PlatformView);
            });
#endif
#if ANDROID
        // Remember the WebView reference for the native print dialog (PrintManager).
        Microsoft.AspNetCore.Components.WebView.Maui.BlazorWebViewHandler.BlazorWebViewMapper
            .AppendToMapping("StudyLifePrint", (handler, view) =>
                NativeBridge.SetWebView(handler.PlatformView));
#endif

        // Service registration mirrors StudyLife.Client/Program.cs (WASM) - same services,
        // same lifetimes. Only difference: the BaseAddress doesn't come from the browser host,
        // but from the first-run dialog (ServerUrlStore/Preferences).
        builder.Services.AddI18nText(options => options.PersistenceLevel = PersistanceLevel.SessionAndLocal);

        builder.Services.AddSingleton<ServerUrlStore>();
        builder.Services.AddSingleton<DeepLinkService>();
        builder.Services.AddScoped<INativeAppAuth, NativeAppAuth>();
        builder.Services.AddScoped<INativePush, NativePush>();
        builder.Services.AddScoped<INativeIcsIntake, NativeIcsIntake>();
        builder.Services.AddScoped<INativeFileExport, NativeFileExport>();
        builder.Services.AddScoped<INativeHealthData, NativeHealthData>();
        builder.Services.AddScoped<SessionTokenStore>();
        builder.Services.AddScoped(sp =>
        {
            // As long as the server isn't configured yet, AppRoot doesn't render the client
            // app - so this HttpClient is only created AFTER ServerUrlStore.Save() (scoped =
            // lazy on first injection). The .invalid address is just a placeholder that is
            // never actually requested.
            var baseUri = sp.GetRequiredService<ServerUrlStore>().BaseUri
                ?? new Uri("https://unconfigured.invalid/");
            var handler = new SessionHandler(sp.GetRequiredService<SessionTokenStore>(), baseUri)
            {
                InnerHandler = new HttpClientHandler()
            };
            return new HttpClient(handler) { BaseAddress = baseUri };
        });
        builder.Services.AddScoped<AppStateService>();
        builder.Services.AddScoped<TimerService>();
        builder.Services.AddScoped<NotificationService>();

        // Missing here until now (StudyLife.Client/Program.cs's WASM registration was never
        // mirrored over) - MarketplaceBrowserModal.razor @injects this, and DI throws resolving
        // it before the component's own OnInitializedAsync/try-catch ever runs, so the Setup
        // page's Marketplace card silently did nothing on tap: no exception surfaced to the
        // user, just DiagLoggerProvider swallowing it below.
        builder.Services.AddHttpClient<MarketplaceClient>(client =>
        {
            client.BaseAddress = new Uri("https://api.github.com/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("StudyLife-Marketplace-Client");
        });

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif
        // Also active in release builds: catches, among other things, Blazor's own "Unhandled
        // exception rendering component..." (Renderer.HandleException), which otherwise ends up
        // nowhere visible before the "An unhandled error has occurred" banner appears.
        builder.Logging.AddProvider(new DiagLoggerProvider());

        return builder.Build();
    }
}
