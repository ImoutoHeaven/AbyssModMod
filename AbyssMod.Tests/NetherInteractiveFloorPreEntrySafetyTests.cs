using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherInteractiveFloorPreEntrySafetyTests
{
    [Fact]
    public void Event_is_safe_only_when_every_positive_weight_master_row_has_a_safe_exit()
    {
        NetherInteractiveFloorPreEntrySafetyResult result = Evaluate(Input(
            NetherFloorNodeType.Event,
            events:
            [
                Event(100, 1001),
                Event(101, 1002),
            ],
            parts:
            [
                Part(1001, targetType1: (int)NetherEffectKind.Heal, parameter1: 1),
                Part(1002, targetType1: (int)NetherEffectKind.Heal, parameter1: 100),
            ]
        ));

        Assert.True(result.IsSafe);
        Assert.Equal(2, result.SafeOptionNumberByEventId.Count);
        Assert.Equal(1, result.SafeOptionNumberByEventId[100]);
        Assert.Equal(1, result.SafeOptionNumberByEventId[101]);
    }

    [Fact]
    public void One_possible_event_row_without_safe_option_makes_the_floor_unsafe()
    {
        NetherInteractiveFloorPreEntrySafetyResult result = Evaluate(Input(
            NetherFloorNodeType.Event,
            events: [Event(100, 1001), Event(101, 1002)],
            parts:
            [
                Part(1001, targetType1: (int)NetherEffectKind.Heal, parameter1: 1),
                Part(1002, targetType1: (int)NetherEffectKind.Damage, parameter1: 500),
            ],
            hp: 500
        ));

        Assert.False(result.IsSafe);
        Assert.Equal(NetherPauseReason.UnsafeHp, result.PauseReason);
        Assert.Contains("event-row-101", result.Detail);
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
        bool canCloseShop = true
    ) => new(
        FloorKind: kind,
        FloorMasterId: 900,
        MapFloorRows: [new NetherFloorMasterBoundsRow(900, 0, 10)],
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
