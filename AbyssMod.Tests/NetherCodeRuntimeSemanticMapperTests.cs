#nullable enable

using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherCodeRuntimeSemanticMapperTests
{
    [Fact]
    public void Raw_technique_master_keeps_lane_coverage_and_research_facts_explicitly_unknown()
    {
        NetherCodeCandidate candidate = NetherCodeRuntimeSemanticMapper.MapCandidate(
            codeId: 51001,
            rawCategory: (int)NetherCodeCategory.Technique,
            effectType: 1,
            level: 2,
            rarity: 3
        );

        Assert.True(candidate.IsKnown);
        Assert.Equal(NetherCodeEffectKind.General, candidate.EffectKind);
        Assert.False(candidate.PartyCoverageKnown);
        Assert.False(candidate.IsResearchOnlyKnown);
        Assert.Equal(0, candidate.PartyCoverage);
        Assert.False(candidate.IsResearchOnly);
    }

    [Fact]
    public void Category_proven_safe_is_preserved_as_safe_without_inventing_ordinary_lane_facts()
    {
        NetherCodeCandidate candidate = NetherCodeRuntimeSemanticMapper.MapCandidate(
            codeId: 30024,
            rawCategory: (int)NetherCodeCategory.ErosionResistance,
            effectType: 99,
            level: 1,
            rarity: 4
        );

        Assert.True(candidate.IsKnown);
        Assert.Equal(NetherCodeEffectKind.Safe, candidate.EffectKind);
        Assert.False(candidate.PartyCoverageKnown);
        Assert.False(candidate.IsResearchOnlyKnown);
    }
}
