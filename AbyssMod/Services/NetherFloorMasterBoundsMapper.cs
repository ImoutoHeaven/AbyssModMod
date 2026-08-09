#nullable enable

using System;
using System.Collections.Generic;

namespace AbyssMod.Services;

/// <summary>
/// Raw fields copied directly from one <c>MNetherMapFloors</c> row by the runtime bridge.  Long
/// storage is intentional: an unexpected packaged field width/value can be rejected before it
/// is narrowed to the evaluator's Int32 range.
/// </summary>
internal readonly record struct NetherFloorMasterBoundsRow(
    long MasterFloorId,
    long MinimumErosionPoint,
    long MaximumErosionPoint
)
{
    public bool HasRequiredFields { get; init; } = true;
}

/// <summary>
/// Nullable bounds distinguish a missing/ambiguous master mapping from a validated map-row
/// generation eligibility range. These values are not an action erosion cost.
/// </summary>
internal readonly record struct NetherFloorMasterBounds(
    long MasterFloorId,
    int? MinimumErosionPoint,
    int? MaximumErosionPoint,
    bool IsKnown,
    string Detail
);

/// <summary>
/// Resolves a server floor's exact master ID against the complete MNetherMapFloors cache. Any
/// malformed or ambiguous cache row is unsafe: this mapper never picks an arbitrary duplicate.
/// The min/max erosion fields are retained only as map-generation metadata.
/// </summary>
internal sealed class NetherFloorMasterBoundsMapper
{
    public NetherFloorMasterBounds Map(
        long runtimeFloorMasterId,
        IReadOnlyList<NetherFloorMasterBoundsRow>? rows
    )
    {
        if (runtimeFloorMasterId <= 0)
            return Unknown(runtimeFloorMasterId, "invalid-runtime-floor-master-id");
        if (rows == null || rows.Count == 0)
            return Unknown(runtimeFloorMasterId, "missing-m-nether-map-floors-rows");

        var seen = new HashSet<long>();
        NetherFloorMasterBoundsRow? target = null;
        foreach (NetherFloorMasterBoundsRow row in rows)
        {
            if (!row.HasRequiredFields)
                return Unknown(runtimeFloorMasterId, "missing-m-nether-map-floor-required-field");
            if (row.MasterFloorId <= 0)
                return Unknown(runtimeFloorMasterId, "invalid-m-nether-map-floor-id");
            if (!seen.Add(row.MasterFloorId))
                return Unknown(runtimeFloorMasterId, "duplicate-m-nether-map-floor-id:" + row.MasterFloorId);
            if (row.MasterFloorId == runtimeFloorMasterId)
                target = row;
        }

        if (!target.HasValue)
            return Unknown(runtimeFloorMasterId, "missing-m-nether-map-floor:" + runtimeFloorMasterId);

        NetherFloorMasterBoundsRow mapped = target.Value;
        if (mapped.MinimumErosionPoint is < 0 || mapped.MaximumErosionPoint < mapped.MinimumErosionPoint)
            return Unknown(runtimeFloorMasterId, "invalid-m-nether-map-floor-erosion-range");
        if (mapped.MinimumErosionPoint > int.MaxValue || mapped.MaximumErosionPoint > int.MaxValue)
            return Unknown(runtimeFloorMasterId, "overflow-m-nether-map-floor-erosion-range");

        return new NetherFloorMasterBounds(
            runtimeFloorMasterId,
            checked((int)mapped.MinimumErosionPoint),
            checked((int)mapped.MaximumErosionPoint),
            IsKnown: true,
            Detail: string.Empty
        );
    }

    private static NetherFloorMasterBounds Unknown(long masterFloorId, string detail) => new(
        masterFloorId,
        MinimumErosionPoint: null,
        MaximumErosionPoint: null,
        IsKnown: false,
        Detail: detail
    );
}
