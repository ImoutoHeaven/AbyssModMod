using System.Collections;
using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherRuntimeInteractivePreEntryInputCaptureTests
{
    [Fact]
    public void Exact_floor_and_master_numeric_fields_are_captured_and_all_weighted_rows_are_proved()
    {
        NetherRuntimeInteractivePreEntryCaptureResult result = Capture(
            floor: new FloorFixture { MNetherMapFloorId = 900, ExtendId = 42, FloorType = (int)NetherFloorNodeType.Event },
            events: new object[]
            {
                Event(42, 900, rowWeight: 1, eventType: 4, part1: 1001),
                Event(43, 900, rowWeight: 1, eventType: 4, part1: 1002),
            },
            parts: new object[]
            {
                Part(1001, target1: (int)NetherEffectKind.Heal, parameter1: 1),
                Part(1002, target1: (int)NetherEffectKind.NetherGoldUsed, parameter1: 0),
            }
        );

        Assert.True(result.IsCaptured);
        Assert.NotNull(result.Input);
        Assert.Equal(900, result.Input!.FloorMasterId);
        Assert.Equal(42, result.Input.FloorExtendId);
        Assert.Equal(NetherFloorNodeType.Event, result.Input.FloorKind);
        Assert.True(result.Safety.IsSafe);
        Assert.Equal(2, result.Safety.SafeOptionNumberByEventId.Count);
    }

    [Fact]
    public void Missing_master_or_unknown_authoritative_resource_is_captured_as_fail_closed_input()
    {
        NetherRuntimeInteractivePreEntryCaptureResult missingMaster = Capture(
            mapRows: Array.Empty<object>(),
            events: new object[] { Event(42, 900, 1, 4, 1001) },
            parts: new object[] { Part(1001, (int)NetherEffectKind.Heal, 1) }
        );
        NetherRuntimeInteractivePreEntryCaptureResult missingGold = Capture(
            gold: null,
            events: new object[] { Event(42, 900, 1, 4, 1001) },
            parts: new object[] { Part(1001, (int)NetherEffectKind.Heal, 1) }
        );

        Assert.True(missingMaster.IsCaptured);
        Assert.False(missingMaster.Safety.IsSafe);
        Assert.Equal(NetherPauseReason.UnknownMasterData, missingMaster.Safety.PauseReason);
        Assert.True(missingGold.IsCaptured);
        Assert.False(missingGold.Safety.IsSafe);
        Assert.Equal(NetherPauseReason.UnknownMasterData, missingGold.Safety.PauseReason);
    }

    [Fact]
    public void Duplicate_event_master_or_missing_referenced_part_cannot_be_promoted_to_safe()
    {
        NetherRuntimeInteractivePreEntryCaptureResult duplicate = Capture(
            events: new object[]
            {
                Event(42, 900, 1, 4, 1001),
                Event(42, 900, 1, 4, 1001),
            },
            parts: new object[] { Part(1001, (int)NetherEffectKind.Heal, 1) }
        );
        NetherRuntimeInteractivePreEntryCaptureResult missingPart = Capture(
            events: new object[] { Event(42, 900, 1, 4, 9999) },
            parts: Array.Empty<object>()
        );

        Assert.True(duplicate.IsCaptured);
        Assert.False(duplicate.Safety.IsSafe);
        Assert.Equal(NetherPauseReason.UnknownMasterData, duplicate.Safety.PauseReason);
        Assert.True(missingPart.IsCaptured);
        Assert.False(missingPart.Safety.IsSafe);
        Assert.Equal(NetherPauseReason.UnknownMasterData, missingPart.Safety.PauseReason);
    }

    [Fact]
    public void Bad_part_numeric_shape_is_rejected_without_localized_text_or_default_effect()
    {
        NetherRuntimeInteractivePreEntryCaptureResult result = Capture(
            events: new object[] { Event(42, 900, 1, 4, 1001) },
            parts: new object[] { Part(1001, target1: 99, parameter1: 0) }
        );

        Assert.True(result.IsCaptured);
        Assert.False(result.Safety.IsSafe);
        Assert.Equal(NetherPauseReason.UnknownMasterData, result.Safety.PauseReason);
        Assert.Contains("unsupported-event-target", result.Safety.Detail);
    }

    [Fact]
    public void Shop_close_capability_is_an_explicit_exact_binding_boolean_not_a_default_true()
    {
        NetherRuntimeInteractivePreEntryCaptureResult unavailable = Capture(
            floor: new FloorFixture { MNetherMapFloorId = 900, ExtendId = 0, FloorType = (int)NetherFloorNodeType.Shop },
            canCloseShop: false
        );
        NetherRuntimeInteractivePreEntryCaptureResult available = Capture(
            floor: new FloorFixture { MNetherMapFloorId = 900, ExtendId = 0, FloorType = (int)NetherFloorNodeType.Shop },
            canCloseShop: true
        );

        Assert.True(unavailable.IsCaptured);
        Assert.False(unavailable.Safety.IsSafe);
        Assert.Equal(NetherPauseReason.BindingUnavailable, unavailable.Safety.PauseReason);
        Assert.True(available.IsCaptured);
        Assert.True(available.Safety.IsSafe);
    }

    private static NetherRuntimeInteractivePreEntryCaptureResult Capture(
        object? floor = null,
        IEnumerable? mapRows = null,
        IEnumerable? events = null,
        IEnumerable? parts = null,
        int? erosion = 20,
        IReadOnlyList<int>? hp = null,
        int? gold = 100,
        int? keys = 1,
        bool canCloseShop = false
    ) => new NetherRuntimeInteractivePreEntryInputCapture().Capture(new NetherRuntimeInteractivePreEntryCaptureRequest(
        FloorModel: floor ?? new FloorFixture
        {
            MNetherMapFloorId = 900,
            ExtendId = 42,
            FloorType = (int)NetherFloorNodeType.Event,
        },
        MapFloorRows: mapRows ?? new ArrayList { new MapFloorFixture { id = 900, min_erosion_point = 0, max_erosion_point = 10 } },
        EventRows: events ?? new ArrayList { Event(42, 900, 1, 4, 1001) },
        EventPartRows: parts ?? new ArrayList { Part(1001, (int)NetherEffectKind.Heal, 1) },
        CurrentErosion: erosion,
        ActiveHpPermille: hp ?? new[] { 500 },
        CurrentNetherGold: gold,
        CurrentTreasureKeys: keys,
        Settings: new NetherAutoClimbSettings
        {
            SoftErosionLimit = 90,
            MinimumCharacterHpPermille = 300,
            ShopMode = NetherShopMode.Off,
            TreasureMode = NetherTreasureMode.KeyOnly,
        },
        CanCloseShop: canCloseShop
    ));

    private static EventFixture Event(long eventId, long mapFloorId, int rowWeight, int eventType, long part1) => new()
    {
        id = eventId,
        m_nether_map_floor_id = mapFloorId,
        weight = rowWeight,
        type = eventType,
        m_nether_floor_event_part_id_1 = part1,
    };

    private static PartFixture Part(long partId, int target1, long parameter1) => new()
    {
        id = partId,
        target_type_1 = target1,
        select_parameter_1 = parameter1,
    };

    private sealed class FloorFixture
    {
        public long MNetherMapFloorId { get; init; }
        public long ExtendId { get; init; }
        public int FloorType { get; init; }
    }

    private sealed class MapFloorFixture
    {
        public long id;
        public int min_erosion_point;
        public int max_erosion_point;
    }

    private sealed class EventFixture
    {
        public long id;
        public long m_nether_map_floor_id;
        public int weight;
        public int type;
        public long m_nether_floor_event_part_id_1;
        public long m_nether_floor_event_part_id_2;
        public long m_nether_floor_event_part_id_3;
        public long m_nether_floor_event_part_id_4;
    }

    private sealed class PartFixture
    {
        public long id;
        public int target_type_1;
        public long select_parameter_1;
        public int target_type_2;
        public long select_parameter_2;
        public int target_type_3;
        public long select_parameter_3;
        public int content_type;
        public long content_id;
        public int amount;
    }
}
