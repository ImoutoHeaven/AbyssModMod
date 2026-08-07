#nullable enable

using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherActivePartyHpSafetyMapperTests
{
    [Theory]
    [InlineData(0.299d, 299)]
    [InlineData(0.300d, 300)]
    public void AuthoritativeRatio_UsesCheckedFloorPermille(double hpRatio, int expectedPermille)
    {
        NetherActivePartyHpSafety safety = Map(Member(1, hpRatio));

        Assert.True(safety.IsKnown);
        Assert.Equal(expectedPermille, safety.MinimumHpPermille);
    }

    [Fact]
    public void MultipleActiveMembers_UsesTheLowestPermille()
    {
        NetherActivePartyHpSafety safety = Map(
            Member(1, 0.900d),
            Member(2, 0.300d),
            Member(3, 0.750d)
        );

        Assert.True(safety.IsKnown);
        Assert.Equal(300, safety.MinimumHpPermille);
    }

    [Fact]
    public void DeadMember_IsAnExplicitZeroPermille()
    {
        NetherActivePartyHpSafety safety = Map(
            Member(1, 0.800d),
            Member(2, 0d, isAlive: false)
        );

        Assert.True(safety.IsKnown);
        Assert.Equal(0, safety.MinimumHpPermille);
    }

    [Fact]
    public void EveryRuntimePartyMember_ContributesToTheMinimum()
    {
        NetherActivePartyHpSafety safety = Map(
            Member(1, 0.800d),
            Member(2, 0d)
        );

        Assert.True(safety.IsKnown);
        Assert.Equal(0, safety.MinimumHpPermille);
    }

    [Theory]
    [InlineData(-0.001d)]
    [InlineData(1.001d)]
    public void InvalidAuthoritativeRatio_IsUnknown(double hpRatio)
    {
        NetherActivePartyHpSafety safety = Map(Member(1, hpRatio));

        Assert.False(safety.IsKnown);
        Assert.Null(safety.MinimumHpPermille);
    }

    [Fact]
    public void DuplicateActiveCharacter_IsUnknown()
    {
        NetherActivePartyHpSafety safety = Map(
            Member(1, 0.500d),
            Member(1, 0.600d)
        );

        Assert.False(safety.IsKnown);
        Assert.Null(safety.MinimumHpPermille);
        Assert.Contains("duplicate", safety.Detail);
    }

    [Fact]
    public void NonFiniteAuthoritativeRatio_IsUnknown()
    {
        NetherActivePartyHpSafety safety = Map(Member(1, double.NaN));

        Assert.False(safety.IsKnown);
        Assert.Null(safety.MinimumHpPermille);
        Assert.Contains("non-finite", safety.Detail);
    }

    [Fact]
    public void EmptyRuntimeParty_IsUnknown()
    {
        NetherActivePartyHpSafety safety = Map();

        Assert.False(safety.IsKnown);
        Assert.Null(safety.MinimumHpPermille);
    }

    private static NetherActivePartyHpSafety Map(params NetherActiveBattleMemberHp[] members) =>
        new NetherActivePartyHpSafetyMapper().Map(members);

    private static NetherActiveBattleMemberHp Member(long characterId, double hpRatio, bool isAlive = true) =>
        new(characterId, hpRatio, isAlive);
}
