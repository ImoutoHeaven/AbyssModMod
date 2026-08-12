using System.Text.Json;
using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class MasterDataSyncProtocolTests
{
    [Fact]
    public void Class_aliases_add_current_game_type_without_dropping_legacy_type()
    {
        using var document = JsonDocument.Parse(
            "{\"_class_aliases\":[\"MDescriptionTextColor\",\"MDescriptionTextColors\"]}"
        );

        var names = MasterDataSyncProtocol
            .ReadClassNames("MDescriptionTextColors", document.RootElement)
            .ToArray();

        Assert.Equal(
            new[] { "MDescriptionTextColors", "MDescriptionTextColor" },
            names
        );
    }

    [Theory]
    [InlineData("纹章：冲击")]
    [InlineData("纹章：热情")]
    public void Repository_translation_is_written_verbatim_even_for_legacy_seal_rules(
        string translated
    )
    {
        Assert.Equal(
            translated,
            MasterDataSyncProtocol.ResolveRepositoryValue(translated, legacySealRule: true)
        );
    }
}
