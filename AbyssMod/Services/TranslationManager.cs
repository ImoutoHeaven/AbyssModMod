using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AbyssMod;
using AbyssMod.Patches;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using TMPro;
using Utility.Fonts;
using Utility.Toast;

namespace AbyssMod.Services;

/// <summary>
/// 翻译管理器：协调 masterdata 字典（m_*）、ui_texts 与 legacy 兜底字典的加载与查询。
/// </summary>
public class TranslationManager
{
    private static readonly HashSet<string> CriticalTypes =
    [
        TranslationPaths.Names,
        TranslationPaths.UiTexts,
    ];

    private readonly TranslationCache _cache;
    private readonly FontHelper _font;
    private readonly object _loadLock = new();
    private Task _loadTask;

    private readonly ConcurrentDictionary<string, Task> _loadingNovels = new();
    private readonly Dictionary<string, Dictionary<string, string>> _tables = new();
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, string>>> _fieldTables =
        new();

    public Dictionary<string, string> Names { get; private set; } = [];
    public Dictionary<string, string> Titles { get; private set; } = [];
    public Dictionary<string, string> Descriptions { get; private set; } = [];

    /// <summary>非剧情类文本的合并字典（ui_misc 及所有本地类别兜底）。</summary>
    public Dictionary<string, string> Texts { get; private set; } = [];

    public Dictionary<string, string> AbilityDescriptions { get; private set; } = [];
    public ConcurrentDictionary<string, Dictionary<string, string>> Novels { get; private set; } =
        new();

    public FontHelper Font => _font;

    public TranslationManager(TranslationCache cache, FontHelper font)
    {
        _cache = cache;
        _font = font;
    }

    public void Initialize()
    {
        Plugin.Instance.StartCoroutine(
            _font
                .LoadAsync(() =>
                {
                    Logger.Info($"Font loaded: {_font.Asset.name}");
                    TMP_Settings.fallbackFontAssets.Add(_font.Asset);
                })
                .WrapToIl2Cpp()
        );
        _ = EnsureStaticTranslationsLoadedAsync();
    }

    public Task EnsureStaticTranslationsLoadedAsync()
    {
        lock (_loadLock)
            return _loadTask ??= LoadTranslationAsync();
    }

    public void EnsureStaticTranslationsLoaded()
    {
        EnsureStaticTranslationsLoadedAsync().GetAwaiter().GetResult();
    }

    public Dictionary<string, string> GetTable(string type)
    {
        return _tables.TryGetValue(type, out var table) ? table : null;
    }

    public Dictionary<string, string> GetFieldTable(string type, string field)
    {
        return StaticBundleProtocol.GetFieldTable(_fieldTables, type, field) ?? GetTable(type);
    }

    public async Task LoadTranslationAsync()
    {
        if (!Config.Translation.Value)
            return;

        await _cache.FetchManifestAsync();

        var bundle = await _cache.LoadStaticBundleAsync();
        if (bundle != null)
        {
            foreach (var (type, fields) in bundle)
            {
                _fieldTables[type] = fields;
                _tables[type] = StaticBundleProtocol.Flatten(fields);
            }
            Logger.Info($"Static translation bundle loaded. Tables: {_fieldTables.Count}");
        }
        else
        {
            Logger.Warn("MasterData static translation bundle load failed.");
            Toast.Warn("加载失败", "MasterData 静态翻译合并包加载失败");
        }

        var tasks = new Dictionary<string, Task<Dictionary<string, string>>>
        {
            [TranslationPaths.Names] = _cache.LoadAsync(TranslationPaths.Names),
            [TranslationPaths.UiTexts] = _cache.LoadAsync(TranslationPaths.UiTexts),
        };

        await Task.WhenAll(tasks.Values);

        foreach (var (type, task) in tasks)
        {
            if (task.Result != null)
            {
                _tables[type] = task.Result;
                Logger.Info($"Translation loaded [{type}]. Total: {task.Result.Count}");
            }
            else
            {
                Logger.Warn($"Translation load failed [{type}]");
                if (CriticalTypes.Contains(type))
                    Toast.Warn("加载失败", $"翻译加载失败: {type}");
            }
        }

        Names = GetTable(TranslationPaths.Names) ?? [];
        Titles = [];
        Descriptions = [];
        AbilityDescriptions = GetFieldTable("m_ability_details", "description") ?? [];

        MachineTranslator.ReloadFromDisk();

        await MergeLocalAddOnFallbackAsync();
        AbilityTextMatcher.Rebuild(AbilityDescriptions);
        TemplateTextMatcher.Rebuild(Texts, Titles, Descriptions);
    }

    private async Task MergeLocalAddOnFallbackAsync()
    {
        var merged = new Dictionary<string, string>();
        var masterKeys = BuildMasterDataKeySet();

        void MergeTable(string type, ISet<string> skipKeys = null)
        {
            if (_tables.TryGetValue(type, out var table))
                MergeInto(merged, table, type, skipKeys);
        }

        var language = Config.TranslationLanguage.Value;
        var localCategories = TranslationPaths
            .EnumerateLocalCategories(_cache.CacheDir, language)
            .Where(cat => cat != TranslationPaths.Ui)
            .ToList();

        // 本地 add-on 只补充上游 static bundle 未覆盖的文本。
        if (localCategories.Count > 0)
        {
            var localTasks = localCategories.Select(cat => _cache.LoadAsync(cat)).ToList();
            await Task.WhenAll(localTasks);
            for (int i = 0; i < localCategories.Count; i++)
                MergeInto(
                    merged,
                    localTasks[i].Result,
                    $"add-on/{localCategories[i]}",
                    masterKeys
                );
        }

        // 上游 static bundle 是所有 MasterData 字典的权威层。
        foreach (string type in _fieldTables.Keys)
        {
            if (type.StartsWith("m_", StringComparison.Ordinal))
                MergeTable(type);
        }

        // names / ui_texts 仅补 MasterData 未覆盖的 key。
        MergeTable(TranslationPaths.Names, masterKeys);
        MergeTable(TranslationPaths.UiTexts, masterKeys);

        Texts = merged;
        Titles = Texts;
        Descriptions = Texts;
        Logger.Info(
            $"Non-story text fallback merged. Total: {Texts.Count} "
                + $"(add-on: {localCategories.Count}, m_* keys: {masterKeys.Count})"
        );
    }

    private HashSet<string> BuildMasterDataKeySet()
    {
        var keys = new HashSet<string>();
        foreach (string type in _fieldTables.Keys)
        {
            if (!type.StartsWith("m_", StringComparison.Ordinal))
                continue;
            if (!_tables.TryGetValue(type, out var table))
                continue;
            foreach (string key in table.Keys)
                keys.Add(key);
        }
        return keys;
    }

    private static void MergeInto(
        Dictionary<string, string> target,
        Dictionary<string, string> source,
        string label,
        ISet<string> skipKeys = null
    )
    {
        if (source == null || source.Count == 0)
            return;

        int merged = 0;
        foreach (var kv in source)
        {
            if (skipKeys != null && skipKeys.Contains(kv.Key))
                continue;
            target[kv.Key] = kv.Value;
            merged++;
        }

        Logger.Info($"Text fallback '{label}' merged. Added: {merged} (source: {source.Count})");
    }

    public async Task GetNovelTranslationAsync(string novelId)
    {
        if (Novels.ContainsKey(novelId))
            return;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var existingTask = _loadingNovels.GetOrAdd(novelId, tcs.Task);

        if (existingTask != tcs.Task)
        {
            await existingTask;
            return;
        }

        try
        {
            var translations = await _cache.LoadAsync(TranslationPaths.Novels, novelId);
            if (translations != null)
            {
                Novels[novelId] = translations;
                Logger.Info($"Scenario translation loaded. Total: {translations.Count}");
            }
            else
            {
                Logger.Warn($"Translations loaded failed: {novelId}");
                Toast.Warn("加载失败", $"剧本ID: {novelId}");
            }
            tcs.SetResult();
        }
        catch (Exception ex)
        {
            tcs.SetException(ex);
            throw;
        }
        finally
        {
            _loadingNovels.TryRemove(novelId, out _);
        }
    }
}
