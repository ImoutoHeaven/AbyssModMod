using System.Collections.Generic;

namespace AbyssMod.Services;

/// <summary>
/// 将游戏内已代入数值的 UI 文本（如「スタミナを500消費」）匹配到
/// 字典中带 {0} 占位符的模板 key（如「スタミナを{0}消費する」）。
/// 算法与 <see cref="MachineTranslator"/> 的数字模板一致。
/// </summary>
public static class TemplateTextMatcher
{
    private static Dictionary<string, string> _exact = new();

    public static void Rebuild(params Dictionary<string, string>[] sources)
    {
        var merged = new Dictionary<string, string>();
        if (sources != null)
        {
            foreach (var source in sources)
            {
                if (source == null)
                    continue;
                foreach (var kv in source)
                    merged[kv.Key] = kv.Value;
            }
        }
        _exact = merged;
    }

    public static bool TryTranslate(string text, out string result)
    {
        result = null;
        if (string.IsNullOrEmpty(text) || _exact.Count == 0)
            return false;

        if (_exact.TryGetValue(text, out result))
            return true;

        var (template, numbers) = MachineTranslationTemplate.Normalize(text);
        if (template == text || !_exact.TryGetValue(template, out var translated))
            return false;

        result = MachineTranslationTemplate.Fill(translated, numbers);
        return !string.IsNullOrEmpty(result);
    }
}
