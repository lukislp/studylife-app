using System.Net.Http.Json;
using Microsoft.JSInterop;
using StudyLife.Shared;
#if IOS
using CoreSpotlight;
using Foundation;
#endif

namespace StudyLife.App.Services;

/// <summary>
/// iOS Spotlight index over notes and courses: makes them discoverable in system search,
/// tapping opens the app on the matching list (AppDelegate.ContinueUserActivity).
/// Refreshed after app boot and during background refresh; errors are silent (search is
/// a convenience feature, never a blocker). Complete no-op on other platforms.
/// </summary>
public static class SpotlightIndexer
{
    public const string NotePrefix = "note-";
    public const string CoursePrefix = "course-";

    // Same 26 languages as the app's i18ntext tables - see NativeText's doc comment.
    private static readonly Dictionary<string, string> NoteFallback = new()
    {
        ["bg"] = "Бележка",
        ["cs"] = "Poznámka",
        ["da"] = "Note",
        ["de"] = "Notiz",
        ["el"] = "Σημείωση",
        ["en"] = "Note",
        ["es"] = "Nota",
        ["et"] = "Märge",
        ["fi"] = "Muistiinpano",
        ["fr"] = "Note",
        ["ga"] = "Nóta",
        ["hr"] = "Bilješka",
        ["hu"] = "Jegyzet",
        ["it"] = "Nota",
        ["lt"] = "Užrašas",
        ["lv"] = "Piezīme",
        ["mt"] = "Nota",
        ["nl"] = "Notitie",
        ["pl"] = "Notatka",
        ["pt"] = "Nota",
        ["ro"] = "Notiță",
        ["ru"] = "Заметка",
        ["sk"] = "Poznámka",
        ["sl"] = "Zapisek",
        ["sv"] = "Anteckning",
        ["uk"] = "Нотатка",
    };

    private static readonly Dictionary<string, string> CourseFallback = new()
    {
        ["bg"] = "StudyLife курс",
        ["cs"] = "Kurz StudyLife",
        ["da"] = "StudyLife-kursus",
        ["de"] = "StudyLife-Kurs",
        ["el"] = "Μάθημα StudyLife",
        ["en"] = "StudyLife course",
        ["es"] = "Curso de StudyLife",
        ["et"] = "StudyLife'i kursus",
        ["fi"] = "StudyLife-kurssi",
        ["fr"] = "Cours StudyLife",
        ["ga"] = "Cúrsa StudyLife",
        ["hr"] = "StudyLife kolegij",
        ["hu"] = "StudyLife kurzus",
        ["it"] = "Corso StudyLife",
        ["lt"] = "StudyLife kursas",
        ["lv"] = "StudyLife kurss",
        ["mt"] = "Kors StudyLife",
        ["nl"] = "StudyLife-cursus",
        ["pl"] = "Kurs StudyLife",
        ["pt"] = "Curso StudyLife",
        ["ro"] = "Curs StudyLife",
        ["ru"] = "Курс StudyLife",
        ["sk"] = "Kurz StudyLife",
        ["sl"] = "Tečaj StudyLife",
        ["sv"] = "StudyLife-kurs",
        ["uk"] = "Курс StudyLife",
    };

    public static async Task ReindexAsync(HttpClient http, IJSRuntime jsRuntime)
    {
#if IOS
        try
        {
            var notes = await http.GetFromJsonAsync<List<NoteDto>>("api/notes") ?? new();
            var courses = await http.GetFromJsonAsync<List<CourseDto>>("api/courses") ?? new();
            var lang = await NativeText.GetCurrentLanguageAsync(jsRuntime);
            var noteFallback = NativeText.Get(NoteFallback, lang);
            var courseFallback = NativeText.Get(CourseFallback, lang);

            var items = new List<CSSearchableItem>();
            foreach (var note in notes)
            {
                var attributes = new CSSearchableItemAttributeSet(UniformTypeIdentifiers.UTTypes.Text)
                {
                    Title = string.IsNullOrWhiteSpace(note.Title) ? noteFallback : note.Title,
                    ContentDescription = note.Content.Length > 200 ? note.Content[..200] : note.Content,
                };
                items.Add(new CSSearchableItem($"{NotePrefix}{note.Id}", "studylife.notes", attributes));
            }
            foreach (var course in courses)
            {
                var attributes = new CSSearchableItemAttributeSet(UniformTypeIdentifiers.UTTypes.Item)
                {
                    Title = course.Name,
                    ContentDescription = courseFallback,
                };
                items.Add(new CSSearchableItem($"{CoursePrefix}{course.Id}", "studylife.courses", attributes));
            }

            var index = CSSearchableIndex.DefaultSearchableIndex;
            if (index == null) return;
            // Replace old entries by domain (this way deleted notes disappear too).
            index.DeleteWithDomain(new[] { "studylife.notes", "studylife.courses" }, _ =>
                index.Index(items.ToArray(), _ => { }));
        }
        catch { /* offline/error - index simply stays at its last state */ }
#else
        await Task.CompletedTask;
#endif
    }
}
