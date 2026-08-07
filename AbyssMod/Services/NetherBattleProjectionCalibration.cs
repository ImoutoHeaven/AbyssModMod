#nullable enable

using System;
using System.Globalization;

namespace AbyssMod.Services;

/// <summary>
/// Result of comparing an immutable pre-click combat projection to the first authoritative
/// post-settlement GET snapshot.  A successful comparison deliberately requests a rebaseline:
/// the server snapshot, not a locally accumulated prediction, becomes the next route baseline.
/// </summary>
internal readonly record struct NetherBattleProjectionCalibrationObservation(
    bool IsAccepted,
    bool RequiresRebaseline,
    int? ActualErosionDelta,
    NetherPauseReason PauseReason,
    string Detail
);

/// <summary>
/// Validates the battle-specific projection captured before native floor selection.  This is
/// intentionally separate from event calibration: a changed Nether-code portfolio after a
/// battle is not a benign event rebaseline, because it invalidates the safety identity used to
/// decide whether that battle could be entered at all.
/// </summary>
internal sealed class NetherBattleProjectionCalibration
{
    public NetherBattleProjectionCalibrationObservation Observe(
        NetherBattleSettlementContract? contract,
        NetherSnapshot? before,
        NetherSnapshot? after,
        NetherActiveCodeErosionProjection? postBattleCodes
    )
    {
        if (contract?.EntryProjection is not NetherBattleProjectionPayload projection)
            return Unknown("battle-projection-missing");
        if (before == null || after == null)
            return Unknown("battle-authoritative-snapshot-missing");
        if (!HasMatchingImmutableIdentity(contract, projection))
            return Unknown("battle-projection-identity-mismatch");
        if (!HasMatchingEntrySnapshot(contract, projection, before))
            return Unknown("battle-projection-entry-snapshot-mismatch");
        if (!HasMatchingSettlementSnapshot(contract, after))
            return Unknown("battle-authoritative-settlement-target-mismatch");
        if (postBattleCodes is not { ErosionProjectionKnown: true }
            || string.IsNullOrEmpty(postBattleCodes.CodeHash))
        {
            return Unknown("battle-post-get-code-projection-unknown:" + (postBattleCodes?.Detail ?? "missing"));
        }
        if (!string.Equals(projection.CodeHash, postBattleCodes.CodeHash, StringComparison.Ordinal))
        {
            return Drift("battle-projection-code-hash-changed");
        }

        int actualDelta;
        try
        {
            actualDelta = checked(after.ErosionPoint - projection.PreBattleErosion);
        }
        catch (OverflowException)
        {
            return Unknown("battle-actual-erosion-delta-overflow");
        }

        if (projection.FloorMinimumErosion < 0
            || projection.FloorMaximumErosion < projection.FloorMinimumErosion
            || projection.ProjectedMinimumErosion > projection.ProjectedMaximumErosion)
        {
            return Unknown("battle-projection-range-invalid");
        }
        if (after.ErosionPoint < 0)
            return Unknown("battle-authoritative-erosion-invalid");
        if (actualDelta < 0)
            return Drift("battle-actual-erosion-decreased:" + actualDelta.ToString(CultureInfo.InvariantCulture));
        if (after.ErosionPoint < projection.ProjectedMinimumErosion
            || after.ErosionPoint > projection.ProjectedMaximumErosion)
        {
            return Drift(
                "battle-actual-erosion-outside-projection:actual="
                    + after.ErosionPoint.ToString(CultureInfo.InvariantCulture)
                    + ":minimum="
                    + projection.ProjectedMinimumErosion.ToString(CultureInfo.InvariantCulture)
                    + ":maximum="
                    + projection.ProjectedMaximumErosion.ToString(CultureInfo.InvariantCulture)
            );
        }

        return new NetherBattleProjectionCalibrationObservation(
            IsAccepted: true,
            RequiresRebaseline: true,
            ActualErosionDelta: actualDelta,
            PauseReason: NetherPauseReason.None,
            Detail: "battle-projection-rebaseline:actual-delta="
                + actualDelta.ToString(CultureInfo.InvariantCulture)
        );
    }

    private static bool HasMatchingImmutableIdentity(
        NetherBattleSettlementContract contract,
        NetherBattleProjectionPayload projection
    ) => !string.IsNullOrEmpty(contract.ProjectionIdentity)
        && !string.IsNullOrEmpty(projection.ProjectionIdentity)
        && !string.IsNullOrEmpty(projection.CodeHash)
        && string.Equals(contract.ProjectionIdentity, projection.ProjectionIdentity, StringComparison.Ordinal);

    private static bool HasMatchingEntrySnapshot(
        NetherBattleSettlementContract contract,
        NetherBattleProjectionPayload projection,
        NetherSnapshot before
    ) => before.Status == contract.EntryStatus
        && before.MapId == contract.EntryMapId
        && before.CurrentFloorId == contract.EntryFloorId
        && projection.MapId == contract.EntryMapId
        && projection.FloorId == contract.EntryFloorId
        && projection.PreBattleErosion == before.ErosionPoint;

    private static bool HasMatchingSettlementSnapshot(
        NetherBattleSettlementContract contract,
        NetherSnapshot after
    ) => after.Status == contract.ExpectedStatus
        && after.MapId == contract.ExpectedMapId
        && after.CurrentFloorId == contract.ExpectedFloorId;

    private static NetherBattleProjectionCalibrationObservation Unknown(string detail) => new(
        IsAccepted: false,
        RequiresRebaseline: false,
        ActualErosionDelta: null,
        PauseReason: NetherPauseReason.BattleProjectionUnknown,
        Detail: detail
    );

    private static NetherBattleProjectionCalibrationObservation Drift(string detail) => new(
        IsAccepted: false,
        RequiresRebaseline: false,
        ActualErosionDelta: null,
        PauseReason: NetherPauseReason.BattleProjectionDrift,
        Detail: detail
    );
}
