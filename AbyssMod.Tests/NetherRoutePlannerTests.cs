using System.Collections.Generic;
using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherRoutePlannerTests
{
    [Fact]
    public void Event_type_four_remains_event_and_is_not_classified_as_battle()
    {
        NetherFloorNode eventNode = Node(2, 2, NetherFloorNodeType.Event, 1);
        NetherRoutePlan plan = Plan(Snapshot(1, Node(1, 1, NetherFloorNodeType.Recovery), eventNode, Terminal(3, 3, 2)));

        NetherFloorNode selected = Assert.IsType<NetherFloorNode>(plan.SelectedNode);
        Assert.Equal(NetherFloorNodeType.Event, selected.NodeType);
        Assert.NotEqual(NetherFloorNodeType.Battle, selected.NodeType);
    }

    [Fact]
    public void Locked_hidden_node_is_never_selected()
    {
        NetherFloorNode locked = Node(2, 2, NetherFloorNodeType.Recovery, 1) with { IsHidden = true, IsUnlocked = false, RewardTier = 99 };
        NetherFloorNode safe = Node(3, 2, NetherFloorNodeType.Recovery, 1) with { RewardTier = 1 };
        NetherRoutePlan plan = Plan(Snapshot(1, Node(1, 1, NetherFloorNodeType.Recovery), locked, safe, Terminal(4, 3, 3)));

        Assert.Equal(3, Assert.IsType<NetherFloorNode>(plan.SelectedNode).FloorId);
        Assert.Contains(plan.Audit, audit => audit.FloorId == 2 && audit.Reason == "locked");
    }

    [Fact]
    public void Candidate_leading_to_dead_end_is_rejected_even_when_reward_is_higher()
    {
        NetherFloorNode deadEnd = Node(2, 2, NetherFloorNodeType.Recovery, 1) with { RewardTier = 99 };
        NetherFloorNode route = Node(3, 2, NetherFloorNodeType.Recovery, 1) with { RewardTier = 1 };
        NetherRoutePlan plan = Plan(Snapshot(1, Node(1, 1, NetherFloorNodeType.Recovery), deadEnd, route, Terminal(4, 3, 3)));

        Assert.Equal(3, Assert.IsType<NetherFloorNode>(plan.SelectedNode).FloorId);
        Assert.Contains(plan.Audit, audit => audit.FloorId == 2 && audit.Reason == "dead-end");
    }

    [Fact]
    public void Planner_uses_server_prev_ids_instead_of_master_next_guess()
    {
        NetherFloorNode masterAdjacentButServerDisconnected = Node(2, 2, NetherFloorNodeType.Recovery, 4) with { RewardTier = 99 };
        NetherFloorNode serverConnected = Node(3, 2, NetherFloorNodeType.Recovery, 1) with { RewardTier = 1 };
        NetherRoutePlan plan = Plan(Snapshot(1, Node(1, 1, NetherFloorNodeType.Recovery), masterAdjacentButServerDisconnected, serverConnected, Terminal(4, 3, 3)));

        Assert.Equal(3, Assert.IsType<NetherFloorNode>(plan.SelectedNode).FloorId);
    }

    [Fact]
    public void Newly_opened_node_is_considered_only_in_the_next_snapshot()
    {
        NetherFloorNode current = Node(1, 1, NetherFloorNodeType.Recovery);
        NetherFloorNode locked = Node(2, 2, NetherFloorNodeType.Recovery, 1) with { IsHidden = true, IsUnlocked = false, RewardTier = 9 };
        NetherFloorNode fallback = Node(3, 2, NetherFloorNodeType.Recovery, 1) with { RewardTier = 1 };
        NetherFloorNode terminal = Terminal(4, 3, 2, 3);

        NetherRoutePlan before = Plan(Snapshot(1, current, locked, fallback, terminal));
        NetherRoutePlan after = Plan(Snapshot(1, current, locked with { IsUnlocked = true }, fallback, terminal));

        Assert.Equal(3, Assert.IsType<NetherFloorNode>(before.SelectedNode).FloorId);
        Assert.Equal(2, Assert.IsType<NetherFloorNode>(after.SelectedNode).FloorId);
    }

    [Theory]
    [InlineData((int)NetherFloorNodeType.Unknown)]
    [InlineData((int)NetherFloorNodeType.Default)]
    public void Unknown_or_default_floor_causes_fail_closed_pause(int rawNodeType)
    {
        NetherFloorNodeType nodeType = (NetherFloorNodeType)rawNodeType;
        NetherRoutePlan plan = Plan(Snapshot(1, Node(1, 1, NetherFloorNodeType.Recovery), Node(2, 2, nodeType, 1), Terminal(3, 3, 2)));

        Assert.Null(plan.SelectedNode);
        Assert.Equal(NetherPauseReason.UnknownFloor, plan.PauseReason);
    }

    [Fact]
    public void Candidate_whose_terminal_erosion_budget_reaches_100_is_rejected()
    {
        NetherRouteSafetyContext context = Context() with
        {
            MinimumWorstCaseErosionToTerminal = new Dictionary<long, int> { [2] = 10 },
        };
        NetherSnapshot snapshot = Snapshot(1, erosion: 90, Node(1, 1, NetherFloorNodeType.Recovery), Node(2, 2, NetherFloorNodeType.Recovery, 1), Terminal(3, 3, 2));

        NetherRoutePlan plan = new NetherRoutePlanner().Plan(snapshot, context);

        Assert.Null(plan.SelectedNode);
        Assert.Equal(NetherPauseReason.UnsafeErosion, plan.PauseReason);
    }

    [Fact]
    public void Equivalent_candidates_use_floor_index_then_id_for_stable_tie_breaking()
    {
        NetherFloorNode laterIndex = Node(30, 2, NetherFloorNodeType.Recovery, 1) with { FloorIndex = 3 };
        NetherFloorNode firstIdAtSameIndex = Node(10, 2, NetherFloorNodeType.Recovery, 1) with { FloorIndex = 2 };
        NetherFloorNode laterIdAtSameIndex = Node(20, 2, NetherFloorNodeType.Recovery, 1) with { FloorIndex = 2 };
        NetherRoutePlan plan = Plan(Snapshot(1, Node(1, 1, NetherFloorNodeType.Recovery), laterIndex, laterIdAtSameIndex, firstIdAtSameIndex, Terminal(40, 3, 10, 20, 30)));

        Assert.Equal(10, Assert.IsType<NetherFloorNode>(plan.SelectedNode).FloorId);
    }

    private static NetherRoutePlan Plan(NetherSnapshot snapshot) => new NetherRoutePlanner().Plan(snapshot, Context());

    private static NetherRouteSafetyContext Context() => new()
    {
        MinimumWorstCaseErosionToTerminal = new Dictionary<long, int>(),
        HpSafeByFloorId = new Dictionary<long, bool>(),
        KnownNodeByFloorId = new Dictionary<long, bool>(),
        SafeCodeOpportunityByFloorId = new Dictionary<long, int>(),
        ProjectedErosionDeltaByFloorId = new Dictionary<long, int>(),
        ProjectedHpDeltaByFloorId = new Dictionary<long, int>(),
    };

    private static NetherSnapshot Snapshot(long currentFloorId, params NetherFloorNode[] floors) => Snapshot(currentFloorId, erosion: 20, floors);

    private static NetherSnapshot Snapshot(long currentFloorId, int erosion, params NetherFloorNode[] floors) => new()
    {
        Status = NetherSessionStatus.Play,
        CurrentFloorId = currentFloorId,
        FloorLevel = 1,
        FloorIndex = 1,
        ErosionPoint = erosion,
        Floors = floors,
    };

    private static NetherFloorNode Node(long id, int level, NetherFloorNodeType type, params long[] previousIds) => new(id, level, (int)id, type)
    {
        IsUnlocked = true,
        PreviousFloorIds = previousIds,
    };

    private static NetherFloorNode Terminal(long id, int level, params long[] previousIds) => Node(id, level, NetherFloorNodeType.Boss, previousIds);
}
