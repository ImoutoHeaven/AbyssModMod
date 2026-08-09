#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace AbyssMod.Services;

internal sealed record NetherRouteSafetyContext
{
    /// <summary>Effective target resolved from configuration, server, and master limits.</summary>
    public int MaximumFloorLevel { get; init; } = int.MaxValue;
    public IReadOnlyDictionary<long, int> MinimumWorstCaseErosionToTerminal { get; init; } = new Dictionary<long, int>();
    public IReadOnlyDictionary<long, bool> HpSafeByFloorId { get; init; } = new Dictionary<long, bool>();
    public IReadOnlyDictionary<long, bool> KnownNodeByFloorId { get; init; } = new Dictionary<long, bool>();
    public IReadOnlyDictionary<long, bool> HardSafeByFloorId { get; init; } = new Dictionary<long, bool>();
    public IReadOnlyDictionary<long, int> SafeCodeOpportunityByFloorId { get; init; } = new Dictionary<long, int>();
    public IReadOnlyDictionary<long, int> ProjectedErosionDeltaByFloorId { get; init; } = new Dictionary<long, int>();
    public IReadOnlyDictionary<long, int> ProjectedHpDeltaByFloorId { get; init; } = new Dictionary<long, int>();
    public IReadOnlyDictionary<long, string> UnknownDetailByFloorId { get; init; } = new Dictionary<long, string>();

    public bool IsHpSafe(long floorId) => !HpSafeByFloorId.TryGetValue(floorId, out bool safe) || safe;
    public bool IsKnown(long floorId) => !KnownNodeByFloorId.TryGetValue(floorId, out bool known) || known;
    public bool IsHardSafe(long floorId) => !HardSafeByFloorId.TryGetValue(floorId, out bool safe) || safe;
    public int MinimumWorstCaseErosion(long floorId) => MinimumWorstCaseErosionToTerminal.TryGetValue(floorId, out int value) ? value : 0;
    public int SafeCodeOpportunity(long floorId) => SafeCodeOpportunityByFloorId.TryGetValue(floorId, out int value) ? value : 0;
    public int ProjectedErosionDelta(long floorId) => ProjectedErosionDeltaByFloorId.TryGetValue(floorId, out int value) ? value : 0;
    public int ProjectedHpDelta(long floorId) => ProjectedHpDeltaByFloorId.TryGetValue(floorId, out int value) ? value : 0;
    public string UnknownDetail(long floorId) => UnknownDetailByFloorId.TryGetValue(floorId, out string? value)
        ? value
        : "missing-context-entry";

    public string DiagnosticDetail(long floorId)
    {
        if (!KnownNodeByFloorId.TryGetValue(floorId, out bool known))
            return "missing-context-entry";
        if (!known)
            return UnknownDetail(floorId);
        if (!HardSafeByFloorId.TryGetValue(floorId, out bool hardSafe))
            return "missing-hard-safety-entry";
        return hardSafe ? "known-terminal-path" : "known-no-terminal-path";
    }
}

internal readonly record struct NetherRouteCandidateAudit(long FloorId, string Reason)
{
    public string Detail { get; init; } = string.Empty;
}

internal sealed record NetherRoutePlan
{
    public NetherFloorNode? SelectedNode { get; init; }
    public NetherPauseReason PauseReason { get; init; }
    public string PauseDetail { get; init; } = string.Empty;
    public IReadOnlyList<NetherRouteCandidateAudit> Audit { get; init; } = Array.Empty<NetherRouteCandidateAudit>();
    public bool HasSelection => SelectedNode != null;
}

internal sealed class NetherRoutePlanner
{
    public NetherRoutePlan Plan(NetherSnapshot snapshot, NetherRouteSafetyContext context)
    {
        if (snapshot == null)
            throw new ArgumentNullException(nameof(snapshot));
        if (context == null)
            throw new ArgumentNullException(nameof(context));

        var audit = new List<NetherRouteCandidateAudit>();
        if (!TryCreateNodeIndex(snapshot.Floors, out Dictionary<long, NetherFloorNode>? nodes, out string indexError))
            return Pause(NetherPauseReason.InvalidGraph, indexError, audit);
        long currentNodeId = snapshot.CurrentNodeId > 0 ? snapshot.CurrentNodeId : snapshot.CurrentFloorId;
        if (!nodes.TryGetValue(currentNodeId, out NetherFloorNode? current))
            return Pause(NetherPauseReason.InvalidGraph, "missing-current-floor", audit);
        if (!HasOnlyKnownPredecessors(nodes, out string predecessorError))
            return Pause(NetherPauseReason.InvalidGraph, predecessorError, audit);

        HashSet<long> terminalReachable = FindTerminalReachable(nodes);
        if (terminalReachable.Count == 0)
            return Pause(NetherPauseReason.InvalidGraph, "missing-segment-terminal", audit);

        List<NetherFloorNode> candidates = nodes.Values
            .Where(node => node.NodeId != current.NodeId && node.PreviousFloorIds.Contains(current.NodeId))
            .ToList();
        if (candidates.Count == 0)
            return Pause(NetherPauseReason.NoSafeRoute, "no-current-frontier", audit);

        foreach (NetherFloorNode candidate in candidates)
        {
            if (candidate.NodeType is NetherFloorNodeType.Unknown or NetherFloorNodeType.Default)
            {
                audit.Add(new NetherRouteCandidateAudit(candidate.NodeId, "unknown-floor"));
                return Pause(NetherPauseReason.UnknownFloor, "unknown-frontier-floor", audit);
            }
        }

        var safeCandidates = new List<Candidate>();
        foreach (NetherFloorNode candidate in candidates)
        {
            if (candidate.FloorLevel > context.MaximumFloorLevel)
            {
                audit.Add(new NetherRouteCandidateAudit(candidate.NodeId, "above-target-depth"));
                continue;
            }
            if (!candidate.IsUnlocked)
            {
                audit.Add(new NetherRouteCandidateAudit(candidate.NodeId, "locked"));
                continue;
            }
            if (!terminalReachable.Contains(candidate.NodeId))
            {
                audit.Add(new NetherRouteCandidateAudit(candidate.NodeId, "dead-end"));
                continue;
            }
            if (!context.IsKnown(candidate.NodeId))
            {
                audit.Add(new NetherRouteCandidateAudit(candidate.NodeId, "unknown-node")
                {
                    Detail = context.UnknownDetail(candidate.NodeId),
                });
                continue;
            }
            if (!context.IsHardSafe(candidate.NodeId))
            {
                audit.Add(new NetherRouteCandidateAudit(candidate.NodeId, "unsafe"));
                continue;
            }
            if (!context.IsHpSafe(candidate.NodeId))
            {
                audit.Add(new NetherRouteCandidateAudit(candidate.NodeId, "unsafe-hp"));
                continue;
            }
            if (!IsBelowHardErosionLimit(snapshot.ErosionPoint, context.MinimumWorstCaseErosion(candidate.NodeId)))
            {
                audit.Add(new NetherRouteCandidateAudit(candidate.NodeId, "terminal-erosion-100"));
                continue;
            }

            safeCandidates.Add(new Candidate(
                candidate,
                true,
                true,
                context.ProjectedErosionDelta(candidate.NodeId),
                context.ProjectedHpDelta(candidate.NodeId),
                context.SafeCodeOpportunity(candidate.NodeId)
            ));
        }

        if (safeCandidates.Count == 0)
        {
            NetherPauseReason reason = audit.Any(item => item.Reason == "terminal-erosion-100")
                ? NetherPauseReason.UnsafeErosion
                : audit.Any(item => item.Reason == "unsafe-hp")
                    ? NetherPauseReason.UnsafeHp
                : audit.Any(item => item.Reason == "unknown-node")
                    ? NetherPauseReason.UnknownMasterData
                    : audit.Any(item => item.Reason == "above-target-depth")
                        ? NetherPauseReason.TargetReachedOutsideCheckpoint
                    : NetherPauseReason.NoSafeRoute;
            return Pause(reason, "no-safe-frontier", audit);
        }

        Candidate selected = safeCandidates
            .OrderByDescending(candidate => candidate.HardSafe)
            .ThenByDescending(candidate => candidate.TerminalReachable)
            .ThenBy(candidate => candidate.ProjectedErosionDelta)
            .ThenByDescending(candidate => candidate.ProjectedHpDelta)
            .ThenByDescending(candidate => candidate.SafeCodeOpportunity)
            .ThenByDescending(candidate => candidate.Node.RewardTier)
            .ThenBy(candidate => candidate.Node.OptionalCombatCount)
            .ThenBy(candidate => candidate.Node.FloorIndex)
            .ThenBy(candidate => candidate.Node.FloorId)
            .ThenBy(candidate => candidate.Node.NodeId)
            .First();

        audit.Add(new NetherRouteCandidateAudit(selected.Node.NodeId, "selected"));
        return new NetherRoutePlan { SelectedNode = selected.Node, Audit = audit };
    }

    private static bool TryCreateNodeIndex(
        IReadOnlyList<NetherFloorNode> floors,
        out Dictionary<long, NetherFloorNode> nodes,
        out string error
    )
    {
        nodes = new Dictionary<long, NetherFloorNode>();
        error = string.Empty;
        if (floors.Count == 0)
        {
            error = "empty-floor-graph";
            return false;
        }

        foreach (NetherFloorNode node in floors)
        {
            if (node.FloorId <= 0 || node.NodeId <= 0 || !nodes.TryAdd(node.NodeId, node))
            {
                error = "duplicate-or-invalid-floor-id";
                return false;
            }
        }
        return true;
    }

    private static bool HasOnlyKnownPredecessors(
        IReadOnlyDictionary<long, NetherFloorNode> nodes,
        out string error
    )
    {
        foreach (NetherFloorNode node in nodes.Values)
        {
            foreach (long previousId in node.PreviousFloorIds)
            {
                if (!nodes.ContainsKey(previousId))
                {
                    error = $"missing-prev-floor:{node.FloorId}:{previousId}";
                    return false;
                }
            }
        }
        error = string.Empty;
        return true;
    }

    private static HashSet<long> FindTerminalReachable(IReadOnlyDictionary<long, NetherFloorNode> nodes)
    {
        var reachable = new HashSet<long>();
        var pending = new Stack<long>();
        foreach (NetherFloorNode terminal in nodes.Values.Where(node => node.NodeType == NetherFloorNodeType.Boss))
        {
            if (reachable.Add(terminal.NodeId))
                pending.Push(terminal.NodeId);
        }

        while (pending.Count > 0)
        {
            long floorId = pending.Pop();
            foreach (long previousId in nodes[floorId].PreviousFloorIds)
            {
                if (reachable.Add(previousId))
                    pending.Push(previousId);
            }
        }
        return reachable;
    }

    private static bool IsBelowHardErosionLimit(int current, int worstCaseDelta)
    {
        try
        {
            return checked(current + worstCaseDelta) < 100;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static NetherRoutePlan Pause(
        NetherPauseReason reason,
        string detail,
        IReadOnlyList<NetherRouteCandidateAudit> audit
    ) => new()
    {
        PauseReason = reason,
        PauseDetail = detail,
        Audit = audit,
    };

    private readonly record struct Candidate(
        NetherFloorNode Node,
        bool HardSafe,
        bool TerminalReachable,
        int ProjectedErosionDelta,
        int ProjectedHpDelta,
        int SafeCodeOpportunity
    );
}
