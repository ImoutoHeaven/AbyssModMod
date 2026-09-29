using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NovelTranslationLoadPolicyTests
{
    [Fact]
    public void Character_novel_segments_wait_for_the_character_open_date()
    {
        Assert.False(CharacterNovelTranslationPolicy.CanLoadRemote(
            "hmn_11120100002", "hmn_11120100002", isOpen: false));
        Assert.False(CharacterNovelTranslationPolicy.CanLoadRemote(
            "hmr_11120100022", "hmr_11120100021", isOpen: false));
        Assert.False(CharacterNovelTranslationPolicy.CanLoadRemote(
            "men_11120100002", "men_11120100001", isOpen: false));
        Assert.True(CharacterNovelTranslationPolicy.CanLoadRemote(
            "men_10080100001", "hmn_10080100001", isOpen: true));
        Assert.False(CharacterNovelTranslationPolicy.CanLoadRemote(
            "men_10080100001", "hmn_10080100001", isOpen: false));
        Assert.True(CharacterNovelTranslationPolicy.CanLoadRemote(
            "hmr_11120100022",
            "hmr_11120100021",
            isOpen: true));
        Assert.False(CharacterNovelTranslationPolicy.CanLoadRemote(
            "future_character_00002",
            "future_character_00001",
            isOpen: false));
        Assert.True(CharacterNovelTranslationPolicy.CanLoadRemote(
            "main_01001", "hmn_11120100002", isOpen: false));
    }

    [Fact]
    public void Character_prefixes_stay_isolated_on_the_same_numeric_key()
    {
        Assert.True(CharacterNovelTranslationPolicy.CanLoadRemote(
            "mas_10080100002", "hmn_10080100001", isOpen: false));
        Assert.True(CharacterNovelTranslationPolicy.CanLoadRemote(
            "hmr_10080100001", "hmn_10080100001", isOpen: false));
        Assert.False(CharacterNovelTranslationPolicy.CanLoadRemote(
            "hmn_1", "hmn_2", isOpen: false));
        Assert.True(CharacterNovelTranslationPolicy.CanLoadRemote(
            "hmn_1", "hmr_2", isOpen: false));
        Assert.True(CharacterNovelTranslationPolicy.CanLoadRemote(
            "hmn_1008010000x", "hmn_10080100001", isOpen: false));
        Assert.True(CharacterNovelTranslationPolicy.CanLoadRemote(
            "hmn_", "hmn_10080100001", isOpen: false));
        Assert.True(CharacterNovelTranslationPolicy.CanLoadRemote(
            null!, "hmn_10080100001", isOpen: false));
    }

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
