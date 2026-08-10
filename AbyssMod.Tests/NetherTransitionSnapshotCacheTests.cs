#nullable enable

using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public sealed class NetherTransitionSnapshotCacheTests
{
    [Fact]
    public void Scene_teardown_uses_fresh_datastore_identity_with_cached_floor_graph()
    {
        var cache = new NetherTransitionSnapshotCache();
        NetherSnapshot before = Snapshot(NetherSessionStatus.Play, floorId: 30, floorLevel: 7, apiFloorIndex: 1);
        cache.ObserveFullSnapshot(before);
        cache.BeginBattle();

        NetherRuntimeSnapshotResult result = cache.TryCompose(
            new NetherAuthoritativeTransitionState
            {
                Status = NetherSessionStatus.Battle,
                NetherId = 1,
                MapId = 1,
                CurrentFloorId = 27,
                FloorLevel = 8,
                FloorIndex = 1,
                MaxFloorLevel = 130,
                ContinuanceFloorLevel = 10,
                ErosionPoint = 5,
                TicketCount = 13,
                SignalCount = 0,
                TreasureKeyCount = 0,
                NetherGold = 45,
                CodeReloadCount = 1,
                CodeCapacity = 28,
                LockReward = 0,
                Codes = Array.Empty<NetherCodeState>(),
                AcquiredItems = Array.Empty<NetherRewardItem>(),
            },
            requireFreshBattleCharacters: false
        );

        Assert.True(result.IsSuccess, result.Detail);
        Assert.Equal(NetherSessionStatus.Battle, result.Snapshot!.Status);
        Assert.Equal(27, result.Snapshot.CurrentFloorId);
        Assert.Equal(38654705666, result.Snapshot.CurrentNodeId);
        Assert.Equal(before.Floors, result.Snapshot.Floors);
        Assert.Equal(before.Characters, result.Snapshot.Characters);
        Assert.Equal(45, result.Snapshot.NetherGold);
    }

    [Fact]
    public void Battle_status_zero_master_floor_id_uses_unique_authoritative_coordinate()
    {
        var cache = new NetherTransitionSnapshotCache();
        cache.ObserveFullSnapshot(Snapshot(NetherSessionStatus.Play, floorId: 30, floorLevel: 7, apiFloorIndex: 1));
        cache.BeginBattle();

        NetherRuntimeSnapshotResult result = cache.TryCompose(
            TransitionState(
                NetherSessionStatus.Battle,
                floorId: 0,
                floorLevel: 8,
                apiFloorIndex: 1
            ),
            requireFreshBattleCharacters: false
        );

        Assert.True(result.IsSuccess, result.Detail);
        Assert.Equal(27, result.Snapshot!.CurrentFloorId);
        Assert.Equal(38654705666, result.Snapshot.CurrentNodeId);
    }

    [Fact]
    public void Battle_status_zero_master_floor_id_fails_when_coordinate_is_not_unique()
    {
        var cache = new NetherTransitionSnapshotCache();
        NetherSnapshot snapshot = Snapshot(
            NetherSessionStatus.Play,
            floorId: 30,
            floorLevel: 7,
            apiFloorIndex: 1
        );
        cache.ObserveFullSnapshot(snapshot with
        {
            Floors = snapshot.Floors.Concat(new[]
            {
                new NetherFloorNode(99, 8, 1, NetherFloorNodeType.Boss)
                {
                    NodeId = 38654705667,
                    ApiFloorIndex = 1,
                    IsUnlocked = true,
                },
            }).ToArray(),
        });

        NetherRuntimeSnapshotResult result = cache.TryCompose(
            TransitionState(
                NetherSessionStatus.Battle,
                floorId: 0,
                floorLevel: 8,
                apiFloorIndex: 1
            ),
            requireFreshBattleCharacters: false
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(
            "authoritative-battle-coordinate-not-unique:level=8:api-index=1:matches=2",
            result.Detail
        );
    }

    [Fact]
    public void Play_status_zero_master_floor_id_remains_invalid()
    {
        var cache = new NetherTransitionSnapshotCache();
        cache.ObserveFullSnapshot(Snapshot(NetherSessionStatus.Play, floorId: 30, floorLevel: 7, apiFloorIndex: 1));

        NetherRuntimeSnapshotResult result = cache.TryCompose(
            TransitionState(
                NetherSessionStatus.Play,
                floorId: 0,
                floorLevel: 8,
                apiFloorIndex: 1
            ),
            requireFreshBattleCharacters: false
        );

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid-authoritative-current-floor", result.Detail);
    }

    [Fact]
    public void Clear_result_characters_replace_prebattle_hp_in_postbattle_snapshot()
    {
        var cache = new NetherTransitionSnapshotCache();
        cache.ObserveFullSnapshot(Snapshot(NetherSessionStatus.Play, floorId: 30, floorLevel: 7, apiFloorIndex: 1));
        cache.BeginBattle();
        Assert.True(cache.ObserveBattleResultCharacters(new[]
        {
            new NetherCharacterState(1001, 720, true),
            new NetherCharacterState(1002, 0, false),
        }));

        NetherRuntimeSnapshotResult result = cache.TryCompose(
            new NetherAuthoritativeTransitionState
            {
                Status = NetherSessionStatus.Play,
                NetherId = 1,
                MapId = 1,
                CurrentFloorId = 27,
                FloorLevel = 8,
                FloorIndex = 1,
                MaxFloorLevel = 130,
                ContinuanceFloorLevel = 10,
                ErosionPoint = 10,
                TicketCount = 13,
                SignalCount = 0,
                TreasureKeyCount = 0,
                NetherGold = 50,
                CodeReloadCount = 1,
                CodeCapacity = 28,
                LockReward = 0,
                Codes = new[]
                {
                    new NetherCodeState(30024, NetherCodeEffectKind.Safe, 1),
                },
                AcquiredItems = Array.Empty<NetherRewardItem>(),
            },
            requireFreshBattleCharacters: true
        );

        Assert.True(result.IsSuccess, result.Detail);
        Assert.Equal(720, result.Snapshot!.Characters[0].HpPermille);
        Assert.Equal(0, result.Snapshot.Characters[1].HpPermille);
        Assert.Contains("1001:720", result.Snapshot.CharacterHpHash);
        Assert.Contains("30024", result.Snapshot.CodeHash);
    }

    [Fact]
    public void Postbattle_snapshot_fails_closed_without_authoritative_result_characters()
    {
        var cache = new NetherTransitionSnapshotCache();
        cache.ObserveFullSnapshot(Snapshot(NetherSessionStatus.Play, floorId: 30, floorLevel: 7, apiFloorIndex: 1));
        cache.BeginBattle();

        NetherRuntimeSnapshotResult result = cache.TryCompose(
            new NetherAuthoritativeTransitionState
            {
                Status = NetherSessionStatus.Play,
                NetherId = 1,
                MapId = 1,
                CurrentFloorId = 27,
                FloorLevel = 8,
                FloorIndex = 1,
                MaxFloorLevel = 130,
                ContinuanceFloorLevel = 10,
                ErosionPoint = 10,
                TicketCount = 13,
                CodeCapacity = 28,
                Codes = Array.Empty<NetherCodeState>(),
                AcquiredItems = Array.Empty<NetherRewardItem>(),
            },
            requireFreshBattleCharacters: true
        );

        Assert.False(result.IsSuccess);
        Assert.Equal("missing-authoritative-battle-result-characters", result.Detail);
    }

    [Fact]
    public void Transition_cannot_reuse_a_graph_from_another_map()
    {
        var cache = new NetherTransitionSnapshotCache();
        cache.ObserveFullSnapshot(Snapshot(NetherSessionStatus.Play, floorId: 30, floorLevel: 7, apiFloorIndex: 1));

        NetherRuntimeSnapshotResult result = cache.TryCompose(
            new NetherAuthoritativeTransitionState
            {
                Status = NetherSessionStatus.Battle,
                NetherId = 1,
                MapId = 2,
                CurrentFloorId = 27,
                FloorLevel = 8,
                FloorIndex = 1,
                Codes = Array.Empty<NetherCodeState>(),
                AcquiredItems = Array.Empty<NetherRewardItem>(),
            },
            requireFreshBattleCharacters: false
        );

        Assert.False(result.IsSuccess);
        Assert.Contains("cached-transition-owner-mismatch", result.Detail);
    }

    private static NetherAuthoritativeTransitionState TransitionState(
        NetherSessionStatus status,
        long floorId,
        int floorLevel,
        int apiFloorIndex
    ) => new()
    {
        Status = status,
        NetherId = 1,
        MapId = 1,
        CurrentFloorId = floorId,
        FloorLevel = floorLevel,
        FloorIndex = apiFloorIndex,
        MaxFloorLevel = 130,
        ContinuanceFloorLevel = 10,
        ErosionPoint = 5,
        TicketCount = 13,
        SignalCount = 0,
        TreasureKeyCount = 0,
        NetherGold = 45,
        CodeReloadCount = 1,
        CodeCapacity = 28,
        LockReward = 0,
        Codes = Array.Empty<NetherCodeState>(),
        AcquiredItems = Array.Empty<NetherRewardItem>(),
    };

    private static NetherSnapshot Snapshot(
        NetherSessionStatus status,
        long floorId,
        int floorLevel,
        int apiFloorIndex
    ) => new()
    {
        Status = status,
        NetherId = 1,
        MapId = 1,
        CurrentFloorId = floorId,
        CurrentNodeId = floorId == 30 ? 34359738370 : 38654705666,
        FloorLevel = floorLevel,
        FloorIndex = apiFloorIndex,
        MaxFloorLevel = 130,
        ContinuanceFloorLevel = 10,
        MasterMaxFloorLevel = 130,
        ErosionPoint = 5,
        TicketCount = 13,
        NetherGold = 45,
        CodeReloadCount = 1,
        CodeCapacity = 28,
        Characters = new[]
        {
            new NetherCharacterState(1001, 900, true),
            new NetherCharacterState(1002, 1000, true),
        },
        Codes = Array.Empty<NetherCodeState>(),
        Floors = new[]
        {
            new NetherFloorNode(30, 7, 1, NetherFloorNodeType.Event)
            {
                NodeId = 34359738370,
                ApiFloorIndex = 1,
                IsUnlocked = true,
            },
            new NetherFloorNode(27, 8, 1, NetherFloorNodeType.MiniBoss)
            {
                NodeId = 38654705666,
                ApiFloorIndex = 1,
                IsUnlocked = true,
                PreviousFloorIds = new[] { 34359738370L },
            },
        },
        CharacterHpHash = "1001:900:1|1002:1000:1",
        CodeHash = string.Empty,
        MapHash = "cached-map-1",
    };
}
