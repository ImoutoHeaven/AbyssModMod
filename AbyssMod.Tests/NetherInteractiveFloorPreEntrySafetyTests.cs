using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherInteractiveFloorPreEntrySafetyTests
{
    [Fact]
    public void Zero_extend_id_uses_the_first_floor_event_row_like_the_native_resolver()
    {
        NetherInteractiveFloorPreEntrySafetyResult result = Evaluate(Input(
            NetherFloorNodeType.Event,
            events:
            [
                Event(100, 1001) with { Weight = 0 },
                Event(101, 1002),
            ],
            parts:
            [
                Part(1001, targetType1: (int)NetherEffectKind.Heal, parameter1: 1),
                Part(1002, targetType1: (int)NetherEffectKind.Damage, parameter1: 500),
            ]
        ));

        Assert.True(result.IsSafe);
        Assert.Single(result.SafeOptionNumberByEventId);
        Assert.Equal(1, result.SafeOptionNumberByEventId[100]);
    }

    [Fact]
    public void Positive_extend_id_uses_the_exact_event_row_without_generation_filters()
    {
        NetherInteractiveFloorPreEntrySafetyResult result = Evaluate(Input(
            NetherFloorNodeType.Event,
            events:
            [
                Event(101, 1002),
                Event(100, 1001) with { MapFloorMasterId = 901, Weight = 0 },
            ],
            parts:
            [
                Part(1001, targetType1: (int)NetherEffectKind.Heal, parameter1: 1),
                Part(1002, targetType1: (int)NetherEffectKind.Damage, parameter1: 500),
            ],
            hp: 500,
            floorExtendId: 100
        ));

        Assert.True(result.IsSafe, result.PauseReason + ":" + result.Detail);
        Assert.Single(result.SafeOptionNumberByEventId);
        Assert.Equal(1, result.SafeOptionNumberByEventId[100]);
    }

    [Fact]
    public void Recovery_accepts_a_completely_neutral_master_option_as_the_safe_exit()
    {
        NetherInteractiveFloorPreEntrySafetyResult result = Evaluate(Input(
            NetherFloorNodeType.Recovery,
            events: [Event(100, 1001)],
            parts: [Part(1001, targetType1: (int)NetherEffectKind.NetherGoldUsed, parameter1: 0)]
        ));

        Assert.True(result.IsSafe);
        Assert.Equal(1, result.SafeOptionNumberByEventId[100]);
    }

    [Fact]
    public void Map_generation_erosion_range_is_not_an_interactive_action_cost()
    {
        NetherInteractiveFloorPreEntrySafetyResult result = Evaluate(Input(
            NetherFloorNodeType.Recovery,
            events: [Event(100, 1001)],
            parts: [Part(1001, targetType1: (int)NetherEffectKind.NetherGoldUsed, parameter1: 0)],
            erosion: 0,
            mapMinimumErosion: 0,
            mapMaximumErosion: 100
        ));

        Assert.True(result.IsSafe, result.PauseReason + ":" + result.Detail);
        Assert.Equal(0, result.WorstCaseProjection!.Value.ErosionDelta);
    }

    [Theory]
    [InlineData((int)NetherEffectKind.Damage, 201, 500, 20, (int)NetherPauseReason.UnsafeHp)]
    [InlineData((int)NetherEffectKind.Erosion, 70, 500, 20, (int)NetherPauseReason.UnsafeErosion)]
    public void Damage_below_configured_hp_or_erosion_at_soft_limit_is_not_a_safe_exit(
        int targetType,
        long parameter,
        int hp,
        int erosion,
        int expectedReason
    )
    {
        NetherInteractiveFloorPreEntrySafetyResult result = Evaluate(Input(
            NetherFloorNodeType.Event,
            events: [Event(100, 1001)],
            parts: [Part(1001, targetType1: targetType, parameter1: parameter)],
            hp: hp,
            erosion: erosion
        ));

        Assert.False(result.IsSafe);
        Assert.Equal((NetherPauseReason)expectedReason, result.PauseReason);
    }

    [Fact]
    public void Battle_trigger_option_requires_a_nonbattle_safe_fallback_in_the_same_possible_row()
    {
        NetherInteractiveFloorPreEntrySafetyResult onlyBattle = Evaluate(Input(
            NetherFloorNodeType.Event,
            events: [Event(100, 1001)],
            parts: [Part(1001, targetType1: (int)NetherEffectKind.Battle, parameter1: 0)]
        ));
        NetherInteractiveFloorPreEntrySafetyResult fallback = Evaluate(Input(
            NetherFloorNodeType.Event,
            events: [Event(100, 1001, 1002)],
            parts:
            [
                Part(1001, targetType1: (int)NetherEffectKind.Battle, parameter1: 0),
                Part(1002, targetType1: (int)NetherEffectKind.NetherGoldUsed, parameter1: 0),
            ]
        ));

        Assert.False(onlyBattle.IsSafe);
        Assert.Equal(NetherPauseReason.NoSafeRoute, onlyBattle.PauseReason);
        Assert.True(fallback.IsSafe);
        Assert.Equal(2, fallback.SafeOptionNumberByEventId[100]);
    }

    [Theory]
    [InlineData(160, (int)NetherEffectKind.AbyssCodeOffer)]
    [InlineData(165, (int)NetherEffectKind.NetherGoldGain)]
    [InlineData(166, (int)NetherEffectKind.TreasureKeyGain)]
    public void Native_resource_content_allows_zero_content_id(int contentType, int expectedKind)
    {
        NetherInteractiveFloorPreEntrySafetyResult result = Evaluate(Input(
            NetherFloorNodeType.Event,
            events: [Event(100, 1001)],
            parts:
            [
                Part(
                    1001,
                    targetType1: 0,
                    parameter1: 0,
                    contentType: contentType,
                    contentId: 0,
                    amount: 30
                ),
            ]
        ));

        Assert.True(result.IsSafe, result.PauseReason + ":" + result.Detail);
        Assert.Equal(1, result.SafeOptionNumberByEventId[100]);
        NetherEffect effect = Assert.Single(result.SafeOptionProjectionByEventId[100].ExpectedEffects);
        Assert.Equal((NetherEffectKind)expectedKind, effect.Kind);
        Assert.Equal(0, effect.ContentId);
        Assert.Equal(30, effect.Amount);
    }

    [Fact]
    public void Native_code_transform_target_and_code_offer_content_are_exact_safe_options()
    {
        NetherInteractiveFloorPreEntrySafetyResult transform = Evaluate(Input(
            NetherFloorNodeType.Recovery,
            events: [Event(354, 700)],
            parts: [Part(700, targetType1: 7, parameter1: 0)],
            codes: [Code(40024, NetherCodeEffectKind.Risk)]
        ));
        NetherInteractiveFloorPreEntrySafetyResult offer = Evaluate(Input(
            NetherFloorNodeType.Event,
            events: [Event(355, 701)],
            parts: [Part(701, targetType1: 0, parameter1: 0, contentType: 160, contentId: 0, amount: 1)]
        ));

        Assert.True(transform.IsSafe, transform.PauseReason + ":" + transform.Detail);
        Assert.Equal(1, transform.SafeOptionNumberByEventId[354]);
        NetherEffect transformEffect = Assert.Single(transform.SafeOptionProjectionByEventId[354].ExpectedEffects);
        Assert.Equal(NetherEffectKind.AbyssCodeTransform, transformEffect.Kind);
        Assert.Equal(0, transformEffect.ReplacementCodeId);

        Assert.True(offer.IsSafe, offer.PauseReason + ":" + offer.Detail);
        NetherEffect offerEffect = Assert.Single(offer.SafeOptionProjectionByEventId[355].ExpectedEffects);
        Assert.Equal(NetherEffectKind.AbyssCodeOffer, offerEffect.Kind);
        Assert.Equal(0, offerEffect.ContentId);
    }

    [Fact]
    public void Transform_option_without_a_removable_current_code_fails_before_floor_click()
    {
        NetherInteractiveFloorPreEntrySafetyResult result = Evaluate(Input(
            NetherFloorNodeType.Event,
            events: [Event(100, 1001)],
            parts: [Part(1001, targetType1: 7, parameter1: 0)],
            codes: [Code(30024, NetherCodeEffectKind.Safe)]
        ));

        Assert.False(result.IsSafe);
        Assert.Equal(NetherPauseReason.NoSafeRoute, result.PauseReason);
        Assert.Contains("no-removable-code", result.Detail);
    }

    [Fact]
    public void Three_targets_plus_native_content_are_all_retained()
    {
        NetherInteractiveFloorPreEntrySafetyResult result = Evaluate(Input(
            NetherFloorNodeType.Event,
            events: [Event(100, 1001)],
            parts:
            [
                Part(
                    1001,
                    targetType1: (int)NetherEffectKind.Heal,
                    parameter1: 10,
                    targetType2: (int)NetherEffectKind.ErosionHeal,
                    parameter2: 5,
                    targetType3: (int)NetherEffectKind.NetherGoldUsed,
                    parameter3: 0,
                    contentType: 160,
                    contentId: 0,
                    amount: 1
                ),
            ]
        ));

        Assert.True(result.IsSafe, result.PauseReason + ":" + result.Detail);
        Assert.Equal(4, result.SafeOptionProjectionByEventId[100].ExpectedEffects.Count);
    }

    [Fact]
    public void Structural_part_corruption_is_not_hidden_by_a_known_safe_option()
    {
        NetherInteractiveFloorPreEntrySafetyResult result = Evaluate(Input(
            NetherFloorNodeType.Event,
            events: [Event(100, 1001, 9999)],
            parts: [Part(1001, targetType1: (int)NetherEffectKind.Heal, parameter1: 1)]
        ));

        Assert.False(result.IsSafe);
        Assert.Equal(NetherPauseReason.UnknownMasterData, result.PauseReason);
        Assert.Contains("missing-m-nether-floor-event-part:9999", result.Detail);
    }

    [Fact]
    public void Shop_off_requires_an_observable_close_and_safe_exact_floor_bounds()
    {
        NetherInteractiveFloorPreEntrySafetyResult canClose = Evaluate(Input(
            NetherFloorNodeType.Shop,
            canCloseShop: true
        ));
        NetherInteractiveFloorPreEntrySafetyResult cannotClose = Evaluate(Input(
            NetherFloorNodeType.Shop,
            canCloseShop: false
        ));

        Assert.True(canClose.IsSafe);
        Assert.False(cannotClose.IsSafe);
        Assert.Equal(NetherPauseReason.BindingUnavailable, cannotClose.PauseReason);
    }

    [Fact]
    public void Treasure_key_only_requires_a_key_and_known_safe_master_bounds()
    {
        NetherInteractiveFloorPreEntrySafetyResult key = Evaluate(Input(
            NetherFloorNodeType.Treasure,
            keys: 1
        ));
        NetherInteractiveFloorPreEntrySafetyResult noKey = Evaluate(Input(
            NetherFloorNodeType.Treasure,
            keys: 0
        ));

        Assert.True(key.IsSafe);
        Assert.False(noKey.IsSafe);
        Assert.Equal(NetherPauseReason.NoSafeRoute, noKey.PauseReason);
    }

    [Fact]
    public void Missing_or_duplicate_master_data_is_never_a_safe_interactive_exit()
    {
        NetherInteractiveFloorPreEntrySafetyResult missingPart = Evaluate(Input(
            NetherFloorNodeType.Event,
            events: [Event(100, 9999)],
            parts: []
        ));
        NetherInteractiveFloorPreEntrySafetyResult duplicatePart = Evaluate(Input(
            NetherFloorNodeType.Event,
            events: [Event(100, 1001)],
            parts:
            [
                Part(1001, targetType1: (int)NetherEffectKind.Heal, parameter1: 1),
                Part(1001, targetType1: (int)NetherEffectKind.Heal, parameter1: 1),
            ]
        ));

        Assert.False(missingPart.IsSafe);
        Assert.Equal(NetherPauseReason.UnknownMasterData, missingPart.PauseReason);
        Assert.False(duplicatePart.IsSafe);
        Assert.Equal(NetherPauseReason.UnknownMasterData, duplicatePart.PauseReason);
    }

    private static NetherInteractiveFloorPreEntrySafetyResult Evaluate(NetherInteractiveFloorPreEntrySafetyInput input) =>
        new NetherInteractiveFloorPreEntrySafety().Evaluate(input);

    private static NetherInteractiveFloorPreEntrySafetyInput Input(
        NetherFloorNodeType kind,
        IReadOnlyList<NetherFloorEventMasterRow>? events = null,
        IReadOnlyList<NetherFloorEventPartMasterRow>? parts = null,
        int erosion = 20,
        int hp = 500,
        int gold = 100,
        int keys = 1,
        bool canCloseShop = true,
        int mapMinimumErosion = 0,
        int mapMaximumErosion = 10,
        long floorExtendId = 0,
        IReadOnlyList<NetherCodeState>? codes = null
    ) => new(
        FloorKind: kind,
        FloorMasterId: 900,
        MapFloorRows: [new NetherFloorMasterBoundsRow(900, mapMinimumErosion, mapMaximumErosion)],
        EventRows: events ?? [],
        EventPartRows: parts ?? [],
        CurrentErosion: erosion,
        ActiveHpPermille: [hp],
        CurrentNetherGold: gold,
        CurrentTreasureKeys: keys,
        Settings: new NetherAutoClimbSettings
        {
            SoftErosionLimit = 90,
            MinimumCharacterHpPermille = 300,
            ShopMode = NetherShopMode.Off,
            TreasureMode = NetherTreasureMode.KeyOnly,
        }
    )
    {
        CanCloseShop = canCloseShop,
        FloorExtendId = floorExtendId,
        CurrentCodes = codes ?? [Code(40024, NetherCodeEffectKind.Risk)],
        CodeCapacity = 5,
    };

    private static NetherCodeState Code(long id, NetherCodeEffectKind kind) => new(id, kind, 1)
    {
        IsKnown = true,
        Category = kind == NetherCodeEffectKind.Safe
            ? NetherCodeCategory.ErosionResistance
            : NetherCodeCategory.ErosionEnhancement,
        Rarity = 1,
    };

    private static NetherFloorEventMasterRow Event(long eventId, params long[] partIds) => new(
        EventId: eventId,
        MapFloorMasterId: 900,
        Weight: 1,
        PartId1: partIds.ElementAtOrDefault(0),
        PartId2: partIds.ElementAtOrDefault(1),
        PartId3: partIds.ElementAtOrDefault(2),
        PartId4: partIds.ElementAtOrDefault(3)
    );

    private static NetherFloorEventPartMasterRow Part(
        long partId,
        int targetType1,
        long parameter1,
        int targetType2 = 0,
        long parameter2 = 0,
        int targetType3 = 0,
        long parameter3 = 0,
        int contentType = 0,
        long contentId = 0,
        long amount = 0
    ) => new(
        PartId: partId,
        TargetType1: targetType1,
        SelectParameter1: parameter1,
        TargetType2: targetType2,
        SelectParameter2: parameter2,
        TargetType3: targetType3,
        SelectParameter3: parameter3,
        ContentType: contentType,
        ContentId: contentId,
        Amount: amount
    );
}
