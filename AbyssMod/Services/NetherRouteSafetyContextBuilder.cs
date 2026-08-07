#nullable enable

using System;
using System.Collections.Generic;

namespace AbyssMod.Services;

/// <summary>
/// Joins one server-rendered graph node with the complete, already-authoritative inputs needed
/// to evaluate it.  Nullable score/delta fields mean unavailable source data; they are never
/// converted to a benign zero by the builder.
/// </summary>
internal sealed record NetherRouteSafetyFloorInput(
    NetherFloorNode ServerNode,
    NetherFloorSafetyInput EvaluationInput,
    int? ProjectedHpDelta,
    int? SafeCodeOpportunity
);

/// <summary>
/// Immutable server/master snapshot for one route decision.  The terminal set is deliberately
/// explicit: a Boss-shaped node that is not the server's necessary terminal receives no soft
/// limit exemption.
/// </summary>
internal sealed record NetherRouteSafetyContextBuilderInput(
    IReadOnlyList<NetherRouteSafetyFloorInput> Floors,
    IReadOnlySet<long> NecessaryTerminalFloorIds,
    IReadOnlyDictionary<long, bool> SafeExitKnownByFloorId,
    int MaximumFloorLevel
);

/// <summary>
/// Builds the complete fail-closed maps consumed by <see cref="NetherRoutePlanner"/>.  Values
/// are inserted for every unique server floor before traversal; absent source data uses explicit
/// unsafe sentinels instead of relying on a dictionary lookup fallback.
/// </summary>
internal sealed class NetherRouteSafetyContextBuilder
{
    private const int UnknownErosion = int.MaxValue;
    private const int UnknownScalar = int.MinValue;

    public NetherRouteSafetyContext Build(NetherRouteSafetyContextBuilderInput input)
    {
        if (input == null)
            throw new ArgumentNullException(nameof(input));

        IReadOnlyList<NetherRouteSafetyFloorInput> floors = input.Floors ?? Array.Empty<NetherRouteSafetyFloorInput>();
        var nodes = new Dictionary<long, NetherRouteSafetyFloorInput>();
        var duplicateOrInvalidIds = new HashSet<long>();
        foreach (NetherRouteSafetyFloorInput floor in floors)
        {
            if (floor == null || floor.ServerNode == null || floor.ServerNode.FloorId <= 0)
            {
                if (floor?.ServerNode != null)
                    duplicateOrInvalidIds.Add(floor.ServerNode.FloorId);
                continue;
            }
            if (!nodes.TryAdd(floor.ServerNode.FloorId, floor))
                duplicateOrInvalidIds.Add(floor.ServerNode.FloorId);
        }

        var context = new MutableContext(input.MaximumFloorLevel);
        foreach ((long floorId, NetherRouteSafetyFloorInput floor) in nodes)
            context.AddUnknown(floorId);

        if (nodes.Count == 0)
            return context.ToImmutable();

        IReadOnlyDictionary<long, bool>? safeExits = input.SafeExitKnownByFloorId;
        IReadOnlySet<long>? requestedTerminals = input.NecessaryTerminalFloorIds;
        var graphInvalid = new HashSet<long>(duplicateOrInvalidIds);
        var successors = CreateSuccessorIndex(nodes, graphInvalid);
        HashSet<long> terminals = ResolveTerminals(nodes, requestedTerminals, out bool terminalDefinitionValid);
        HashSet<long> cyclic = FindCycleNodes(nodes.Keys, successors);

        var states = new Dictionary<long, FloorState>();
        foreach ((long floorId, NetherRouteSafetyFloorInput floor) in nodes)
        {
            bool exitKnown = safeExits != null
                && safeExits.TryGetValue(floorId, out bool hasSafeExit)
                && hasSafeExit;
            bool terminalKindMatches = terminals.Contains(floorId)
                ? floor.EvaluationInput.Kind == NetherFloorSafetyKind.NecessaryTerminal
                    && floor.ServerNode.NodeType == NetherFloorNodeType.Boss
                : floor.EvaluationInput.Kind == NetherFloorSafetyKind.Optional;
            NetherFloorSafetyEvaluation evaluation = new NetherFloorSafetyEvaluator().Evaluate(floor.EvaluationInput);
            bool hasProjectedMetadata = floor.ProjectedHpDelta.HasValue && floor.SafeCodeOpportunity.HasValue;
            bool evaluationKnown = evaluation.PauseReason is not NetherPauseReason.UnknownMasterData
                and not NetherPauseReason.UnknownEffect
                and not NetherPauseReason.InvalidConfiguration;
            bool locallyKnown = exitKnown
                && terminalKindMatches
                && hasProjectedMetadata
                && evaluationKnown
                && !graphInvalid.Contains(floorId)
                && !duplicateOrInvalidIds.Contains(floorId);

            int projectedErosion = TryGetProjectedDelta(floor.EvaluationInput.CurrentErosion, evaluation);
            if (!locallyKnown)
            {
                context.SetUnsafe(floorId);
                states[floorId] = new FloorState(false, false, UnknownErosion);
                continue;
            }

            bool hpSafe = IsHpSafe(floor.EvaluationInput, floor.ProjectedHpDelta);
            context.SetKnown(
                floorId,
                hpSafe,
                projectedErosion,
                floor.ProjectedHpDelta!.Value,
                floor.SafeCodeOpportunity!.Value
            );
            states[floorId] = new FloorState(
                IsKnown: true,
                // HP is an entry gate, not permission to erase an otherwise known terminal
                // path. A proved Recovery may be selected, reconciled, and then followed by a
                // fresh Boss decision; the Boss itself remains HP-ineligible at this snapshot.
                // The evaluator deliberately reports that Boss as UnsafeHp.  It still has a
                // known erosion cost for reverse reachability, otherwise a proven Recovery
                // would be rejected merely because its eventual Boss is not enterable *yet*.
                // Only that narrowly-scoped combat/HP case may contribute a terminal path;
                // every other evaluator pause remains terminal-unsafe.
                IsEligibleForTerminalPath: (evaluation.IsSafe
                        || (evaluation.PauseReason == NetherPauseReason.UnsafeHp
                            && IsCombat(floor.EvaluationInput.NodeType)))
                    && projectedErosion != UnknownErosion,
                ProjectedErosionDelta: projectedErosion
            );
        }

        if (!terminalDefinitionValid)
        {
            foreach (long floorId in nodes.Keys)
                context.SetHardUnsafe(floorId);
            return context.ToImmutable();
        }

        var minimumCosts = new Dictionary<long, int>();
        var visiting = new HashSet<long>();
        foreach (long floorId in nodes.Keys)
        {
            if (TryGetMinimumWorstCaseCost(
                floorId,
                states,
                terminals,
                successors,
                cyclic,
                minimumCosts,
                visiting,
                out int cost
            ))
            {
                context.SetTerminalCost(floorId, cost);
            }
            else
            {
                context.SetHardUnsafe(floorId);
            }
        }

        return context.ToImmutable();
    }

    private static Dictionary<long, List<long>> CreateSuccessorIndex(
        IReadOnlyDictionary<long, NetherRouteSafetyFloorInput> nodes,
        ISet<long> graphInvalid
    )
    {
        var successors = new Dictionary<long, List<long>>();
        foreach (long floorId in nodes.Keys)
            successors[floorId] = new List<long>();

        foreach ((long floorId, NetherRouteSafetyFloorInput floor) in nodes)
        {
            IReadOnlyList<long>? previousIds = floor.ServerNode.PreviousFloorIds;
            if (previousIds == null)
            {
                graphInvalid.Add(floorId);
                continue;
            }
            foreach (long previousId in previousIds)
            {
                if (!nodes.ContainsKey(previousId))
                {
                    graphInvalid.Add(floorId);
                    continue;
                }
                successors[previousId].Add(floorId);
            }
        }
        return successors;
    }

    private static HashSet<long> ResolveTerminals(
        IReadOnlyDictionary<long, NetherRouteSafetyFloorInput> nodes,
        IReadOnlySet<long>? requestedTerminals,
        out bool valid
    )
    {
        valid = requestedTerminals != null && requestedTerminals.Count > 0;
        var terminals = new HashSet<long>();
        if (requestedTerminals == null)
            return terminals;

        foreach (long terminalId in requestedTerminals)
        {
            if (!nodes.TryGetValue(terminalId, out NetherRouteSafetyFloorInput? terminal)
                || terminal.ServerNode.NodeType != NetherFloorNodeType.Boss
                || terminal.EvaluationInput.Kind != NetherFloorSafetyKind.NecessaryTerminal)
            {
                valid = false;
                continue;
            }
            terminals.Add(terminalId);
        }
        return terminals;
    }

    private static HashSet<long> FindCycleNodes(
        IEnumerable<long> floorIds,
        IReadOnlyDictionary<long, List<long>> successors
    )
    {
        var colors = new Dictionary<long, int>();
        var stack = new List<long>();
        var stackPositions = new Dictionary<long, int>();
        var cyclic = new HashSet<long>();

        foreach (long floorId in floorIds)
        {
            if (!colors.ContainsKey(floorId))
                Visit(floorId);
        }
        return cyclic;

        void Visit(long floorId)
        {
            colors[floorId] = 1;
            stackPositions[floorId] = stack.Count;
            stack.Add(floorId);
            foreach (long nextId in successors[floorId])
            {
                if (!colors.TryGetValue(nextId, out int color))
                {
                    Visit(nextId);
                    continue;
                }
                if (color != 1 || !stackPositions.TryGetValue(nextId, out int cycleStart))
                    continue;
                for (int index = cycleStart; index < stack.Count; index++)
                    cyclic.Add(stack[index]);
            }
            stack.RemoveAt(stack.Count - 1);
            stackPositions.Remove(floorId);
            colors[floorId] = 2;
        }
    }

    private static bool TryGetMinimumWorstCaseCost(
        long floorId,
        IReadOnlyDictionary<long, FloorState> states,
        IReadOnlySet<long> terminals,
        IReadOnlyDictionary<long, List<long>> successors,
        IReadOnlySet<long> cyclic,
        IDictionary<long, int> costs,
        ISet<long> visiting,
        out int cost
    )
    {
        if (costs.TryGetValue(floorId, out cost))
            return true;
        if (cyclic.Contains(floorId)
            || !states.TryGetValue(floorId, out FloorState state)
            || !state.IsKnown
            || !state.IsEligibleForTerminalPath
            || !visiting.Add(floorId))
        {
            cost = UnknownErosion;
            return false;
        }

        try
        {
            if (terminals.Contains(floorId))
            {
                cost = state.ProjectedErosionDelta;
                costs[floorId] = cost;
                return true;
            }

            bool foundTerminalPath = false;
            int smallestSuccessorCost = UnknownErosion;
            foreach (long nextId in successors[floorId])
            {
                if (!TryGetMinimumWorstCaseCost(
                    nextId,
                    states,
                    terminals,
                    successors,
                    cyclic,
                    costs,
                    visiting,
                    out int successorCost
                ))
                    continue;
                if (!foundTerminalPath || successorCost < smallestSuccessorCost)
                {
                    foundTerminalPath = true;
                    smallestSuccessorCost = successorCost;
                }
            }
            if (!foundTerminalPath)
            {
                cost = UnknownErosion;
                return false;
            }

            cost = checked(state.ProjectedErosionDelta + smallestSuccessorCost);
            costs[floorId] = cost;
            return true;
        }
        catch (OverflowException)
        {
            cost = UnknownErosion;
            return false;
        }
        finally
        {
            visiting.Remove(floorId);
        }
    }

    private static int TryGetProjectedDelta(int currentErosion, NetherFloorSafetyEvaluation evaluation)
    {
        if (!evaluation.ProjectedMaximumErosion.HasValue)
            return UnknownErosion;
        try
        {
            return checked(evaluation.ProjectedMaximumErosion.Value - currentErosion);
        }
        catch (OverflowException)
        {
            return UnknownErosion;
        }
    }

    private static bool IsHpSafe(NetherFloorSafetyInput input, int? projectedHpDelta)
    {
        if (!input.AllInputsKnown
            || input.CurrentHpPermille == null
            || input.CurrentHpPermille.Count == 0
            || input.MinimumHpPermille is < 0 or > 1000)
            return false;
        foreach (int hpPermille in input.CurrentHpPermille)
        {
            if (hpPermille is < 0 or > 1000)
                return false;
            if (input.NodeType is NetherFloorNodeType.Battle
                or NetherFloorNodeType.MiniBoss
                or NetherFloorNodeType.Boss)
            {
                if (hpPermille < input.MinimumHpPermille)
                    return false;
                continue;
            }
            if (hpPermille < input.MinimumHpPermille)
            {
                if (!projectedHpDelta.HasValue)
                    return false;
                try
                {
                    if (checked(hpPermille + projectedHpDelta.Value) < input.MinimumHpPermille)
                        return false;
                }
                catch (OverflowException)
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static bool IsCombat(NetherFloorNodeType nodeType) => nodeType is
        NetherFloorNodeType.Battle
        or NetherFloorNodeType.MiniBoss
        or NetherFloorNodeType.Boss;

    private readonly record struct FloorState(
        bool IsKnown,
        bool IsEligibleForTerminalPath,
        int ProjectedErosionDelta
    );

    private sealed class MutableContext
    {
        private readonly Dictionary<long, int> _minimumWorstCase = new();
        private readonly Dictionary<long, bool> _hpSafe = new();
        private readonly Dictionary<long, bool> _known = new();
        private readonly Dictionary<long, bool> _hardSafe = new();
        private readonly Dictionary<long, int> _safeCodeOpportunity = new();
        private readonly Dictionary<long, int> _projectedErosion = new();
        private readonly Dictionary<long, int> _projectedHp = new();
        private readonly int _maximumFloorLevel;

        public MutableContext(int maximumFloorLevel) => _maximumFloorLevel = maximumFloorLevel;

        public void AddUnknown(long floorId)
        {
            _minimumWorstCase[floorId] = UnknownErosion;
            _hpSafe[floorId] = false;
            _known[floorId] = false;
            _hardSafe[floorId] = false;
            _safeCodeOpportunity[floorId] = UnknownScalar;
            _projectedErosion[floorId] = UnknownErosion;
            _projectedHp[floorId] = UnknownScalar;
        }

        public void SetUnsafe(long floorId) => AddUnknown(floorId);

        public void SetKnown(
            long floorId,
            bool hpSafe,
            int projectedErosion,
            int projectedHp,
            int safeCodeOpportunity
        )
        {
            _hpSafe[floorId] = hpSafe;
            _known[floorId] = true;
            _hardSafe[floorId] = false;
            _projectedErosion[floorId] = projectedErosion;
            _projectedHp[floorId] = projectedHp;
            _safeCodeOpportunity[floorId] = safeCodeOpportunity;
        }

        public void SetTerminalCost(long floorId, int cost)
        {
            _minimumWorstCase[floorId] = cost;
            _hardSafe[floorId] = true;
        }

        public void SetHardUnsafe(long floorId)
        {
            _minimumWorstCase[floorId] = UnknownErosion;
            _hardSafe[floorId] = false;
        }

        public NetherRouteSafetyContext ToImmutable() => new()
        {
            MaximumFloorLevel = _maximumFloorLevel,
            MinimumWorstCaseErosionToTerminal = _minimumWorstCase,
            HpSafeByFloorId = _hpSafe,
            KnownNodeByFloorId = _known,
            HardSafeByFloorId = _hardSafe,
            SafeCodeOpportunityByFloorId = _safeCodeOpportunity,
            ProjectedErosionDeltaByFloorId = _projectedErosion,
            ProjectedHpDeltaByFloorId = _projectedHp,
        };
    }
}
