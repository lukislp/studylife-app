using StudyLife.App.Core;

namespace StudyLife.App.Core.Tests;

public class HardwareArrowKeysTests
{
    // iOS reports the arrow keys of a hardware keyboard with the numeric-pad flag set.
    private const KeyModifiers ArrowFlags = KeyModifiers.NumericPad;

    private static KeyPress Press(HardwareKey key, PressPhase phase, KeyModifiers modifiers = ArrowFlags) =>
        new(key, modifiers, phase);

    private static HardwareArrowKeys Claimed()
    {
        var keys = new HardwareArrowKeys();
        keys.PageDeclared(HardwareArrowKeys.HorizontalIntent);
        return keys;
    }

    [Fact]
    public void Nothing_is_claimed_until_a_page_asks()
    {
        var keys = new HardwareArrowKeys();

        Assert.False(keys.Claimed);
        Assert.Null(keys.ScriptFor(ArrowKey.Left));
        Assert.Null(keys.ScriptFor(ArrowKey.Right));
        Assert.Equal(PressDecision.PassThrough, keys.Decide([Press(HardwareKey.LeftArrow, PressPhase.Began)]));
    }

    [Fact]
    public void The_calendar_intent_claims_both_keys_and_forwards_them_as_dom_keys()
    {
        var keys = Claimed();

        Assert.True(keys.Claimed);
        Assert.Equal("nativeHorizontalArrowKey('ArrowLeft')", keys.ScriptFor(ArrowKey.Left));
        Assert.Equal("nativeHorizontalArrowKey('ArrowRight')", keys.ScriptFor(ArrowKey.Right));
    }

    [Theory]
    [InlineData(null)]       // attribute removed: text field or dialog focused, or page left
    [InlineData("")]
    [InlineData("vertical")] // an intent this app does not serve
    public void Withdrawn_or_unknown_intent_releases_the_keys(string? intent)
    {
        var keys = Claimed();

        keys.PageDeclared(intent);

        Assert.False(keys.Claimed);
        Assert.Null(keys.ScriptFor(ArrowKey.Left));
        Assert.Equal(PressDecision.PassThrough, keys.Decide([Press(HardwareKey.RightArrow, PressPhase.Began)]));
    }

    [Theory]
    [InlineData(HardwareKey.LeftArrow, ArrowKey.Left)]
    [InlineData(HardwareKey.RightArrow, ArrowKey.Right)]
    public void A_claimed_arrow_press_is_swallowed_and_forwarded_once(HardwareKey key, ArrowKey expected)
    {
        var keys = Claimed();

        var down = keys.Decide([Press(key, PressPhase.Began)]);
        var up = keys.Decide([Press(key, PressPhase.Ended)]);

        Assert.Equal(new PressDecision(Swallow: true, Forward: expected), down);
        Assert.Equal(new PressDecision(Swallow: true, Forward: null), up); // key-up: swallowed, not forwarded again
    }

    [Theory]
    [InlineData(KeyModifiers.None)]                                // a keyboard that does not set the flag
    [InlineData(KeyModifiers.NumericPad)]                          // what iOS reports for the arrow keys
    [InlineData(KeyModifiers.NumericPad | KeyModifiers.CapsLock)]  // Caps Lock on is a state, not a chord
    public void The_numeric_pad_and_caps_lock_flags_do_not_make_an_arrow_a_shortcut(KeyModifiers modifiers)
    {
        var keys = Claimed();

        var decision = keys.Decide([Press(HardwareKey.LeftArrow, PressPhase.Began, modifiers)]);

        Assert.Equal(new PressDecision(true, ArrowKey.Left), decision);
    }

    [Theory]
    [InlineData(KeyModifiers.Shift)]     // text selection
    [InlineData(KeyModifiers.Command)]   // line start/end, system shortcuts
    [InlineData(KeyModifiers.Alternate)] // word-wise caret movement
    [InlineData(KeyModifiers.Control)]
    public void Arrows_with_a_modifier_stay_with_the_system(KeyModifiers modifier)
    {
        var keys = Claimed();

        var decision = keys.Decide([Press(HardwareKey.RightArrow, PressPhase.Began, ArrowFlags | modifier)]);

        Assert.Equal(PressDecision.PassThrough, decision);
    }

    [Fact]
    public void Other_keys_are_never_touched()
    {
        var keys = Claimed();

        // T (today) and the vertical arrows reach the page through WebKit as before.
        Assert.Equal(PressDecision.PassThrough, keys.Decide([Press(HardwareKey.Other, PressPhase.Began, KeyModifiers.None)]));
        Assert.Equal(PressDecision.PassThrough, keys.Decide([Press(HardwareKey.Other, PressPhase.Ended, KeyModifiers.None)]));
    }

    [Fact]
    public void A_press_that_began_unclaimed_is_never_swallowed_halfway()
    {
        var keys = new HardwareArrowKeys();

        // The caret moves in a text field (unclaimed) ...
        Assert.Equal(PressDecision.PassThrough, keys.Decide([Press(HardwareKey.LeftArrow, PressPhase.Began)]));
        // ... the page claims the keys while the key is still down (focus left the field) ...
        keys.PageDeclared(HardwareArrowKeys.HorizontalIntent);
        // ... the key-up still goes to UIKit, which saw the key-down.
        Assert.Equal(PressDecision.PassThrough, keys.Decide([Press(HardwareKey.LeftArrow, PressPhase.Ended)]));
    }

    [Fact]
    public void A_press_that_began_claimed_is_swallowed_to_its_end_even_after_the_claim_is_withdrawn()
    {
        var keys = Claimed();
        keys.Decide([Press(HardwareKey.RightArrow, PressPhase.Began)]);

        keys.PageDeclared(null); // e.g. a dialog opened in response to the key

        Assert.Equal(new PressDecision(true, null), keys.Decide([Press(HardwareKey.RightArrow, PressPhase.Ended)]));
        // The press is over: the next one follows the current (withdrawn) claim.
        Assert.Equal(PressDecision.PassThrough, keys.Decide([Press(HardwareKey.RightArrow, PressPhase.Began)]));
    }

    [Fact]
    public void A_cancelled_press_also_ends_the_swallowed_press()
    {
        var keys = Claimed();
        keys.Decide([Press(HardwareKey.LeftArrow, PressPhase.Began)]);

        Assert.Equal(new PressDecision(true, null), keys.Decide([Press(HardwareKey.LeftArrow, PressPhase.Cancelled)]));

        keys.PageDeclared(null);
        Assert.Equal(PressDecision.PassThrough, keys.Decide([Press(HardwareKey.LeftArrow, PressPhase.Ended)]));
    }

    [Fact]
    public void Stationary_presses_do_not_count_but_a_mixed_event_goes_on_whole()
    {
        var keys = Claimed();

        // Another key held down while Left is pressed: only the changed press decides.
        var arrowWithHeldKey = keys.Decide([
            Press(HardwareKey.Other, PressPhase.Stationary, KeyModifiers.None),
            Press(HardwareKey.LeftArrow, PressPhase.Began),
        ]);
        Assert.Equal(new PressDecision(true, ArrowKey.Left), arrowWithHeldKey);

        // Left and another key change in the same event: UIKit gets the whole event.
        var mixed = keys.Decide([
            Press(HardwareKey.Other, PressPhase.Began, KeyModifiers.None),
            Press(HardwareKey.RightArrow, PressPhase.Began),
        ]);
        Assert.Equal(PressDecision.PassThrough, mixed);
    }

    [Fact]
    public void An_event_without_changed_presses_goes_on()
    {
        var keys = Claimed();

        Assert.Equal(PressDecision.PassThrough, keys.Decide([]));
        Assert.Equal(PressDecision.PassThrough, keys.Decide([Press(HardwareKey.LeftArrow, PressPhase.Stationary)]));
    }
}
