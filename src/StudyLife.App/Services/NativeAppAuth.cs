using StudyLife.App.Core;
using StudyLife.Client.Services;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
#if WINDOWS
using System.Net;
using System.Net.Sockets;
using Microsoft.JSInterop;
#endif

namespace StudyLife.App.Services;

/// <summary>
/// Native implementation of INativeAppAuth: opens the existing login/register/link page
/// with ?app=1 in the system browser - there WebAuthn works fully against the real
/// server domain (never in the BlazorWebView itself: its origin is the virtual host
/// https://0.0.0.0, which matches no rpId). Return channel per platform:
/// - iOS/Android/MacCatalyst: WebAuthenticator (ASWebAuthenticationSession/Custom Tabs)
///   with the studylife://auth custom scheme.
/// - Windows: default browser + loopback HTTP listener (RFC 8252 pattern) - unpackaged
///   Windows apps can't register custom schemes; the auth page gets the loopback URL
///   via ?appret=... (client validates strictly against 127.0.0.1/localhost).
/// The page hands the sign-in back only as a PKCE code bound to this request's challenge, and with
/// this request's random state (AppSignInRequest, 2026-11-16 audit M2): a redirect without the
/// state, or with a raw token, is refused, so another app firing studylife://auth or a web page
/// hitting the loopback port cannot sign the app into a foreign account.
/// </summary>
public sealed class NativeAppAuth : INativeAppAuth
{
    public const string CallbackScheme = "studylife";

    private readonly ServerUrlStore _urls;
    private readonly ServerSwitcher _switcher;
    private readonly Toolbelt.Blazor.I18nText.I18nText _i18n;
#if WINDOWS
    private readonly IJSRuntime _js;

    public NativeAppAuth(ServerUrlStore urls, ServerSwitcher switcher, Toolbelt.Blazor.I18nText.I18nText i18n, IJSRuntime js)
    {
        _urls = urls;
        _switcher = switcher;
        _i18n = i18n;
        _js = js;
    }
#else
    public NativeAppAuth(ServerUrlStore urls, ServerSwitcher switcher, Toolbelt.Blazor.I18nText.I18nText i18n)
    {
        _urls = urls;
        _switcher = switcher;
        _i18n = i18n;
    }
#endif

    /// <summary>"&amp;applang=xx": the auth page in the system browser speaks the language the app shows
    /// (AppReturnContext.Language in the client) instead of whatever that browser stored last.</summary>
    private async Task<string> LanguageQueryAsync() =>
        "&applang=" + Uri.EscapeDataString(await _i18n.GetCurrentLanguageAsync());

    /// <summary>Deliberately hardcoded to false, even though AppleSigningInfo.HasAssociatedDomains
    /// returns true with paid signing: the entitlement alone isn't enough to make the native
    /// in-app passkey dialog work - since iOS 14, iOS's swcd loads the AASA file
    /// exclusively via Apple's own CDN (app-site-association.cdn-apple.com), which in turn
    /// has to be populated by Apple's crawler from the public internet. The server here is
    /// deliberately reachable only internally, so the CDN can never verify the domain -
    /// the attempt fails deterministically with ASAuthorizationError code=1004.
    /// That's why the system browser flow (AuthenticateAsync) remains the only passkey path,
    /// regardless of signing status. If the server ever becomes publicly reachable,
    /// switch this back to AppleSigningInfo.HasAssociatedDomains.</summary>
    public bool SupportsInAppPasskeys => false;

    public Task<string?> CreatePasskeyAsync(string optionsJson)
    {
#if IOS
        return AppleInAppPasskeys.CreateAsync(optionsJson);
#else
        return Task.FromResult<string?>(null);
#endif
    }

    public Task<string?> GetPasskeyAssertionAsync(string optionsJson)
    {
#if IOS
        return AppleInAppPasskeys.GetAssertionAsync(optionsJson);
#else
        return Task.FromResult<string?>(null);
#endif
    }

    /// <summary>The account was deleted on the server (Setup, "Delete account"): wipe the copies this
    /// app keeps outside the web view - widget snapshot (and its Watch/Live Activity relatives) and the
    /// Spotlight index. The session token and the offline caches are already gone (the client clears
    /// them the way a logout does, which also wipes these copies now); the server URL preference stays, it
    /// belongs to the device, not the account. Kept as an explicit call so a deletion wipes even if the
    /// logout hook is not wired.</summary>
    public Task AccountDeletedAsync() => NativeLocalCopies.WipeAsync();

    /// <summary>Host of the connected server, shown in Setup and next to the sign-in screen.</summary>
    public string? ServerHost => _urls.BaseUri is { } uri ? ServerAddress.DisplayHost(uri) : null;

    /// <summary>The user confirmed leaving this server: one hand-over sequence (see ServerSwitcher).</summary>
    public Task ChangeServerAsync() => _switcher.ChangeAsync();

    public bool IsAvailable => _urls.IsConfigured
        && (DeviceInfo.Platform == DevicePlatform.iOS
            || DeviceInfo.Platform == DevicePlatform.Android
            || DeviceInfo.Platform == DevicePlatform.MacCatalyst
            || DeviceInfo.Platform == DevicePlatform.WinUI);

    public async Task<string?> AuthenticateAsync(string startPage)
    {
        // ?app=1 must go BEFORE any fragment ("register#name=..." => "register?app=1#name=...");
        // the fragment carries pre-fill values without ending up in server logs.
        var path = startPage;
        var fragment = "";
        var hashIndex = startPage.IndexOf('#');
        if (hashIndex >= 0)
        {
            path = startPage[..hashIndex];
            fragment = startPage[hashIndex..];
        }

        var language = await LanguageQueryAsync();
#if WINDOWS
        return await AuthenticateViaLoopbackAsync(path, language, fragment);
#else
        var request = AppSignInRequest.Create();
        var startUrl = new Uri(_urls.BaseUri!, $"{path}?{request.StartQuery()}{language}{fragment}");
        try
        {
            var result = await WebAuthenticator.Default.AuthenticateAsync(
                startUrl, new Uri($"{CallbackScheme}://auth"));
            return await CompleteAsync(request, result.Properties);
        }
        catch (TaskCanceledException)
        {
            return null; // user closed the browser sheet
        }
#endif
    }

    /// <summary>The redirect back, checked by the request (state, code) and redeemed: the session
    /// token, "" for a finished device link, null for anything else (cancelled, forged, failed).</summary>
    private async Task<string?> CompleteAsync(AppSignInRequest request, IReadOnlyDictionary<string, string> parameters)
    {
        var result = request.Interpret(parameters);
        return result.Outcome switch
        {
            AppSignInOutcome.Code => await ExchangeCodeAsync(result.Code!, request.CodeVerifier),
            AppSignInOutcome.Linked => "",
            _ => null,
        };
    }

    /// <summary>Redeems a handoff code for the real session token (POST api/auth/exchange).
    /// Non-2xx/exception/empty token all collapse to null, same "cancelled or failed" convention
    /// the callers already use for the WebAuthenticator/loopback cases.</summary>
    private async Task<string?> ExchangeCodeAsync(string code, string codeVerifier)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var response = await http.PostAsJsonAsync(
                new Uri(_urls.BaseUri!, "api/auth/exchange"),
                new { code, codeVerifier });
            if (!response.IsSuccessStatusCode)
                return null;

            var result = await response.Content.ReadFromJsonAsync<ExchangeResponse>(
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return result?.Token is { Length: > 0 } token ? token : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed class ExchangeResponse
    {
        public string? Token { get; set; }
    }

#if WINDOWS
    private async Task<string?> AuthenticateViaLoopbackAsync(string path, string language, string fragment)
    {
        // Loopback listener on a free port; the auth page redirects to
        // http://127.0.0.1:{port}/?code=...&state=... once done. Loopback prefixes need no
        // URL ACL/admin rights.
        var port = GetFreeLoopbackPort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var request = AppSignInRequest.Create();
        var returnUrl = $"http://127.0.0.1:{port}/";
        var startUrl = new Uri(_urls.BaseUri!, $"{path}?{request.StartQuery(returnUrl)}{language}{fragment}");
        await Launcher.Default.OpenAsync(startUrl);

        // 5 minutes is generous for the passkey dialog + a possible cross-device QR scan.
        var contextTask = listener.GetContextAsync();
        var finished = await Task.WhenAny(contextTask, Task.Delay(TimeSpan.FromMinutes(5)));
        if (finished != contextTask)
            return null;

        var context = await contextTask;
        var query = context.Request.QueryString;
        var parameters = query.AllKeys.Where(k => k is not null)
            .ToDictionary(k => k!, k => query[k] ?? "");

        // Localized instead of a hardcoded bilingual string. Toolbelt.Blazor.I18nText's
        // GetTextTableAsync needs a ComponentBase owner for its lifecycle tracking, which
        // this plain service class isn't - so this reads the SAME stored-language key the
        // library itself uses (see AppRoot.razor's BootAsync) via plain JS interop and picks
        // from a small embedded table instead of going through the component-bound API.
        var lang = await _js.InvokeAsync<string?>("localStorage.getItem", "Toolbelt.Blazor.I18nText.CurrentLanguage");
        var doneText = LoopbackDoneTranslations.TryGetValue(lang ?? "en", out var text)
            ? text : LoopbackDoneTranslations["en"];

        var html = "<!DOCTYPE html><html lang=\"" + (lang ?? "en") + "\"><meta charset=\"utf-8\">" +
            "<body style=\"font-family:sans-serif;background:#0e0e0f;color:#e8e6e0;" +
            "display:flex;align-items:center;justify-content:center;min-height:100vh\">" +
            "<div style=\"text-align:center\"><div style=\"font-size:2rem;color:#CC785C\">✦</div>" +
            $"<p>{System.Net.WebUtility.HtmlEncode(doneText)}</p></div></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();

        // Same checks as the mobile branch: this request's state, then the code.
        return await CompleteAsync(request, parameters);
    }

    private static int GetFreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>Same 26 languages as the app's i18ntext tables, kept here as a small embedded
    /// dictionary instead of a formal i18n table because this one string is built outside any
    /// Blazor component (see the comment above where this is used).</summary>
    private static readonly Dictionary<string, string> LoopbackDoneTranslations = new()
    {
        ["en"] = "Done — close this window and return to the StudyAtrium app.",
        ["de"] = "Fertig — du kannst dieses Fenster schließen und zurück zur StudyAtrium-App wechseln.",
        ["bg"] = "Готово — можеш да затвориш този прозорец и да се върнеш в приложението StudyAtrium.",
        ["cs"] = "Hotovo — toto okno můžeš zavřít a vrátit se do aplikace StudyAtrium.",
        ["da"] = "Færdig — du kan lukke dette vindue og vende tilbage til StudyAtrium-appen.",
        ["el"] = "Έτοιμο — μπορείς να κλείσεις αυτό το παράθυρο και να επιστρέψεις στην εφαρμογή StudyAtrium.",
        ["es"] = "Listo — puedes cerrar esta ventana y volver a la app de StudyAtrium.",
        ["et"] = "Valmis — võid selle akna sulgeda ja naasta StudyAtrium rakendusse.",
        ["fi"] = "Valmis — voit sulkea tämän ikkunan ja palata StudyAtrium-sovellukseen.",
        ["fr"] = "Terminé — tu peux fermer cette fenêtre et retourner à l'application StudyAtrium.",
        ["ga"] = "Críochnaithe — is féidir leat an fhuinneog seo a dhúnadh agus filleadh ar aip StudyAtrium.",
        ["hr"] = "Gotovo — možeš zatvoriti ovaj prozor i vratiti se u aplikaciju StudyAtrium.",
        ["hu"] = "Kész — bezárhatod ezt az ablakot, és visszatérhetsz a StudyAtrium alkalmazásba.",
        ["it"] = "Fatto — puoi chiudere questa finestra e tornare all'app StudyAtrium.",
        ["lt"] = "Baigta — gali uždaryti šį langą ir grįžti į „StudyAtrium“ programėlę.",
        ["lv"] = "Gatavs — vari aizvērt šo logu un atgriezties StudyAtrium lietotnē.",
        ["mt"] = "Lest — tista' tagħlaq din it-tieqa u terġa' lura għall-app StudyAtrium.",
        ["nl"] = "Klaar — je kunt dit venster sluiten en teruggaan naar de StudyAtrium-app.",
        ["pl"] = "Gotowe — możesz zamknąć to okno i wrócić do aplikacji StudyAtrium.",
        ["pt"] = "Pronto — podes fechar esta janela e voltar à app StudyAtrium.",
        ["ro"] = "Gata — poți închide această fereastră și reveni la aplicația StudyAtrium.",
        ["ru"] = "Готово — можешь закрыть это окно и вернуться в приложение StudyAtrium.",
        ["sk"] = "Hotovo — toto okno môžeš zavrieť a vrátiť sa do aplikácie StudyAtrium.",
        ["sl"] = "Končano — to okno lahko zapreš in se vrneš v aplikacijo StudyAtrium.",
        ["sv"] = "Klart — du kan stänga det här fönstret och gå tillbaka till StudyAtrium-appen.",
        ["uk"] = "Готово — можеш закрити це вікно і повернутися до застосунку StudyAtrium.",
    };
#endif
}
