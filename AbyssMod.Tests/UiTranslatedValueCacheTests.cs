using System.Collections.Generic;
using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class UiTranslatedValueCacheTests
{
    [Fact]
    public void Recognizes_translated_values_with_ordinal_matching()
    {
        var cache = new UiTranslatedValueCache();
        var table = new Dictionary<string, string>
        {
            ["A"] = "ABC",
            ["B"] = "甲",
        };

        Assert.True(cache.Contains(table, "ABC"));
        Assert.True(cache.Contains(table, "甲"));
        Assert.False(cache.Contains(table, "abc"));
    }

    [Fact]
    public void Invalidates_when_translation_manager_publishes_a_replacement_table()
    {
        var cache = new UiTranslatedValueCache();
        var original = new Dictionary<string, string> { ["A"] = "旧译文" };
        var replacement = new Dictionary<string, string> { ["A"] = "新译文" };

        Assert.True(cache.Contains(original, "旧译文"));
        Assert.False(cache.Contains(replacement, "旧译文"));
        Assert.True(cache.Contains(replacement, "新译文"));
    }
}
