using Foundation;
using ObjCRuntime;
using StudyLife.App.Core;
using StudyLife.App.Services;
using UIKit;

namespace StudyLife.App;

/// <summary>
/// The app's UIApplication (principal class, see Program.Main). It exists for one reason: hardware
/// key events pass through <see cref="SendEvent"/> before UIKit hands them to any responder, the
/// focus engine or a key command. On the iPad the bare Left/Right arrow keys never reach the
/// WebView's page (letters do), and key commands on the app delegate were not reached either, so
/// this is the one place where the app can still see them. What happens to a key event is decided
/// in StudyLife.App.Core.HardwareArrowKeys; this class only translates UIKit's types and either
/// passes the event on or swallows it and forwards the arrow to the page.
/// https://developer.apple.com/documentation/uikit/uiapplication/sendevent(_:)
/// </summary>
[Register(nameof(StudyAtriumApplication))]
public sealed class StudyAtriumApplication : UIApplication
{
    // UIApplicationMain creates the instance natively; the runtime wraps it through this constructor.
    public StudyAtriumApplication(NativeHandle handle) : base(handle)
    {
    }

    public override void SendEvent(UIEvent uievent)
    {
        if (uievent is UIPressesEvent pressesEvent && TakeForPage(pressesEvent))
            return;
        base.SendEvent(uievent);
    }

    private static bool TakeForPage(UIPressesEvent pressesEvent)
    {
        var presses = new List<KeyPress>();
        foreach (var press in pressesEvent.AllPresses)
        {
            // Only keyboard presses carry a key (remote/game controller presses do not).
            if (press.Key is not { } key) return false;
            presses.Add(new KeyPress(KeyOf(key.KeyCode), ModifiersOf(key.ModifierFlags), PhaseOf(press.Phase)));
        }

        var decision = NativeBridge.ArrowKeys.Decide(presses);
        if (decision.Forward is { } arrow)
            NativeBridge.ForwardArrowKey(arrow);
        return decision.Swallow;
    }

    private static HardwareKey KeyOf(UIKeyboardHidUsage code) => code switch
    {
        UIKeyboardHidUsage.KeyboardLeftArrow => HardwareKey.LeftArrow,
        UIKeyboardHidUsage.KeyboardRightArrow => HardwareKey.RightArrow,
        _ => HardwareKey.Other,
    };

    private static KeyModifiers ModifiersOf(UIKeyModifierFlags flags)
    {
        var modifiers = KeyModifiers.None;
        if (flags.HasFlag(UIKeyModifierFlags.Shift)) modifiers |= KeyModifiers.Shift;
        if (flags.HasFlag(UIKeyModifierFlags.Control)) modifiers |= KeyModifiers.Control;
        if (flags.HasFlag(UIKeyModifierFlags.Alternate)) modifiers |= KeyModifiers.Alternate;
        if (flags.HasFlag(UIKeyModifierFlags.Command)) modifiers |= KeyModifiers.Command;
        if (flags.HasFlag(UIKeyModifierFlags.AlphaShift)) modifiers |= KeyModifiers.CapsLock;
        if (flags.HasFlag(UIKeyModifierFlags.NumericPad)) modifiers |= KeyModifiers.NumericPad;
        return modifiers;
    }

    private static PressPhase PhaseOf(UIPressPhase phase) => phase switch
    {
        UIPressPhase.Began => PressPhase.Began,
        UIPressPhase.Changed => PressPhase.Changed,
        UIPressPhase.Ended => PressPhase.Ended,
        UIPressPhase.Cancelled => PressPhase.Cancelled,
        _ => PressPhase.Stationary,
    };
}
