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

    /// <summary>True while the app is in the foreground. Network checks that retry on their own (the sign-in
    /// bar) stay quiet while it is false and ask again from <see cref="Resumed"/>.</summary>
    public static bool InForeground { get; private set; } = true;

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new MainPage()) { Title = "StudyAtrium" };
        window.Stopped += (_, _) => InForeground = false;
        window.Resumed += (_, _) =>
        {
            InForeground = true;
            Resumed?.Invoke();
        };
        return window;
    }
}
