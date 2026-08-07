using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherCodeSelectionNativeFlowTests
{
    [Fact]
    public void Replacement_waits_for_the_generated_confirmation_task_and_a_fresh_replace_popup()
    {
        var flow = new NetherCodeSelectionNativeFlow();

        Assert.True(flow.Begin(codeId: 30024, replaceCodeId: 10001, popupSequenceBaseline: 4));
        Assert.Equal(NetherCodeSelectionNativeStage.AwaitingConfirmationTask, flow.Stage);

        Assert.True(flow.ObserveConfirmationTask());
        Assert.Equal(NetherCodeSelectionNativeStage.AwaitingReplacementPopup, flow.Stage);
        Assert.False(flow.CanSubmitReplacement(popupSequence: 4));
        Assert.True(flow.CanSubmitReplacement(popupSequence: 5));

        Assert.True(flow.SubmitReplacement(popupSequence: 5));
        Assert.Equal(NetherCodeSelectionNativeStage.AwaitingCompletion, flow.Stage);
        Assert.True(flow.CompleteConfirmationTask());
        Assert.Equal(NetherCodeSelectionNativeStage.Completed, flow.Stage);
    }

    [Fact]
    public void Direct_offer_never_requires_a_replace_popup_but_still_waits_for_confirmation_task()
    {
        var flow = new NetherCodeSelectionNativeFlow();

        Assert.True(flow.Begin(codeId: 30024, replaceCodeId: 0, popupSequenceBaseline: 9));
        Assert.True(flow.ObserveConfirmationTask());

        Assert.Equal(NetherCodeSelectionNativeStage.AwaitingCompletion, flow.Stage);
        Assert.False(flow.CanSubmitReplacement(popupSequence: 10));
        Assert.True(flow.CompleteConfirmationTask());
    }

    [Fact]
    public void Out_of_order_or_stale_replace_popup_is_rejected()
    {
        var flow = new NetherCodeSelectionNativeFlow();

        Assert.True(flow.Begin(codeId: 30024, replaceCodeId: 10001, popupSequenceBaseline: 5));
        Assert.False(flow.SubmitReplacement(popupSequence: 6));
        Assert.True(flow.ObserveConfirmationTask());
        Assert.False(flow.SubmitReplacement(popupSequence: 5));
    }
}
