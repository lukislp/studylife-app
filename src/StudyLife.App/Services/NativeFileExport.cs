using StudyLife.Client.Services;

namespace StudyLife.App.Services;

/// <summary>
/// File export for the native app shell (the client's INativeFileExport hook): the browser's
/// blob download doesn't work in the WebView (no download manager) - instead the file is
/// written to the cache directory and offered via the system share/save sheet
/// (iOS: "Save to Files"/AirDrop/..., Android: save/share targets).
/// First consumer: the recovery codes download in PasskeyDeviceManager.
/// </summary>
public sealed class NativeFileExport : INativeFileExport
{
    public bool IsAvailable => true;

    public async Task SaveTextAsync(string fileName, string content)
    {
        var path = Path.Combine(FileSystem.CacheDirectory, fileName);
        await File.WriteAllTextAsync(path, content);
        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = fileName,
            File = new ShareFile(path),
        });
    }
}
