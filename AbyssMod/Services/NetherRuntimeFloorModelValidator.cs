#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace AbyssMod.Services;

/// <summary>
/// Creates the stable runtime identity used by route planning.  The packaged client addresses
/// a rendered node by floor level plus API floor index; m_nether_map_floor_id is a reusable
/// master/template ID and therefore must never be used as the global dictionary key.
/// </summary>
internal static class NetherRuntimeFloorModelValidator
{
    public static bool TryCreateNodeId(
        long floorMasterId,
        int floorLevel,
        int uiFloorIndex,
        int apiFloorIndex,
        ISet<long> acceptedNodeIds,
        out long nodeId,
        out string error
    )
    {
        if (acceptedNodeIds == null)
            throw new ArgumentNullException(nameof(acceptedNodeIds));

        nodeId = 0;

        if (floorMasterId <= 0)
        {
            error = "invalid-floor-master-id:"
                + floorMasterId.ToString(CultureInfo.InvariantCulture)
                + ":level="
                + floorLevel.ToString(CultureInfo.InvariantCulture)
                + ":ui-index="
                + uiFloorIndex.ToString(CultureInfo.InvariantCulture)
                + ":api-index="
                + apiFloorIndex.ToString(CultureInfo.InvariantCulture);
            return false;
        }
        if (floorLevel < 0 || floorLevel == int.MaxValue)
        {
            error = "invalid-floor-level:"
                + floorLevel.ToString(CultureInfo.InvariantCulture)
                + ":master-id="
                + floorMasterId.ToString(CultureInfo.InvariantCulture);
            return false;
        }
        if (uiFloorIndex < 0)
        {
            error = "invalid-ui-floor-index:"
                + uiFloorIndex.ToString(CultureInfo.InvariantCulture)
                + ":master-id="
                + floorMasterId.ToString(CultureInfo.InvariantCulture)
                + ":level="
                + floorLevel.ToString(CultureInfo.InvariantCulture);
            return false;
        }
        if (apiFloorIndex < 0 || apiFloorIndex == int.MaxValue)
        {
            error = "invalid-api-floor-index:"
                + apiFloorIndex.ToString(CultureInfo.InvariantCulture)
                + ":master-id="
                + floorMasterId.ToString(CultureInfo.InvariantCulture)
                + ":level="
                + floorLevel.ToString(CultureInfo.InvariantCulture);
            return false;
        }

        nodeId = ((long)(floorLevel + 1) << 32) | (uint)(apiFloorIndex + 1);
        if (!acceptedNodeIds.Add(nodeId))
        {
            error = "duplicate-runtime-floor-node:"
                + nodeId.ToString(CultureInfo.InvariantCulture)
                + ":master-id="
                + floorMasterId.ToString(CultureInfo.InvariantCulture)
                + ":level="
                + floorLevel.ToString(CultureInfo.InvariantCulture)
                + ":api-index="
                + apiFloorIndex.ToString(CultureInfo.InvariantCulture);
            nodeId = 0;
            return false;
        }

        error = string.Empty;
        return true;
    }
}
