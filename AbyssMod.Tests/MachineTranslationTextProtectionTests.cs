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
    public void Item_context_takes_precedence_over_character_name_field()
    {
        Assert.Equal(
            TranslationPaths.Items,
            MachineTranslationCategoryPolicy.ResolveNameFieldCategory(
                TranslationPaths.Items,
                isNameField: true
            )
        );
    }

    [Fact]
    public void Non_item_name_field_remains_excluded_from_machine_translation()
    {
        var category = MachineTranslationCategoryPolicy.ResolveNameFieldCategory(
            "system",
            isNameField: true
        );

        Assert.Equal("name", category);
        Assert.False(MachineTranslationCategoryPolicy.CanTranslate(category));
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

    [Fact]
    public void Numeric_templates_preserve_runtime_format_placeholders_end_to_end()
    {
        var (template, numbers) = MachineTranslationTemplate.Normalize(
            "ダメージ{0}、固定値100"
        );

        Assert.Equal("ダメージ{0}、固定値100", template);
        Assert.Empty(numbers);
        Assert.Equal(
            "伤害{0}，固定值100",
            MachineTranslationTemplate.Fill("伤害{0}，固定值100", numbers)
        );
    }

    [Fact]
    public void Numeric_templates_still_restore_literal_numbers_without_runtime_placeholders()
    {
        var (template, numbers) = MachineTranslationTemplate.Normalize("ランク123");

        Assert.Equal("ランク{0}", template);
        Assert.Equal(["123"], numbers);
        Assert.Equal("等级123", MachineTranslationTemplate.Fill("等级{0}", numbers));
        Assert.Null(MachineTranslationTemplate.Fill("等级{1}", numbers));
    }
}
