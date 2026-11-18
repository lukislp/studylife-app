namespace StudyLife.App.Core;

/// <summary>
/// The imprint shown next to the privacy policy on the first-run screen. The client's own link component
/// (LegalLinkAnchors, which also honours an operator's address) only exists in newer client releases;
/// until the pin has it this carries the publisher's imprint and the label for the 26 UI languages
/// (the same wording as the client's AboutText "Imprint").
/// </summary>
public static class ImprintLink
{
    public const string Url = "https://lktec.org/impressum";

    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bg"] = "Правна информация",
        ["cs"] = "Právní informace",
        ["da"] = "Juridisk meddelelse",
        ["de"] = "Impressum",
        ["el"] = "Νομικές πληροφορίες",
        ["en"] = "Imprint",
        ["es"] = "Aviso legal",
        ["et"] = "Õiguslik teave",
        ["fi"] = "Oikeudellinen huomautus",
        ["fr"] = "Mentions légales",
        ["ga"] = "Fógra dlíthiúil",
        ["hr"] = "Pravna obavijest",
        ["hu"] = "Impresszum",
        ["it"] = "Note legali",
        ["lt"] = "Teisinė informacija",
        ["lv"] = "Juridiskā informācija",
        ["mt"] = "Avviż legali",
        ["nl"] = "Colofon",
        ["pl"] = "Nota prawna",
        ["pt"] = "Aviso legal",
        ["ro"] = "Informații legale",
        ["ru"] = "Правовая информация",
        ["sk"] = "Právne informácie",
        ["sl"] = "Pravno obvestilo",
        ["sv"] = "Juridisk information",
        ["uk"] = "Правова інформація",
    };

    /// <summary>The label for a UI language tag such as "de" or "pt-BR"; English for anything else.</summary>
    public static string Label(string? language)
    {
        var primary = (language ?? "").Split('-', '_')[0];
        return Labels.TryGetValue(primary, out var label) ? label : Labels["en"];
    }
}
