using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class BattleSessionAutoSLStateMachineTests
{
    [Fact]
    public void Pending_response_keeps_the_job_waiting()
    {
        var machine = new BattleSessionAutoSLStateMachine();

        Assert.Equal(BattleSessionAutoSLState.Waiting, machine.State);
        Assert.Equal(BattleSessionAutoSLTransition.Pending, machine.ObservePending());
        Assert.Equal(BattleSessionAutoSLState.Waiting, machine.State);
        Assert.Equal(0, machine.RetryCount);
    }

    [Fact]
    public void Successful_rare_response_completes_without_a_retry()
    {
        var machine = new BattleSessionAutoSLStateMachine();
        var report = new BattleDropProbeReport(
            [new BattleDropItem(1, 2, 3, 1, 5, true)],
            1
        );

        Assert.Equal(BattleSessionAutoSLTransition.Complete, machine.ObserveResponse(report));
        Assert.Equal(BattleSessionAutoSLState.Completed, machine.State);
        Assert.Equal(0, machine.RetryCount);
    }

    [Fact]
    public void Non_rare_response_enters_the_next_waiting_attempt()
    {
        var machine = new BattleSessionAutoSLStateMachine();
        var report = new BattleDropProbeReport([], 0);

        Assert.Equal(BattleSessionAutoSLTransition.Retry, machine.ObserveResponse(report));
        Assert.Equal(BattleSessionAutoSLState.Waiting, machine.State);
        Assert.Equal(1, machine.RetryCount);
    }

    [Fact]
    public void Canceled_response_ends_the_job_without_retrying()
    {
        var machine = new BattleSessionAutoSLStateMachine();

        Assert.Equal(BattleSessionAutoSLTransition.Cancel, machine.ObserveCanceled());
        Assert.Equal(BattleSessionAutoSLState.Canceled, machine.State);
        Assert.Equal(0, machine.RetryCount);
    }

    [Fact]
    public void Faulted_response_ends_the_job_without_retrying()
    {
        var machine = new BattleSessionAutoSLStateMachine();

        Assert.Equal(BattleSessionAutoSLTransition.Fault, machine.ObserveFaulted());
        Assert.Equal(BattleSessionAutoSLState.Faulted, machine.State);
        Assert.Equal(0, machine.RetryCount);
    }
}
