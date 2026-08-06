namespace AbyssMod.Services;

public enum BattleSessionAutoSLState
{
    Waiting,
    Completed,
    Canceled,
    Faulted,
}

public enum BattleSessionAutoSLTransition
{
    Pending,
    Complete,
    Retry,
    Cancel,
    Fault,
}

public sealed class BattleSessionAutoSLStateMachine
{
    public BattleSessionAutoSLState State { get; private set; } = BattleSessionAutoSLState.Waiting;
    public int RetryCount { get; private set; }

    public BattleSessionAutoSLStateMachine() { }

    public BattleSessionAutoSLTransition ObservePending() =>
        IsTerminal ? BattleSessionAutoSLTransition.Complete : BattleSessionAutoSLTransition.Pending;

    public BattleSessionAutoSLTransition ObserveResponse(BattleDropProbeReport report)
        => ObserveDecision(BattleSessionAutoSLPolicy.ShouldRetry(report));

    public BattleSessionAutoSLTransition ObserveDecision(bool shouldRetry)
    {
        if (IsTerminal)
            return BattleSessionAutoSLTransition.Complete;

        if (!shouldRetry)
        {
            State = BattleSessionAutoSLState.Completed;
            return BattleSessionAutoSLTransition.Complete;
        }

        RetryCount++;
        return BattleSessionAutoSLTransition.Retry;
    }

    public BattleSessionAutoSLTransition ObserveCanceled()
    {
        if (IsTerminal)
            return BattleSessionAutoSLTransition.Complete;

        State = BattleSessionAutoSLState.Canceled;
        return BattleSessionAutoSLTransition.Cancel;
    }

    public BattleSessionAutoSLTransition ObserveFaulted()
    {
        if (IsTerminal)
            return BattleSessionAutoSLTransition.Complete;

        State = BattleSessionAutoSLState.Faulted;
        return BattleSessionAutoSLTransition.Fault;
    }

    private bool IsTerminal => State != BattleSessionAutoSLState.Waiting;
}
