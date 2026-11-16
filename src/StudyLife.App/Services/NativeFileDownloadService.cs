using StudyLife.App.Core;
using StudyLife.Client.Services;
#if IOS || MACCATALYST
using CoreGraphics;
using Foundation;
using UIKit;
#endif

namespace StudyLife.App.Services;

/// <summary>
/// The app's IFileDownloadService (studylife repo, Client/Services/IFileDownloadService.cs): every
/// download of the shared client - backup and JSON export, the export before deleting an account,
/// the recovery codes - ends here. The browser's blob download cannot work in the BlazorWebView: it
/// has no download manager, and on iOS/Mac Catalyst its navigation policy cancels the blob: URL and
/// passes it to UIApplication.OpenUrl, which silently does nothing.
///
/// iOS/Mac Catalyst: the file goes to the cache (ExportFiles) and the system share sheet opens
/// ("Save to Files", AirDrop, ...), presented by this class from the WebView's own window and
/// anchored to the middle of the WebView. MAUI's Share.RequestAsync is not used there: it anchors
/// the iPad popover to a zero rectangle in the corner of whatever it finds as the current view
/// controller, and its task only completes when the sheet is dismissed; if UIKit refuses the
/// presentation, that task never completes and the tap does nothing, with no error.
/// Here a refused presentation throws, so the client shows its error text.
/// Android: the cache file is shared with MAUI's Share (save/share targets).
/// Windows: the system "Save as" dialog writes the file where the user chooses.
/// </summary>
public sealed class NativeFileDownloadService : IFileDownloadService
{
    public async Task SaveAsync(FileDownload file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
#if WINDOWS
            await MainThread.InvokeOnMainThreadAsync(() => SaveWithDialogAsync(file));
#else
            var path = await Task.Run(() => ExportFiles.Write(FileSystem.CacheDirectory, file.FileName, file.Content), cancellationToken);
            await MainThread.InvokeOnMainThreadAsync(() => OfferAsync(path, file.FileName));
#endif
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DeveloperDiagnostics.Error("download", "download_not_offered", $"{file.FileName}: {ex.Message}", ex);
            throw;
        }
    }

#if IOS || MACCATALYST
    private static Task OfferAsync(string path, string fileName)
    {
        var webView = NativeBridge.CurrentWebView
            ?? throw new InvalidOperationException("No WebView to present the share sheet from.");
        var presenter = webView.Window?.RootViewController
            ?? throw new InvalidOperationException("The WebView is not in a window.");
        while (presenter.PresentedViewController is { IsBeingDismissed: false } top)
            presenter = top;

        var sheet = new UIActivityViewController([NSUrl.FromFilename(path)], null);
        if (sheet.PopoverPresentationController is { } popover)
        {
            // iPad and Mac: a popover needs an anchor; the middle of the page, without an arrow.
            var bounds = webView.Bounds;
            popover.SourceView = webView;
            popover.SourceRect = new CGRect(bounds.GetMidX(), bounds.GetMidY(), 0, 0);
            popover.PermittedArrowDirections = 0;
        }
        presenter.PresentViewController(sheet, true, null);
        // UIKit sets this at once when it accepts the presentation and only logs a warning when it
        // refuses one - which would otherwise be a tap that does nothing.
        if (!ReferenceEquals(presenter.PresentedViewController, sheet))
            throw new InvalidOperationException($"The share sheet for {fileName} was not presented.");
        return Task.CompletedTask;
    }
#elif ANDROID
    private static Task OfferAsync(string path, string fileName) =>
        Share.Default.RequestAsync(new ShareFileRequest { Title = fileName, File = new ShareFile(path) });
#elif WINDOWS
    private static async Task SaveWithDialogAsync(FileDownload file)
    {
        var window = Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Microsoft.UI.Xaml.Window
            ?? throw new InvalidOperationException("No app window for the save dialog.");
        var extension = Path.GetExtension(file.FileName);
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
            SuggestedFileName = Path.GetFileNameWithoutExtension(file.FileName),
        };
        picker.FileTypeChoices.Add(extension.Length > 1 ? extension[1..].ToUpperInvariant() : "File",
            [extension.Length > 1 ? extension : ".bin"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));

        var target = await picker.PickSaveFileAsync();
        if (target is null) return; // the user cancelled the dialog: nothing to save, not an error
        await Windows.Storage.FileIO.WriteBytesAsync(target, file.Content);
    }
#else
    private static Task OfferAsync(string path, string fileName) =>
        throw new PlatformNotSupportedException();
#endif
}
