using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherCodePopupNativeBindingTests
{
    [Fact]
    public void Normal_offer_confirmation_uses_distinct_confirm_callback_not_cancel()
    {
        Assert.Equal("<SetupPopupEvent>b__12_2", NetherCodePopupNativeBinding.ConfirmCallback);
        Assert.Equal("<SetupPopupEvent>b__12_0", NetherCodePopupNativeBinding.CancelCallback);
        Assert.NotEqual(NetherCodePopupNativeBinding.CancelCallback, NetherCodePopupNativeBinding.ConfirmCallback);
        Assert.NotEqual(NetherCodePopupNativeBinding.DetailCallback, NetherCodePopupNativeBinding.ConfirmCallback);
    }

    [Fact]
    public void Confirm_descriptor_selects_only_the_exact_generated_confirm_delegate()
    {
        NetherNativeMethodDescriptor expected = NetherCodePopupNativeBinding.ConfirmDescriptor(
            "Project.Nether.AbyssCodeSelectPopup.AbyssCodeSelectPopupController"
        );
        NetherNativeBindingSelection selected = NetherNativeMethodBindingSelector.Select(
            expected,
            [
                new NetherNativeMethodDescriptor(
                    NetherCodePopupNativeBinding.CancelCallback,
                    new[] { "UniRx.Unit", "Project.Nether.AbyssCodeSelectPopup.AbyssCodeSelectPopupController" },
                    "System.Void"
                ),
                expected,
            ]
        );

        Assert.Equal(NetherNativeActionResultKind.Started, selected.ResultKind);
        Assert.Equal(NetherCodePopupNativeBinding.ConfirmCallback, selected.Method!.Name);
    }
}
