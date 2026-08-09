using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherEventPolicyTests
{
    [Fact]
    public void Event_option_combines_all_three_effect_targets()
    {
        NetherEventDecision decision = EventPolicy().DecideEvent(
            Snapshot(erosion: 50, hp: 500),
            [Option(1, new NetherEffect(NetherEffectKind.ErosionHeal, 5), new NetherEffect(NetherEffectKind.Heal, 100), new NetherEffect(NetherEffectKind.Item, 1))],
            Settings()
        );

        Assert.Equal(NetherEventDecisionKind.Select, decision.Kind);
        Assert.Equal(1, decision.OptionNumber);
        Assert.Equal(45, decision.ProjectedErosion);
        Assert.Equal(100, decision.HpDelta);
    }

    [Fact]
    public void Lethal_damage_option_is_rejected()
    {
        NetherEventDecision decision = EventPolicy().DecideEvent(
            Snapshot(hp: 100),
            [Option(1, new NetherEffect(NetherEffectKind.Damage, 100))],
            Settings()
        );

        Assert.Equal(NetherEventDecisionKind.Pause, decision.Kind);
        Assert.Equal(NetherPauseReason.UnsafeHp, decision.PauseReason);
    }

    [Fact]
    public void Erosion_option_reaching_hard_limit_is_rejected()
    {
        NetherEventDecision decision = EventPolicy().DecideEvent(
            Snapshot(erosion: 90),
            [Option(1, new NetherEffect(NetherEffectKind.Erosion, 10))],
            Settings()
        );

        Assert.Equal(NetherEventDecisionKind.Pause, decision.Kind);
        Assert.Equal(NetherPauseReason.UnsafeErosion, decision.PauseReason);
    }

    [Fact]
    public void Erosion_heal_beats_hp_heal_when_erosion_pressure_is_higher()
    {
        NetherEventDecision decision = EventPolicy().DecideEvent(
            Snapshot(erosion: 85, hp: 700),
            [Option(1, new NetherEffect(NetherEffectKind.Heal, 200)), Option(2, new NetherEffect(NetherEffectKind.ErosionHeal, 5))],
            Settings()
        );

        Assert.Equal(2, decision.OptionNumber);
    }

    [Fact]
    public void Hp_heal_beats_code_offer_when_character_is_below_soft_hp()
    {
        NetherEventDecision decision = EventPolicy().DecideEvent(
            Snapshot(hp: 100),
            [
                Option(1, new NetherEffect(NetherEffectKind.AbyssCodeOffer, 1)),
                Option(2, new NetherEffect(NetherEffectKind.Heal, 250)),
            ],
            Settings()
        );

        Assert.Equal(2, decision.OptionNumber);
    }

    [Fact]
    public void Unknown_target_or_content_pauses_instead_of_selecting()
    {
        NetherEventDecision decision = EventPolicy().DecideEvent(
            Snapshot(),
            [Option(1, new NetherEffect(NetherEffectKind.Unknown, 0) { Known = false })],
            Settings()
        );

        Assert.Equal(NetherEventDecisionKind.Pause, decision.Kind);
        Assert.Equal(NetherPauseReason.UnknownEffect, decision.PauseReason);
    }

    [Fact]
    public void Event_triggered_battle_is_marked_battle_only_after_event_selection()
    {
        NetherEventDecision decision = EventPolicy().DecideEvent(
            Snapshot(),
            [Option(1, new NetherEffect(NetherEffectKind.Battle, 0))],
            Settings()
        );

        Assert.Equal(NetherEventDecisionKind.Select, decision.Kind);
        Assert.Equal(NetherActionKind.SelectEventOption, decision.ActionKind);
        Assert.True(decision.StartsBattleAfterSelection);
    }

    [Fact]
    public void KeyOnly_selects_the_exact_key_cost_option_when_key_is_available()
    {
        NetherEventDecision decision = EventPolicy().DecideTreasure(
            Snapshot(keys: 1),
            [
                Option(1, new NetherEffect(NetherEffectKind.TreasureKeyUsed, 1), new NetherEffect(NetherEffectKind.Item, 1)),
                Option(2, new NetherEffect(NetherEffectKind.TreasureKeyUsed, 2), new NetherEffect(NetherEffectKind.Item, 1)),
            ],
            Settings()
        );

        Assert.Equal(NetherEventDecisionKind.Select, decision.Kind);
        Assert.Equal(1, decision.OptionNumber);
    }

    [Fact]
    public void KeyOnly_pauses_when_already_in_treasure_without_a_key()
    {
        NetherEventDecision decision = EventPolicy().DecideTreasure(
            Snapshot(keys: 0),
            [Option(1, new NetherEffect(NetherEffectKind.TreasureKeyUsed, 1))],
            Settings()
        );

        Assert.Equal(NetherEventDecisionKind.Pause, decision.Kind);
        Assert.Equal(NetherPauseReason.NoSafeRoute, decision.PauseReason);
    }

    [Fact]
    public void Treasure_never_selects_hp_or_erosion_payment()
    {
        NetherEventDecision decision = EventPolicy().DecideTreasure(
            Snapshot(keys: 1),
            [
                Option(1, new NetherEffect(NetherEffectKind.Damage, 1)),
                Option(2, new NetherEffect(NetherEffectKind.Erosion, 1)),
                Option(3, new NetherEffect(NetherEffectKind.TreasureKeyUsed, 1), new NetherEffect(NetherEffectKind.Item, 1)),
            ],
            Settings()
        );

        Assert.Equal(NetherEventDecisionKind.Select, decision.Kind);
        Assert.Equal(3, decision.OptionNumber);
    }

    [Fact]
    public void ShopOff_never_creates_a_purchase_request()
    {
        NetherShopDecision decision = EventPolicy().DecideShop(
            Snapshot(gold: 100),
            [new NetherShopContent(1, 2, 91, NetherRewardRarity.Gold, 10, usesNetherGold: true)],
            Settings(shopMode: NetherShopMode.Off)
        );

        Assert.Equal(NetherShopDecisionKind.Leave, decision.Kind);
        Assert.Equal(0, decision.ContentId);
    }

    [Fact]
    public void EquipmentBags_requires_type_91_gold_or_better_and_nether_gold_cost()
    {
        NetherShopDecision decision = EventPolicy().DecideShop(
            Snapshot(gold: 100),
            [
                new NetherShopContent(1, 1, 90, NetherRewardRarity.UniqueWeapon, 1, usesNetherGold: true),
                new NetherShopContent(2, 2, 91, NetherRewardRarity.Purple, 1, usesNetherGold: true),
                new NetherShopContent(3, 3, 91, NetherRewardRarity.Gold, 1, usesNetherGold: false),
                new NetherShopContent(4, 4, 91, NetherRewardRarity.Gold, 101, usesNetherGold: true),
                new NetherShopContent(5, 5, 91, NetherRewardRarity.Gold, 40, usesNetherGold: true),
            ],
            Settings(shopMode: NetherShopMode.EquipmentBags)
        );

        Assert.Equal(NetherShopDecisionKind.Buy, decision.Kind);
        Assert.Equal(5, decision.ContentId);
        Assert.Equal(1, decision.Amount);
    }

    [Fact]
    public void Recovery_prefers_erosion_heal_over_neutral_choice()
    {
        NetherEventDecision decision = EventPolicy().DecideRecovery(
            Snapshot(erosion: 70),
            [Option(1, new NetherEffect(NetherEffectKind.Item, 1)), Option(2, new NetherEffect(NetherEffectKind.ErosionHeal, 3))],
            Settings()
        );

        Assert.Equal(NetherEventDecisionKind.Select, decision.Kind);
        Assert.Equal(2, decision.OptionNumber);
    }

    [Fact]
    public void Recovery_allows_a_completely_neutral_safe_fallback_when_no_positive_choice_exists()
    {
        NetherEventDecision decision = EventPolicy().DecideRecovery(
            Snapshot(erosion: 70),
            [Option(1, new NetherEffect(NetherEffectKind.NetherGoldUsed, 0))],
            Settings()
        );

        Assert.Equal(NetherEventDecisionKind.Select, decision.Kind);
        Assert.Equal(1, decision.OptionNumber);
    }

    private static NetherEventPolicy EventPolicy() => new();

    private static NetherAutoClimbSettings Settings(
        NetherTreasureMode treasureMode = NetherTreasureMode.KeyOnly,
        NetherShopMode shopMode = NetherShopMode.Off
    ) => new()
    {
        SoftErosionLimit = 90,
        MinimumCharacterHpPermille = 300,
        TreasureMode = treasureMode,
        ShopMode = shopMode,
    };

    private static NetherSnapshot Snapshot(int erosion = 20, int hp = 500, int keys = 0, int gold = 0) => new()
    {
        ErosionPoint = erosion,
        TreasureKeyCount = keys,
        NetherGold = gold,
        Characters = [new NetherCharacterState(1, hp)],
    };

    private static NetherEventOption Option(int number, params NetherEffect[] effects) => new(number, effects);
}
