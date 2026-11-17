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

#if IOS
    private static readonly string[] Domains = ["studylife.notes", "studylife.courses"];

    /// <summary>Note titles and excerpts are personal: the app's own index keeps them under complete file
    /// protection, so they are only readable while the device is unlocked (the default index uses the
    /// weaker "until first unlock"). A refresh while locked simply fails and the next one catches up.</summary>
    private static readonly Lazy<CSSearchableIndex> Index =
        new(() => new CSSearchableIndex("studylife-protected", CSFileProtection.Complete));

    /// <summary>Builds before the protected index put their items into the default index; removed on every
    /// reindex and clear so nothing stays there with the weaker protection.</summary>
    private static Task ClearLegacyDefaultIndexAsync() =>
        DeleteDomainsAsync(CSSearchableIndex.DefaultSearchableIndex);

    private static async Task DeleteDomainsAsync(CSSearchableIndex? index)
    {
        if (index == null) return;
        var done = new TaskCompletionSource();
        index.DeleteWithDomain(Domains, _ => done.TrySetResult());
        await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(5)));
    }
#endif

    /// <summary>Removes every note and course from the system search index after the account was
    /// deleted (INativeAppAuth.AccountDeletedAsync) - the index otherwise keeps titles and note
    /// excerpts until the next reindex, which never comes for a deleted account. No-op elsewhere.</summary>
    public static async Task ClearAsync()
    {
#if IOS
        try
        {
            await DeleteDomainsAsync(Index.Value);
            await ClearLegacyDefaultIndexAsync();
        }
        catch { /* best effort - the index simply keeps its last state */ }
#else
        await Task.CompletedTask;
#endif
    }

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
                    ContentDescription = "StudyAtrium-Kurs",
                };
                items.Add(new CSSearchableItem($"{CoursePrefix}{course.Id}", "studylife.courses", attributes));
            }

            await ClearLegacyDefaultIndexAsync();
            var index = Index.Value;
            // Replace old entries by domain (this way deleted notes disappear too).
            index.DeleteWithDomain(Domains, _ =>
                index.Index(items.ToArray(), _ => { }));
        }
        catch { /* offline/error - index simply stays at its last state */ }
#else
        await Task.CompletedTask;
#endif
    }
}
