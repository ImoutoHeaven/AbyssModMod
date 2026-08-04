using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AbyssMod.Services;

internal static class MachineTranslationCategoryPolicy
{
    public const string NovelTypewriter = "novel_message";
    private const string Name = "name";

    public static bool CanProcess(bool translationEnabled, string category) =>
        translationEnabled && CanTranslate(category);

    public static bool CanTranslate(string category) =>
        !string.Equals(category, Name, System.StringComparison.Ordinal)
        && !string.Equals(category, NovelTypewriter, System.StringComparison.Ordinal);

    public static string ResolveNameFieldCategory(string contextualCategory, bool isNameField)
    {
        if (string.Equals(
                contextualCategory,
                TranslationPaths.Items,
                System.StringComparison.Ordinal
            ))
            return TranslationPaths.Items;

        return isNameField ? Name : string.Empty;
    }

    public static bool IsExcludedFromGenericProcessing(string category) =>
        string.Equals(category, NovelTypewriter, System.StringComparison.Ordinal);
}

internal static class MachineTranslationTextProtection
{
    private static readonly Regex ProtectedSyntax = new(
        @"<[^>]*>|\{\d+\}|\\r\\n|\\n|\\r|\r\n|\n|\r",
        RegexOptions.Compiled
    );

    private static readonly Regex Token = new(
        @"__ABYSS_TOKEN_(\d+)__",
        RegexOptions.Compiled
    );

    public static bool HasKana(string text)
    {
        foreach (var c in text)
            if ((c >= '\u3040' && c <= '\u309F') || (c >= '\u30A0' && c <= '\u30FF'))
                return true;
        return false;
    }

    public static ProtectedMachineTranslationText Protect(string text)
    {
        var values = new List<string>();
        var protectedText = ProtectedSyntax.Replace(text, match =>
        {
            values.Add(match.Value);
            return $"__ABYSS_TOKEN_{values.Count - 1}__";
        });
        return new ProtectedMachineTranslationText(protectedText, values);
    }

    internal static bool HasOnlyExpectedTokens(string text, IReadOnlyList<string> expected)
    {
        var matches = Token.Matches(text);
        if (matches.Count != expected.Count)
            return false;

        for (var i = 0; i < matches.Count; i++)
            if (matches[i].Value != expected[i])
                return false;
        return true;
    }
}

internal sealed class ProtectedMachineTranslationText
{
    public string Text { get; }

    public IReadOnlyList<string> Tokens { get; }

    public ProtectedMachineTranslationText(string text, IReadOnlyList<string> values)
    {
        Text = text;
        Tokens = BuildTokens(values.Count);
        _values = values;
    }

    private readonly IReadOnlyList<string> _values;

    public bool TryRestore(string response, out string restored)
    {
        if (!MachineTranslationTextProtection.HasOnlyExpectedTokens(response, Tokens))
        {
            restored = string.Empty;
            return false;
        }

        restored = response;
        for (var i = 0; i < Tokens.Count; i++)
            restored = restored.Replace(Tokens[i], _values[i]);
        return true;
    }

    private static IReadOnlyList<string> BuildTokens(int count)
    {
        var tokens = new string[count];
        for (var i = 0; i < count; i++)
            tokens[i] = $"__ABYSS_TOKEN_{i}__";
        return tokens;
    }
}
