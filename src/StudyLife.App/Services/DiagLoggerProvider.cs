using Microsoft.Extensions.Logging;

namespace StudyLife.App.Services;

/// <summary>
/// Writes warning/error log entries (including Blazor's own "Unhandled exception rendering
/// component..." from Renderer.HandleException) to Console.WriteLine with an explicit flush -
/// same pattern as AppleInAppPasskeys.LogDiag, visible via
/// "xcrun devicectl device process launch --console". Without this provider, the exception
/// that triggers the "An unhandled error has occurred" banner ends up nowhere visible
/// (release builds otherwise register no logging provider, AddDebug() only works with a
/// native debugger attached).
/// </summary>
internal sealed class DiagLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new DiagLogger(categoryName);

    public void Dispose()
    {
    }

    private sealed class DiagLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            Console.WriteLine($"[Diag:{category}] {logLevel}: {formatter(state, exception)}");
            if (exception != null)
                Console.WriteLine(exception.ToString());
            Console.Out.Flush();
        }
    }
}
