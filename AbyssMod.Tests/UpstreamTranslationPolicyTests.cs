using AbyssMod;
using Xunit;

namespace AbyssMod.Tests;

public class UpstreamTranslationPolicyTests
{
    [Fact]
    public void Resolve_ignores_legacy_cdn_and_language_values()
    {
        var resolved = UpstreamTranslationPolicy.Resolve(
            "https://raw.githubusercontent.com/s88037zz/dotabyss-translation/main/translations",
            "zh_Hant"
        );

        Assert.Equal(
            "https://raw.githubusercontent.com/anosu/dotabyss-translation/refs/heads/main/translations",
            resolved.Cdn
        );
        Assert.Equal("zh_Hans", resolved.Language);
    }
}
