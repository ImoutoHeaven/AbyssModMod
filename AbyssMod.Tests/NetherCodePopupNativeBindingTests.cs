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

    [Fact]
    public void Keep_uses_the_exact_cancel_callback_not_the_confirm_or_detail_callback()
    {
        const string controller = "Project.Nether.AbyssCodeSelectPopup.AbyssCodeSelectPopupController";
        NetherNativeMethodDescriptor expected = NetherCodePopupNativeBinding.CancelDescriptor(controller);

        NetherNativeBindingSelection selected = NetherNativeMethodBindingSelector.Select(
            expected,
            [
                NetherCodePopupNativeBinding.ConfirmDescriptor(controller),
                new NetherNativeMethodDescriptor(
                    NetherCodePopupNativeBinding.DetailCallback,
                    new[] { "System.Int32", controller, "Project.Nether.AbyssCodeSelectPopup.AbyssCodeSelectPopup" },
                    "System.Void"
                ),
                expected,
            ]
        );

        Assert.Equal(NetherNativeActionResultKind.Started, selected.ResultKind);
        Assert.Equal(NetherCodePopupNativeBinding.CancelCallback, selected.Method!.Name);
    }

    [Fact]
    public void Keep_cancel_sequence_requires_the_exact_static_generated_task_signature()
    {
        const string controller = "Project.Nether.AbyssCodeSelectPopup.AbyssCodeSelectPopupController";
        NetherNativeMethodDescriptor expected = NetherCodePopupNativeBinding.CancelSequenceDescriptor(controller);

        NetherNativeBindingSelection selected = NetherNativeMethodBindingSelector.Select(
            expected,
            [
                expected with { IsStatic = false },
                expected with { ParameterTypeNames = new[] { controller } },
                expected with { ReturnTypeName = "System.Void" },
                expected with { IsStatic = true },
            ]
        );

        Assert.Equal(NetherNativeActionResultKind.Started, selected.ResultKind);
        Assert.True(selected.Method!.IsStatic);
    }

    [Fact]
    public void Keep_cancel_sequence_rejects_an_instance_lookalike_when_no_exact_static_target_exists()
    {
        const string controller = "Project.Nether.AbyssCodeSelectPopup.AbyssCodeSelectPopupController";
        NetherNativeMethodDescriptor expected = NetherCodePopupNativeBinding.CancelSequenceDescriptor(controller);

        NetherNativeBindingSelection selected = NetherNativeMethodBindingSelector.Select(
            expected,
            [expected with { IsStatic = false }]
        );

        Assert.Equal(NetherNativeActionResultKind.BindingUnavailable, selected.ResultKind);
        Assert.Null(selected.Method);
    }
}
