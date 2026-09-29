#nullable enable

using System;
using System.Collections.Concurrent;

namespace AbyssMod.Services;

internal static class CharacterNovelTranslationPolicy
{
    public static bool CanLoadRemote(string novelId, string masterScriptId, bool isOpen)
    {
        string? family = GetFamily(novelId);
        return family == null
            || !string.Equals(family, GetFamily(masterScriptId), StringComparison.Ordinal)
            || isOpen;
    }

    // The prefix selects the master table (hmn_/hmr_ character novels, men_ home novels).
    // A men_ home novel shares its character and route key with the hmn_ rows, so men_ maps
    // to hmn_ while every other prefix keeps its own namespace.
    public static string? GetFamily(string novelId)
    {
        if (string.IsNullOrEmpty(novelId)
            || !char.IsDigit(novelId[novelId.Length - 1]))
            return null;

        int separator = novelId.IndexOf('_');
        if (separator < 0)
            return null;

        string prefix = novelId.Substring(0, separator);
        if (prefix == "men")
            prefix = "hmn";

        int start = separator + 1;
        string key = start < novelId.Length - 1
            ? novelId.Substring(start, novelId.Length - start - 1)
            : string.Empty;
        return prefix + ":" + key;
    }

    public static bool IsKnownCharacterScript(string novelId) =>
        novelId?.StartsWith("hmn_", StringComparison.Ordinal) == true
        || novelId?.StartsWith("hmr_", StringComparison.Ordinal) == true
        || novelId?.StartsWith("men_", StringComparison.Ordinal) == true;
}

internal sealed class NovelTranslationLoadPolicy
{
    private readonly TimeSpan _retryDelay;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _retryAfter = new();

    public NovelTranslationLoadPolicy(TimeSpan retryDelay)
    {
        _retryDelay = retryDelay;
    }

    public bool CanRequest(string novelId, DateTimeOffset now) =>
        !_retryAfter.TryGetValue(novelId, out var retryAfter) || now >= retryAfter;

    public void MarkFailed(string novelId, DateTimeOffset now)
    {
        _retryAfter[novelId] = now + _retryDelay;
    }

    public void MarkSucceeded(string novelId)
    {
        _retryAfter.TryRemove(novelId, out _);
    }
}
