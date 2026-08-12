using Xunit;

namespace AbyssMod.Tests;

public class TranslationManagerThreadAffinityTests
{
    [Fact]
    public void Load_completion_does_not_directly_refresh_unity_text()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "AbyssMod", "Services", "TranslationManager.cs")
        );

        Assert.DoesNotContain("GeneralTextPatch.RefreshAllVisibleText();", source);
    }

    [Fact]
    public void Static_translation_completion_does_not_reset_the_live_machine_cache()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "AbyssMod", "Services", "TranslationManager.cs")
        );
        var start = source.IndexOf(
            "public async Task LoadTranslationAsync()",
            StringComparison.Ordinal
        );
        var end = source.IndexOf(
            "private async Task<Dictionary<string, string>> BuildLocalAddOnFallbackAsync",
            start,
            StringComparison.Ordinal
        );

        Assert.DoesNotContain(
            "MachineTranslator.ReloadFromDisk();",
            source.Substring(start, end - start)
        );
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
