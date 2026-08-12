using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class ContextualMachineTranslationTests
{
    [Fact]
    public void Same_template_is_isolated_by_transform_path()
    {
        var store = new ContextualMachineTranslationStore();
        store.Set("Root/Inventory/Sort", "入手", "获得时间");
        store.Set("Root/Rewards/Sort", "入手", "获取");

        Assert.True(store.TryGet("Root/Inventory/Sort", "入手", out var inventory));
        Assert.Equal("获得时间", inventory);
        Assert.True(store.TryGet("Root/Rewards/Sort", "入手", out var rewards));
        Assert.Equal("获取", rewards);
        Assert.False(store.TryGet("Root/Unknown/Sort", "入手", out _));
    }

    [Fact]
    public void Versioned_context_cache_round_trips_but_legacy_flat_cache_is_rejected()
    {
        var store = new ContextualMachineTranslationStore();
        store.Set("Root/Panel/Text", "確認", "确认");

        string json = store.Serialize();

        Assert.Contains("\"version\":1", json);
        Assert.True(ContextualMachineTranslationStore.TryDeserialize(json, out var loaded));
        Assert.True(loaded.TryGet("Root/Panel/Text", "確認", out var translated));
        Assert.Equal("确认", translated);
        Assert.False(
            ContextualMachineTranslationStore.TryDeserialize("{\"確認\":\"确认\"}", out _)
        );
    }

    [Fact]
    public void Contextual_queue_keeps_identical_templates_as_independent_jobs()
    {
        var queue = new TranslationQueue();
        queue.EnqueueContextual("Root/A/Text", "確認", "ui_texts", foreground: true);
        queue.EnqueueContextual("Root/B/Text", "確認", "ui_texts", foreground: true);

        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal("確認", first.Template);
        Assert.Equal("Root/A/Text", first.ContextPath);
        queue.CompleteSuccess(first);

        Assert.True(queue.TryDequeue(out var second));
        Assert.Equal("確認", second.Template);
        Assert.Equal("Root/B/Text", second.ContextPath);
    }

    [Fact]
    public void Llm_user_prompt_contains_path_context_and_source_template()
    {
        string prompt = ContextualMachineTranslationProtocol.BuildUserPrompt(
            "Root/Inventory/Sort",
            "入手順"
        );

        Assert.Contains("Root/Inventory/Sort", prompt);
        Assert.EndsWith("入手順", prompt);
    }
}
