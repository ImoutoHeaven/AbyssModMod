using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherCheckpointNativeFlowTests
{
    [Fact]
    public void Continue_requires_real_continue_then_boost_then_pristine_return_before_task_completion()
    {
        var flow = new NetherCheckpointNativeFlow();
        Assert.True(flow.Begin(new NetherPlannedAction(NetherActionKind.Continue) { ReturnLockReward = 1 }));
        Assert.Equal(NetherCheckpointNativeStage.AwaitingContinuePopup, flow.Stage);

        Assert.True(flow.SubmitContinue(canBoost: true));
        Assert.Equal(NetherCheckpointNativeStage.AwaitingBoostConfirmation, flow.Stage);
        Assert.False(flow.CanSubmitReturnSelection);

        Assert.True(flow.SubmitBoostConfirmation());
        Assert.Equal(NetherCheckpointNativeStage.AwaitingPristineReturnPopup, flow.Stage);
        Assert.True(flow.CanSubmitReturnSelection);

        Assert.True(flow.SubmitReturnSelection());
        Assert.Equal(NetherCheckpointNativeStage.AwaitingTerminalTask, flow.Stage);
    }

    [Fact]
    public void Finish_never_enters_return_item_stage()
    {
        var flow = new NetherCheckpointNativeFlow();
        Assert.True(flow.Begin(new NetherPlannedAction(NetherActionKind.FinishAtCheckpoint)));

        Assert.True(flow.SubmitFinish());

        Assert.Equal(NetherCheckpointNativeStage.AwaitingTerminalTask, flow.Stage);
        Assert.False(flow.CanSubmitReturnSelection);
    }

    [Fact]
    public void Sequence_rejects_a_second_checkpoint_while_first_is_in_flight()
    {
        var flow = new NetherCheckpointNativeFlow();
        Assert.True(flow.Begin(new NetherPlannedAction(NetherActionKind.Continue)));

        Assert.False(flow.Begin(new NetherPlannedAction(NetherActionKind.FinishAtCheckpoint)));
    }
}
