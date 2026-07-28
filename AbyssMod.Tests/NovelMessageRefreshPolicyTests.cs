using AbyssMod.Patches;
using Xunit;

namespace AbyssMod.Tests;

public class NovelMessageRefreshPolicyTests
{
    [Fact]
    public void Refresh_waits_until_the_current_message_has_finished_typing()
    {
        Assert.False(NovelMessageRefreshPolicy.ShouldRefresh(
            typewriterFinished: false,
            source: "わわわわ～～～っ！",
            lastRefreshedSource: null,
            translated: "哇哇哇哇～～～！"));
    }

    [Fact]
    public void Refreshes_an_unmodified_completed_message_once_translation_is_available()
    {
        Assert.True(NovelMessageRefreshPolicy.ShouldRefresh(
            typewriterFinished: true,
            source: "わわわわ～～～っ！",
            lastRefreshedSource: null,
            translated: "哇哇哇哇～～～！"));
    }

    [Fact]
    public void Does_not_refresh_the_same_message_twice()
    {
        Assert.False(NovelMessageRefreshPolicy.ShouldRefresh(
            typewriterFinished: true,
            source: "わわわわ～～～っ！",
            lastRefreshedSource: "わわわわ～～～っ！",
            translated: "哇哇哇哇～～～！"));
    }

    [Fact]
    public void Does_not_track_a_parse_from_another_novel_text_component()
    {
        Assert.False(NovelMessageRefreshPolicy.ShouldTrackRefreshCandidate(
            translationEnabled: true,
            belongsToCurrentMessage: false,
            source: "わわわわ～～～っ！",
            displayed: "わわわわ～～～っ！"));
    }

    [Fact]
    public void Does_not_track_or_refresh_when_translation_is_disabled()
    {
        Assert.False(NovelMessageRefreshPolicy.ShouldTrackRefreshCandidate(
            translationEnabled: false,
            belongsToCurrentMessage: true,
            source: "わわわわ～～～っ！",
            displayed: "わわわわ～～～っ！"));

        Assert.False(NovelMessageRefreshPolicy.ShouldRefresh(
            translationEnabled: false,
            typewriterFinished: true,
            source: "わわわわ～～～っ！",
            lastRefreshedSource: null,
            translated: "哇哇哇哇～～～！"));
    }

    [Fact]
    public void Processes_a_main_window_message_once_when_translation_is_enabled()
    {
        Assert.True(NovelMessageRefreshPolicy.ShouldProcessMessageWindow(
            translationEnabled: true,
            isRefreshReplay: false,
            message: "わわわわ～～～っ！"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Does_not_process_disabled_or_refresh_replayed_window_messages(
        bool translationEnabled,
        bool isRefreshReplay
    )
    {
        Assert.False(NovelMessageRefreshPolicy.ShouldProcessMessageWindow(
            translationEnabled,
            isRefreshReplay,
            "わわわわ～～～っ！"));
    }

    [Fact]
    public void Processes_a_complete_novel_text_when_translation_is_enabled()
    {
        Assert.True(NovelMessageRefreshPolicy.ShouldProcessNovelText(
            translationEnabled: true,
            isRefreshReplay: false,
            message: "わわわわ～～～っ！"));
    }

    [Fact]
    public void Tracks_an_untranslated_complete_message_from_the_current_window()
    {
        Assert.True(NovelMessageRefreshPolicy.ShouldTrackRefreshCandidate(
            translationEnabled: true,
            belongsToCurrentMessage: true,
            source: "わわわわ～～～っ！",
            displayed: "わわわわ～～～っ！"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Captures_disabled_novel_text_but_ignores_refresh_replays(
        bool translationEnabled,
        bool isRefreshReplay
    )
    {
        Assert.Equal(!isRefreshReplay, NovelMessageRefreshPolicy.ShouldProcessNovelText(
            translationEnabled,
            isRefreshReplay,
            "わわわわ～～～っ！"));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public void Resets_current_state_only_for_a_new_main_window_parse(
        bool belongsToCurrentMessage,
        bool isRefreshReplay,
        bool expected
    )
    {
        Assert.Equal(expected, NovelMessageRefreshPolicy.ShouldResetCurrentMessage(
            belongsToCurrentMessage,
            isRefreshReplay
        ));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("静态译文", true)]
    public void Uses_only_nonempty_static_translations_as_authoritative(
        string? translated,
        bool expected
    )
    {
        Assert.Equal(expected, NovelMessageRefreshPolicy.HasAuthoritativeTranslation(translated));
    }
}
