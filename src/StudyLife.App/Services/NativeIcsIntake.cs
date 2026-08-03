using StudyLife.Client.Services;

namespace StudyLife.App.Services;

/// <summary>
/// App-side implementation of INativeIcsIntake: the AppDelegate (iOS) or MainActivity (Android)
/// drop an .ics file shared/opened via the system here and navigate to /calendar via
/// DeepLinkService - the calendar page picks it up there (once) and
/// feeds it into the existing import review flow.
/// </summary>
public sealed class NativeIcsIntake : INativeIcsIntake
{
    private static readonly object Lock = new();
    private static string? _fileName;
    private static byte[]? _content;

    public static void SetPending(string fileName, byte[] content)
    {
        lock (Lock)
        {
            _fileName = fileName;
            _content = content;
        }
    }

    public (string FileName, byte[] Content)? TakePending()
    {
        lock (Lock)
        {
            if (_fileName is null || _content is null) return null;
            var result = (_fileName, _content);
            _fileName = null;
            _content = null;
            return result;
        }
    }
}
