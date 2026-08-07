#nullable enable

using System.Collections.Generic;
using System.Linq;
using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherActiveCodeErosionProjectionMapperTests
{
    [Fact]
    public void HashAndCodeIds_AreOrderIndependentAndIncludeEveryMasterParameter()
    {
        NetherActiveCodeErosionProjection first = Map(
            new[] { Possession(40024, 2), Possession(30024, 1) },
            new[] { Master(30024, 6, 5, 0, 0), Master(40024, 9, 7, 0, 0) }
        );
        NetherActiveCodeErosionProjection second = Map(
            new[] { Possession(30024, 1), Possession(40024, 2) },
            new[] { Master(40024, 9, 7, 0, 0), Master(30024, 6, 5, 0, 0) }
        );

        Assert.True(first.ErosionProjectionKnown);
        Assert.Equal(first.CodeHash, second.CodeHash);
        Assert.Equal(new long[] { 30024, 40024 }, first.SortedCodeIds);
        Assert.Contains("30024:1:6:5:0:0", first.CodeHash);
        Assert.Contains("40024:2:9:7:0:0", first.CodeHash);
    }

    [Fact]
    public void EffectTypesSixThroughNine_MapToExactErosionModifiers()
    {
        NetherActiveCodeErosionProjection projection = Map(
            new[] { Possession(6), Possession(7), Possession(8), Possession(9) },
            new[]
            {
                Master(6, 6, 11, 0, 0),
                Master(7, 7, 12, 0, 0),
                Master(8, 8, 13, 0, 0),
                Master(9, 9, 14, 0, 0),
            }
        );

        Assert.True(projection.ErosionProjectionKnown);
        Assert.Equal(
            new[]
            {
                NetherCodeEffectKind.ErosionAdditionUp,
                NetherCodeEffectKind.ErosionAdditionDown,
                NetherCodeEffectKind.ErosionRateUp,
                NetherCodeEffectKind.ErosionRateDown,
            },
            projection.ErosionEffects.Select(effect => effect.EffectKind)
        );
        Assert.Equal(new[] { 11, 12, 13, 14 }, projection.ErosionEffects.Select(effect => effect.Amount));
    }

    [Fact]
    public void OrdinaryEffectsOneAndTwo_AreKnownButDoNotAlterErosionProjection()
    {
        NetherActiveCodeErosionProjection projection = Map(
            new[] { Possession(1), Possession(2) },
            new[] { Master(1, 1, 99, 88, 77), Master(2, 2, 66, 55, 44) }
        );

        Assert.True(projection.ErosionProjectionKnown);
        Assert.Empty(projection.ErosionEffects);
        Assert.Equal(new long[] { 1, 2 }, projection.SortedCodeIds);
        Assert.Contains("1:1:1:99:88:77", projection.CodeHash);
        Assert.Contains("2:1:2:66:55:44", projection.CodeHash);
    }

    [Theory]
    [InlineData(10, 1, 0, 0)]
    [InlineData(6, 0, 0, 0)]
    [InlineData(8, 10, 1, 0)]
    public void UnknownOrInvalidEffectParameters_AreFailClosed(
        int effectType,
        long parameter1,
        long parameter2,
        long parameter3
    )
    {
        NetherActiveCodeErosionProjection projection = Map(
            new[] { Possession(1) },
            new[] { Master(1, effectType, parameter1, parameter2, parameter3) }
        );

        Assert.False(projection.ErosionProjectionKnown);
        Assert.Empty(projection.ErosionEffects);
    }

    [Fact]
    public void DuplicateActiveMaster_IsAmbiguousAndFailClosed()
    {
        NetherActiveCodeErosionProjection projection = Map(
            new[] { Possession(1) },
            new[] { Master(1, 6, 1, 0, 0), Master(1, 6, 1, 0, 0) }
        );

        Assert.False(projection.ErosionProjectionKnown);
        Assert.Contains("duplicate", projection.Detail);
    }

    [Fact]
    public void EmptyPossession_IsKnownWithAnEmptyProjection()
    {
        NetherActiveCodeErosionProjection projection = Map(
            System.Array.Empty<NetherPossessionCodeErosionInput>(),
            System.Array.Empty<NetherCodeErosionMasterInput>()
        );

        Assert.True(projection.ErosionProjectionKnown);
        Assert.Empty(projection.SortedCodeIds);
        Assert.Empty(projection.ErosionEffects);
        Assert.Equal("nether-codes:none", projection.CodeHash);
    }

    private static NetherActiveCodeErosionProjection Map(
        IReadOnlyList<NetherPossessionCodeErosionInput> possessions,
        IReadOnlyList<NetherCodeErosionMasterInput> masters
    ) => new NetherActiveCodeErosionProjectionMapper().Map(possessions, masters);

    private static NetherPossessionCodeErosionInput Possession(long codeId, int amount = 1) =>
        new(codeId, amount);

    private static NetherCodeErosionMasterInput Master(
        long codeId,
        int effectType,
        long parameter1,
        long parameter2,
        long parameter3
    ) => new(codeId, effectType, parameter1, parameter2, parameter3);
}
