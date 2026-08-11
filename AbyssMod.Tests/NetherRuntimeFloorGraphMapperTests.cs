using System;
using System.Collections.Generic;
using System.Linq;
using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public sealed class NetherRuntimeFloorGraphMapperTests
{
    [Fact]
    public void Reused_master_floor_id_at_different_runtime_coordinates_is_preserved()
    {
        NetherRuntimeFloorRaw[] raw =
        {
            Floor(masterId: 1, level: 0, uiIndex: 0, apiIndex: 0),
            Floor(masterId: 3, level: 1, uiIndex: 0, apiIndex: 0, previousMasterIds: new long[] { 1 }),
            Floor(masterId: 4, level: 2, uiIndex: 0, apiIndex: 1, previousMasterIds: new long[] { 3 }),
            Floor(masterId: 5, level: 3, uiIndex: 0, apiIndex: 0, previousMasterIds: new long[] { 4 }),
            Floor(masterId: 3, level: 4, uiIndex: 2, apiIndex: 2, previousMasterIds: new long[] { 5 }),
        };

        bool mapped = NetherRuntimeFloorGraphMapper.TryMap(raw, out IReadOnlyList<NetherFloorNode> nodes, out string error);

        Assert.True(mapped, error);
        Assert.Equal(5, nodes.Count);
        NetherFloorNode[] reused = nodes.Where(node => node.FloorId == 3).OrderBy(node => node.FloorLevel).ToArray();
        Assert.Equal(2, reused.Length);
        Assert.NotEqual(reused[0].NodeId, reused[1].NodeId);
        Assert.Equal(0, reused[0].FloorIndex);
        Assert.Equal(2, reused[1].FloorIndex);
        Assert.Equal(2, reused[1].ApiFloorIndex);
        NetherFloorNode previous = Assert.Single(nodes, node => node.FloorLevel == 3);
        Assert.Equal(new long[] { previous.NodeId }, reused[1].PreviousFloorIds);
    }

    [Fact]
    public void Missing_alternative_previous_master_id_is_ignored_while_present_match_is_linked()
    {
        NetherRuntimeFloorRaw[] raw =
        {
            Floor(masterId: 87, level: 19, uiIndex: 0, apiIndex: 0),
            Floor(
                masterId: 90,
                level: 20,
                uiIndex: 1,
                apiIndex: 1,
                previousMasterIds: new long[] { 88, 87 }
            ),
        };

        bool mapped = NetherRuntimeFloorGraphMapper.TryMap(raw, out IReadOnlyList<NetherFloorNode> nodes, out string error);

        Assert.True(mapped, error);
        NetherFloorNode previous = Assert.Single(nodes, node => node.FloorId == 87);
        NetherFloorNode current = Assert.Single(nodes, node => node.FloorId == 90);
        Assert.Equal(new long[] { previous.NodeId }, current.PreviousFloorIds);
    }

    [Fact]
    public void Duplicate_server_coordinate_is_rejected_even_when_master_ids_differ()
    {
        NetherRuntimeFloorRaw[] raw =
        {
            Floor(masterId: 3, level: 4, uiIndex: 0, apiIndex: 2),
            Floor(masterId: 9, level: 4, uiIndex: 1, apiIndex: 2),
        };

        bool mapped = NetherRuntimeFloorGraphMapper.TryMap(raw, out _, out string error);

        Assert.False(mapped);
        Assert.StartsWith("duplicate-runtime-floor-node:", error, StringComparison.Ordinal);
    }

    private static NetherRuntimeFloorRaw Floor(
        long masterId,
        int level,
        int uiIndex,
        int apiIndex,
        IReadOnlyList<long>? previousMasterIds = null
    ) => new(masterId, level, uiIndex, apiIndex, NetherFloorNodeType.Recovery)
    {
        IsUnlocked = true,
        PreviousMasterFloorIds = previousMasterIds ?? Array.Empty<long>(),
    };
}
