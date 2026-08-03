using System.Net.Http.Json;
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

    public static async Task ReindexAsync(HttpClient http)
    {
#if IOS
        try
        {
            var notes = await http.GetFromJsonAsync<List<NoteDto>>("api/notes") ?? new();
            var courses = await http.GetFromJsonAsync<List<CourseDto>>("api/courses") ?? new();

            var items = new List<CSSearchableItem>();
            foreach (var note in notes)
            {
                var attributes = new CSSearchableItemAttributeSet(UniformTypeIdentifiers.UTTypes.Text)
                {
                    Title = string.IsNullOrWhiteSpace(note.Title) ? "Notiz" : note.Title,
                    ContentDescription = note.Content.Length > 200 ? note.Content[..200] : note.Content,
                };
                items.Add(new CSSearchableItem($"{NotePrefix}{note.Id}", "studylife.notes", attributes));
            }
            foreach (var course in courses)
            {
                var attributes = new CSSearchableItemAttributeSet(UniformTypeIdentifiers.UTTypes.Item)
                {
                    Title = course.Name,
                    ContentDescription = "StudyLife-Kurs",
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
