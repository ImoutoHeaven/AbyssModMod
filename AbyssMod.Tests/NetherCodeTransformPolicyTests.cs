#nullable enable

using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public sealed class NetherCodeTransformPolicyTests
{
    [Fact]
    public void Risk_and_low_value_general_codes_are_removed_before_safe_codes()
    {
        NetherCodeTransformDecision risk = Decide(
            Code(30024, NetherCodeEffectKind.Safe, rarity: 1, level: 1),
            Code(51001, NetherCodeEffectKind.General, rarity: 5, level: 5),
            Code(40024, NetherCodeEffectKind.Risk, rarity: 5, level: 5)
        );
        NetherCodeTransformDecision general = Decide(
            Code(30024, NetherCodeEffectKind.Safe, rarity: 1, level: 1),
            Code(51001, NetherCodeEffectKind.General, rarity: 4, level: 3),
            Code(51002, NetherCodeEffectKind.General, rarity: 2, level: 1)
        );

        Assert.True(risk.CanTransform, risk.Detail);
        Assert.Equal(40024, risk.RemoveCodeId);
        Assert.True(general.CanTransform, general.Detail);
        Assert.Equal(51002, general.RemoveCodeId);
    }

    [Fact]
    public void Preferred_safe_and_effective_safe_codes_are_protected()
    {
        NetherCodeTransformDecision decision = Decide(
            Code(30024, NetherCodeEffectKind.Safe, rarity: 1, level: 1),
            Code(30025, NetherCodeEffectKind.Safe, rarity: 1, level: 5)
        );

        Assert.False(decision.CanTransform);
        Assert.Equal(NetherPauseReason.NoSafeRoute, decision.PauseReason);
        Assert.Contains("no-removable-code", decision.Detail);
    }

    [Fact]
    public void Invalid_or_duplicate_portfolio_fails_closed()
    {
        Assert.Equal(
            NetherPauseReason.UnknownMasterData,
            new NetherCodeTransformPolicy().Decide(
                [Code(1, NetherCodeEffectKind.General), Code(1, NetherCodeEffectKind.General)],
                capacity: 5
            ).PauseReason
        );
        Assert.Equal(
            NetherPauseReason.UnknownMasterData,
            new NetherCodeTransformPolicy().Decide(
                [Code(1, NetherCodeEffectKind.General) with { IsKnown = false }],
                capacity: 5
            ).PauseReason
        );
    }

    private static NetherCodeTransformDecision Decide(params NetherCodeState[] codes) =>
        new NetherCodeTransformPolicy().Decide(codes, capacity: 5);

    private static NetherCodeState Code(
        long id,
        NetherCodeEffectKind kind,
        int rarity = 1,
        int level = 1
    ) => new(id, kind, level)
    {
        IsKnown = true,
        Category = kind switch
        {
            NetherCodeEffectKind.Safe => NetherCodeCategory.ErosionResistance,
            NetherCodeEffectKind.Risk => NetherCodeCategory.ErosionEnhancement,
            _ => NetherCodeCategory.Technique,
        },
        Rarity = rarity,
    };
}
