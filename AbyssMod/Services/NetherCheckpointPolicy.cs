#nullable enable

using System;

namespace AbyssMod.Services;

internal enum NetherCheckpointDecisionKind
{
    None,
    ContinueOneTicket,
    FinishNormally,
    PauseAtNonCheckpointTarget,
    AwaitResult,
    Pause,
}

internal sealed record NetherCheckpointDecision
{
    public NetherCheckpointDecisionKind Kind { get; init; }
    public int EffectiveMaxDepth { get; init; }
    public int TicketCount { get; init; }
    public NetherPauseReason PauseReason { get; init; }
    public string Detail { get; init; } = string.Empty;
}

internal sealed class NetherCheckpointPolicy
{
    public NetherCheckpointDecision Decide(NetherSnapshot snapshot, NetherAutoClimbSettings settings)
    {
        if (snapshot == null)
            throw new ArgumentNullException(nameof(snapshot));
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));
        if (settings.MaxDepth < 1)
            return Pause(NetherPauseReason.InvalidConfiguration, "invalid-max-depth");
        if (snapshot.MaxFloorLevel < 1 || snapshot.MasterMaxFloorLevel < 1 || snapshot.FloorLevel < 0)
            return Pause(NetherPauseReason.UnknownMasterData, "invalid-server-or-master-depth");

        int target = Math.Min(settings.MaxDepth, Math.Min(snapshot.MaxFloorLevel, snapshot.MasterMaxFloorLevel));
        if (snapshot.Status == NetherSessionStatus.Clear)
            return new NetherCheckpointDecision { Kind = NetherCheckpointDecisionKind.AwaitResult, EffectiveMaxDepth = target };
        if (snapshot.Status == NetherSessionStatus.Lose)
            return new NetherCheckpointDecision
            {
                Kind = NetherCheckpointDecisionKind.Pause,
                EffectiveMaxDepth = target,
                PauseReason = NetherPauseReason.Lose,
                Detail = "lose-no-signal-auto-use",
            };
        if (snapshot.Status == NetherSessionStatus.NotPlayed)
            return Pause(NetherPauseReason.NotPlayed, "not-played", target);
        if (snapshot.Status == NetherSessionStatus.Unknown)
            return Pause(NetherPauseReason.UnknownStatus, "unknown-status", target);

        if (snapshot.Status == NetherSessionStatus.Sleep)
        {
            if (snapshot.FloorLevel >= target || snapshot.TicketCount < 1)
                return new NetherCheckpointDecision { Kind = NetherCheckpointDecisionKind.FinishNormally, EffectiveMaxDepth = target };
            return new NetherCheckpointDecision
            {
                Kind = NetherCheckpointDecisionKind.ContinueOneTicket,
                EffectiveMaxDepth = target,
                TicketCount = 1,
            };
        }

        if (snapshot.FloorLevel >= target)
        {
            return new NetherCheckpointDecision
            {
                Kind = NetherCheckpointDecisionKind.PauseAtNonCheckpointTarget,
                EffectiveMaxDepth = target,
                PauseReason = NetherPauseReason.TargetReachedOutsideCheckpoint,
                Detail = "target-reached-outside-sleep",
            };
        }

        return new NetherCheckpointDecision { Kind = NetherCheckpointDecisionKind.None, EffectiveMaxDepth = target };
    }

    public bool CanEnterFloor(NetherSnapshot snapshot, NetherAutoClimbSettings settings, int floorLevel)
    {
        NetherCheckpointDecision decision = Decide(snapshot, settings);
        return decision.PauseReason == NetherPauseReason.None
            && floorLevel >= 1
            && floorLevel <= decision.EffectiveMaxDepth;
    }

    private static NetherCheckpointDecision Pause(NetherPauseReason reason, string detail, int target = 0) => new()
    {
        Kind = NetherCheckpointDecisionKind.Pause,
        EffectiveMaxDepth = target,
        PauseReason = reason,
        Detail = detail,
    };
}
