#nullable enable

using System;
using System.Collections.Generic;

namespace AbyssMod.Patches;

public static class NovelTextTranslation
{
    private const string UserPlaceholder = "<user>";

    public static bool TryTranslate(
        Dictionary<string, string> translations,
        string source,
        string displayName,
        out string translated
    )
    {
        translated = source;
        if (translations == null || string.IsNullOrEmpty(source))
            return false;
        if (!translations.TryGetValue(source, out string? value) || string.IsNullOrEmpty(value))
            return false;

        translated = ExpandUserPlaceholder(value, displayName);
        return true;
    }

    public static string ExpandUserPlaceholder(string value, string displayName)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(displayName))
            return value;

        return value.Replace(UserPlaceholder, displayName, StringComparison.Ordinal);
    }
}
