using Xunit;

namespace AbyssMod.Tests;

public class TranslationLoadingPolicyTests
{
    [Fact]
    public void Static_resources_load_independently_of_the_display_toggle()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "AbyssMod", "Services", "TranslationManager.cs")
        );
        var start = source.IndexOf("public async Task LoadTranslationAsync()", StringComparison.Ordinal);
        var end = source.IndexOf("private async Task<Dictionary<string, string>> BuildLocalAddOnFallbackAsync", start, StringComparison.Ordinal);
        var method = source.Substring(start, end - start);

        Assert.DoesNotContain("Config.Translation.Value", method);
    }

    [Fact]
    public void Current_novel_refresh_requests_loading_without_blocking_the_unity_thread()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "AbyssMod", "Patches", "TranslationPatch.cs")
        );
        var start = source.IndexOf("private static void RefreshStaticMessage()", StringComparison.Ordinal);
        var end = source.IndexOf("private static void RefreshMachineTranslationMessage()", start, StringComparison.Ordinal);
        var method = source.Substring(start, end - start);

        Assert.Contains("RequestCurrentNovelTranslation();", method);
        Assert.DoesNotContain(".Wait()", method);
        Assert.DoesNotContain("!Plugin.Trans.Novels.ContainsKey", method);

        var managerSource = File.ReadAllText(
            Path.Combine(root, "AbyssMod", "Services", "TranslationManager.cs")
        );
        Assert.Contains("Task.Run", managerSource);
    }

    [Fact]
    public void Enabled_novel_entry_waits_for_translation_before_rendering()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "AbyssMod", "Patches", "TranslationPatch.cs")
        );
        var start = source.IndexOf("public static void SetupTranslation", StringComparison.Ordinal);
        var end = source.IndexOf("[HarmonyPostfix]", start + 1, StringComparison.Ordinal);
        var method = source.Substring(start, end - start);

        Assert.Contains("EnsureNovelTranslationLoaded", method);
    }

    [Fact]
    public void Machine_translation_handle_checks_the_display_toggle_before_cache_lookup()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "AbyssMod", "Services", "MachineTranslator.cs")
        );
        var start = source.IndexOf("public static string Handle", StringComparison.Ordinal);
        var cacheLookup = source.IndexOf("_cache.TryGetValue", start, StringComparison.Ordinal);
        var beforeCacheLookup = source.Substring(start, cacheLookup - start);

        Assert.Contains("Config.Translation.Value", beforeCacheLookup);
    }

    [Fact]
    public void Static_tables_are_published_only_after_a_complete_snapshot_is_built()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "AbyssMod", "Services", "TranslationManager.cs")
        );
        var start = source.IndexOf("public async Task LoadTranslationAsync()", StringComparison.Ordinal);
        var end = source.IndexOf("private async Task", start + 1, StringComparison.Ordinal);
        var method = source.Substring(start, end - start);

        Assert.Contains("_snapshot = new TranslationSnapshot(", method);
        Assert.DoesNotContain("_tables[type] =", method);
        Assert.DoesNotContain("_fieldTables[type] =", method);
    }

    [Fact]
    public void A_resolved_current_message_fallback_cancels_the_old_machine_candidate()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "AbyssMod", "Patches", "TranslationPatch.cs")
        );
        var start = source.IndexOf("private static void ResolveCurrentMessageFallback()", StringComparison.Ordinal);
        var end = source.IndexOf("private static void TrackMachineTranslationMessage", start, StringComparison.Ordinal);
        var method = source.Substring(start, end - start);

        Assert.Contains("ClearMachineTranslationMessage();", method);
    }

    [Fact]
    public void Cached_machine_fallback_waits_for_the_current_typewriter_to_finish()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "AbyssMod", "Patches", "TranslationPatch.cs")
        );
        var start = source.IndexOf("private static void ResolveCurrentMessageFallback()", StringComparison.Ordinal);
        var end = source.IndexOf("private static void TrackMachineTranslationMessage", start, StringComparison.Ordinal);
        var method = source.Substring(start, end - start);

        Assert.Contains("_staticMessageWindow._isPlay", method);
        Assert.Contains("TrackMachineTranslationMessage", method);
    }

    [Fact]
    public void Repeated_tracking_of_the_same_machine_candidate_preserves_refresh_state()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "AbyssMod", "Patches", "TranslationPatch.cs")
        );
        var start = source.IndexOf("private static void TrackMachineTranslationMessage", StringComparison.Ordinal);
        var end = source.IndexOf("private static void ClearStaticMessage", start, StringComparison.Ordinal);
        var method = source.Substring(start, end - start);

        Assert.Contains("ReferenceEquals(_messageWindow, messageWindow)", method);
        Assert.Contains("string.Equals(_machineTranslationSource, source", method);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AbyssMod.Tests", "AbyssMod.Tests.csproj")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
