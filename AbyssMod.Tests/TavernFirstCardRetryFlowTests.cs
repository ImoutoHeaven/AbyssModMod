using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class TavernFirstCardRetryFlowTests
{
    [Fact]
    public void Replay_fault_fails_open_to_the_last_server_response()
    {
        var flow = new TavernFirstCardRetryFlow();
        flow.ObserveResponse(shouldRetry: true, workedCount: 0, enabled: true);
        flow.OnCooldownElapsed(enabled: true);

        Assert.Equal(
            TavernFirstCardRetryAction.AcceptCurrentResponse,
            flow.OnReplayFault()
        );
        Assert.Equal(1, flow.RetryCount);
    }

    [Fact]
    public void Changed_native_worked_count_fails_open_instead_of_replaying_again()
    {
        var flow = new TavernFirstCardRetryFlow();
        Assert.Equal(
            TavernFirstCardRetryAction.ScheduleRetry,
            flow.ObserveResponse(shouldRetry: true, workedCount: 0, enabled: true)
        );
        Assert.Equal(
            TavernFirstCardRetryAction.InvokeReplay,
            flow.OnCooldownElapsed(enabled: true)
        );

        Assert.Equal(
            TavernFirstCardRetryAction.AcceptCurrentResponse,
            flow.ObserveResponse(shouldRetry: true, workedCount: 1, enabled: true)
        );
        Assert.Equal(1, flow.RetryCount);
    }

    [Fact]
    public void Unmatched_first_response_is_replayed_after_cooldown_until_a_target_arrives()
    {
        var flow = new TavernFirstCardRetryFlow();

        Assert.Equal(
            TavernFirstCardRetryAction.ScheduleRetry,
            flow.ObserveResponse(shouldRetry: true, workedCount: 0, enabled: true)
        );
        Assert.Equal(
            TavernFirstCardRetryAction.InvokeReplay,
            flow.OnCooldownElapsed(enabled: true)
        );
        Assert.Equal(
            TavernFirstCardRetryAction.AcceptCurrentResponse,
            flow.ObserveResponse(shouldRetry: false, workedCount: 0, enabled: true)
        );
        Assert.Equal(1, flow.RetryCount);
    }
}
