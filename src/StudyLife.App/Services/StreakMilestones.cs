using Microsoft.JSInterop;
using Microsoft.Maui.Storage;

namespace StudyLife.App.Services;

/// <summary>
/// One-off celebration notification when the study streak crosses a milestone. High-water-mark
/// semantics (like an achievement unlock): once a milestone has been celebrated it never fires
/// again, even if the streak later resets and climbs back through it - matches how e.g.
/// Duolingo badges behave, and avoids re-celebrating on every single HomeWidgetSnapshot update.
/// </summary>
public static class StreakMilestones
{
    private static readonly int[] Milestones = { 3, 7, 14, 30, 50, 100, 180, 365 };
    private const string LastCelebratedKey = "LastCelebratedStreakMilestone";

    // Same 26 languages as the app's i18ntext tables - see NativeText's doc comment for why
    // this is a small embedded table instead of a formal i18n table.
    private static readonly Dictionary<string, string> TitleTemplates = new()
    {
        ["bg"] = "🔥 {0} дни серия!",
        ["cs"] = "🔥 {0} dní v řadě!",
        ["da"] = "🔥 {0} dages stime!",
        ["de"] = "🔥 {0} Tage Lernserie!",
        ["el"] = "🔥 Σερί {0} ημερών!",
        ["en"] = "🔥 {0}-day study streak!",
        ["es"] = "🔥 ¡Racha de {0} días!",
        ["et"] = "🔥 {0}-päevane seeria!",
        ["fi"] = "🔥 {0} päivän putki!",
        ["fr"] = "🔥 Série de {0} jours !",
        ["ga"] = "🔥 Sraith {0} lá!",
        ["hr"] = "🔥 Niz od {0} dana!",
        ["hu"] = "🔥 {0} napos sorozat!",
        ["it"] = "🔥 Serie di {0} giorni!",
        ["lt"] = "🔥 {0} dienų serija!",
        ["lv"] = "🔥 {0} dienu sērija!",
        ["mt"] = "🔥 Serje ta' {0} jum!",
        ["nl"] = "🔥 {0}-daagse reeks!",
        ["pl"] = "🔥 Seria {0} dni!",
        ["pt"] = "🔥 Sequência de {0} dias!",
        ["ro"] = "🔥 Serie de {0} zile!",
        ["ru"] = "🔥 Серия {0} дней!",
        ["sk"] = "🔥 Séria {0} dní!",
        ["sl"] = "🔥 Niz {0} dni!",
        ["sv"] = "🔥 {0} dagars svit!",
        ["uk"] = "🔥 Серія {0} днів!",
    };

    private static readonly Dictionary<string, string> BodyTemplates = new()
    {
        ["bg"] = "Постигна серия от {0} дни в StudyLife. Продължавай така!",
        ["cs"] = "Dosáhl jsi série {0} dní ve StudyLife. Tak dál!",
        ["da"] = "Du har nået en {0}-dages stime i StudyLife. Bliv ved!",
        ["de"] = "Du hast eine {0}-Tage-Serie in StudyLife erreicht. Weiter so!",
        ["el"] = "Πέτυχες σερί {0} ημερών στο StudyLife. Συνέχισε έτσι!",
        ["en"] = "You've reached a {0}-day streak in StudyLife. Keep it up!",
        ["es"] = "Has alcanzado una racha de {0} días en StudyLife. ¡Sigue así!",
        ["et"] = "Saavutasid StudyLife'is {0}-päevase seeria. Jätka samas vaimus!",
        ["fi"] = "Saavutit {0} päivän putken StudyLifessa. Jatka samaan malliin!",
        ["fr"] = "Tu as atteint une série de {0} jours dans StudyLife. Continue comme ça !",
        ["ga"] = "Bhain tú amach sraith {0} lá in StudyLife. Coinnigh ort!",
        ["hr"] = "Postigao si niz od {0} dana u StudyLifeu. Samo tako nastavi!",
        ["hu"] = "Elérted a {0} napos sorozatot a StudyLife-ban. Így tovább!",
        ["it"] = "Hai raggiunto una serie di {0} giorni in StudyLife. Continua così!",
        ["lt"] = "Pasiekei {0} dienų seriją StudyLife. Taip ir toliau!",
        ["lv"] = "Tu esi sasniedzis {0} dienu sēriju StudyLife. Turpini tāpat!",
        ["mt"] = "Laħaqt serje ta' {0} jum fi StudyLife. Ibqa' sejjer hekk!",
        ["nl"] = "Je hebt een reeks van {0} dagen bereikt in StudyLife. Ga zo door!",
        ["pl"] = "Osiągnąłeś serię {0} dni w StudyLife. Tak trzymaj!",
        ["pt"] = "Alcançaste uma sequência de {0} dias no StudyLife. Continua assim!",
        ["ro"] = "Ai atins o serie de {0} zile în StudyLife. Așa continuă!",
        ["ru"] = "Вы достигли серии в {0} дней в StudyLife. Продолжайте в том же духе!",
        ["sk"] = "Dosiahol si sériu {0} dní v StudyLife. Tak ďalej!",
        ["sl"] = "Dosegel si niz {0} dni v StudyLife. Tako naprej!",
        ["sv"] = "Du har nått en svit på {0} dagar i StudyLife. Fortsätt så!",
        ["uk"] = "Ти досяг серії {0} днів у StudyLife. Так тримати!",
    };

    public static async Task CheckAndCelebrateAsync(int streakDays, IJSRuntime jsRuntime)
    {
        try
        {
            var last = Preferences.Default.Get(LastCelebratedKey, 0);
            var reached = Milestones.Where(m => m <= streakDays && m > last).OrderByDescending(m => m).FirstOrDefault();
            if (reached == 0) return;

            Preferences.Default.Set(LastCelebratedKey, reached);
            var lang = await NativeText.GetCurrentLanguageAsync(jsRuntime);
            await NativeBridge.ShowNotificationAsync(
                string.Format(NativeText.Get(TitleTemplates, lang), reached),
                string.Format(NativeText.Get(BodyTemplates, lang), reached));
        }
        catch { /* best effort - a missed celebration must never break the widget update */ }
    }
}
