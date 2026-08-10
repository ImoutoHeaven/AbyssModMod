#nullable enable

using System;

namespace AbyssMod.Services;

/// <summary>
/// Native battle task observation is distinct from settlement authority.  Clear/close task
/// terminal evidence starts exactly one GET-only refresh; only its target-specific snapshot
/// can settle the pending battle contract.
/// </summary>
internal interface INetherBattleSettlementDriver
{
    bool IsF11Busy { get; }

    NetherNativeActionResult PollBattleLifecycle();

    bool TryConsumeBattleClear();

    bool TryConsumeBattleClose();
}

/// <summary>
/// Reads the exact live possession/master projection after the GET-only settlement refresh.
/// It has no mutation capability; unknown code semantics are therefore evidence to pause, not
/// a reason to reuse the pre-battle fingerprint.
/// </summary>
internal interface INetherBattleProjectionSnapshotDriver
{
    NetherActiveCodeErosionProjection TryCaptureActiveCodeErosionProjection();
}

internal enum NetherBattleSettlementStepKind
{
    AwaitingF11,
    AwaitingBattle,
    AwaitingSettlement,
    Settled,
    Unchanged,
    WrongTarget,
    ProjectionUnknown,
    ProjectionDrift,
    BindingUnavailable,
    Faulted,
    Canceled,
    SceneLost,
}

internal readonly record struct NetherBattleSettlementStep(
    NetherBattleSettlementStepKind Kind,
    NetherActionOutcome Outcome,
    NetherSnapshot? Snapshot,
    string Detail,
    NetherPauseReason PauseReason = NetherPauseReason.None
)
{
    public static NetherBattleSettlementStep Create(
        NetherBattleSettlementStepKind kind,
        NetherActionOutcome outcome = NetherActionOutcome.Ambiguous,
        NetherSnapshot? snapshot = null,
        string detail = "",
        NetherPauseReason pauseReason = NetherPauseReason.None
    ) => new(kind, outcome, snapshot, detail, pauseReason);
}

internal sealed class NetherBattleSettlementCoordinator
{
    private readonly INetherBattleSettlementDriver _battle;
    private readonly NetherReadOnlyReconcileCoordinator _reconcile;
    private readonly INetherBattleProjectionSnapshotDriver _projectionSnapshot;
    private readonly NetherBattleProjectionCalibration _projectionCalibration = new();
    private NetherPlannedAction? _action;
    private NetherSnapshot? _before;
    private bool _settlementObserved;

    public NetherBattleSettlementCoordinator(
        INetherBattleSettlementDriver battle,
        INetherReadOnlyReconcileDriver readOnly,
        INetherBattleProjectionSnapshotDriver projectionSnapshot
    )
    {
        _battle = battle ?? throw new ArgumentNullException(nameof(battle));
        _reconcile = new NetherReadOnlyReconcileCoordinator(readOnly ?? throw new ArgumentNullException(nameof(readOnly)));
        _projectionSnapshot = projectionSnapshot ?? throw new ArgumentNullException(nameof(projectionSnapshot));
    }

    public bool IsActive => _action != null;

    public bool Begin(NetherPlannedAction action, NetherSnapshot before)
    {
        if (_action != null || before == null || action.Kind != NetherActionKind.BattleSettlement)
            return false;
        NetherBattleSettlementContract? contract = action.BattleSettlement;
        if (contract == null
            || contract.EntryStatus != NetherSessionStatus.Battle
            || before.Status != contract.EntryStatus
            || before.MapId != contract.EntryMapId
            || before.CurrentFloorId != contract.EntryFloorId
            || contract.ExpectedStatus == NetherSessionStatus.Unknown
            || contract.ExpectedMapId <= 0
            || contract.ExpectedFloorId <= 0
            || contract.EntryProjection == null)
        {
            return false;
        }

        _action = action;
        _before = before;
        _settlementObserved = false;
        _reconcile.Reset();
        return true;
    }

    public NetherBattleSettlementStep Pump()
    {
        if (_action is not NetherPlannedAction action || _before == null)
            return NetherBattleSettlementStep.Create(NetherBattleSettlementStepKind.BindingUnavailable, detail: "missing-battle-settlement-contract");

        if (_settlementObserved)
            return PumpSettlement(action, _before);

        if (_battle.IsF11Busy)
            return NetherBattleSettlementStep.Create(NetherBattleSettlementStepKind.AwaitingF11, detail: "f11-nether-battle-busy");

        NetherNativeActionResult lifecycle = _battle.PollBattleLifecycle();
        if (lifecycle.Kind == NetherNativeActionResultKind.Started)
            return NetherBattleSettlementStep.Create(NetherBattleSettlementStepKind.AwaitingBattle, detail: lifecycle.Detail);
        if (lifecycle.Kind == NetherNativeActionResultKind.BindingUnavailable)
            return Terminate(NetherBattleSettlementStepKind.BindingUnavailable, detail: lifecycle.Detail);
        if (lifecycle.Kind == NetherNativeActionResultKind.UnknownOutcome)
        {
            return lifecycle.Detail.IndexOf("canceled", StringComparison.OrdinalIgnoreCase) >= 0
                ? Terminate(NetherBattleSettlementStepKind.Canceled, detail: lifecycle.Detail)
                : Terminate(NetherBattleSettlementStepKind.Faulted, detail: lifecycle.Detail);
        }
        if (lifecycle.Kind != NetherNativeActionResultKind.Completed)
            return Terminate(NetherBattleSettlementStepKind.Faulted, detail: lifecycle.Detail);

        if (!_battle.TryConsumeBattleClear() && !_battle.TryConsumeBattleClose())
            return NetherBattleSettlementStep.Create(NetherBattleSettlementStepKind.AwaitingBattle, detail: "battle-parent-not-settled");

        _settlementObserved = true;
        return NetherBattleSettlementStep.Create(NetherBattleSettlementStepKind.AwaitingSettlement, detail: "battle-parent-terminal-observed");
    }

    public NetherBattleSettlementStep TerminateForSceneLoss() =>
        Terminate(NetherBattleSettlementStepKind.SceneLost, detail: "nether-battle-scene-lost");

    private NetherBattleSettlementStep PumpSettlement(NetherPlannedAction action, NetherSnapshot before)
    {
        NetherReadOnlyReconcileStep refresh = _reconcile.Pump();
        if (refresh.Kind == NetherReadOnlyReconcileStepKind.Pending)
            return NetherBattleSettlementStep.Create(NetherBattleSettlementStepKind.AwaitingSettlement, detail: refresh.Detail);
        if (refresh.Kind == NetherReadOnlyReconcileStepKind.BindingUnavailable)
            return Terminate(NetherBattleSettlementStepKind.BindingUnavailable, detail: refresh.Detail);
        if (refresh.Kind != NetherReadOnlyReconcileStepKind.Applied || refresh.Snapshot == null)
            return Terminate(NetherBattleSettlementStepKind.Faulted, detail: refresh.Detail);

        NetherActionOutcome outcome = NetherActionReconcilePolicy.Evaluate(action, before, refresh.Snapshot);
        return outcome switch
        {
            NetherActionOutcome.Applied => SettleAuthoritativeProjection(action, before, refresh.Snapshot, outcome),
            NetherActionOutcome.NotApplied => Terminate(NetherBattleSettlementStepKind.Unchanged, outcome, refresh.Snapshot),
            _ => Terminate(NetherBattleSettlementStepKind.WrongTarget, outcome, refresh.Snapshot),
        };
    }

    private NetherBattleSettlementStep SettleAuthoritativeProjection(
        NetherPlannedAction action,
        NetherSnapshot before,
        NetherSnapshot after,
        NetherActionOutcome outcome
    )
    {
        NetherBattleProjectionCalibrationObservation calibration = _projectionCalibration.Observe(
            action.BattleSettlement,
            before,
            after,
            _projectionSnapshot.TryCaptureActiveCodeErosionProjection()
        );
        if (calibration.IsAccepted)
        {
            return Terminate(
                NetherBattleSettlementStepKind.Settled,
                outcome,
                after,
                calibration.Detail
            );
        }

        NetherBattleSettlementStepKind kind = calibration.PauseReason == NetherPauseReason.BattleProjectionDrift
            ? NetherBattleSettlementStepKind.ProjectionDrift
            : NetherBattleSettlementStepKind.ProjectionUnknown;
        return Terminate(kind, outcome, after, calibration.Detail, calibration.PauseReason);
    }

    private NetherBattleSettlementStep Terminate(
        NetherBattleSettlementStepKind kind,
        NetherActionOutcome outcome = NetherActionOutcome.Ambiguous,
        NetherSnapshot? snapshot = null,
        string detail = "",
        NetherPauseReason pauseReason = NetherPauseReason.None
    )
    {
        _action = null;
        _before = null;
        _settlementObserved = false;
        _reconcile.Reset();
        return NetherBattleSettlementStep.Create(kind, outcome, snapshot, detail, pauseReason);
    }
}
