#nullable enable

using System;

namespace AbyssMod.Services;

/// <summary>
/// Observes the exact StartQuest task created by the native battle scene.  Implementations
/// may poll already-captured task evidence only; this seam has no method that can start,
/// replay, cancel, or otherwise mutate a Nether battle request.
/// </summary>
internal interface INetherBattleIngressDriver
{
    NetherNativeActionResult PollBattleStart();

    void CancelBattleStartObservation();
}

internal enum NetherBattleIngressStepKind
{
    AwaitingStart = 0,
    Reconciling = 1,
    Entered = 2,
    WrongTarget = 3,
    BindingUnavailable = 4,
    Canceled = 5,
    Faulted = 6,
}

internal readonly record struct NetherBattleIngressStep(
    NetherBattleIngressStepKind Kind,
    NetherSnapshot? Snapshot,
    string Detail
)
{
    public static NetherBattleIngressStep Create(
        NetherBattleIngressStepKind kind,
        NetherSnapshot? snapshot = null,
        string detail = ""
    ) => new(kind, snapshot, detail);
}

/// <summary>
/// Owns the gap between a terminal OnFloorClickedEventAsync parent and the later battle-scene
/// StartQuestAsync task.  Only a successful exact StartQuest task unlocks one GET-only
/// authority refresh; the final floor/status must still satisfy the immutable SelectFloor
/// contract before the controller may create a BattleSettlement action.
/// </summary>
internal sealed class NetherBattleIngressCoordinator
{
    private readonly INetherBattleIngressDriver _battle;
    private readonly NetherReadOnlyReconcileCoordinator _reconcile;
    private NetherPlannedAction? _action;
    private NetherSnapshot? _before;
    private bool _startCompleted;

    public NetherBattleIngressCoordinator(
        INetherBattleIngressDriver battle,
        INetherReadOnlyReconcileDriver readOnly
    )
    {
        _battle = battle ?? throw new ArgumentNullException(nameof(battle));
        _reconcile = new NetherReadOnlyReconcileCoordinator(
            readOnly ?? throw new ArgumentNullException(nameof(readOnly))
        );
    }

    public bool IsActive => _action != null;

    public bool Begin(NetherPlannedAction action, NetherSnapshot before)
    {
        NetherBattleProjectionPayload? projection = action.BattleProjection;
        if (_action != null
            || before == null
            || action.Kind != NetherActionKind.SelectFloor
            || action.FloorId <= 0
            || action.ExpectedBeforeStatus != NetherSessionStatus.Play
            || action.ExpectedAfterStatus != NetherSessionStatus.Battle
            || before.Status != NetherSessionStatus.Play
            || projection == null
            || projection.MapId != before.MapId
            || projection.FloorId != action.FloorId
            || string.IsNullOrEmpty(projection.ProjectionIdentity))
        {
            return false;
        }

        _action = action;
        _before = before;
        _startCompleted = false;
        _reconcile.Reset();
        return true;
    }

    public NetherBattleIngressStep Pump()
    {
        if (_action is not NetherPlannedAction action || _before == null)
        {
            return NetherBattleIngressStep.Create(
                NetherBattleIngressStepKind.BindingUnavailable,
                detail: "missing-battle-ingress-contract"
            );
        }

        if (!_startCompleted)
        {
            NetherNativeActionResult start = _battle.PollBattleStart();
            switch (start.Kind)
            {
                case NetherNativeActionResultKind.Started:
                    return NetherBattleIngressStep.Create(
                        NetherBattleIngressStepKind.AwaitingStart,
                        detail: start.Detail
                    );
                case NetherNativeActionResultKind.Completed:
                    _startCompleted = true;
                    break;
                case NetherNativeActionResultKind.BindingUnavailable:
                    return Terminate(NetherBattleIngressStepKind.BindingUnavailable, start.Detail);
                case NetherNativeActionResultKind.UnknownOutcome:
                    return Terminate(
                        start.Detail.IndexOf("canceled", StringComparison.OrdinalIgnoreCase) >= 0
                            ? NetherBattleIngressStepKind.Canceled
                            : NetherBattleIngressStepKind.Faulted,
                        start.Detail
                    );
                default:
                    return Terminate(
                        NetherBattleIngressStepKind.Faulted,
                        "battle-start-poll:" + start.Kind + ":" + start.Detail
                    );
            }
        }

        NetherReadOnlyReconcileStep refresh = _reconcile.Pump();
        if (refresh.Kind == NetherReadOnlyReconcileStepKind.Pending)
        {
            return NetherBattleIngressStep.Create(
                NetherBattleIngressStepKind.Reconciling,
                detail: refresh.Detail
            );
        }
        if (refresh.Kind == NetherReadOnlyReconcileStepKind.BindingUnavailable)
            return Terminate(NetherBattleIngressStepKind.BindingUnavailable, refresh.Detail);
        if (refresh.Kind != NetherReadOnlyReconcileStepKind.Applied || refresh.Snapshot == null)
        {
            return Terminate(
                NetherBattleIngressStepKind.Faulted,
                "battle-ingress-refresh:" + refresh.Kind + ":" + refresh.Detail
            );
        }

        NetherSnapshot after = refresh.Snapshot;
        NetherActionOutcome outcome = NetherActionReconcilePolicy.Evaluate(action, _before, after);
        if (outcome != NetherActionOutcome.Applied
            || after.Status != NetherSessionStatus.Battle
            || after.MapId != action.BattleProjection!.MapId
            || after.CurrentFloorId != action.FloorId)
        {
            return Terminate(
                NetherBattleIngressStepKind.WrongTarget,
                "battle-ingress-target:outcome=" + outcome
                + ":status=" + after.Status
                + ":map=" + after.MapId
                + ":floor=" + after.CurrentFloorId,
                after
            );
        }

        return Terminate(
            NetherBattleIngressStepKind.Entered,
            "battle-ingress-authoritative",
            after
        );
    }

    public void Reset()
    {
        _battle.CancelBattleStartObservation();
        _action = null;
        _before = null;
        _startCompleted = false;
        _reconcile.Reset();
    }

    private NetherBattleIngressStep Terminate(
        NetherBattleIngressStepKind kind,
        string detail,
        NetherSnapshot? snapshot = null
    )
    {
        Reset();
        return NetherBattleIngressStep.Create(kind, snapshot, detail);
    }
}
