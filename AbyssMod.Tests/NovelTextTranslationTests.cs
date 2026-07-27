using System.Collections.Generic;
using AbyssMod.Patches;
using Xunit;

namespace AbyssMod.Tests;

public class NovelTextTranslationTests
{
    [Fact]
    public void Looks_up_the_exact_source_before_expanding_the_user_placeholder()
    {
        var translations = new Dictionary<string, string>
        {
            ["ようこそ、Alice"] = "欢迎，<user>",
        };

        Assert.True(NovelTextTranslation.TryTranslate(
            translations,
            "ようこそ、Alice",
            "Alice",
            out string translated));
        Assert.Equal("欢迎，Alice", translated);
    }

    [Fact]
    public void Matches_a_source_key_that_already_uses_the_user_placeholder()
    {
        var translations = new Dictionary<string, string>
        {
            ["ようこそ、<user>"] = "欢迎，<user>",
        };

        Assert.True(NovelTextTranslation.TryTranslate(
            translations,
            "ようこそ、<user>",
            "Alice",
            out string translated));
        Assert.Equal("欢迎，Alice", translated);
    }
}
