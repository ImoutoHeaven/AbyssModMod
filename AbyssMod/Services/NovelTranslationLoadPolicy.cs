using System;
using System.Collections.Concurrent;

namespace AbyssMod.Services;

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
