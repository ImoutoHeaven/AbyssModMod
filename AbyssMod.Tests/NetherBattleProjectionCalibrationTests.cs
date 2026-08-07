using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherBattleProjectionCalibrationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(10)]
    public void Exact_authoritative_battle_delta_within_projection_rebaselines(int delta)
    {
        var calibration = new NetherBattleProjectionCalibration();
        NetherBattleSettlementContract contract = Contract(projectedMinimum: 40, projectedMaximum: 50);

        NetherBattleProjectionCalibrationObservation observation = calibration.Observe(
            contract,
            Snapshot(NetherSessionStatus.Battle, erosion: 40),
            Snapshot(NetherSessionStatus.Play, erosion: 40 + delta),
            ActiveCodes("code-before")
        );

        Assert.True(observation.IsAccepted);
        Assert.True(observation.RequiresRebaseline);
        Assert.Equal(delta, observation.ActualErosionDelta);
        Assert.Equal(NetherPauseReason.None, observation.PauseReason);
    }

    [Fact]
    public void Actual_erosion_outside_projection_or_decrease_is_named_drift()
    {
        var calibration = new NetherBattleProjectionCalibration();
        NetherBattleSettlementContract contract = Contract(projectedMinimum: 40, projectedMaximum: 45);

        NetherBattleProjectionCalibrationObservation outside = calibration.Observe(
            contract,
            Snapshot(NetherSessionStatus.Battle, erosion: 40),
            Snapshot(NetherSessionStatus.Play, erosion: 46),
            ActiveCodes("code-before")
        );
        NetherBattleProjectionCalibrationObservation decreased = calibration.Observe(
            contract,
            Snapshot(NetherSessionStatus.Battle, erosion: 40),
            Snapshot(NetherSessionStatus.Play, erosion: 39),
            ActiveCodes("code-before")
        );

        Assert.False(outside.IsAccepted);
        Assert.Equal(NetherPauseReason.BattleProjectionDrift, outside.PauseReason);
        Assert.Contains("outside", outside.Detail);
        Assert.False(decreased.IsAccepted);
        Assert.Equal(NetherPauseReason.BattleProjectionDrift, decreased.PauseReason);
        Assert.Contains("decreased", decreased.Detail);
    }

    [Fact]
    public void Code_hash_change_or_unknown_authority_is_named_and_cannot_settle()
    {
        var calibration = new NetherBattleProjectionCalibration();
        NetherBattleSettlementContract contract = Contract(projectedMinimum: 40, projectedMaximum: 50);

        NetherBattleProjectionCalibrationObservation changed = calibration.Observe(
            contract,
            Snapshot(NetherSessionStatus.Battle, erosion: 40),
            Snapshot(NetherSessionStatus.Play, erosion: 45),
            ActiveCodes("code-after")
        );
        NetherBattleProjectionCalibrationObservation unknown = calibration.Observe(
            contract,
            Snapshot(NetherSessionStatus.Battle, erosion: 40),
            null,
            new NetherActiveCodeErosionProjection { ErosionProjectionKnown = false, Detail = "post-get-code-unknown" }
        );

        Assert.False(changed.IsAccepted);
        Assert.Equal(NetherPauseReason.BattleProjectionDrift, changed.PauseReason);
        Assert.Contains("code-hash", changed.Detail);
        Assert.False(unknown.IsAccepted);
        Assert.Equal(NetherPauseReason.BattleProjectionUnknown, unknown.PauseReason);
    }

    [Fact]
    public void Checked_delta_overflow_and_invalid_identity_fail_closed()
    {
        var calibration = new NetherBattleProjectionCalibration();
        NetherBattleSettlementContract overflow = Contract(
            projectedMinimum: int.MinValue,
            projectedMaximum: int.MaxValue,
            preBattleErosion: int.MinValue
        );
        NetherBattleSettlementContract wrongIdentity = Contract(projectedMinimum: 40, projectedMaximum: 50) with
        {
            ProjectionIdentity = "different-contract-identity",
        };

        NetherBattleProjectionCalibrationObservation overflowObservation = calibration.Observe(
            overflow,
            Snapshot(NetherSessionStatus.Battle, erosion: int.MinValue),
            Snapshot(NetherSessionStatus.Play, erosion: int.MaxValue),
            ActiveCodes("code-before")
        );
        NetherBattleProjectionCalibrationObservation identityObservation = calibration.Observe(
            wrongIdentity,
            Snapshot(NetherSessionStatus.Battle, erosion: 40),
            Snapshot(NetherSessionStatus.Play, erosion: 45),
            ActiveCodes("code-before")
        );

        Assert.False(overflowObservation.IsAccepted);
        Assert.Equal(NetherPauseReason.BattleProjectionUnknown, overflowObservation.PauseReason);
        Assert.Contains("overflow", overflowObservation.Detail);
        Assert.False(identityObservation.IsAccepted);
        Assert.Equal(NetherPauseReason.BattleProjectionUnknown, identityObservation.PauseReason);
        Assert.Contains("identity", identityObservation.Detail);
    }

    private static NetherBattleSettlementContract Contract(
        int projectedMinimum,
        int projectedMaximum,
        int preBattleErosion = 40
    ) => new(
        EntryMapId: 2,
        EntryFloorId: 10,
        EntryStatus: NetherSessionStatus.Battle,
        ExpectedMapId: 2,
        ExpectedFloorId: 10,
        ExpectedStatus: NetherSessionStatus.Play,
        ProjectionIdentity: "battle-2-10"
    )
    {
        EntryProjection = new NetherBattleProjectionPayload(
            MapId: 2,
            FloorId: 10,
            PreBattleErosion: preBattleErosion,
            FloorMinimumErosion: 0,
            FloorMaximumErosion: 10,
            ProjectedMinimumErosion: projectedMinimum,
            ProjectedMaximumErosion: projectedMaximum,
            CodeHash: "code-before",
            ProjectionIdentity: "battle-2-10"
        ),
    };

    private static NetherActiveCodeErosionProjection ActiveCodes(string codeHash) => new()
    {
        ErosionProjectionKnown = true,
        CodeHash = codeHash,
    };

    private static NetherSnapshot Snapshot(NetherSessionStatus status, int erosion) => new()
    {
        Status = status,
        MapId = 2,
        CurrentFloorId = 10,
        ErosionPoint = erosion,
    };
}
