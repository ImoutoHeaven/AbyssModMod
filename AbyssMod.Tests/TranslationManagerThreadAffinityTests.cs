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
