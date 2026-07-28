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
    private sealed class TranslationSnapshot
    {
        public static readonly TranslationSnapshot Empty = new(
            new Dictionary<string, Dictionary<string, string>>(),
            new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(),
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            new Dictionary<string, string>()
        );

        public TranslationSnapshot(
            Dictionary<string, Dictionary<string, string>> tables,
            Dictionary<string, Dictionary<string, Dictionary<string, string>>> fieldTables,
            Dictionary<string, string> names,
            Dictionary<string, string> titles,
            Dictionary<string, string> descriptions,
            Dictionary<string, string> texts,
            Dictionary<string, string> abilityDescriptions
        )
        {
            Tables = tables;
            FieldTables = fieldTables;
            Names = names;
            Titles = titles;
            Descriptions = descriptions;
            Texts = texts;
            AbilityDescriptions = abilityDescriptions;
        }

        public Dictionary<string, Dictionary<string, string>> Tables { get; }
        public Dictionary<string, Dictionary<string, Dictionary<string, string>>> FieldTables { get; }
        public Dictionary<string, string> Names { get; }
        public Dictionary<string, string> Titles { get; }
        public Dictionary<string, string> Descriptions { get; }
        public Dictionary<string, string> Texts { get; }
        public Dictionary<string, string> AbilityDescriptions { get; }
    }

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
    private readonly NovelTranslationLoadPolicy _novelLoadPolicy = new(TimeSpan.FromSeconds(30));
    private volatile TranslationSnapshot _snapshot = TranslationSnapshot.Empty;

    public Dictionary<string, string> Names => _snapshot.Names;
    public Dictionary<string, string> Titles => _snapshot.Titles;
    public Dictionary<string, string> Descriptions => _snapshot.Descriptions;

    /// <summary>非剧情类文本的合并字典（ui_misc 及所有本地类别兜底）。</summary>
    public Dictionary<string, string> Texts => _snapshot.Texts;

    public Dictionary<string, string> AbilityDescriptions => _snapshot.AbilityDescriptions;
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
        var snapshot = _snapshot;
        return snapshot.Tables.TryGetValue(type, out var table) ? table : null;
    }

    public Dictionary<string, string> GetFieldTable(string type, string field)
    {
        var snapshot = _snapshot;
        return StaticBundleProtocol.GetFieldTable(snapshot.FieldTables, type, field)
            ?? (snapshot.Tables.TryGetValue(type, out var table) ? table : null);
    }

    public async Task LoadTranslationAsync()
    {
        var loadedTables = new Dictionary<string, Dictionary<string, string>>();
        var loadedFieldTables =
            new Dictionary<string, Dictionary<string, Dictionary<string, string>>>();

        await _cache.FetchManifestAsync();

        var bundle = await _cache.LoadStaticBundleAsync();
        if (bundle != null)
        {
            foreach (var (type, fields) in bundle)
            {
                loadedFieldTables[type] = fields;
                loadedTables[type] = StaticBundleProtocol.Flatten(fields);
            }
            Logger.Info($"Static translation bundle loaded. Tables: {loadedFieldTables.Count}");
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
                loadedTables[type] = task.Result;
                Logger.Info($"Translation loaded [{type}]. Total: {task.Result.Count}");
            }
            else
            {
                Logger.Warn($"Translation load failed [{type}]");
                if (CriticalTypes.Contains(type))
                    Toast.Warn("加载失败", $"翻译加载失败: {type}");
            }
        }

        var loadedNames = loadedTables.TryGetValue(TranslationPaths.Names, out var names)
            ? names
            : new Dictionary<string, string>();
        var loadedAbilityDescriptions = StaticBundleProtocol.GetFieldTable(
            loadedFieldTables,
            "m_ability_details",
            "description"
        );
        if (loadedAbilityDescriptions == null)
            loadedAbilityDescriptions = loadedTables.TryGetValue(
                "m_ability_details",
                out var abilityDetails
            )
                ? abilityDetails
                : new Dictionary<string, string>();

        MachineTranslator.ReloadFromDisk();

        var loadedTexts = await BuildLocalAddOnFallbackAsync(loadedTables, loadedFieldTables);
        AbilityTextMatcher.Rebuild(loadedAbilityDescriptions);
        TemplateTextMatcher.Rebuild(loadedTexts);

        _snapshot = new TranslationSnapshot(
            loadedTables,
            loadedFieldTables,
            loadedNames,
            loadedTexts,
            loadedTexts,
            loadedTexts,
            loadedAbilityDescriptions
        );
    }

    private async Task<Dictionary<string, string>> BuildLocalAddOnFallbackAsync(
        Dictionary<string, Dictionary<string, string>> tables,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> fieldTables
    )
    {
        var merged = new Dictionary<string, string>();
        var masterKeys = BuildMasterDataKeySet(tables, fieldTables);

        void MergeTable(string type, ISet<string> skipKeys = null)
        {
            if (tables.TryGetValue(type, out var table))
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
        foreach (string type in fieldTables.Keys)
        {
            if (type.StartsWith("m_", StringComparison.Ordinal))
                MergeTable(type);
        }

        // names / ui_texts 仅补 MasterData 未覆盖的 key。
        MergeTable(TranslationPaths.Names, masterKeys);
        MergeTable(TranslationPaths.UiTexts, masterKeys);

        Logger.Info(
            $"Non-story text fallback merged. Total: {merged.Count} "
                + $"(add-on: {localCategories.Count}, m_* keys: {masterKeys.Count})"
        );
        return merged;
    }

    private static HashSet<string> BuildMasterDataKeySet(
        Dictionary<string, Dictionary<string, string>> tables,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> fieldTables
    )
    {
        var keys = new HashSet<string>();
        foreach (string type in fieldTables.Keys)
        {
            if (!type.StartsWith("m_", StringComparison.Ordinal))
                continue;
            if (!tables.TryGetValue(type, out var table))
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
                _novelLoadPolicy.MarkSucceeded(novelId);
                Logger.Info($"Scenario translation loaded. Total: {translations.Count}");
            }
            else
            {
                Logger.Warn($"Translations loaded failed: {novelId}");
                Toast.Warn("加载失败", $"剧本ID: {novelId}");
                _novelLoadPolicy.MarkFailed(novelId, DateTimeOffset.UtcNow);
            }
            tcs.SetResult();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Scenario translation load failed [{novelId}]: {ex.Message}");
            _novelLoadPolicy.MarkFailed(novelId, DateTimeOffset.UtcNow);
            tcs.SetResult();
        }
        finally
        {
            _loadingNovels.TryRemove(novelId, out _);
        }
    }

    public void RequestNovelTranslation(string novelId)
    {
        if (string.IsNullOrEmpty(novelId)
            || Novels.ContainsKey(novelId)
            || _loadingNovels.ContainsKey(novelId)
            || !_novelLoadPolicy.CanRequest(novelId, DateTimeOffset.UtcNow))
            return;

        _ = Task.Run(() => GetNovelTranslationAsync(novelId));
    }

    public void EnsureNovelTranslationLoaded(string novelId)
    {
        if (string.IsNullOrEmpty(novelId)
            || Novels.ContainsKey(novelId)
            || !_novelLoadPolicy.CanRequest(novelId, DateTimeOffset.UtcNow))
            return;

        GetNovelTranslationAsync(novelId).GetAwaiter().GetResult();
    }

}
