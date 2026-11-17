using StudyLife.App.Core;

namespace StudyLife.App.Core.Tests;

public class HardwareArrowKeysTests
{
    [Fact]
    public void Nothing_is_claimed_until_a_page_asks()
    {
        var keys = new HardwareArrowKeys();

        Assert.False(keys.Claimed);
        Assert.Null(keys.ScriptFor(ArrowKey.Left));
        Assert.Null(keys.ScriptFor(ArrowKey.Right));
    }

    [Fact]
    public void The_calendar_intent_claims_both_keys_and_forwards_them_as_dom_keys()
    {
        var keys = new HardwareArrowKeys();

        keys.PageDeclared("horizontal");

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
        var keys = new HardwareArrowKeys();
        keys.PageDeclared("horizontal");

        keys.PageDeclared(intent);

        Assert.False(keys.Claimed);
        Assert.Null(keys.ScriptFor(ArrowKey.Left));
    }
}
