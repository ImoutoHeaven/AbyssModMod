using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AbyssMod.Services;

/// <summary>
/// 技能描述模糊匹配：游戏 UI 常显示已代入数值的日文（31.4%），
/// 而 ability_descriptions 字典 key 为含 {[...]} 的 Masterdata 模板。
/// </summary>
public static class AbilityTextMatcher
{
    private static Dictionary<string, string> _exact = new();
    private static Dictionary<string, string> _bySkeleton = new();

    private static readonly Regex KeySlot = new(@"\{\[[^\]]+\]\}", RegexOptions.Compiled);
    private static readonly Regex ValueSlot = new(
        @"\{[0-9]+(?:\.[0-9]+)?%\}|[0-9]+(?:\.[0-9]+)?%",
        RegexOptions.Compiled
    );
    private static readonly Regex TranslationSlot = new(@"\{\[[^\]]+\]\}", RegexOptions.Compiled);

    public static void Rebuild(Dictionary<string, string> abilityDescriptions)
    {
        var exact = abilityDescriptions ?? new Dictionary<string, string>();
        var bySkeleton = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var kv in exact)
        {
            string skeleton = ToKeySkeleton(kv.Key);
            if (string.IsNullOrEmpty(skeleton))
                continue;
            bySkeleton.TryAdd(skeleton, kv.Value);
        }

        _exact = exact;
        _bySkeleton = bySkeleton;
    }

    public static bool LooksLikeAbility(string text)
    {
        if (string.IsNullOrEmpty(text) || !HasKana(text))
            return false;

        return text.Contains("ダメージ", StringComparison.Ordinal)
            || text.Contains("HIT", StringComparison.OrdinalIgnoreCase)
            || text.Contains("紋章", StringComparison.Ordinal)
            || text.Contains("スキル", StringComparison.Ordinal)
            || text.Contains("【効果】", StringComparison.Ordinal)
            || text.Contains("【発動条件】", StringComparison.Ordinal)
            || text.Contains("フォースチェイン", StringComparison.Ordinal);
    }

    public static bool TryTranslate(string text, out string result)
    {
        result = null;
        if (string.IsNullOrEmpty(text) || _exact.Count == 0)
            return false;

        text = NormalizeBrackets(text);

        if (_exact.TryGetValue(text, out result))
            return true;

        string skeleton = ToValueSkeleton(text);
        if (!_bySkeleton.TryGetValue(skeleton, out string templateTranslation))
            return false;

        var values = ExtractValues(text);
        result = ApplyValues(templateTranslation, values);
        return !string.IsNullOrEmpty(result);
    }

    private static string NormalizeBrackets(string s) =>
        s.Replace('「', '【').Replace('」', '】');

    private static string ToKeySkeleton(string s)
    {
        s = NormalizeBrackets(s);
        int i = 0;
        return KeySlot.Replace(s, _ => "{" + (i++) + "}");
    }

    private static string ToValueSkeleton(string s)
    {
        s = NormalizeBrackets(s);
        int i = 0;
        return ValueSlot.Replace(s, _ => "{" + (i++) + "}");
    }

    private static List<string> ExtractValues(string s)
    {
        s = NormalizeBrackets(s);
        var values = new List<string>();
        foreach (Match m in ValueSlot.Matches(s))
        {
            string v = m.Value;
            if (v.StartsWith('{') && v.EndsWith('}'))
                v = v[1..^1];
            values.Add(v);
        }
        return values;
    }

    private static string ApplyValues(string translation, List<string> values)
    {
        if (values.Count == 0)
            return translation;

        int i = 0;
        bool ok = true;
        string result = TranslationSlot.Replace(translation, _ =>
        {
            if (i >= values.Count)
            {
                ok = false;
                return _.Value;
            }
            return values[i++];
        });
        return ok ? result : null;
    }

    private static bool HasKana(string s)
    {
        foreach (char c in s)
        {
            if ((c >= '\u3040' && c <= '\u309F') || (c >= '\u30A0' && c <= '\u30FF'))
                return true;
        }
        return false;
    }
}
