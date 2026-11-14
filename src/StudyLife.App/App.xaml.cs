namespace StudyLife.App;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the app comes back to the foreground (AppRoot checks whether the system language
    /// for the app changed meanwhile).</summary>
    public static event Action? Resumed;

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new MainPage()) { Title = "StudyLife" };
        window.Resumed += (_, _) => Resumed?.Invoke();
        return window;
    }
}
