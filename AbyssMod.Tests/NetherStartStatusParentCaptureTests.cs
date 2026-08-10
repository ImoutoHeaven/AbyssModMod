#nullable enable

using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public sealed class NetherStartStatusParentCaptureTests
{
    [Fact]
    public void Popup_registered_between_state_machine_enter_and_exit_uses_the_matching_parent_task()
    {
        var capture = new NetherStartStatusParentCapture();
        var stateMachine = new object();
        var controller = new object();
        var parentTask = new object();

        Assert.True(capture.ObserveStateMachineEnter(stateMachine, controller));
        Assert.True(capture.TryAttachPopup(controller));
        Assert.False(capture.IsReady(controller));

        Assert.True(capture.ObserveStateMachineExit(stateMachine, parentTask));
        Assert.True(capture.IsReady(controller));
        Assert.True(capture.TryGetParentTask(controller, out object? captured));
        Assert.Same(parentTask, captured);
    }

    [Fact]
    public void Attached_parent_cannot_be_replaced_by_an_unrelated_state_machine()
    {
        var capture = new NetherStartStatusParentCapture();
        var owner = new object();
        var intruder = new object();
        var controller = new object();
        var ownerTask = new object();

        Assert.True(capture.ObserveStateMachineEnter(owner, controller));
        Assert.True(capture.TryAttachPopup(controller));
        Assert.False(capture.ObserveStateMachineEnter(intruder, controller));
        Assert.False(capture.ObserveStateMachineExit(intruder, new object()));
        Assert.True(capture.ObserveStateMachineExit(owner, ownerTask));

        Assert.True(capture.TryGetParentTask(controller, out object? captured));
        Assert.Same(ownerTask, captured);
    }

    [Fact]
    public void New_state_machine_replaces_an_unattached_stale_candidate()
    {
        var capture = new NetherStartStatusParentCapture();
        var stale = new object();
        var current = new object();
        var controller = new object();
        var currentTask = new object();

        Assert.True(capture.ObserveStateMachineEnter(stale, controller));
        Assert.True(capture.ObserveStateMachineEnter(current, controller));
        Assert.False(capture.ObserveStateMachineExit(stale, new object()));
        Assert.True(capture.TryAttachPopup(controller));
        Assert.True(capture.ObserveStateMachineExit(current, currentTask));

        Assert.True(capture.TryGetParentTask(controller, out object? captured));
        Assert.Same(currentTask, captured);
    }

    [Fact]
    public void Clear_invalidates_state_machine_popup_and_parent_task_together()
    {
        var capture = new NetherStartStatusParentCapture();
        var stateMachine = new object();
        var controller = new object();

        Assert.True(capture.ObserveStateMachineEnter(stateMachine, controller));
        Assert.True(capture.TryAttachPopup(controller));
        Assert.True(capture.ObserveStateMachineExit(stateMachine, new object()));

        capture.Clear();

        Assert.False(capture.IsReady(controller));
        Assert.False(capture.TryGetParentTask(controller, out _));
        Assert.False(capture.TryAttachPopup(controller));
    }
}
