using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public sealed class BattleSessionRetryRequestFlowTests
{
    [Fact]
    public void Normal_retry_waits_once_then_invokes_start()
    {
        var flow = new BattleSessionRetryRequestFlow();

        flow.Schedule(closeBeforeStart: false);

        Assert.Equal(
            BattleSessionRetryRequestPhase.CooldownBeforeStart,
            flow.Phase
        );
        Assert.Equal(
            BattleSessionRetryRequestAction.InvokeStart,
            flow.OnCooldownElapsed(autoSlEnabled: true)
        );
        Assert.Equal(BattleSessionRetryRequestPhase.Starting, flow.Phase);
    }

    [Fact]
    public void Idle_retry_requires_close_and_a_second_cooldown_before_start()
    {
        var flow = new BattleSessionRetryRequestFlow();

        flow.Schedule(closeBeforeStart: true);
        Assert.Equal(
            BattleSessionRetryRequestPhase.CooldownBeforeClose,
            flow.Phase
        );
        Assert.Equal(
            BattleSessionRetryRequestAction.InvokeClose,
            flow.OnCooldownElapsed(autoSlEnabled: true)
        );
        Assert.Equal(BattleSessionRetryRequestPhase.Closing, flow.Phase);

        flow.OnCloseSucceeded();

        Assert.Equal(
            BattleSessionRetryRequestPhase.CooldownBeforeStart,
            flow.Phase
        );
        Assert.Equal(
            BattleSessionRetryRequestAction.InvokeStart,
            flow.OnCooldownElapsed(autoSlEnabled: true)
        );
        Assert.Equal(BattleSessionRetryRequestPhase.Starting, flow.Phase);
    }

    [Fact]
    public void Disabling_before_idle_close_accepts_the_still_open_session()
    {
        var flow = new BattleSessionRetryRequestFlow();
        flow.Schedule(closeBeforeStart: true);

        Assert.Equal(
            BattleSessionRetryRequestAction.AcceptCurrentResponse,
            flow.OnCooldownElapsed(autoSlEnabled: false)
        );
        Assert.Equal(BattleSessionRetryRequestPhase.Completed, flow.Phase);
    }

    [Fact]
    public void Disabling_after_idle_close_still_restores_one_fresh_session()
    {
        var flow = new BattleSessionRetryRequestFlow();
        flow.Schedule(closeBeforeStart: true);
        flow.OnCooldownElapsed(autoSlEnabled: true);
        flow.OnCloseSucceeded();

        Assert.Equal(
            BattleSessionRetryRequestAction.InvokeStart,
            flow.OnCooldownElapsed(autoSlEnabled: false)
        );
        Assert.Equal(BattleSessionRetryRequestPhase.Starting, flow.Phase);
    }

    [Fact]
    public void A_restored_start_response_can_begin_the_next_idle_cycle()
    {
        var flow = new BattleSessionRetryRequestFlow();
        flow.Schedule(closeBeforeStart: true);
        flow.OnCooldownElapsed(autoSlEnabled: true);
        flow.OnCloseSucceeded();
        flow.OnCooldownElapsed(autoSlEnabled: true);

        flow.OnStartResponseReceived();
        flow.Schedule(closeBeforeStart: true);

        Assert.Equal(
            BattleSessionRetryRequestPhase.CooldownBeforeClose,
            flow.Phase
        );
    }
}
