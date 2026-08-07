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

    [Fact]
    public void Clear_is_not_completed_until_result_response()
    {
        var machine = new NetherAutoClimbStateMachine();

        machine.Toggle(isInNether: true);
        machine.ObserveStable(Fingerprint(NetherSessionStatus.Clear, 20));

        Assert.Equal(NetherAutoClimbPhase.AwaitingSceneChange, machine.Phase);
        Assert.NotEqual(NetherAutoClimbPhase.Completed, machine.Phase);

        machine.Complete();

        Assert.Equal(NetherAutoClimbPhase.Completed, machine.Phase);
    }

    [Fact]
    public void Lose_pauses_for_user_control_and_never_waits_forever_for_a_result_request()
    {
        var machine = new NetherAutoClimbStateMachine();

        machine.Toggle(isInNether: true);
        machine.ObserveStable(Fingerprint(NetherSessionStatus.Lose, 20));

        Assert.Equal(NetherAutoClimbPhase.Paused, machine.Phase);
        Assert.Equal(NetherPauseReason.Lose, machine.PauseReason);
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

    [Fact]
    public void Invalid_live_config_pauses_instead_of_clamping_to_a_dangerous_default()
    {
        var gate = new NetherAutoClimbSettingsSnapshotGate();

        bool accepted = gate.TryCapture(
            new NetherAutoClimbSettings { MaxDepth = 0, SoftErosionLimit = 90 },
            NetherAutoClimbPhase.Stable,
            out _,
            out NetherPauseReason reason,
            out string detail);

        Assert.False(accepted);
        Assert.Equal(NetherPauseReason.InvalidConfiguration, reason);
        Assert.Equal("invalid-max-depth", detail);
    }

    [Fact]
    public void Zero_soft_erosion_limit_is_invalid_instead_of_disabling_the_safety_boundary()
    {
        var gate = new NetherAutoClimbSettingsSnapshotGate();

        bool accepted = gate.TryCapture(
            new NetherAutoClimbSettings { SoftErosionLimit = 0 },
            NetherAutoClimbPhase.Stable,
            out _,
            out NetherPauseReason reason,
            out string detail);

        Assert.False(accepted);
        Assert.Equal(NetherPauseReason.InvalidConfiguration, reason);
        Assert.Equal("invalid-soft-erosion-limit", detail);
    }

    [Fact]
    public void Positive_max_depth_above_the_default_is_valid_and_later_clamped_by_server_limits()
    {
        var gate = new NetherAutoClimbSettingsSnapshotGate();

        bool accepted = gate.TryCapture(
            new NetherAutoClimbSettings { MaxDepth = 500 },
            NetherAutoClimbPhase.Stable,
            out NetherAutoClimbSettings settings,
            out _,
            out _);

        Assert.True(accepted);
        Assert.Equal(500, settings.MaxDepth);
    }

    [Fact]
    public void Reloaded_settings_apply_only_after_the_current_action_reconciles()
    {
        var gate = new NetherAutoClimbSettingsSnapshotGate();
        var original = new NetherAutoClimbSettings { MaxDepth = 130, SoftErosionLimit = 90 };
        var reloaded = new NetherAutoClimbSettings { MaxDepth = 70, SoftErosionLimit = 80 };

        Assert.True(gate.TryCapture(original, NetherAutoClimbPhase.Stable, out NetherAutoClimbSettings active, out _, out _));
        Assert.True(gate.TryCapture(reloaded, NetherAutoClimbPhase.ExecutingNativeAction, out NetherAutoClimbSettings duringAction, out _, out _));
        Assert.True(gate.TryCapture(reloaded, NetherAutoClimbPhase.Stable, out NetherAutoClimbSettings afterReconcile, out _, out _));

        Assert.Same(original, active);
        Assert.Same(original, duringAction);
        Assert.Same(reloaded, afterReconcile);
    }

    [Fact]
    public void F12_disable_stops_new_actions_but_preserves_unknown_outcome_reconciliation()
    {
        var machine = StableMachine();
        NetherSnapshotFingerprint fingerprint = Fingerprint(NetherSessionStatus.Play, 10);

        Assert.True(machine.TryBegin(new NetherPlannedAction(NetherActionKind.SelectFloor), fingerprint));
        machine.ObserveUnknownOutcome();
        machine.Toggle(isInNether: true);

        Assert.False(machine.IsEnabled);
        Assert.Equal(NetherAutoClimbPhase.Reconciling, machine.Phase);
        Assert.Equal(NetherActionKind.SelectFloor, machine.PendingAction!.Value.Kind);
        Assert.False(machine.TryBegin(new NetherPlannedAction(NetherActionKind.SelectFloor), fingerprint));
    }

    [Fact]
    public void F12_disable_while_native_action_is_still_in_flight_keeps_polling_before_reconcile()
    {
        var machine = StableMachine();
        NetherSnapshotFingerprint fingerprint = Fingerprint(NetherSessionStatus.Play, 10);

        Assert.True(machine.TryBegin(new NetherPlannedAction(NetherActionKind.SelectFloor), fingerprint));
        machine.Toggle(isInNether: true);

        Assert.False(machine.IsEnabled);
        Assert.Equal(NetherAutoClimbPhase.ExecutingNativeAction, machine.Phase);
        Assert.Equal(NetherActionKind.SelectFloor, machine.PendingAction!.Value.Kind);
    }

    [Fact]
    public void F12_cannot_reenable_over_a_still_in_flight_native_action()
    {
        var machine = StableMachine();
        NetherSnapshotFingerprint fingerprint = Fingerprint(NetherSessionStatus.Play, 10);

        Assert.True(machine.TryBegin(new NetherPlannedAction(NetherActionKind.SelectFloor), fingerprint));
        machine.Toggle(isInNether: true); // off
        machine.Toggle(isInNether: true); // attempted re-enable before task observation

        Assert.False(machine.IsEnabled);
        Assert.Equal(NetherAutoClimbPhase.ExecutingNativeAction, machine.Phase);
        Assert.Equal(NetherActionKind.SelectFloor, machine.PendingAction!.Value.Kind);
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
