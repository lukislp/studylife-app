namespace StudyLife.App.Core;

/// <summary>
/// Hands the bare Left/Right hardware arrow keys to the page on the iPad. With a hardware keyboard,
/// letters such as T reach the WebView's page as keydown events, the arrow keys do not: UIKit
/// consumes them on their way through the app (keyboard scrolling, focus navigation) before WebKit
/// can dispatch them, so the calendar's week switching could not work. Key commands registered on
/// the app delegate did not help either (TestFlight 1.12.24): they are only matched once the press
/// has travelled the responder chain up to the application, and the arrows never get that far.
///
/// The app therefore looks at every key press where it enters the app (UIApplication.SendEvent,
/// before any responder, focus engine or key command sees it) and asks <see cref="Decide"/> what to
/// do with it. A bare Left/Right press is taken out of the event stream and forwarded to the page as
/// a keydown, but only while the page declares that it wants those keys (the calendar sets
/// data-page-arrow-keys="horizontal" on &lt;html&gt; and removes it while a text field or a dialog has
/// focus). Every other press - other keys, arrows with a modifier, arrows while no page asks - goes
/// on unchanged, so the caret keeps moving in text fields.
/// </summary>
public sealed class HardwareArrowKeys
{
    /// <summary>The value of data-page-arrow-keys that asks for Left/Right.</summary>
    public const string HorizontalIntent = "horizontal";

    // Modifier flags that do not make an arrow press a shortcut: iOS reports the arrow keys of a
    // hardware keyboard with the numeric-pad flag set, and Caps Lock is a state, not a chord.
    private const KeyModifiers NotAChord = KeyModifiers.NumericPad | KeyModifiers.CapsLock;

    // Arrow keys whose press began in a swallowed event: the rest of that press (changed, ended,
    // cancelled) is swallowed too, so UIKit never sees half a press.
    private readonly HashSet<HardwareKey> _held = [];

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

    /// <summary>
    /// Decides what happens to one hardware key event (all presses UIKit bundled into it). The event
    /// is swallowed only when every press that changed in it belongs to the page: a bare Left/Right
    /// press that begins while the keys are claimed, or the continuation of one that began that way.
    /// A mixed event goes on unchanged as a whole.
    /// </summary>
    public PressDecision Decide(IReadOnlyList<KeyPress> presses)
    {
        var changed = presses.Where(p => p.Phase != PressPhase.Stationary).ToList();
        if (changed.Count == 0 || !changed.All(BelongsToPage)) return PressDecision.PassThrough;

        ArrowKey? forward = null;
        foreach (var press in changed)
        {
            switch (press.Phase)
            {
                case PressPhase.Began:
                    _held.Add(press.Key);
                    forward ??= press.Key == HardwareKey.LeftArrow ? ArrowKey.Left : ArrowKey.Right;
                    break;
                case PressPhase.Ended:
                case PressPhase.Cancelled:
                    _held.Remove(press.Key);
                    break;
            }
        }
        return new PressDecision(Swallow: true, Forward: forward);
    }

    private bool BelongsToPage(KeyPress press) => press.Phase == PressPhase.Began
        ? Claimed && IsBareHorizontalArrow(press)
        : _held.Contains(press.Key);

    private static bool IsBareHorizontalArrow(KeyPress press) =>
        press.Key is HardwareKey.LeftArrow or HardwareKey.RightArrow
        && (press.Modifiers & ~NotAChord) == KeyModifiers.None;
}

public enum ArrowKey { Left, Right }

/// <summary>The hardware keys the decision tells apart (the platform maps its key codes onto these).</summary>
public enum HardwareKey { Other, LeftArrow, RightArrow }

/// <summary>Modifier flags of a key press, as the platform reports them.</summary>
[Flags]
public enum KeyModifiers
{
    None = 0,
    Shift = 1 << 0,
    Control = 1 << 1,
    Alternate = 1 << 2,
    Command = 1 << 3,
    CapsLock = 1 << 4,
    NumericPad = 1 << 5,
}

/// <summary>Where a press is in its life cycle within one event.</summary>
public enum PressPhase { Began, Changed, Stationary, Ended, Cancelled }

/// <summary>One press of a hardware key event.</summary>
public readonly record struct KeyPress(HardwareKey Key, KeyModifiers Modifiers, PressPhase Phase);

/// <summary>What the app does with a key event: swallow it (UIKit never sees it) and, for a press
/// that just began, forward that arrow to the page.</summary>
public readonly record struct PressDecision(bool Swallow, ArrowKey? Forward)
{
    public static PressDecision PassThrough => default;
}
