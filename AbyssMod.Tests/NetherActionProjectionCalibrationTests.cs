using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherActionProjectionCalibrationTests
{
    [Fact]
    public void Matching_event_projection_clears_without_a_pause()
    {
        var calibration = new NetherActionProjectionCalibration();
        calibration.Expect(Decision(erosion: 25, hpDelta: -50), Snapshot(erosion: 20, hp: 900));

        NetherProjectionObservation observation = calibration.Observe(Snapshot(erosion: 25, hp: 850));

        Assert.False(observation.IsDrift);
        Assert.False(observation.RequiresRebaseline);
    }

    [Fact]
    public void Lower_than_projected_hp_or_wrong_erosion_fails_closed_as_drift()
    {
        var calibration = new NetherActionProjectionCalibration();
        calibration.Expect(Decision(erosion: 25, hpDelta: -50), Snapshot(erosion: 20, hp: 900));

        NetherProjectionObservation observation = calibration.Observe(Snapshot(erosion: 24, hp: 840));

        Assert.True(observation.IsDrift);
        Assert.Equal(NetherPauseReason.ErosionDrift, observation.PauseReason);
    }

    [Fact]
    public void Code_change_rebaselines_erosion_but_still_rejects_unexpected_damage()
    {
        var calibration = new NetherActionProjectionCalibration();
        calibration.Expect(Decision(erosion: 25, hpDelta: -50), Snapshot(erosion: 20, hp: 900, codeHash: "before"));

        NetherProjectionObservation observation = calibration.Observe(Snapshot(erosion: 20, hp: 840, codeHash: "after"));

        Assert.True(observation.IsDrift);
        Assert.Equal(NetherPauseReason.UnsafeHp, observation.PauseReason);
    }

    private static NetherEventDecision Decision(int erosion, int hpDelta) => new()
    {
        Kind = NetherEventDecisionKind.Select,
        ProjectedErosion = erosion,
        HpDelta = hpDelta,
    };

    private static NetherSnapshot Snapshot(int erosion, int hp, string codeHash = "same") => new()
    {
        ErosionPoint = erosion,
        CodeHash = codeHash,
        Characters = [new NetherCharacterState(100, hp)],
    };
}
