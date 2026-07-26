#nullable enable

namespace AbyssMod.Patches;

internal static class NovelMessageRefreshPolicy
{
    public static bool ShouldProcessMessageWindow(
        bool translationEnabled,
        bool isRefreshReplay,
        string message
    ) => translationEnabled && !isRefreshReplay && !string.IsNullOrEmpty(message);

    public static bool ShouldProcessNovelText(
        bool translationEnabled,
        bool isRefreshReplay,
        string message
    ) => translationEnabled && !isRefreshReplay && !string.IsNullOrEmpty(message);

    public static bool ShouldTrackRefreshCandidate(
        bool translationEnabled,
        bool belongsToCurrentMessage,
        string source,
        string displayed
    ) => translationEnabled
        && belongsToCurrentMessage
        && !string.IsNullOrEmpty(source)
        && string.Equals(source, displayed, System.StringComparison.Ordinal);

    public static bool ShouldRefresh(
        bool translationEnabled,
        bool typewriterFinished,
        string source,
        string? lastRefreshedSource,
        string translated
    ) => translationEnabled
        && ShouldRefresh(typewriterFinished, source, lastRefreshedSource, translated);

    public static bool ShouldRefresh(
        bool typewriterFinished,
        string source,
        string? lastRefreshedSource,
        string translated
    ) => typewriterFinished
        && !string.IsNullOrEmpty(source)
        && !string.IsNullOrEmpty(translated)
        && !string.Equals(source, translated, System.StringComparison.Ordinal)
        && !string.Equals(source, lastRefreshedSource, System.StringComparison.Ordinal);
}
