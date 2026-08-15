using System;

namespace AbyssMod.Services;

public enum BattleSessionRetryRequestPhase
{
    ResponseReady = 0,
    CooldownBeforeClose = 1,
    Closing = 2,
    CooldownBeforeStart = 3,
    Starting = 4,
    Completed = 5,
}

public enum BattleSessionRetryRequestAction
{
    None = 0,
    AcceptCurrentResponse = 1,
    InvokeClose = 2,
    InvokeStart = 3,
}

public enum BattleSessionExplorationRetryMode
{
    Exploration = 0,
    IdleExploration = 1,
    HuntEvent = 2,
    EventBonus = 3,
    StoryEvent = 4,
    TrainingEvent = 5,
    CommissionEvent = 6,
    MiningEvent = 7,
}

public static class BattleSessionExplorationRetryPolicy
{
    public static bool RequiresCloseBeforeStart(BattleSessionExplorationRetryMode mode) =>
        mode switch
        {
            BattleSessionExplorationRetryMode.Exploration => false,
            BattleSessionExplorationRetryMode.IdleExploration => true,
            BattleSessionExplorationRetryMode.HuntEvent => true,
            BattleSessionExplorationRetryMode.EventBonus => true,
            BattleSessionExplorationRetryMode.StoryEvent => true,
            BattleSessionExplorationRetryMode.TrainingEvent => true,
            BattleSessionExplorationRetryMode.CommissionEvent => true,
            BattleSessionExplorationRetryMode.MiningEvent => true,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };

    public static string GetLogName(BattleSessionExplorationRetryMode mode) =>
        mode switch
        {
            BattleSessionExplorationRetryMode.Exploration => "exploration",
            BattleSessionExplorationRetryMode.IdleExploration => "idle-exploration",
            BattleSessionExplorationRetryMode.HuntEvent => "hunt-event",
            BattleSessionExplorationRetryMode.EventBonus => "event-bonus",
            BattleSessionExplorationRetryMode.StoryEvent => "story-event",
            BattleSessionExplorationRetryMode.TrainingEvent => "training-event",
            BattleSessionExplorationRetryMode.CommissionEvent => "commission-event",
            BattleSessionExplorationRetryMode.MiningEvent => "mining-event",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };
}

/// <summary>
/// Models the request ordering for one Auto-SL retry without depending on Unity or UniTask.
/// Normal battles use response -> cooldown -> start. Session-bound exploration
/// transports use response -> cooldown -> close -> cooldown -> start.
/// </summary>
public sealed class BattleSessionRetryRequestFlow
{
    private bool _mustRestoreSession;

    public BattleSessionRetryRequestPhase Phase { get; private set; } =
        BattleSessionRetryRequestPhase.ResponseReady;

    public void Schedule(bool closeBeforeStart)
    {
        Require(BattleSessionRetryRequestPhase.ResponseReady, nameof(Schedule));
        _mustRestoreSession = false;
        Phase = closeBeforeStart
            ? BattleSessionRetryRequestPhase.CooldownBeforeClose
            : BattleSessionRetryRequestPhase.CooldownBeforeStart;
    }

    public BattleSessionRetryRequestAction OnCooldownElapsed(bool autoSlEnabled)
    {
        if (Phase == BattleSessionRetryRequestPhase.CooldownBeforeClose)
        {
            if (!autoSlEnabled)
            {
                Phase = BattleSessionRetryRequestPhase.Completed;
                return BattleSessionRetryRequestAction.AcceptCurrentResponse;
            }

            Phase = BattleSessionRetryRequestPhase.Closing;
            return BattleSessionRetryRequestAction.InvokeClose;
        }

        if (Phase == BattleSessionRetryRequestPhase.CooldownBeforeStart)
        {
            if (!autoSlEnabled && !_mustRestoreSession)
            {
                Phase = BattleSessionRetryRequestPhase.Completed;
                return BattleSessionRetryRequestAction.AcceptCurrentResponse;
            }

            Phase = BattleSessionRetryRequestPhase.Starting;
            return BattleSessionRetryRequestAction.InvokeStart;
        }

        throw new InvalidOperationException(
            $"OnCooldownElapsed is invalid while request flow is {Phase}."
        );
    }

    public void OnCloseSucceeded()
    {
        Require(BattleSessionRetryRequestPhase.Closing, nameof(OnCloseSucceeded));
        _mustRestoreSession = true;
        Phase = BattleSessionRetryRequestPhase.CooldownBeforeStart;
    }

    public void OnStartResponseReceived()
    {
        Require(BattleSessionRetryRequestPhase.Starting, nameof(OnStartResponseReceived));
        _mustRestoreSession = false;
        Phase = BattleSessionRetryRequestPhase.ResponseReady;
    }

    private void Require(BattleSessionRetryRequestPhase expected, string operation)
    {
        if (Phase != expected)
        {
            throw new InvalidOperationException(
                $"{operation} requires request flow {expected}, but it is {Phase}."
            );
        }
    }
}
