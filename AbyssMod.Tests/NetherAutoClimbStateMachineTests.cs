using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherAutoClimbStateMachineTests
{
    [Fact]
    public void Toggle_outside_nether_stays_disabled_with_not_in_nether_reason()
    {
        var machine = new NetherAutoClimbStateMachine();

        machine.Toggle(isInNether: false);

        Assert.False(machine.IsEnabled);
        Assert.Equal(NetherAutoClimbPhase.Disabled, machine.Phase);
        Assert.Equal(NetherPauseReason.NotInNether, machine.PauseReason);
    }

    [Fact]
    public void One_in_flight_action_rejects_a_second_action()
    {
        var machine = StableMachine();
        NetherSnapshotFingerprint fingerprint = Fingerprint(NetherSessionStatus.Play, 10);

        Assert.True(machine.TryBegin(new NetherPlannedAction(NetherActionKind.SelectFloor), fingerprint));
        Assert.False(machine.TryBegin(new NetherPlannedAction(NetherActionKind.SelectFloor), fingerprint));
        Assert.Equal(NetherAutoClimbPhase.ExecutingNativeAction, machine.Phase);
    }

    [Fact]
    public void Unknown_outcome_requires_reconcile_before_another_action()
    {
        var machine = StableMachine();
        NetherSnapshotFingerprint before = Fingerprint(NetherSessionStatus.Play, 10);

        Assert.True(machine.TryBegin(new NetherPlannedAction(NetherActionKind.SelectFloor), before));
        machine.ObserveUnknownOutcome();

        Assert.Equal(NetherAutoClimbPhase.Reconciling, machine.Phase);
        Assert.False(machine.TryBegin(new NetherPlannedAction(NetherActionKind.SelectFloor), before));
        Assert.Equal(NetherActionKind.SelectFloor, machine.PendingAction!.Value.Kind);
    }

    [Fact]
    public void Changed_fingerprint_confirms_action_and_returns_stable()
    {
        var machine = StableMachine();
        NetherSnapshotFingerprint before = Fingerprint(NetherSessionStatus.Play, 10);
        NetherSnapshotFingerprint after = Fingerprint(NetherSessionStatus.Play, 11);

        Assert.True(machine.TryBegin(new NetherPlannedAction(NetherActionKind.SelectFloor), before));

        machine.ObserveActionResult(after, NetherActionOutcome.Applied);

        Assert.Equal(NetherAutoClimbPhase.Stable, machine.Phase);
        Assert.Null(machine.PendingAction);
    }

    [Fact]
    public void Unchanged_fingerprint_after_known_failure_returns_stable_without_retrying()
    {
        var machine = StableMachine();
        NetherSnapshotFingerprint fingerprint = Fingerprint(NetherSessionStatus.Play, 10);

        Assert.True(machine.TryBegin(new NetherPlannedAction(NetherActionKind.SelectFloor), fingerprint));

        machine.ObserveActionResult(fingerprint, NetherActionOutcome.NotApplied);

        Assert.Equal(NetherAutoClimbPhase.Stable, machine.Phase);
        Assert.Null(machine.PendingAction);
        Assert.False(machine.TryBegin(new NetherPlannedAction(NetherActionKind.SelectFloor), fingerprint));
    }

    [Fact]
    public void Ambiguous_fingerprint_pauses_instead_of_replaying_action()
    {
        var machine = StableMachine();
        NetherSnapshotFingerprint before = Fingerprint(NetherSessionStatus.Play, 10);
        NetherSnapshotFingerprint after = Fingerprint(NetherSessionStatus.Play, 12);

        Assert.True(machine.TryBegin(new NetherPlannedAction(NetherActionKind.SelectFloor), before));

        machine.ObserveActionResult(after, NetherActionOutcome.Ambiguous);

        Assert.Equal(NetherAutoClimbPhase.Paused, machine.Phase);
        Assert.Equal(NetherPauseReason.AmbiguousServerOutcome, machine.PauseReason);
        Assert.False(machine.TryBegin(new NetherPlannedAction(NetherActionKind.SelectFloor), after));
    }

    [Theory]
    [InlineData((int)NetherSessionStatus.Clear)]
    [InlineData((int)NetherSessionStatus.Lose)]
    public void Clear_or_lose_is_not_completed_until_result_response(int statusValue)
    {
        var machine = new NetherAutoClimbStateMachine();
        NetherSessionStatus status = (NetherSessionStatus)statusValue;

        machine.Toggle(isInNether: true);
        machine.ObserveStable(Fingerprint(status, 20));

        Assert.Equal(NetherAutoClimbPhase.AwaitingSceneChange, machine.Phase);
        Assert.NotEqual(NetherAutoClimbPhase.Completed, machine.Phase);

        machine.Complete();

        Assert.Equal(NetherAutoClimbPhase.Completed, machine.Phase);
    }

    [Fact]
    public void F11_busy_moves_battle_wait_to_awaiting_f11_and_back()
    {
        var machine = StableMachine();
        NetherSnapshotFingerprint fingerprint = Fingerprint(NetherSessionStatus.Battle, 30);

        Assert.True(machine.TryBegin(new NetherPlannedAction(NetherActionKind.AwaitNativeFlow), fingerprint));
        Assert.Equal(NetherAutoClimbPhase.AwaitingBattle, machine.Phase);

        machine.ObserveF11Busy(isBusy: true);
        Assert.Equal(NetherAutoClimbPhase.AwaitingF11, machine.Phase);

        machine.ObserveF11Busy(isBusy: false);
        Assert.Equal(NetherAutoClimbPhase.AwaitingBattle, machine.Phase);
    }

    [Fact]
    public void Native_binding_selector_requires_one_exact_full_signature_match()
    {
        NetherNativeMethodDescriptor expected = new(
            "OnFloorClickedEventAsync",
            new[] { "System.Int32", "System.Int32" },
            "Cysharp.Threading.Tasks.UniTask");
        NetherNativeMethodDescriptor selected = expected;
        NetherNativeMethodDescriptor wrongReturn = new(
            "OnFloorClickedEventAsync",
            new[] { "System.Int32", "System.Int32" },
            "System.Void");

        NetherNativeBindingSelection selection = NetherNativeMethodBindingSelector.Select(
            expected,
            new[] { selected, wrongReturn });

        Assert.Equal(NetherNativeActionResultKind.Started, selection.ResultKind);
        Assert.Equal(selected, selection.Method);
    }

    [Fact]
    public void Native_binding_selector_fails_closed_for_zero_or_ambiguous_matches()
    {
        NetherNativeMethodDescriptor expected = new(
            "OnFloorClickedEventAsync",
            new[] { "System.Int32", "System.Int32" },
            "Cysharp.Threading.Tasks.UniTask");
        NetherNativeMethodDescriptor wrongArity = new(
            "OnFloorClickedEventAsync",
            new[] { "System.Int32" },
            "Cysharp.Threading.Tasks.UniTask");

        NetherNativeBindingSelection none = NetherNativeMethodBindingSelector.Select(expected, new[] { wrongArity });
        NetherNativeBindingSelection many = NetherNativeMethodBindingSelector.Select(expected, new[] { expected, expected });

        Assert.Equal(NetherNativeActionResultKind.BindingUnavailable, none.ResultKind);
        Assert.Null(none.Method);
        Assert.Equal(NetherNativeActionResultKind.BindingUnavailable, many.ResultKind);
        Assert.Null(many.Method);
    }

    private static NetherAutoClimbStateMachine StableMachine()
    {
        var machine = new NetherAutoClimbStateMachine();
        machine.Toggle(isInNether: true);
        machine.ObserveStable(Fingerprint(NetherSessionStatus.Play, 1));
        return machine;
    }

    private static NetherSnapshotFingerprint Fingerprint(NetherSessionStatus status, int floorLevel) => new(
        status,
        netherId: 100,
        mapId: 200,
        floorLevel,
        floorIndex: 1,
        erosionPoint: 20,
        characterHpHash: "1000:1000",
        codeHash: "30024",
        mapHash: $"map-{floorLevel}"
    );
}
