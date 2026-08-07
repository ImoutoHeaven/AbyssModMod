#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace AbyssMod.Services;

/// <summary>
/// The complete read-only runtime inputs consumed by production route planning.  Individual
/// entries remain nullable/unknown so a missing master or runtime observation cannot be
/// converted to the old permissive <c>current &lt; 100</c> / <c>HP &gt; 0</c> maps.
/// </summary>
internal sealed record NetherRuntimeRouteSafetyData
{
    public IReadOnlyDictionary<long, NetherFloorMasterBounds> FloorBoundsByFloorId { get; init; } =
        new Dictionary<long, NetherFloorMasterBounds>();
    public NetherActivePartyHpSafety ActivePartyHp { get; init; } = new(
        IsKnown: false,
        MinimumHpPermille: null,
        Detail: "missing-active-party-hp"
    );
    public NetherActiveCodeErosionProjection ActiveCodeErosion { get; init; } = new()
    {
        ErosionProjectionKnown = false,
        CodeHash = "nether-codes:unknown",
        Detail = "missing-active-code-erosion-projection",
    };
    public string Detail { get; init; } = string.Empty;
}

/// <summary>
/// One production route decision and the exact pre-mutation battle evidence created for each
/// safe combat candidate.  The Controller stores the selected payload in the pending floor
/// action before invoking native selection.
/// </summary>
internal sealed record NetherProductionRouteSafetyPlan
{
    public NetherRoutePlan Route { get; init; } = new();
    public NetherRouteSafetyContext Context { get; init; } = new();
    public IReadOnlyDictionary<long, NetherBattleProjectionPayload> BattleProjectionByFloorId { get; init; } =
        new Dictionary<long, NetherBattleProjectionPayload>();
}

/// <summary>
/// Production wiring for the safety pipeline:
/// runtime master/HP/code observations → battle projection → full route-safety context → route
/// planner.  It intentionally owns no Unity/reflection/API operation, which makes the same
/// Controller decision chain executable in characterization tests.
/// </summary>
internal sealed class NetherRouteSafetyProductionCoordinator
{
    private const int HardErosionLimit = 100;
    private readonly NetherBattleRouteProjectionBuilder _battleProjectionBuilder = new();
    private readonly NetherFloorMasterBoundsMapper _floorBoundsMapper = new();
    private readonly NetherRouteSafetyContextBuilder _contextBuilder = new();
    private readonly NetherRoutePlanner _routePlanner = new();

    public NetherProductionRouteSafetyPlan Plan(
        NetherSnapshot snapshot,
        int effectiveMaximumDepth,
        NetherAutoClimbSettings settings,
        NetherRuntimeRouteSafetyData runtime,
        NetherRuntimeInteractivePreEntryInputsResult? interactivePreEntry = null
    )
    {
        if (snapshot == null)
            throw new ArgumentNullException(nameof(snapshot));
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));
        if (runtime == null)
            throw new ArgumentNullException(nameof(runtime));

        IReadOnlyList<NetherFloorNode> serverFloors = snapshot.Floors ?? Array.Empty<NetherFloorNode>();
        HashSet<long> necessaryTerminalIds = ResolveNecessaryTerminalFloorIds(serverFloors);
        var floorInputs = new List<NetherRouteSafetyFloorInput>(serverFloors.Count);
        var safeExitKnown = new Dictionary<long, bool>();
        var payloads = new Dictionary<long, NetherBattleProjectionPayload>();

        foreach (NetherFloorNode floor in serverFloors)
        {
            if (floor == null || floor.FloorId <= 0)
                continue;

            bool necessaryTerminal = necessaryTerminalIds.Contains(floor.FloorId);
            if (!IsCombat(floor.NodeType))
            {
                if (TryBuildInteractiveSafetyInput(
                        snapshot,
                        floor,
                        settings,
                        interactivePreEntry,
                        out NetherFloorSafetyInput interactiveInput
                    ))
                {
                    floorInputs.Add(new NetherRouteSafetyFloorInput(
                        floor,
                        interactiveInput,
                        // The pre-entry proof establishes a safe native exit, but does not
                        // invent a future heal or code offer.  The popup dispatcher validates
                        // the exact option again under its owned parent task.
                        ProjectedHpDelta: 0,
                        SafeCodeOpportunity: 0
                    ));
                    safeExitKnown[floor.FloorId] = true;
                }
                else
                {
                    floorInputs.Add(new NetherRouteSafetyFloorInput(
                        floor,
                        UnknownInput(snapshot, floor, necessaryTerminal),
                        ProjectedHpDelta: null,
                        SafeCodeOpportunity: null
                    ));
                    safeExitKnown[floor.FloorId] = false;
                }
                continue;
            }

            NetherBattleRouteProjection projection = BuildCombatProjection(
                snapshot,
                floor,
                necessaryTerminal,
                settings,
                runtime
            );
            NetherFloorSafetyInput evaluationInput = projection.EvaluatorInput
                ?? UnknownInput(snapshot, floor, necessaryTerminal);
            floorInputs.Add(new NetherRouteSafetyFloorInput(
                floor,
                evaluationInput,
                // Battle HP loss is calibrated after authoritative settlement; this route gate
                // asserts only the exact pre-entry minimum HP and never invents a future heal.
                ProjectedHpDelta: projection.EvaluatorInput == null ? null : 0,
                SafeCodeOpportunity: projection.EvaluatorInput == null ? null : 0
            ));
            safeExitKnown[floor.FloorId] = projection.EvaluatorInput != null;

            if (projection.IsSafe)
            {
                payloads[floor.FloorId] = CreatePayload(
                    snapshot,
                    floor,
                    projection,
                    runtime.ActiveCodeErosion.CodeHash
                );
            }
        }

        NetherRouteSafetyContext context = _contextBuilder.Build(new NetherRouteSafetyContextBuilderInput(
            Floors: floorInputs,
            NecessaryTerminalFloorIds: necessaryTerminalIds,
            SafeExitKnownByFloorId: safeExitKnown,
            MaximumFloorLevel: effectiveMaximumDepth
        ));
        NetherRoutePlan route = _routePlanner.Plan(snapshot, context);
        return new NetherProductionRouteSafetyPlan
        {
            Route = route,
            Context = context,
            BattleProjectionByFloorId = payloads,
        };
    }

    private NetherBattleRouteProjection BuildCombatProjection(
        NetherSnapshot snapshot,
        NetherFloorNode floor,
        bool necessaryTerminal,
        NetherAutoClimbSettings settings,
        NetherRuntimeRouteSafetyData runtime
    )
    {
        NetherFloorMasterBounds bounds = default;
        bool hasBounds = runtime.FloorBoundsByFloorId != null
            && runtime.FloorBoundsByFloorId.TryGetValue(floor.FloorId, out bounds)
            && bounds.IsKnown
            && bounds.MinimumErosionPoint.HasValue
            && bounds.MaximumErosionPoint.HasValue;
        NetherActiveCodeErosionProjection codeProjection = runtime.ActiveCodeErosion
            ?? NetherActiveCodeErosionProjectionMapper.Unknown("missing-active-code-erosion-projection");
        IReadOnlyList<NetherCodeEffect> codeEffects = codeProjection.ErosionEffects
            ?? Array.Empty<NetherCodeEffect>();
        bool hasHp = runtime.ActivePartyHp.IsKnown
            && runtime.ActivePartyHp.MinimumHpPermille.HasValue;
        bool hasCode = codeProjection.ErosionProjectionKnown
            && !string.IsNullOrEmpty(codeProjection.CodeHash)
            && codeProjection.ErosionEffects != null;
        bool hasValidCurrentErosion = snapshot.ErosionPoint is >= 0 and < HardErosionLimit;
        bool allInputsKnown = hasBounds && hasHp && hasCode && hasValidCurrentErosion;

        return _battleProjectionBuilder.Build(new NetherBattleRouteProjectionInput(
            FloorId: floor.FloorId,
            FloorKind: floor.NodeType,
            MinimumErosionPoint: hasBounds ? bounds.MinimumErosionPoint : null,
            MaximumErosionPoint: hasBounds ? bounds.MaximumErosionPoint : null,
            CurrentErosion: snapshot.ErosionPoint,
            ActiveHpPermille: hasHp
                ? new[] { runtime.ActivePartyHp.MinimumHpPermille!.Value }
                : Array.Empty<int>(),
            ActiveCodeEffects: hasCode
                ? codeEffects
                : Array.Empty<NetherCodeEffect>(),
            CodeHash: hasCode ? codeProjection.CodeHash : string.Empty,
            Settings: settings,
            HardErosionLimit: HardErosionLimit
        )
        {
            HasMasterData = hasBounds,
            IsCodeHashKnown = hasCode,
            AllInputsKnown = allInputsKnown,
        });
    }

    /// <summary>
    /// Admits an interactive node only when the exact live capture has already proved all
    /// server-possible popup rows have a safe exit.  The context builder still receives a
    /// complete evaluator input so reverse terminal reachability cannot turn a missing map
    /// range, resource, or stale capture into a permissive dictionary default.
    /// </summary>
    private bool TryBuildInteractiveSafetyInput(
        NetherSnapshot snapshot,
        NetherFloorNode floor,
        NetherAutoClimbSettings settings,
        NetherRuntimeInteractivePreEntryInputsResult? interactivePreEntry,
        out NetherFloorSafetyInput safetyInput
    )
    {
        safetyInput = default;
        if (!IsInteractive(floor.NodeType)
            || interactivePreEntry == null
            || !interactivePreEntry.IsSuccess
            || interactivePreEntry.ByFloorMasterId == null
            || !interactivePreEntry.ByFloorMasterId.TryGetValue(floor.FloorId, out NetherRuntimeInteractivePreEntryCaptureResult? capture)
            || !capture.IsCaptured
            || capture.Input == null
            || !capture.Safety.IsSafe)
        {
            return false;
        }

        NetherInteractiveFloorPreEntrySafetyInput captured = capture.Input;
        if (captured.FloorMasterId != floor.FloorId
            || captured.FloorKind != floor.NodeType
            || captured.Settings == null
            || captured.Settings != settings
            || !captured.CurrentErosion.HasValue
            || captured.CurrentErosion.Value != snapshot.ErosionPoint
            || !captured.CurrentNetherGold.HasValue
            || captured.CurrentNetherGold.Value != snapshot.NetherGold
            || !captured.CurrentTreasureKeys.HasValue
            || captured.CurrentTreasureKeys.Value != snapshot.TreasureKeyCount
            || captured.ActiveHpPermille == null)
        {
            return false;
        }

        IReadOnlyList<int> expectedActiveHp = snapshot.Characters == null
            ? Array.Empty<int>()
            : snapshot.Characters
                .Where(character => character.IsActive)
                .Select(character => character.HpPermille)
                .ToArray();
        if (expectedActiveHp.Count == 0
            || !captured.ActiveHpPermille.SequenceEqual(expectedActiveHp))
        {
            return false;
        }

        NetherFloorMasterBounds bounds = _floorBoundsMapper.Map(
            captured.FloorMasterId,
            captured.MapFloorRows
        );
        if (!bounds.IsKnown
            || !bounds.MinimumErosionPoint.HasValue
            || !bounds.MaximumErosionPoint.HasValue)
        {
            return false;
        }

        safetyInput = new NetherFloorSafetyInput(
            CurrentErosion: snapshot.ErosionPoint,
            FloorMinimumErosion: bounds.MinimumErosionPoint.Value,
            FloorMaximumErosion: bounds.MaximumErosionPoint.Value,
            KnownModifierDelta: 0,
            Kind: NetherFloorSafetyKind.Optional,
            NodeType: floor.NodeType,
            CurrentHpPermille: expectedActiveHp,
            MinimumHpPermille: settings.MinimumCharacterHpPermille,
            SoftErosionLimit: settings.SoftErosionLimit,
            HardErosionLimit: HardErosionLimit,
            AllInputsKnown: true
        )
        {
            ErosionModifiers = Array.Empty<NetherErosionModifier>(),
        };
        return true;
    }

    private static NetherFloorSafetyInput UnknownInput(
        NetherSnapshot snapshot,
        NetherFloorNode floor,
        bool necessaryTerminal
    ) => new(
        CurrentErosion: snapshot.ErosionPoint,
        FloorMinimumErosion: 0,
        FloorMaximumErosion: 0,
        KnownModifierDelta: 0,
        Kind: necessaryTerminal ? NetherFloorSafetyKind.NecessaryTerminal : NetherFloorSafetyKind.Optional,
        NodeType: floor.NodeType,
        CurrentHpPermille: Array.Empty<int>(),
        MinimumHpPermille: 0,
        SoftErosionLimit: 90,
        HardErosionLimit: HardErosionLimit,
        AllInputsKnown: false
    )
    {
        ErosionModifiers = null,
    };

    private static NetherBattleProjectionPayload CreatePayload(
        NetherSnapshot snapshot,
        NetherFloorNode floor,
        NetherBattleRouteProjection projection,
        string codeHash
    )
    {
        NetherFloorSafetyInput input = projection.EvaluatorInput!.Value;
        return new NetherBattleProjectionPayload(
            MapId: snapshot.MapId,
            FloorId: floor.FloorId,
            PreBattleErosion: snapshot.ErosionPoint,
            FloorMinimumErosion: input.FloorMinimumErosion,
            FloorMaximumErosion: input.FloorMaximumErosion,
            ProjectedMinimumErosion: projection.ProjectedMinimumErosion!.Value,
            ProjectedMaximumErosion: projection.ProjectedMaximumErosion!.Value,
            CodeHash: codeHash,
            ProjectionIdentity: projection.ProjectionIdentity
        );
    }

    private static bool IsCombat(NetherFloorNodeType type) => type is
        NetherFloorNodeType.Battle or NetherFloorNodeType.MiniBoss or NetherFloorNodeType.Boss;

    private static bool IsInteractive(NetherFloorNodeType type) => type is
        NetherFloorNodeType.Event or NetherFloorNodeType.Recovery or NetherFloorNodeType.Shop or NetherFloorNodeType.Treasure;

    private static HashSet<long> ResolveNecessaryTerminalFloorIds(IReadOnlyList<NetherFloorNode> floors)
    {
        var predecessorIds = new HashSet<long>();
        foreach (NetherFloorNode floor in floors)
        {
            if (floor?.PreviousFloorIds == null)
                continue;
            foreach (long previousId in floor.PreviousFloorIds)
                predecessorIds.Add(previousId);
        }
        return floors
            .Where(floor => floor != null
                && floor.FloorId > 0
                && floor.NodeType == NetherFloorNodeType.Boss
                && !predecessorIds.Contains(floor.FloorId))
            .Select(floor => floor.FloorId)
            .ToHashSet();
    }
}
