#nullable enable

using System.Collections.Generic;
using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public sealed class NetherFloorEventSequenceTaskFlowTests
{
    [Fact]
    public void Completed_click_task_waits_for_the_real_floor_event_sequence_before_parent_terminal()
    {
        var click = new object();
        var sequence = new object();
        var statuses = new Dictionary<object, NetherNativeActionResult>
        {
            [click] = NetherNativeActionResult.Completed("click-scheduled-unitask-void"),
            [sequence] = NetherNativeActionResult.Started("event-popup-open"),
        };
        var flow = new NetherFloorEventSequenceTaskFlow(maximumMissingPolls: 2);

        Assert.True(flow.Begin());
        Assert.True(flow.ObserveClickTask(click));

        NetherNativeActionResult beforeSequence = flow.Pump(task => statuses[task]);
        Assert.Equal(NetherNativeActionResultKind.Started, beforeSequence.Kind);
        Assert.Equal("awaiting-native-floor-event-sequence-task", beforeSequence.Detail);
        Assert.True(flow.IsActive);
        Assert.True(flow.HasClickEvidence);

        Assert.True(flow.ObserveEventSequenceTask(sequence));
        NetherNativeActionResult popupOpen = flow.Pump(task => statuses[task]);
        Assert.Equal(NetherNativeActionResultKind.Started, popupOpen.Kind);
        Assert.Equal("event-popup-open", popupOpen.Detail);

        statuses[sequence] = NetherNativeActionResult.Completed("event-option-confirmed");
        NetherNativeActionResult terminal = flow.Pump(task => statuses[task]);
        Assert.Equal(NetherNativeActionResultKind.Completed, terminal.Kind);
        Assert.Equal("event-option-confirmed", terminal.Detail);
        Assert.False(flow.IsActive);
    }

    [Fact]
    public void Missing_floor_event_sequence_is_bounded_and_never_converted_to_click_completion()
    {
        var click = new object();
        var flow = new NetherFloorEventSequenceTaskFlow(maximumMissingPolls: 1);
        Assert.True(flow.Begin());
        Assert.True(flow.ObserveClickTask(click));

        Assert.Equal(
            NetherNativeActionResultKind.Started,
            flow.Pump(_ => NetherNativeActionResult.Completed("click-complete")).Kind
        );
        NetherNativeActionResult timeout = flow.Pump(
            _ => throw new Xunit.Sdk.XunitException("completed click must not be polled twice")
        );

        Assert.Equal(NetherNativeActionResultKind.BindingUnavailable, timeout.Kind);
        Assert.Equal("native-floor-event-sequence-task-timeout", timeout.Detail);
        Assert.False(flow.IsActive);
    }

    [Fact]
    public void Event_sequence_fault_is_terminal_and_cannot_replay_the_floor_click()
    {
        var click = new object();
        var sequence = new object();
        var flow = new NetherFloorEventSequenceTaskFlow(maximumMissingPolls: 2);
        Assert.True(flow.Begin());
        Assert.True(flow.ObserveClickTask(click));
        Assert.True(flow.ObserveEventSequenceTask(sequence));

        int clickPolls = 0;
        NetherNativeActionResult first = flow.Pump(task =>
        {
            if (ReferenceEquals(task, click))
            {
                clickPolls++;
                return NetherNativeActionResult.Completed("click-complete");
            }
            return NetherNativeActionResult.UnknownOutcome("event-sequence-fault");
        });

        Assert.Equal(NetherNativeActionResultKind.UnknownOutcome, first.Kind);
        Assert.Equal("event-sequence-fault", first.Detail);
        Assert.Equal(1, clickPolls);
        Assert.False(flow.IsActive);
    }

    [Fact]
    public void Recovered_sequence_is_polled_directly_without_replaying_a_floor_click()
    {
        var sequence = new object();
        var flow = new NetherFloorEventSequenceTaskFlow(maximumMissingPolls: 2);

        Assert.True(flow.BeginRecovered(sequence));
        Assert.True(flow.HasEventSequenceEvidence);

        int polls = 0;
        NetherNativeActionResult pending = flow.Pump(task =>
        {
            Assert.Same(sequence, task);
            polls++;
            return NetherNativeActionResult.Started("recovered-event-popup-open");
        });
        Assert.Equal(NetherNativeActionResultKind.Started, pending.Kind);

        NetherNativeActionResult complete = flow.Pump(task =>
        {
            Assert.Same(sequence, task);
            polls++;
            return NetherNativeActionResult.Completed("recovered-event-confirmed");
        });
        Assert.Equal(NetherNativeActionResultKind.Completed, complete.Kind);
        Assert.Equal(2, polls);
        Assert.False(flow.IsActive);
    }
}
