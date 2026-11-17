namespace StudyLife.App.Core;

/// <summary>
/// Hands the bare Left/Right hardware arrow keys to the page on the iPad. With a hardware keyboard,
/// UIKit keeps the arrow keys for its own keyboard navigation before WebKit can dispatch them to the
/// page - letters such as T arrive, the arrows never do, so the calendar's week switching could not
/// work. The app claims the two keys with priority key commands, but only while the page declares
/// that it wants them (the calendar sets data-page-arrow-keys="horizontal" on &lt;html&gt; and removes
/// it while a text field or a dialog has focus), and forwards them to the page as keydown events.
/// Everywhere else the arrow keys keep their native behaviour.
/// </summary>
public sealed class HardwareArrowKeys
{
    /// <summary>The value of data-page-arrow-keys that asks for Left/Right.</summary>
    public const string HorizontalIntent = "horizontal";

    /// <summary>Whether the app currently claims Left/Right for the page.</summary>
    public bool Claimed { get; private set; }

    /// <summary>Records the page's declared intent (the attribute's value, or null when it is absent).</summary>
    public void PageDeclared(string? intent) => Claimed = intent == HorizontalIntent;

    /// <summary>The script that hands a claimed key to the page (js/native-bridge.js), or null when
    /// the key is not claimed right now - then it must not be swallowed.</summary>
    public string? ScriptFor(ArrowKey key) => !Claimed ? null : key switch
    {
        ArrowKey.Left => "nativeHorizontalArrowKey('ArrowLeft')",
        ArrowKey.Right => "nativeHorizontalArrowKey('ArrowRight')",
        _ => null,
    };
}

public enum ArrowKey { Left, Right }
