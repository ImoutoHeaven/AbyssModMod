#nullable enable

using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherCodeKeepCancelCoordinatorTests
{
    [Fact]
    public void Exact_owner_task_must_be_observed_and_complete_before_the_parent_may_continue()
    {
        var coordinator = new NetherCodeKeepCancelCoordinator(maximumPendingPumps: 2);
        var owner = new NetherCodeKeepCancelOwner(
            NetherActionKind.SelectFloor,
            Generation: 3,
            Sequence: 7,
            DecisionEpoch: 1
        );

        Assert.True(coordinator.Begin(owner));
        Assert.False(coordinator.ObserveTask(owner with { Sequence = 8 }));
        Assert.True(coordinator.ObserveTask(owner));
        Assert.Equal(NetherCodeKeepCancelStage.AwaitingTaskTerminal, coordinator.Stage);

        Assert.Equal(
            NetherNativeActionResultKind.Started,
            coordinator.Pump(() => NetherNativeActionResult.Started("cancel-task-pending")).Kind
        );
        Assert.Equal(
            NetherNativeActionResultKind.Completed,
            coordinator.Pump(() => NetherNativeActionResult.Completed("cancel-task-terminal")).Kind
        );
        Assert.Equal(NetherCodeKeepCancelStage.Completed, coordinator.Stage);
        Assert.False(coordinator.Begin(owner));
    }

    [Fact]
    public void Missing_faulted_or_stale_task_never_reopens_the_exact_cancel_action()
    {
        var coordinator = new NetherCodeKeepCancelCoordinator(maximumPendingPumps: 1);
        var owner = new NetherCodeKeepCancelOwner(
            NetherActionKind.SelectFloor,
            Generation: 3,
            Sequence: 7,
            DecisionEpoch: 1
        );

        Assert.True(coordinator.Begin(owner));
        Assert.Equal(NetherNativeActionResultKind.Started, coordinator.Pump(() => throw new Xunit.Sdk.XunitException("no task yet")).Kind);
        Assert.Equal(NetherNativeActionResultKind.BindingUnavailable, coordinator.Pump(() => throw new Xunit.Sdk.XunitException("timeout must not poll task")).Kind);
        Assert.Equal(NetherCodeKeepCancelStage.Faulted, coordinator.Stage);
        Assert.False(coordinator.ObserveTask(owner));
        Assert.False(coordinator.Begin(owner));

        coordinator.Reset();
        Assert.True(coordinator.Begin(owner));
        Assert.True(coordinator.ObserveTask(owner));
        Assert.Equal(
            NetherNativeActionResultKind.BindingUnavailable,
            coordinator.Pump(() => NetherNativeActionResult.UnknownOutcome("native-cancel-fault")).Kind
        );
        Assert.Equal(NetherCodeKeepCancelStage.Faulted, coordinator.Stage);
        Assert.False(coordinator.Begin(owner with { DecisionEpoch = 2 }));
    }
}
