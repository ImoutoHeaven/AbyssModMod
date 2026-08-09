using System;
using System.Collections.Generic;
using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public sealed class NetherRuntimeFloorModelValidatorTests
{
    [Fact]
    public void Floor_zero_start_node_is_valid()
    {
        var seen = new HashSet<long>();

        (bool accepted, long nodeId, string detail) = Validate(101, 0, 0, 0, seen);

        Assert.True(accepted, detail);
        Assert.Equal(string.Empty, detail);
        Assert.True(nodeId > 0);
        Assert.Equal(new long[] { nodeId }, seen);
    }

    [Fact]
    public void Negative_floor_level_remains_invalid()
    {
        var seen = new HashSet<long>();

        (bool accepted, _, string detail) = Validate(101, -1, 0, 0, seen);

        Assert.False(accepted);
        Assert.Equal("invalid-floor-level:-1:master-id=101", detail);
        Assert.Empty(seen);
    }

    [Fact]
    public void Reused_master_id_at_a_different_coordinate_is_valid()
    {
        var seen = new HashSet<long>();
        (bool firstAccepted, long firstNodeId, _) = Validate(101, 0, 0, 0, seen);

        (bool accepted, long secondNodeId, string detail) = Validate(101, 4, 2, 2, seen);

        Assert.True(firstAccepted);
        Assert.True(accepted, detail);
        Assert.NotEqual(firstNodeId, secondNodeId);
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public void Duplicate_runtime_coordinate_remains_invalid()
    {
        var seen = new HashSet<long>();
        Assert.True(Validate(101, 4, 0, 2, seen).Accepted);

        (bool accepted, long nodeId, string detail) = Validate(999, 4, 1, 2, seen);

        Assert.False(accepted);
        Assert.Equal(0, nodeId);
        Assert.StartsWith("duplicate-runtime-floor-node:", detail);
        Assert.Single(seen);
    }

    private static (bool Accepted, long NodeId, string Detail) Validate(
        long masterFloorId,
        int floorLevel,
        int uiFloorIndex,
        int apiFloorIndex,
        ISet<long> seen
    )
    {
        bool accepted = NetherRuntimeFloorModelValidator.TryCreateNodeId(
            masterFloorId,
            floorLevel,
            uiFloorIndex,
            apiFloorIndex,
            seen,
            out long nodeId,
            out string detail
        );
        return (accepted, nodeId, detail);
    }
}
