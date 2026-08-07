#nullable enable

using System;

namespace AbyssMod.Services;

/// <summary>
/// Exact route decision handed from the production safety coordinator to Controller.PlanRoute.
/// It retains the complete context/audit and, for a selected combat node only, the immutable
/// pre-click payload that must accompany SelectFloor into the native parent flow.
/// </summary>
internal sealed record NetherAutoClimbRouteSafetyDecision
{
    public NetherRoutePlan Route { get; init; } = new();
    public NetherRouteSafetyContext Context { get; init; } = new();
    public NetherBattleProjectionPayload? SelectedBattleProjection { get; init; }
    public bool IsCombatSelectionMissingProjection { get; init; }
    /// <summary>
    /// The only SelectFloor action Controller may invoke.  A combat action is constructed here
    /// so its exact Play→Battle reconcile contract and captured projection cannot be separated.
    /// </summary>
    public NetherPlannedAction? SelectFloorAction { get; init; }
}

/// <summary>
/// The testable production seam used by <see cref="NetherAutoClimbController"/>.  It must not
/// synthesize a fallback map: all candidate eligibility comes from
/// <see cref="NetherRouteSafetyProductionCoordinator"/> and its battle/context builders.
/// </summary>
internal sealed class NetherAutoClimbRouteSafetyWiring
{
    private readonly NetherRouteSafetyProductionCoordinator _coordinator;

    public NetherAutoClimbRouteSafetyWiring(NetherRouteSafetyProductionCoordinator? coordinator = null)
    {
        _coordinator = coordinator ?? new NetherRouteSafetyProductionCoordinator();
    }

    public NetherAutoClimbRouteSafetyDecision Plan(
        NetherSnapshot snapshot,
        NetherAutoClimbSettings settings,
        int effectiveMaximumDepth,
        NetherRuntimeRouteSafetyData runtime,
        NetherRuntimeInteractivePreEntryInputsResult? interactivePreEntry = null
    )
    {
        NetherProductionRouteSafetyPlan plan = _coordinator.Plan(
            snapshot,
            effectiveMaximumDepth,
            settings,
            runtime,
            interactivePreEntry
        );
        NetherFloorNode? selected = plan.Route.SelectedNode;
        bool isCombat = selected?.NodeType is NetherFloorNodeType.Battle
            or NetherFloorNodeType.MiniBoss or NetherFloorNodeType.Boss;
        NetherBattleProjectionPayload? projection = null;
        bool missingProjection = false;
        if (isCombat)
        {
            if (!plan.BattleProjectionByFloorId.TryGetValue(selected!.FloorId, out projection))
                missingProjection = true;
        }

        NetherPlannedAction? selectFloorAction = selected == null || missingProjection
            ? null
            : new NetherPlannedAction(NetherActionKind.SelectFloor)
            {
                FloorId = selected.FloorId,
                FloorLevel = selected.FloorLevel,
                FloorIndex = selected.FloorIndex,
                ExpectedBeforeStatus = NetherSessionStatus.Play,
                // An interactive SelectFloor parent has no terminal server state until its
                // owned popup supplies an exact option/content transaction.  Keep a concrete
                // provisional value for a direct non-modal terminal, then let the composer
                // replace it before reconciliation when a modal is observed.
                ExpectedAfterStatus = isCombat ? NetherSessionStatus.Battle : NetherSessionStatus.Play,
                BattleProjection = projection,
            };

        return new NetherAutoClimbRouteSafetyDecision
        {
            Route = plan.Route,
            Context = plan.Context,
            SelectedBattleProjection = projection,
            IsCombatSelectionMissingProjection = missingProjection,
            SelectFloorAction = selectFloorAction,
        };
    }
}
