namespace AbyssMod.Services;

public enum TavernFirstCardRetryAction
{
    AcceptCurrentResponse,
    ScheduleRetry,
    InvokeReplay,
}

public sealed class TavernFirstCardRetryFlow
{
    private int? _initialWorkedCount;

    public int RetryCount { get; private set; }

    public TavernFirstCardRetryAction ObserveResponse(
        bool shouldRetry,
        int workedCount,
        bool enabled
    )
    {
        if (!enabled || !shouldRetry)
            return TavernFirstCardRetryAction.AcceptCurrentResponse;
        if (_initialWorkedCount.HasValue && workedCount != _initialWorkedCount.Value)
            return TavernFirstCardRetryAction.AcceptCurrentResponse;

        _initialWorkedCount ??= workedCount;

        RetryCount++;
        return TavernFirstCardRetryAction.ScheduleRetry;
    }

    public TavernFirstCardRetryAction OnCooldownElapsed(bool enabled) =>
        enabled
            ? TavernFirstCardRetryAction.InvokeReplay
            : TavernFirstCardRetryAction.AcceptCurrentResponse;

    public TavernFirstCardRetryAction OnReplayFault() =>
        TavernFirstCardRetryAction.AcceptCurrentResponse;
}
