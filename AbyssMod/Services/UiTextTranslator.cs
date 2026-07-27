using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace AbyssMod.Services;

/// <summary>
/// ui_texts 查表：原文 key → 译文；或 Transform 路径 key → 译文。
/// </summary>
public static class UiTextTranslator
{
    private static bool _loadRequested;
    private static readonly UiTranslatedValueCache TranslatedValueCache = new();

    public static string Translate(TMP_Text text, string value)
    {
        if (!Config.Translation.Value || Plugin.Trans == null || value == null)
            return value;
        if (value.Length == 0)
            return value;

        Dictionary<string, string> table = GetTable();
        if (table == null || table.Count == 0)
            return value;

        if (IsAlreadyTranslated(table, value))
            return value;

        if (table.TryGetValue(value, out string hit) && !string.IsNullOrEmpty(hit))
            return hit;

        string path = GetTransformPath(text != null ? text.transform : null);
        if (
            !string.IsNullOrEmpty(path)
            && table.TryGetValue(path, out hit)
            && !string.IsNullOrEmpty(hit)
        )
            return hit;

        return value;
    }

    private static bool IsAlreadyTranslated(Dictionary<string, string> table, string value)
    {
        return TranslatedValueCache.Contains(table, value);
    }

    private static Dictionary<string, string> GetTable()
    {
        if (!_loadRequested)
        {
            _loadRequested = true;
            try
            {
                _ = Plugin.Trans.EnsureStaticTranslationsLoadedAsync();
            }
            catch (Exception ex)
            {
                Logger.Warn($"UI text translation load request skipped: {ex.Message}");
            }
        }
        return Plugin.Trans.GetTable(TranslationPaths.UiTexts);
    }

    private static string GetTransformPath(Transform transform)
    {
        if (transform == null)
            return null;

        var stack = new Stack<string>();
        for (Transform t = transform; t != null; t = t.parent)
            stack.Push(t.name);
        return string.Join("/", stack);
    }
}
