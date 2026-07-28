using AbyssMod.Patches;
using Xunit;

namespace AbyssMod.Tests;

public class StaticNovelMessageStateTests
{
    [Fact]
    public void Selects_the_original_message_when_translation_is_disabled()
    {
        var state = new StaticNovelMessageState("原文", "译文");

        Assert.True(state.TrySelect(false, out string message));
        Assert.Equal("原文", message);
        Assert.False(state.TrySelect(false, out _));
    }

    [Fact]
    public void Selects_the_translation_again_when_translation_is_reenabled()
    {
        var state = new StaticNovelMessageState("原文", "译文");

        Assert.True(state.TrySelect(false, out _));

        Assert.True(state.TrySelect(true, out string message));
        Assert.Equal("译文", message);
    }

    [Fact]
    public void Does_not_select_a_message_after_it_is_cleared()
    {
        var state = new StaticNovelMessageState("原文", "译文");
        state.Clear();

        Assert.False(state.TrySelect(false, out _));
    }

    [Fact]
    public void Selects_a_translation_that_arrives_after_the_source_was_displayed()
    {
        var state = new StaticNovelMessageState(
            source: "原文",
            translated: null,
            displayedTranslated: false
        );

        Assert.False(state.TrySelect(false, out _));

        state.SetTranslation("译文");

        Assert.True(state.TrySelect(true, out string message));
        Assert.Equal("译文", message);
    }

    [Fact]
    public void A_machine_translation_displayed_during_parse_can_toggle_back_to_source()
    {
        var state = new StaticNovelMessageState(
            source: "原文",
            translated: null,
            displayedTranslated: false
        );

        Assert.True(state.SetDisplayedTranslation("机翻"));

        Assert.True(state.TrySelect(false, out string message));
        Assert.Equal("原文", message);
    }

    [Fact]
    public void A_late_static_translation_replaces_the_current_machine_translation()
    {
        var state = new StaticNovelMessageState(
            source: "原文",
            translated: "机翻",
            displayedTranslated: true
        );

        state.SetTranslation("静态译文");

        Assert.True(state.TrySelect(true, out string message));
        Assert.Equal("静态译文", message);
    }
}
