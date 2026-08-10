using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherCheckpointPopupWaitCoordinatorTests
{
    public static IEnumerable<object[]> PopupKinds() => new[]
    {
        new object[] { (int)NetherCheckpointPopupKind.Continue },
        new object[] { (int)NetherCheckpointPopupKind.Boost },
        new object[] { (int)NetherCheckpointPopupKind.Return },
        new object[] { (int)NetherCheckpointPopupKind.ReturnScroll },
    };

    [Theory]
    [MemberData(nameof(PopupKinds))]
    public void Each_popup_stage_waits_bounded_and_accepts_a_fresh_late_registration_at_the_boundary(
        int rawKind
    )
    {
        NetherCheckpointPopupKind kind = (NetherCheckpointPopupKind)rawKind;
        var driver = new FakeParentDriver(NetherNativeActionResult.Started("parent-pending"));
        var waits = new NetherCheckpointPopupWaitCoordinator(driver, maximumMissingPolls: 1);

        Assert.True(waits.Begin(NetherActionKind.Continue, ownerGeneration: 21, minimumSequence: 100));
        Assert.Equal(NetherCheckpointPopupWaitResultKind.Waiting, waits.WaitFor(kind, null).Kind);

        NetherCheckpointPopupWaitResult ready = waits.WaitFor(kind, Fresh(kind, 101));

        Assert.Equal(NetherCheckpointPopupWaitResultKind.Ready, ready.Kind);
        Assert.Equal(101, ready.Observation!.Value.Sequence);
        Assert.Equal(2, driver.PollCalls);
    }

    [Theory]
    [MemberData(nameof(PopupKinds))]
    public void Each_missing_popup_stage_expires_instead_of_returning_started_forever(
        int rawKind
    )
    {
        NetherCheckpointPopupKind kind = (NetherCheckpointPopupKind)rawKind;
        var driver = new FakeParentDriver(NetherNativeActionResult.Started("parent-pending"));
        var waits = new NetherCheckpointPopupWaitCoordinator(driver, maximumMissingPolls: 1);

        Assert.True(waits.Begin(NetherActionKind.Continue, ownerGeneration: 21, minimumSequence: 100));
        Assert.Equal(NetherCheckpointPopupWaitResultKind.Waiting, waits.WaitFor(kind, null).Kind);

        NetherCheckpointPopupWaitResult timeout = waits.WaitFor(kind, null);

        Assert.Equal(NetherCheckpointPopupWaitResultKind.BindingUnavailable, timeout.Kind);
        Assert.Contains("timeout", timeout.Detail);
    }

    [Theory]
    [MemberData(nameof(PopupKinds))]
    public void Each_stage_polls_parent_and_names_fault_or_cancel_without_waiting(
        int rawKind
    )
    {
        NetherCheckpointPopupKind kind = (NetherCheckpointPopupKind)rawKind;
        var faultDriver = new FakeParentDriver(NetherNativeActionResult.UnknownOutcome("native-result-faulted"));
        var cancelDriver = new FakeParentDriver(NetherNativeActionResult.UnknownOutcome("native-result-canceled"));
        var faultWaits = new NetherCheckpointPopupWaitCoordinator(faultDriver);
        var cancelWaits = new NetherCheckpointPopupWaitCoordinator(cancelDriver);

        Assert.True(faultWaits.Begin(NetherActionKind.Continue, ownerGeneration: 21, minimumSequence: 100));
        Assert.True(cancelWaits.Begin(NetherActionKind.Continue, ownerGeneration: 21, minimumSequence: 100));

        Assert.Equal(NetherCheckpointPopupWaitResultKind.ParentFaulted, faultWaits.WaitFor(kind, null).Kind);
        Assert.Equal(NetherCheckpointPopupWaitResultKind.ParentCanceled, cancelWaits.WaitFor(kind, null).Kind);
        Assert.Equal(1, faultDriver.PollCalls);
        Assert.Equal(1, cancelDriver.PollCalls);
    }

    [Theory]
    [MemberData(nameof(PopupKinds))]
    public void Early_parent_completion_without_the_expected_stage_is_not_reported_as_started(
        int rawKind
    )
    {
        NetherCheckpointPopupKind kind = (NetherCheckpointPopupKind)rawKind;
        var driver = new FakeParentDriver(NetherNativeActionResult.Completed("parent-terminal"));
        var waits = new NetherCheckpointPopupWaitCoordinator(driver);

        Assert.True(waits.Begin(NetherActionKind.Continue, ownerGeneration: 21, minimumSequence: 100));

        NetherCheckpointPopupWaitResult early = waits.WaitFor(kind, null);

        Assert.Equal(NetherCheckpointPopupWaitResultKind.ParentCompletedEarly, early.Kind);
    }

    [Fact]
    public void Stale_owner_generation_sequence_or_liveness_is_rejected_before_any_native_click()
    {
        var driver = new FakeParentDriver(NetherNativeActionResult.Started("parent-pending"));
        var waits = new NetherCheckpointPopupWaitCoordinator(driver);
        Assert.True(waits.Begin(NetherActionKind.Continue, ownerGeneration: 21, minimumSequence: 100));

        NetherCheckpointPopupWaitResult oldOwner = waits.WaitFor(
            NetherCheckpointPopupKind.Continue,
            Fresh(NetherCheckpointPopupKind.Continue, 101) with { OwnerGeneration = 20 }
        );
        NetherCheckpointPopupWaitResult oldSequence = waits.WaitFor(
            NetherCheckpointPopupKind.Continue,
            Fresh(NetherCheckpointPopupKind.Continue, 100)
        );
        NetherCheckpointPopupWaitResult closed = waits.WaitFor(
            NetherCheckpointPopupKind.Continue,
            Fresh(NetherCheckpointPopupKind.Continue, 102) with { IsLive = false }
        );

        Assert.Equal(NetherCheckpointPopupWaitResultKind.Stale, oldOwner.Kind);
        Assert.Equal(NetherCheckpointPopupWaitResultKind.Stale, oldSequence.Kind);
        Assert.Equal(NetherCheckpointPopupWaitResultKind.Stale, closed.Kind);
    }

    [Fact]
    public void Return_and_scroll_keep_separate_freshness_budgets_after_the_return_popup_is_ready()
    {
        var driver = new FakeParentDriver(NetherNativeActionResult.Started("parent-pending"));
        var waits = new NetherCheckpointPopupWaitCoordinator(driver, maximumMissingPolls: 1);
        Assert.True(waits.Begin(NetherActionKind.Continue, ownerGeneration: 21, minimumSequence: 100));

        Assert.Equal(
            NetherCheckpointPopupWaitResultKind.Ready,
            waits.WaitFor(NetherCheckpointPopupKind.Return, Fresh(NetherCheckpointPopupKind.Return, 101)).Kind
        );
        Assert.Equal(NetherCheckpointPopupWaitResultKind.Waiting, waits.WaitFor(NetherCheckpointPopupKind.ReturnScroll, null).Kind);
        Assert.Equal(
            NetherCheckpointPopupWaitResultKind.Ready,
            waits.WaitFor(NetherCheckpointPopupKind.ReturnScroll, Fresh(NetherCheckpointPopupKind.ReturnScroll, 102)).Kind
        );
    }

    [Fact]
    public void Finish_can_wait_for_its_continue_confirmation_but_never_owns_return_stages()
    {
        var driver = new FakeParentDriver(NetherNativeActionResult.Started("parent-pending"));
        var waits = new NetherCheckpointPopupWaitCoordinator(driver);
        Assert.True(waits.Begin(NetherActionKind.FinishAtCheckpoint, ownerGeneration: 21, minimumSequence: 100));

        Assert.Equal(
            NetherCheckpointPopupWaitResultKind.Ready,
            waits.WaitFor(
                NetherCheckpointPopupKind.Continue,
                new NetherCheckpointPopupObservation(
                    NetherCheckpointPopupKind.Continue,
                    NetherActionKind.FinishAtCheckpoint,
                    21,
                    101,
                    true
                )
            ).Kind
        );
        Assert.Equal(
            NetherCheckpointPopupWaitResultKind.BindingUnavailable,
            waits.WaitFor(NetherCheckpointPopupKind.Return, null).Kind
        );
    }

    private static NetherCheckpointPopupObservation Fresh(NetherCheckpointPopupKind kind, long sequence) => new(
        kind,
        NetherActionKind.Continue,
        OwnerGeneration: 21,
        Sequence: sequence,
        IsLive: true
    );

    private sealed class FakeParentDriver : INetherCheckpointPopupWaitDriver
    {
        private readonly NetherNativeActionResult _result;

        public FakeParentDriver(NetherNativeActionResult result) => _result = result;

        public int PollCalls { get; private set; }

        public NetherNativeActionResult PollCheckpointParent()
        {
            PollCalls++;
            return _result;
        }
    }
}
