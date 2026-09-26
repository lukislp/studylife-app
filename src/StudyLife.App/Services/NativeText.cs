using Microsoft.JSInterop;

namespace StudyLife.App.Services;

/// <summary>
/// Shared "read the app's current language from outside a Blazor component" helper - the same
/// need NativeAppAuth.cs's LoopbackDoneTranslations already solved once, factored out because
/// StreakMilestones/SpotlightIndexer/AppRoot's stand-up nudge all build native
/// notifications/Spotlight text the same way. See NativeAppAuth.cs's comment for why this reads
/// localStorage directly instead of going through Toolbelt.Blazor.I18nText's own API (that one
/// needs a ComponentBase owner for its lifecycle tracking; static service classes aren't one).
/// </summary>
internal static class NativeText
{
    public static async Task<string> GetCurrentLanguageAsync(IJSRuntime jsRuntime)
    {
        try
        {
            var lang = await jsRuntime.InvokeAsync<string?>("localStorage.getItem", "Toolbelt.Blazor.I18nText.CurrentLanguage");
            return string.IsNullOrEmpty(lang) ? "en" : lang;
        }
        catch { return "en"; }
    }

    public static string Get(Dictionary<string, string> table, string language) =>
        table.TryGetValue(language, out var text) ? text : table["en"];
}
