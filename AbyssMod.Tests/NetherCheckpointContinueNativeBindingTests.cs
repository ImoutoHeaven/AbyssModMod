#nullable enable

using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherCheckpointContinueNativeBindingTests
{
    [Theory]
    [InlineData(false, (int)NetherCheckpointNativeStage.AwaitingTerminalTask)]
    [InlineData(true, (int)NetherCheckpointNativeStage.AwaitingBoostConfirmation)]
    public void Exact_continue_callback_enters_the_owned_stage_for_both_canBoost_branches(
        bool canBoost,
        int expectedStage
    )
    {
        NetherNativeMethodDescriptor callback = NetherCheckpointContinueNativeBinding.ContinueCallback;
        var flow = new NetherCheckpointNativeFlow();
        Assert.True(flow.Begin(new NetherPlannedAction(NetherActionKind.Continue)));

        Assert.Equal("<SetupPopupEvent>b__8_2", callback.Name);
        Assert.True(NetherCheckpointContinueNativeBinding.SubmitContinue(flow, canBoost));
        Assert.Equal((NetherCheckpointNativeStage)expectedStage, flow.Stage);
    }

    [Fact]
    public void Boost_confirmation_uses_the_exact_one_ticket_count_before_its_confirm_callback()
    {
        Assert.Equal(1, NetherCheckpointContinueNativeBinding.ExactTicketCount);
        Assert.Equal("<SetupPopupEvent>b__7_2", NetherCheckpointContinueNativeBinding.BoostSetCount.Name);
        Assert.Equal("<SetupPopupEvent>b__7_1", NetherCheckpointContinueNativeBinding.BoostConfirm.Name);
    }
}
