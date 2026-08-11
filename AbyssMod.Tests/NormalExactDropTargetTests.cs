using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public sealed class NormalExactDropTargetTests
{
    [Fact]
    public void Empty_config_disables_exact_targeting()
    {
        NormalExactDropTargetParseResult result = NormalExactDropTargetParser.Parse("  ");

        Assert.Equal(NormalExactDropTargetMode.Disabled, result.Mode);
        Assert.Empty(result.Targets);
        Assert.Equal(string.Empty, result.Error);
        Assert.Equal("none", result.Description);
    }

    [Fact]
    public void Parser_normalizes_case_whitespace_duplicates_and_preserves_first_seen_order()
    {
        NormalExactDropTargetParseResult result = NormalExactDropTargetParser.Parse(
            " armor : 23010440,WEAPON:123, Armor:23010440, accessory : 456 "
        );

        Assert.Equal(NormalExactDropTargetMode.Enabled, result.Mode);
        Assert.Equal(
            ["Armor:23010440", "Weapon:123", "Accessory:456"],
            result.Targets.Select(target => target.Token)
        );
        Assert.Equal(
            "Armor:23010440,Weapon:123,Accessory:456",
            result.Description
        );
    }

    [Fact]
    public void Positive_signed_int64_maximum_is_supported()
    {
        NormalExactDropTargetParseResult result = NormalExactDropTargetParser.Parse(
            $"Weapon:{long.MaxValue}"
        );

        NormalExactDropTarget target = Assert.Single(result.Targets);
        Assert.Equal(long.MaxValue, target.ContentId);
        Assert.Equal(BattleSessionAutoSLPolicy.WeaponContentType, target.ContentType);
    }

    [Theory]
    [InlineData("Item:123")]
    [InlineData("Armor:0")]
    [InlineData("Armor:-1")]
    [InlineData("Armor:9223372036854775808")]
    [InlineData("Armor")]
    [InlineData("Armor:")]
    [InlineData(":123")]
    [InlineData("Armor:1:2")]
    [InlineData("Armor:123,")]
    public void Any_invalid_nonempty_entry_invalidates_the_whole_config(string raw)
    {
        NormalExactDropTargetParseResult result = NormalExactDropTargetParser.Parse(raw);

        Assert.Equal(NormalExactDropTargetMode.Invalid, result.Mode);
        Assert.Empty(result.Targets);
        Assert.NotEmpty(result.Error);
        Assert.StartsWith("invalid-normal-exact-target:", result.Error);
        Assert.StartsWith("invalid:", result.Description);
    }

    [Theory]
    [InlineData(70, "Weapon")]
    [InlineData(80, "Armor")]
    [InlineData(90, "Accessory")]
    public void Supported_content_types_format_as_canonical_names(int contentType, string expected)
    {
        Assert.True(NormalExactDropTarget.TryFormatTypeName(contentType, out string typeName));
        Assert.Equal(expected, typeName);
    }

    [Fact]
    public void Forest_cloak_formats_as_the_canonical_master_data_token()
    {
        var target = new NormalExactDropTarget(80, 23010440);

        Assert.Equal("Armor:23010440", target.Token);
    }

    [Fact]
    public void Plus_suffix_parses_as_family_at_or_above_and_round_trips_canonically()
    {
        NormalExactDropTargetParseResult result = NormalExactDropTargetParser.Parse(
            " armor : 23010440+ "
        );

        NormalExactDropTarget target = Assert.Single(result.Targets);
        Assert.Equal(NormalDropTargetMatchMode.FamilyAtOrAbove, target.MatchMode);
        Assert.Equal(BattleSessionAutoSLPolicy.ArmorContentType, target.ContentType);
        Assert.Equal(23010440, target.ContentId);
        Assert.Equal("Armor:23010440+", target.Token);
        Assert.Equal("Armor:23010440+", result.Description);
    }

    [Fact]
    public void Exact_and_family_versions_of_one_anchor_remain_distinct_or_targets()
    {
        NormalExactDropTargetParseResult result = NormalExactDropTargetParser.Parse(
            "Armor:23010440,Armor:23010440+,Armor:23010440+"
        );

        Assert.Equal(
            ["Armor:23010440", "Armor:23010440+"],
            result.Targets.Select(target => target.Token)
        );
    }

    [Theory]
    [InlineData("Armor:+23010440")]
    [InlineData("Armor:23010440++")]
    [InlineData("Armor:+")]
    public void Misplaced_or_repeated_family_suffix_invalidates_the_config(string raw)
    {
        NormalExactDropTargetParseResult result = NormalExactDropTargetParser.Parse(raw);

        Assert.Equal(NormalExactDropTargetMode.Invalid, result.Mode);
        Assert.Empty(result.Targets);
        Assert.StartsWith("invalid-normal-exact-target:", result.Error);
    }

    [Fact]
    public void Unsupported_numeric_content_type_cannot_be_formatted()
    {
        Assert.False(NormalExactDropTarget.TryFormatTypeName(30, out string typeName));
        Assert.Equal(string.Empty, typeName);
    }
}
