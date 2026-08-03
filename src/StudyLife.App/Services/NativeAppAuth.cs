using StudyLife.Client.Services;
using System.Net.Http.Json;
using System.Security.Cryptography;
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
/// No server-side special case: the token comes from the same login/complete resp.
/// register/complete response as on the web.
/// </summary>
public sealed class NativeAppAuth : INativeAppAuth
{
    public const string CallbackScheme = "studylife";

    private readonly ServerUrlStore _urls;
#if WINDOWS
    private readonly IJSRuntime _js;

    public NativeAppAuth(ServerUrlStore urls, IJSRuntime js)
    {
        _urls = urls;
        _js = js;
    }
#else
    public NativeAppAuth(ServerUrlStore urls) => _urls = urls;
#endif

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

#if WINDOWS
        return await AuthenticateViaLoopbackAsync(path, fragment);
#else
        var codeVerifier = GenerateCodeVerifier();
        var codeChallenge = ComputeCodeChallenge(codeVerifier);
        var startUrl = new Uri(_urls.BaseUri!,
            $"{path}?app=1&appchallenge={Uri.EscapeDataString(codeChallenge)}{fragment}");
        try
        {
            var result = await WebAuthenticator.Default.AuthenticateAsync(
                startUrl, new Uri($"{CallbackScheme}://auth"));

            // PKCE handoff (new server): the redirect only ever carries the opaque code, never
            // the bearer token itself, so this must be redeemed before it's usable. Old servers
            // still put the token directly in the redirect - kept as a fallback below.
            if (result.Properties.TryGetValue("code", out var code) && code is { Length: > 0 })
                return await ExchangeCodeAsync(code, codeVerifier);
            if (result.Properties.TryGetValue("token", out var token) && token is { Length: > 0 })
                return token;
            if (result.Properties.ContainsKey("linked"))
                return "";
            return null;
        }
        catch (TaskCanceledException)
        {
            return null; // user closed the browser sheet
        }
#endif
    }

    /// <summary>PKCE (RFC 7636): the code returned via the studylife:// / loopback redirect is
    /// useless to anything that intercepts it without this verifier, which never leaves this
    /// process's memory - only its SHA-256 (the challenge) is ever sent to the server.</summary>
    private static string GenerateCodeVerifier() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string ComputeCodeChallenge(string codeVerifier) =>
        Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

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
    private async Task<string?> AuthenticateViaLoopbackAsync(string path, string fragment)
    {
        // Loopback listener on a free port; the auth page redirects to
        // http://127.0.0.1:{port}/?token=... once done. Loopback prefixes need no
        // URL ACL/admin rights.
        var port = GetFreeLoopbackPort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var codeVerifier = GenerateCodeVerifier();
        var codeChallenge = ComputeCodeChallenge(codeVerifier);
        var returnUrl = $"http://127.0.0.1:{port}/";
        var startUrl = new Uri(_urls.BaseUri!,
            $"{path}?app=1&appret={Uri.EscapeDataString(returnUrl)}" +
            $"&appchallenge={Uri.EscapeDataString(codeChallenge)}{fragment}");
        await Launcher.Default.OpenAsync(startUrl);

        // 5 minutes is generous for the passkey dialog + a possible cross-device QR scan.
        var contextTask = listener.GetContextAsync();
        var finished = await Task.WhenAny(contextTask, Task.Delay(TimeSpan.FromMinutes(5)));
        if (finished != contextTask)
            return null;

        var context = await contextTask;
        var code = context.Request.QueryString["code"];
        var token = context.Request.QueryString["token"];
        var linked = context.Request.QueryString["linked"];

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

        // Same PKCE-first-then-legacy-token fallback as the mobile branch, see there.
        if (code is { Length: > 0 })
            return await ExchangeCodeAsync(code, codeVerifier);
        if (token is { Length: > 0 })
            return token;
        if (linked == "1")
            return "";
        return null;
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
        ["en"] = "Done — close this window and return to the StudyLife app.",
        ["de"] = "Fertig — du kannst dieses Fenster schließen und zurück zur StudyLife-App wechseln.",
        ["bg"] = "Готово — можеш да затвориш този прозорец и да се върнеш в приложението StudyLife.",
        ["cs"] = "Hotovo — toto okno můžeš zavřít a vrátit se do aplikace StudyLife.",
        ["da"] = "Færdig — du kan lukke dette vindue og vende tilbage til StudyLife-appen.",
        ["el"] = "Έτοιμο — μπορείς να κλείσεις αυτό το παράθυρο και να επιστρέψεις στην εφαρμογή StudyLife.",
        ["es"] = "Listo — puedes cerrar esta ventana y volver a la app de StudyLife.",
        ["et"] = "Valmis — võid selle akna sulgeda ja naasta StudyLife rakendusse.",
        ["fi"] = "Valmis — voit sulkea tämän ikkunan ja palata StudyLife-sovellukseen.",
        ["fr"] = "Terminé — tu peux fermer cette fenêtre et retourner à l'application StudyLife.",
        ["ga"] = "Críochnaithe — is féidir leat an fhuinneog seo a dhúnadh agus filleadh ar aip StudyLife.",
        ["hr"] = "Gotovo — možeš zatvoriti ovaj prozor i vratiti se u aplikaciju StudyLife.",
        ["hu"] = "Kész — bezárhatod ezt az ablakot, és visszatérhetsz a StudyLife alkalmazásba.",
        ["it"] = "Fatto — puoi chiudere questa finestra e tornare all'app StudyLife.",
        ["lt"] = "Baigta — gali uždaryti šį langą ir grįžti į „StudyLife“ programėlę.",
        ["lv"] = "Gatavs — vari aizvērt šo logu un atgriezties StudyLife lietotnē.",
        ["mt"] = "Lest — tista' tagħlaq din it-tieqa u terġa' lura għall-app StudyLife.",
        ["nl"] = "Klaar — je kunt dit venster sluiten en teruggaan naar de StudyLife-app.",
        ["pl"] = "Gotowe — możesz zamknąć to okno i wrócić do aplikacji StudyLife.",
        ["pt"] = "Pronto — podes fechar esta janela e voltar à app StudyLife.",
        ["ro"] = "Gata — poți închide această fereastră și reveni la aplicația StudyLife.",
        ["ru"] = "Готово — можешь закрыть это окно и вернуться в приложение StudyLife.",
        ["sk"] = "Hotovo — toto okno môžeš zavrieť a vrátiť sa do aplikácie StudyLife.",
        ["sl"] = "Končano — to okno lahko zapreš in se vrneš v aplikacijo StudyLife.",
        ["sv"] = "Klart — du kan stänga det här fönstret och gå tillbaka till StudyLife-appen.",
        ["uk"] = "Готово — можеш закрити це вікно і повернутися до застосунку StudyLife.",
    };
#endif
}
