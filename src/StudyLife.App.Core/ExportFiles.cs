namespace StudyLife.App.Core;

/// <summary>
/// Where the app puts a file the user downloads (backup, export, recovery codes) before the system
/// save/share sheet hands it on: one "exports" folder in the app's cache. The folder is emptied before
/// every new file, so an earlier export - the recovery codes are plain text - does not stay behind in
/// the cache longer than until the next download.
/// </summary>
public static class ExportFiles
{
    public const string FolderName = "exports";

    /// <summary>Removes the exports folder with everything in it (data export, recovery codes). Used on sign-out and
    /// account deletion: a file the user downloaded for one account must not stay in the cache after it. No-op when
    /// the folder does not exist.</summary>
    public static void Clear(string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        var folder = Path.GetFullPath(Path.Combine(cacheDirectory, FolderName));
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    /// <summary>Writes <paramref name="content"/> as <paramref name="fileName"/> into a fresh exports
    /// folder under <paramref name="cacheDirectory"/> and returns the full path. The client already
    /// reduced the name to one path segment (FileDownloadName.Sanitize); a name that would still leave
    /// the folder is refused here as well.</summary>
    public static string Write(string cacheDirectory, string fileName, byte[] content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);

        var folder = Path.GetFullPath(Path.Combine(cacheDirectory, FolderName));
        var path = Path.GetFullPath(Path.Combine(folder, fileName));
        if (!string.Equals(Path.GetDirectoryName(path), folder, StringComparison.Ordinal))
            throw new ArgumentException($"'{fileName}' is not a plain file name.", nameof(fileName));

        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(path, content);
        return path;
    }
}
