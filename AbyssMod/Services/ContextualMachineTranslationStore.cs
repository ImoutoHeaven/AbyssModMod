using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AbyssMod.Services;

public static class ContextualMachineTranslationProtocol
{
    public const int CurrentVersion = 1;

    public static string BuildPendingKey(string transformPath, string sourceTemplate)
    {
        byte[] payload = Encoding.UTF8.GetBytes($"{transformPath}\0{sourceTemplate}");
        return $"ui:v{CurrentVersion}:{Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant()}";
    }

    public static string BuildUserPrompt(string transformPath, string sourceTemplate) =>
        "界面路径（仅用于语境消歧，不要翻译路径）：\n"
        + transformPath
        + "\n待翻译日文原文：\n"
        + sourceTemplate;
}

public sealed class ContextualMachineTranslationDocument
{
    public int Version { get; set; }

    public Dictionary<string, Dictionary<string, string>> Entries { get; set; } = new(
        StringComparer.Ordinal
    );
}

/// <summary>
/// Versioned path-and-template machine translation cache. It intentionally refuses the old
/// source-only JSON shape so ambiguous legacy UI entries can never leak into contextual lookups.
/// </summary>
public sealed class ContextualMachineTranslationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private readonly object _lock = new();
    private readonly Dictionary<string, Dictionary<string, string>> _entries;

    public ContextualMachineTranslationStore()
        : this(new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)) { }

    private ContextualMachineTranslationStore(
        Dictionary<string, Dictionary<string, string>> entries
    )
    {
        _entries = entries;
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                int count = 0;
                foreach (var pathEntries in _entries.Values)
                    count += pathEntries?.Count ?? 0;
                return count;
            }
        }
    }

    public bool TryGet(
        string transformPath,
        string sourceTemplate,
        out string translatedTemplate
    )
    {
        translatedTemplate = null;
        lock (_lock)
        {
            return _entries.TryGetValue(transformPath, out var pathEntries)
                && pathEntries.TryGetValue(sourceTemplate, out translatedTemplate)
                && !string.IsNullOrEmpty(translatedTemplate);
        }
    }

    public void Set(string transformPath, string sourceTemplate, string translatedTemplate)
    {
        if (
            string.IsNullOrEmpty(transformPath)
            || string.IsNullOrEmpty(sourceTemplate)
            || string.IsNullOrEmpty(translatedTemplate)
        )
            return;

        lock (_lock)
        {
            if (!_entries.TryGetValue(transformPath, out var pathEntries))
            {
                pathEntries = new Dictionary<string, string>(StringComparer.Ordinal);
                _entries.Add(transformPath, pathEntries);
            }
            pathEntries[sourceTemplate] = translatedTemplate;
        }
    }

    public string Serialize()
    {
        lock (_lock)
        {
            var entries = new Dictionary<string, Dictionary<string, string>>(
                StringComparer.Ordinal
            );
            foreach (var (path, pathEntries) in _entries)
                entries[path] = new Dictionary<string, string>(pathEntries, StringComparer.Ordinal);

            return JsonSerializer.Serialize(
                new ContextualMachineTranslationDocument
                {
                    Version = ContextualMachineTranslationProtocol.CurrentVersion,
                    Entries = entries,
                },
                JsonOptions
            );
        }
    }

    public static bool TryDeserialize(
        string json,
        out ContextualMachineTranslationStore store
    )
    {
        store = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            var document = JsonSerializer.Deserialize<ContextualMachineTranslationDocument>(
                json,
                JsonOptions
            );
            if (
                document?.Version != ContextualMachineTranslationProtocol.CurrentVersion
                || document.Entries == null
            )
                return false;

            var entries = new Dictionary<string, Dictionary<string, string>>(
                StringComparer.Ordinal
            );
            foreach (var (path, pathEntries) in document.Entries)
            {
                if (string.IsNullOrEmpty(path) || pathEntries == null)
                    continue;
                entries[path] = new Dictionary<string, string>(pathEntries, StringComparer.Ordinal);
            }

            store = new ContextualMachineTranslationStore(entries);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
