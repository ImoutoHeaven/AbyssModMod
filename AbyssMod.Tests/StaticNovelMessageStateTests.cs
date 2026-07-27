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
}
