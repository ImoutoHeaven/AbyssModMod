#nullable enable

using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherRuntimeActiveCodeErosionExtractorTests
{
    [Fact]
    public void LivePossessionAndMasterRows_AreMappedThroughTheExactNativeMemberNames()
    {
        NetherActiveCodeErosionProjection projection = new NetherRuntimeActiveCodeErosionExtractor().Extract(
            new[] { new FakePossessionCode(40024, 3), new FakePossessionCode(30024, 2) },
            new[]
            {
                new FakeMasterCode(30024, 6, 4, 0, 0),
                new FakeMasterCode(40024, 9, 7, 0, 0),
            }
        );

        Assert.True(projection.ErosionProjectionKnown);
        Assert.Equal(new long[] { 30024, 40024 }, projection.SortedCodeIds);
        Assert.Equal(2, projection.ErosionEffects.Count);
        Assert.Equal(NetherCodeEffectKind.ErosionAdditionUp, projection.ErosionEffects[0].EffectKind);
        Assert.Equal(NetherCodeEffectKind.ErosionRateDown, projection.ErosionEffects[1].EffectKind);
    }

    [Fact]
    public void MissingExactRuntimeMember_IsUnknown()
    {
        NetherActiveCodeErosionProjection projection = new NetherRuntimeActiveCodeErosionExtractor().Extract(
            new[] { new MissingAmountPossessionCode(30024) },
            new[] { new FakeMasterCode(30024, 6, 4, 0, 0) }
        );

        Assert.False(projection.ErosionProjectionKnown);
        Assert.Contains("possession", projection.Detail);
    }

    private sealed class FakePossessionCode
    {
        public FakePossessionCode(long codeId, int amount)
        {
            MNetherCodeId = codeId;
            Amount = amount;
        }

        public long MNetherCodeId { get; }
        public int Amount { get; }
    }

    private sealed class MissingAmountPossessionCode
    {
        public MissingAmountPossessionCode(long codeId) => MNetherCodeId = codeId;

        public long MNetherCodeId { get; }
    }

    private sealed class FakeMasterCode
    {
        public FakeMasterCode(long id, int effectType, long parameter1, long parameter2, long parameter3)
        {
            this.id = id;
            effect_type = effectType;
            effect_parameter_1 = parameter1;
            effect_parameter_2 = parameter2;
            effect_parameter_3 = parameter3;
        }

        public long id;
        public int effect_type;
        public long effect_parameter_1;
        public long effect_parameter_2;
        public long effect_parameter_3;
    }
}
