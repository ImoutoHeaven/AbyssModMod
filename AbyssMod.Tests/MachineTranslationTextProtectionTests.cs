using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class MachineTranslationTextProtectionTests
{
    [Fact]
    public void Cached_machine_translation_is_not_applied_when_translation_is_disabled()
    {
        Assert.False(MachineTranslationCategoryPolicy.CanProcess(
            translationEnabled: false,
            category: "dialogue"
        ));
    }

    [Theory]
    [InlineData("ability_descriptions", true)]
    [InlineData("ui_misc", true)]
    [InlineData("name", false)]
    public void Translation_eligibility_includes_ability_descriptions(string category, bool expected)
    {
        Assert.Equal(expected, MachineTranslationCategoryPolicy.CanTranslate(category));
    }

    [Fact]
    public void Translation_eligibility_excludes_novel_typewriter_text()
    {
        Assert.False(MachineTranslationCategoryPolicy.CanTranslate("novel_message"));
    }

    [Fact]
    public void Restore_accepts_all_runtime_tokens_in_original_order()
    {
        var protectedText = MachineTranslationTextProtection.Protect(
            "<color=#4CF37B>ダメージ{0}</color>\n次の行\\n終わり"
        );
        var response = "伤害" + string.Join("译文", protectedText.Tokens) + "结束";

        Assert.True(protectedText.TryRestore(response, out var restored));
        Assert.Equal("伤害<color=#4CF37B>译文{0}译文</color>译文\n译文\\n结束", restored);
    }

    [Fact]
    public void Restore_rejects_missing_reordered_or_extra_runtime_tokens()
    {
        var protectedText = MachineTranslationTextProtection.Protect("<br>{0}\n");

        Assert.False(protectedText.TryRestore(protectedText.Tokens[0], out _));
        Assert.False(protectedText.TryRestore(string.Concat(protectedText.Tokens.Reverse()), out _));
        Assert.False(protectedText.TryRestore(string.Concat(protectedText.Tokens) + "__ABYSS_TOKEN_9__", out _));
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\\r\\n")]
    [InlineData("\\r")]
    [InlineData("\\n")]
    public void Restore_preserves_actual_and_escaped_newlines(string newline)
    {
        var protectedText = MachineTranslationTextProtection.Protect("あ" + newline + "い");

        Assert.Single(protectedText.Tokens);
        Assert.True(protectedText.TryRestore("中" + protectedText.Tokens[0] + "文", out var restored));
        Assert.Equal("中" + newline + "文", restored);
    }
}
