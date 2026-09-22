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

    public static string? GetFamily(string novelId)
    {
        if (string.IsNullOrEmpty(novelId)
            || novelId.IndexOf('_') < 0
            || !char.IsDigit(novelId[novelId.Length - 1]))
            return null;

        return novelId.Substring(0, novelId.Length - 1);
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
