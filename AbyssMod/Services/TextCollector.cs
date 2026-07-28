using System.IO;
using BepInEx;

namespace AbyssMod.Services;

/// <summary>
/// 原文收集器：把游戏运行时出现的日文原文按类别写入 dump 目录，
/// 输出格式为 { "日文原文": "" }，方便后续批量翻译填充 value。
/// 每个类别（items / ui / skills ...）各自落盘到 dump/{category}_raw.json。
/// 仅在 Config.CollectText 开启时工作。
/// </summary>
public static class TextCollector
{
    private static readonly object Lock = new();

    private static string DumpPath(string category) =>
        Path.Combine(Paths.PluginPath, MyPluginInfo.PLUGIN_GUID, "dump", $"{category}_raw.json");

    /// <summary>
    /// 记录一条原文到指定类别。重复或空字符串会被忽略。发现新条目时立即落盘。
    /// </summary>
    public static void Record(string category, string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        lock (Lock)
        {
            try
            {
                var path = DumpPath(category);
                TextCollectionStore.Add(path, text);
            }
            catch
            {
                // 收集失败不应影响游戏运行
            }
        }
    }
}
