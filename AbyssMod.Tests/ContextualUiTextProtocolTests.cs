using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class ContextualUiTextProtocolTests
{
    [Fact]
    public void Same_source_text_can_have_different_translations_at_different_paths()
    {
        var index = new ContextualUiTextIndex(
            new Dictionary<string, Dictionary<string, string>>
            {
                ["Root/Inventory/Sort"] = new() { ["入手"] = "获得时间" },
                ["Root/Rewards/Sort"] = new() { ["入手"] = "获取" },
            }
        );

        Assert.True(index.TryTranslate("Root/Inventory/Sort", "入手", out var inventory));
        Assert.Equal("获得时间", inventory);
        Assert.True(index.TryTranslate("Root/Rewards/Sort", "入手", out var rewards));
        Assert.Equal("获取", rewards);
        Assert.False(index.TryTranslate("Root/Unknown/Sort", "入手", out _));
    }

    [Fact]
    public void Exact_path_wins_before_the_most_specific_matching_wildcard()
    {
        var index = new ContextualUiTextIndex(
            new Dictionary<string, Dictionary<string, string>>
            {
                ["Root/*/Text"] = new() { ["確認"] = "通用" },
                ["Root/Panel*/Text"] = new() { ["確認"] = "面板" },
                ["Root/PanelA/Text"] = new() { ["確認"] = "精确" },
            }
        );

        Assert.True(index.TryTranslate("Root/PanelA/Text", "確認", out var exact));
        Assert.Equal("精确", exact);
        Assert.True(index.TryTranslate("Root/PanelB/Text", "確認", out var wildcard));
        Assert.Equal("面板", wildcard);
    }

    [Fact]
    public void Numbered_placeholders_preserve_runtime_values_and_repeated_groups()
    {
        var index = new ContextualUiTextIndex(
            new Dictionary<string, Dictionary<string, string>>
            {
                ["Root/Count"] = new()
                {
                    ["所持数：{0}/{1}"] = "持有：{0}/{1}",
                    ["{0}から{0}"] = "从{0}到{0}",
                },
            }
        );

        Assert.True(index.TryTranslate("Root/Count", "所持数：12/34", out var count));
        Assert.Equal("持有：12/34", count);
        Assert.True(index.TryTranslate("Root/Count", "東京から東京", out var repeated));
        Assert.Equal("从東京到東京", repeated);
        Assert.False(index.TryTranslate("Root/Count", "東京から大阪", out _));
    }

    [Fact]
    public void Protocol_hash_matches_the_recursive_translation_manifest_format()
    {
        var table = new Dictionary<string, Dictionary<string, string>>
        {
            ["B"] = new() { ["z"] = "3" },
            ["A"] = new() { ["y"] = "2", ["x"] = "1" },
        };

        Assert.Equal("f42ae0158b02db4e4b168a9380e3484d", ContextualUiTextProtocol.ComputeHash(table));
        Assert.Equal(3, ContextualUiTextProtocol.CountEntries(table));
    }
}
