#nullable enable

using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public sealed class NetherBattleResultContinuationFlowTests
{
    [Fact]
    public void Ready_view_invokes_native_next_once_and_completes_only_after_new_floor_generation()
    {
        var flow = new NetherBattleResultContinuationFlow(maximumMissingPolls: 2, maximumRebindPolls: 3);
        object controller = new();
        object initializeTask = new();
        int invokeCount = 0;

        flow.Observe(controller, initializeTask, floorGenerationBeforeResult: 7);

        NetherBattleResultContinuationStep first = flow.Pump(
            _ => NetherNativeActionResult.Started("view-initializing"),
            _ => { invokeCount++; return NetherNativeActionResult.Started("native-next"); },
            hasFloorSelection: false,
            currentFloorGeneration: 0,
            allowInvoke: true
        );
        Assert.Equal(NetherBattleResultContinuationStepKind.AwaitingView, first.Kind);
        Assert.Equal(0, invokeCount);

        NetherBattleResultContinuationStep invoked = flow.Pump(
            _ => NetherNativeActionResult.Completed("view-ready"),
            _ => { invokeCount++; return NetherNativeActionResult.Started("native-next"); },
            hasFloorSelection: false,
            currentFloorGeneration: 0,
            allowInvoke: true
        );
        Assert.Equal(NetherBattleResultContinuationStepKind.AwaitingFloorRebind, invoked.Kind);
        Assert.Equal(1, invokeCount);

        NetherBattleResultContinuationStep stale = flow.Pump(
            _ => throw new Xunit.Sdk.XunitException("ready task must not be polled after Next"),
            _ => { invokeCount++; return NetherNativeActionResult.Started("duplicate-next"); },
            hasFloorSelection: true,
            currentFloorGeneration: 7,
            allowInvoke: true
        );
        Assert.Equal(NetherBattleResultContinuationStepKind.AwaitingFloorRebind, stale.Kind);
        Assert.Equal(1, invokeCount);

        NetherBattleResultContinuationStep rebound = flow.Pump(
            _ => throw new Xunit.Sdk.XunitException("ready task must not be polled after Next"),
            _ => { invokeCount++; return NetherNativeActionResult.Started("duplicate-next"); },
            hasFloorSelection: true,
            currentFloorGeneration: 8,
            allowInvoke: true
        );
        Assert.Equal(NetherBattleResultContinuationStepKind.Completed, rebound.Kind);
        Assert.Equal(1, invokeCount);
    }

    [Fact]
    public void View_fault_never_invokes_next()
    {
        var flow = new NetherBattleResultContinuationFlow(maximumMissingPolls: 1, maximumRebindPolls: 1);
        int invokeCount = 0;
        flow.Observe(new object(), new object(), floorGenerationBeforeResult: 3);

        NetherBattleResultContinuationStep step = flow.Pump(
            _ => NetherNativeActionResult.UnknownOutcome("view-init-fault"),
            _ => { invokeCount++; return NetherNativeActionResult.Started("native-next"); },
            hasFloorSelection: false,
            currentFloorGeneration: 0,
            allowInvoke: true
        );

        Assert.Equal(NetherBattleResultContinuationStepKind.Faulted, step.Kind);
        Assert.Equal(0, invokeCount);
    }

    [Fact]
    public void F12_off_before_native_next_cancels_without_mutation()
    {
        var flow = new NetherBattleResultContinuationFlow(maximumMissingPolls: 1, maximumRebindPolls: 1);
        int invokeCount = 0;
        flow.Observe(new object(), new object(), floorGenerationBeforeResult: 3);

        NetherBattleResultContinuationStep step = flow.Pump(
            _ => NetherNativeActionResult.Completed("view-ready"),
            _ => { invokeCount++; return NetherNativeActionResult.Started("native-next"); },
            hasFloorSelection: false,
            currentFloorGeneration: 0,
            allowInvoke: false
        );

        Assert.Equal(NetherBattleResultContinuationStepKind.CanceledBeforeInvoke, step.Kind);
        Assert.Equal(0, invokeCount);
    }

    [Fact]
    public void Missing_result_view_registration_is_bounded()
    {
        var flow = new NetherBattleResultContinuationFlow(maximumMissingPolls: 1, maximumRebindPolls: 1);

        Assert.Equal(
            NetherBattleResultContinuationStepKind.AwaitingView,
            flow.Pump(
                _ => throw new Xunit.Sdk.XunitException("no task may be polled"),
                _ => throw new Xunit.Sdk.XunitException("no callback may be invoked"),
                false,
                0,
                true
            ).Kind
        );
        Assert.Equal(
            NetherBattleResultContinuationStepKind.BindingUnavailable,
            flow.Pump(
                _ => throw new Xunit.Sdk.XunitException("no task may be polled"),
                _ => throw new Xunit.Sdk.XunitException("no callback may be invoked"),
                false,
                0,
                true
            ).Kind
        );
    }
}
