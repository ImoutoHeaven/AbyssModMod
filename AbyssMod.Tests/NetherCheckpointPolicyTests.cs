using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherCheckpointPolicyTests
{
    [Fact]
    public void Effective_max_depth_is_minimum_of_config_server_and_master()
    {
        NetherCheckpointDecision decision = Decide(Snapshot(NetherSessionStatus.Play, floor: 10, max: 120, masterMax: 110), Settings(maxDepth: 130));

        Assert.Equal(110, decision.EffectiveMaxDepth);
    }

    [Fact]
    public void F12_never_enters_a_floor_above_effective_target()
    {
        NetherSnapshot snapshot = Snapshot(NetherSessionStatus.Play, floor: 10, max: 120, masterMax: 110);

        Assert.True(new NetherCheckpointPolicy().CanEnterFloor(snapshot, Settings(maxDepth: 130), 110));
        Assert.False(new NetherCheckpointPolicy().CanEnterFloor(snapshot, Settings(maxDepth: 130), 111));
    }

    [Fact]
    public void Non_sleep_target_floor_pauses_without_cancel_or_result()
    {
        NetherCheckpointDecision decision = Decide(Snapshot(NetherSessionStatus.Play, floor: 50, max: 100, masterMax: 100), Settings(maxDepth: 50));

        Assert.Equal(NetherCheckpointDecisionKind.PauseAtNonCheckpointTarget, decision.Kind);
        Assert.Equal(NetherPauseReason.TargetReachedOutsideCheckpoint, decision.PauseReason);
    }

    [Fact]
    public void Sleep_below_target_with_ticket_continues_with_exactly_one_ticket()
    {
        NetherCheckpointDecision decision = Decide(Snapshot(NetherSessionStatus.Sleep, floor: 40, max: 100, masterMax: 100, tickets: 5), Settings(maxDepth: 50));

        Assert.Equal(NetherCheckpointDecisionKind.ContinueOneTicket, decision.Kind);
        Assert.Equal(1, decision.TicketCount);
    }

    [Fact]
    public void Sleep_at_target_finishes_normally()
    {
        NetherCheckpointDecision decision = Decide(Snapshot(NetherSessionStatus.Sleep, floor: 50, max: 100, masterMax: 100, tickets: 5), Settings(maxDepth: 50));

        Assert.Equal(NetherCheckpointDecisionKind.FinishNormally, decision.Kind);
    }

    [Fact]
    public void Sleep_without_ticket_finishes_normally_instead_of_refusing_start()
    {
        NetherCheckpointDecision decision = Decide(Snapshot(NetherSessionStatus.Sleep, floor: 40, max: 100, masterMax: 100, tickets: 0), Settings(maxDepth: 50));

        Assert.Equal(NetherCheckpointDecisionKind.FinishNormally, decision.Kind);
    }

    [Fact]
    public void Clear_awaits_result_scene_response()
    {
        NetherCheckpointDecision decision = Decide(Snapshot(NetherSessionStatus.Clear, floor: 100, max: 100, masterMax: 100), Settings());

        Assert.Equal(NetherCheckpointDecisionKind.AwaitResult, decision.Kind);
    }

    [Fact]
    public void Lose_pauses_without_using_signal()
    {
        NetherCheckpointDecision decision = Decide(Snapshot(NetherSessionStatus.Lose, floor: 40, max: 100, masterMax: 100, tickets: 1), Settings());

        Assert.Equal(NetherCheckpointDecisionKind.Pause, decision.Kind);
        Assert.Equal(NetherPauseReason.Lose, decision.PauseReason);
        Assert.Equal(0, decision.TicketCount);
    }

    [Fact]
    public void Max_depth_lowered_below_current_floor_pauses_at_the_next_stable_boundary()
    {
        var gate = new NetherAutoClimbSettingsSnapshotGate();
        var snapshot = Snapshot(NetherSessionStatus.Play, floor: 80, max: 130, masterMax: 130);

        Assert.True(gate.TryCapture(new NetherAutoClimbSettings { MaxDepth = 130 }, NetherAutoClimbPhase.Stable, out _, out _, out _));
        Assert.True(gate.TryCapture(new NetherAutoClimbSettings { MaxDepth = 70 }, NetherAutoClimbPhase.Stable, out NetherAutoClimbSettings reloaded, out _, out _));
        NetherCheckpointDecision decision = Decide(snapshot, reloaded);

        Assert.Equal(NetherCheckpointDecisionKind.PauseAtNonCheckpointTarget, decision.Kind);
        Assert.Equal(NetherPauseReason.TargetReachedOutsideCheckpoint, decision.PauseReason);
    }

    private static NetherCheckpointDecision Decide(NetherSnapshot snapshot, NetherAutoClimbSettings settings) => new NetherCheckpointPolicy().Decide(snapshot, settings);

    private static NetherAutoClimbSettings Settings(int maxDepth = 130) => new() { MaxDepth = maxDepth };

    private static NetherSnapshot Snapshot(
        NetherSessionStatus status,
        int floor,
        int max,
        int masterMax,
        int tickets = 0
    ) => new()
    {
        Status = status,
        FloorLevel = floor,
        MaxFloorLevel = max,
        MasterMaxFloorLevel = masterMax,
        TicketCount = tickets,
    };
}
