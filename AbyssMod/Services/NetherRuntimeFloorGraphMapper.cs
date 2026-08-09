#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AbyssMod.Services;

/// <summary>Exact reflection-free copy of one packaged NetherFloorModel.</summary>
internal sealed record NetherRuntimeFloorRaw(
    long MasterFloorId,
    int FloorLevel,
    int FloorIndex,
    int ApiFloorIndex,
    NetherFloorNodeType NodeType
)
{
    public bool IsHidden { get; init; }
    public bool IsUnlocked { get; init; }
    public IReadOnlyList<long> PreviousMasterFloorIds { get; init; } = Array.Empty<long>();
    public int RewardTier { get; init; }
    public int OptionalCombatCount { get; init; }
}

/// <summary>
/// Projects reusable master IDs into unique runtime nodes and resolves native previous-master
/// links only against the immediately preceding level, matching NetherMapModel's own graph
/// construction.
/// </summary>
internal static class NetherRuntimeFloorGraphMapper
{
    public static bool TryMap(
        IReadOnlyList<NetherRuntimeFloorRaw>? rawFloors,
        out IReadOnlyList<NetherFloorNode> nodes,
        out string error
    )
    {
        nodes = Array.Empty<NetherFloorNode>();
        if (rawFloors == null || rawFloors.Count == 0)
        {
            error = "empty-map-floor-model-list";
            return false;
        }

        var acceptedNodeIds = new HashSet<long>();
        var identities = new Dictionary<NetherRuntimeFloorRaw, long>();
        foreach (NetherRuntimeFloorRaw? raw in rawFloors)
        {
            if (raw == null)
            {
                error = "null-runtime-floor-model";
                return false;
            }
            if (!NetherRuntimeFloorModelValidator.TryCreateNodeId(
                    raw.MasterFloorId,
                    raw.FloorLevel,
                    raw.FloorIndex,
                    raw.ApiFloorIndex,
                    acceptedNodeIds,
                    out long nodeId,
                    out error
                ))
            {
                return false;
            }
            identities.Add(raw, nodeId);
        }

        var mapped = new List<NetherFloorNode>(rawFloors.Count);
        foreach (NetherRuntimeFloorRaw raw in rawFloors)
        {
            var previousNodeIds = new List<long>();
            foreach (long previousMasterId in raw.PreviousMasterFloorIds ?? Array.Empty<long>())
            {
                if (previousMasterId <= 0)
                {
                    error = "invalid-floor-prev-master-id:"
                        + raw.MasterFloorId.ToString(CultureInfo.InvariantCulture)
                        + ":"
                        + previousMasterId.ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                long[] matches = rawFloors
                    .Where(candidate => candidate != null
                        && candidate.FloorLevel == raw.FloorLevel - 1
                        && candidate.MasterFloorId == previousMasterId)
                    .Select(candidate => identities[candidate])
                    .Distinct()
                    .ToArray();
                if (matches.Length == 0)
                {
                    error = "missing-prev-master-node:"
                        + identities[raw].ToString(CultureInfo.InvariantCulture)
                        + ":"
                        + previousMasterId.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                previousNodeIds.AddRange(matches);
            }

            mapped.Add(new NetherFloorNode(raw.MasterFloorId, raw.FloorLevel, raw.FloorIndex, raw.NodeType)
            {
                NodeId = identities[raw],
                ApiFloorIndex = raw.ApiFloorIndex,
                IsHidden = raw.IsHidden,
                IsUnlocked = raw.IsUnlocked,
                PreviousFloorIds = previousNodeIds.Distinct().ToArray(),
                RewardTier = raw.RewardTier,
                OptionalCombatCount = raw.OptionalCombatCount,
            });
        }

        nodes = mapped;
        error = string.Empty;
        return true;
    }
}
