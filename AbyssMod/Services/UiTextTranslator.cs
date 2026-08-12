using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace AbyssMod.Services;

/// <summary>
/// ui_texts 路径化递归查表：Transform 路径 → 原文/模板 → 译文。
/// </summary>
public static class UiTextTranslator
{
    private static bool _loadRequested;
    public static string Translate(TMP_Text text, string value)
    {
        if (!Config.Translation.Value || Plugin.Trans == null || value == null)
            return value;
        if (value.Length == 0)
            return value;

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

        string path = GetTransformPath(text);
        return !string.IsNullOrEmpty(path)
            && Plugin.Trans.TryTranslateUiText(path, value, out var translated)
            ? translated
            : value;
    }

    public static string GetTransformPath(TMP_Text text) =>
        GetTransformPath(text != null ? text.transform : null);

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
