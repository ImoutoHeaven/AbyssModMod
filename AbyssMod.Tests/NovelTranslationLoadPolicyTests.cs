using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NovelTranslationLoadPolicyTests
{
    [Fact]
    public void A_failed_load_can_retry_after_the_cooldown()
    {
        var policy = new NovelTranslationLoadPolicy(TimeSpan.FromSeconds(30));
        var failedAt = new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

        policy.MarkFailed("novel-1", failedAt);

        Assert.False(policy.CanRequest("novel-1", failedAt.AddSeconds(29)));
        Assert.True(policy.CanRequest("novel-1", failedAt.AddSeconds(30)));
    }

    [Fact]
    public void A_successful_load_clears_the_failure_cooldown()
    {
        var policy = new NovelTranslationLoadPolicy(TimeSpan.FromMinutes(1));
        var now = new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);
        policy.MarkFailed("novel-1", now);

        policy.MarkSucceeded("novel-1");

        Assert.True(policy.CanRequest("novel-1", now));
    }
}
