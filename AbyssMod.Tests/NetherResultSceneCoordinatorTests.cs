using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherResultSceneCoordinatorTests
{
    [Fact]
    public void Floor_teardown_keeps_result_evidence_until_the_observed_task_succeeds()
    {
        var coordinator = new NetherResultSceneCoordinator(maximumMissingPolls: 2);
        object task = new();

        coordinator.ObserveFloorSelectionTerminated();
        coordinator.ObserveResultTask(task);

        Assert.True(coordinator.HasResultEvidence);
        Assert.True(coordinator.FloorSelectionTerminated);
        Assert.Equal(
            NetherResultSceneStepKind.Pending,
            coordinator.Pump(_ => NetherNativeActionResult.Started("native-result-pending")).Kind);
        Assert.Equal(
            NetherResultSceneStepKind.Succeeded,
            coordinator.Pump(_ => NetherNativeActionResult.Completed("native-result-succeeded")).Kind);
        Assert.False(coordinator.HasResultEvidence);
    }

    [Fact]
    public void Floor_teardown_can_wait_for_a_late_result_registration_but_is_bounded()
    {
        var coordinator = new NetherResultSceneCoordinator(maximumMissingPolls: 1);

        coordinator.ObserveFloorSelectionTerminated();

        Assert.Equal(NetherResultSceneStepKind.Pending, coordinator.Pump(_ => throw new Xunit.Sdk.XunitException("unexpected poll")).Kind);
        Assert.Equal(NetherResultSceneStepKind.BindingUnavailable, coordinator.Pump(_ => throw new Xunit.Sdk.XunitException("unexpected poll")).Kind);

        object task = new();
        coordinator.ObserveResultTask(task);
        Assert.Equal(
            NetherResultSceneStepKind.Succeeded,
            coordinator.Pump(_ => NetherNativeActionResult.Completed("native-result-succeeded")).Kind);
    }

    [Theory]
    [InlineData("native-result-faulted", "Faulted")]
    [InlineData("native-result-canceled", "Canceled")]
    public void Floor_teardown_preserves_fault_or_cancel_evidence_for_named_terminal_handling(
        string detail,
        string expected
    )
    {
        var coordinator = new NetherResultSceneCoordinator(maximumMissingPolls: 1);
        object task = new();
        coordinator.ObserveFloorSelectionTerminated();
        coordinator.ObserveResultTask(task);

        NetherResultSceneStep step = coordinator.Pump(_ => NetherNativeActionResult.UnknownOutcome(detail));

        Assert.Equal(expected, step.Kind.ToString());
        Assert.True(coordinator.HasResultEvidence);
        Assert.True(coordinator.FloorSelectionTerminated);
    }
}
