using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AbyssMod.Services;

/// <summary>
/// 翻译资源路径构建工具。
/// 负责生成远程 URL 和本地缓存路径。
/// </summary>
public static class TranslationPaths
{
    // ──────────────────────────────────────────────────
    // 类型常量（与作者 CDN 仓库一致）
    // ──────────────────────────────────────────────────
    public const string Manifest            = "manifest";
    public const string Names               = "names";
    public const string Titles              = "titles";
    public const string Descriptions        = "descriptions";
    public const string AnotherName         = "another_name";
    public const string AbilityDescriptions = "ability_descriptions";
    public const string Novels              = "novels";
    public const string Static              = "static";
    public const string UiTexts             = "ui_texts";

    // 本仓库自定义类型（作者 CDN 无此目录，不会被覆盖，靠本地文件兜底）
    public const string Items  = "items";
    public const string Ui     = "ui";
    public const string Other  = "other";

    /// <summary>所有本地自定义类别的统一容器目录（translations/add-on/）。</summary>
    public const string AddOn = "add-on";

    /// <summary>由 master_mapping.json dict_types 驱动，启动时由 MasterMapping.Load 填充。</summary>
    public static IReadOnlyList<string> ContentTypes { get; private set; } = [];

    public static void SetContentTypes(List<string> types) => ContentTypes = types;

    /// <summary>上游 v1.0.8 唯一允许的远程资源类型。</summary>
    public static bool IsCdnFlatType(string type) =>
        string.Equals(type, Manifest, StringComparison.Ordinal)
        || string.Equals(type, Names, StringComparison.Ordinal)
        || string.Equals(type, Novels, StringComparison.Ordinal)
        || string.Equals(type, Static, StringComparison.Ordinal)
        || string.Equals(type, UiTexts, StringComparison.Ordinal);

    // ──────────────────────────────────────────────────
    // URL / 路径构建
    // ──────────────────────────────────────────────────

    public static string BuildRemoteUrl(string cdn, string type, string language, string id = null)
    {
        return type switch
        {
            Novels when id != null => $"{cdn}/{Novels}/{id}/{language}.json",
            Novels => throw new ArgumentException("Novel ID is required for novels type"),
            _ => $"{cdn}/{type}/{language}.json",
        };
    }

    public static string BuildAddOnCachePath(string cacheDir, string category, string language) =>
        Path.Combine(cacheDir, AddOn, category, $"{language}.json");

    public static string BuildCachePath(string cacheDir, string type, string language, string id = null)
    {
        if (type == Novels && id != null)
            return Path.Combine(cacheDir, language, Novels, $"{id}.json");
        if (type == Novels)
            throw new ArgumentException("Novel ID is required for novels type");

        return Path.Combine(cacheDir, language, $"{type}.json");
    }

    public static IEnumerable<string> EnumerateLocalCategories(string cacheDir, string language)
    {
        var addOnDir = Path.Combine(cacheDir, AddOn);
        if (!Directory.Exists(addOnDir))
            yield break;

        foreach (var dir in Directory.GetDirectories(addOnDir))
        {
            var langFile = Path.Combine(dir, $"{language}.json");
            if (File.Exists(langFile))
                yield return Path.GetFileName(dir);
        }
    }

    /// <summary>master_mapping dict_types 中尚未列入 ReservedTypes 的类型（用于增量加载）。</summary>
    public static IEnumerable<string> EnumerateMasterDictTypes() =>
        ContentTypes.Where(t => !string.Equals(t, Novels, StringComparison.OrdinalIgnoreCase));
}
